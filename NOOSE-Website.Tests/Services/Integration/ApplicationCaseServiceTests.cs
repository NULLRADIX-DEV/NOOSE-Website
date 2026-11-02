using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Tests for the auto-provisioning of a case + security-check document on HRB assignment.</summary>
public sealed class ApplicationCaseServiceTests
{
    private sealed class Collaborators
    {
        public IRecruitingAutomationService Automation { get; } = Substitute.For<IRecruitingAutomationService>();
        public ICaseService Cases { get; } = Substitute.For<ICaseService>();
        public IDocumentService Documents { get; } = Substitute.For<IDocumentService>();
        public IDocumentTemplateService Templates { get; } = Substitute.For<IDocumentTemplateService>();
        public ISourceService Sources { get; } = Substitute.For<ISourceService>();
        public IPlaceholderService Placeholders { get; } = Substitute.For<IPlaceholderService>();
    }

    private static ApplicationCaseService Build(SqliteTestContext ctx, Collaborators c)
        => new(ctx.Factory, c.Automation, c.Cases, c.Documents, c.Templates, c.Sources, c.Placeholders,
               Substitute.For<ILogger<ApplicationCaseService>>());

    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").AsHrb().WithRank(Rank.Trainee).WithCodename("Falcon").Build();

    private static void SeedBewerbung(SqliteTestContext ctx, string id = "b1", string name = "Max Mustermann", string? linkedCaseId = null)
    {
        using var db = ctx.NewContext();
        db.Bewerbungen.Add(new Bewerbung
        {
            Id = id,
            Name = name,
            ApplicantUserId = "u1",
            CaseNumber = "FIB-B-2026-0001",
            LinkedCaseId = linkedCaseId,
            SubmittedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();
    }

    // wires an enabled config + happy-path return values for every collaborator
    private static Collaborators EnabledCollaborators()
    {
        var c = new Collaborators();
        c.Automation.GetAsync(Arg.Any<CancellationToken>()).Returns(new RecruitingAutomationConfig(true, "tmpl-1"));
        c.Cases.CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Case { Id = "case1", Title = ((CaseInput)ci[0]).Title });
        c.Templates.GetAsync("tmpl-1", Arg.Any<CancellationToken>())
            .Returns(new DocumentTemplate { Id = "tmpl-1", ContentHtml = "<p>{{Name}}</p>" });
        c.Placeholders.ApplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns("<p>expanded</p>");
        c.Documents.CreateAsync(Arg.Any<DocumentInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(di => new Document { Id = "doc1", Title = ((DocumentInput)di[0]).Title });
        return c;
    }

    [Fact]
    public async Task Creates_case_document_and_attachment_with_applicant_name()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Cases.Received(1).CreateAsync(
            Arg.Is<CaseInput>(i => i.Title == "Bewerbungsverfahren | Max Mustermann"
                && i.Classification == Classification.Unknown
                && i.SecrecyLevel == DocumentClassification.Hrb),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        await c.Placeholders.Received(1).ApplyAsync("<p>{{Name}}</p>", nameof(Case), "case1",
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        await c.Documents.Received(1).CreateAsync(
            Arg.Is<DocumentInput>(i => i.Title == "Sicherheitsüberprüfung | Max Mustermann"
                && i.ContentHtml == "<p>expanded</p>"
                && i.Classification == DocumentClassification.Hrb),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        await c.Sources.Received(1).CreateAsync(nameof(Case), "case1",
            Arg.Is<SourceInput>(i => i.Type == SourceType.Document
                && i.TargetType == nameof(Document)
                && i.TargetId == "doc1"
                && i.Title == "Sicherheitsüberprüfung | Max Mustermann"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        using var db = ctx.NewContext();
        var b = await db.Bewerbungen.FirstAsync(x => x.Id == "b1");
        Assert.Equal("case1", b.LinkedCaseId);
    }

    [Fact]
    public async Task Idempotent_when_already_linked()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx, linkedCaseId: "existing");
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Cases.DidNotReceive().CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Discards_duplicate_case_when_claim_is_lost()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();
        // simulate a concurrent assignment winning between the fast-path read and the atomic claim:
        // a rival stamps LinkedCaseId first, and this call still persisted its own (now-duplicate) case
        c.Cases.CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                using var db = ctx.NewContext();
                db.Bewerbungen.Single(x => x.Id == "b1").LinkedCaseId = "winner";
                db.Cases.Add(new Case { Id = "case1", CaseNumber = "FIB-V-2026-0002", Title = ((CaseInput)ci[0]).Title });
                db.CaseAgents.Add(new CaseAgent { CaseId = "case1", AgentId = "hrb", IsCaseLead = true });
                db.SaveChanges();
                return new Case { Id = "case1", Title = ((CaseInput)ci[0]).Title };
            });

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Documents.DidNotReceive().CreateAsync(Arg.Any<DocumentInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        await c.Sources.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SourceInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        using var check = ctx.NewContext();
        Assert.Equal("winner", check.Bewerbungen.Single(x => x.Id == "b1").LinkedCaseId);
        Assert.True(check.Cases.IgnoreQueryFilters().Single(v => v.Id == "case1").IsDeleted);
        Assert.Empty(check.CaseAgents.Where(a => a.CaseId == "case1"));
        Assert.Contains(check.AuditLogs, l => l.EntityType == nameof(Case) && l.EntityId == "case1" && l.Action == AuditAction.Deleted);
    }

    [Fact]
    public async Task NoOp_when_disabled()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();
        c.Automation.GetAsync(Arg.Any<CancellationToken>()).Returns(new RecruitingAutomationConfig(false, "tmpl-1"));

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Cases.DidNotReceive().CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoOp_when_actor_may_not_write()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();

        // team-lead without admin is an only-reader → MayWrite() false
        var reader = ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().WithRank(Rank.Director).Build();
        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", reader);

        await c.Cases.DidNotReceive().CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        await c.Automation.DidNotReceive().GetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_built_in_body_when_template_missing()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();
        c.Templates.GetAsync("tmpl-1", Arg.Any<CancellationToken>()).Returns((DocumentTemplate?)null);

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Placeholders.Received(1).ApplyAsync(ApplicationTemplates.SecurityCheckBody, nameof(Case), "case1",
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }
}
