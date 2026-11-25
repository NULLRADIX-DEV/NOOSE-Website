using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Tests for the auto-provisioning of the security-check document on HRB assignment.</summary>
public sealed class ApplicationCaseServiceTests
{
    private sealed class Collaborators
    {
        public IRecruitingAutomationService Automation { get; } = Substitute.For<IRecruitingAutomationService>();
        public IDocumentService Documents { get; } = Substitute.For<IDocumentService>();
        public IDocumentTemplateService Templates { get; } = Substitute.For<IDocumentTemplateService>();
        public ISourceService Sources { get; } = Substitute.For<ISourceService>();
        public IPlaceholderService Placeholders { get; } = Substitute.For<IPlaceholderService>();
    }

    private static ApplicationCaseService Build(SqliteTestContext ctx, Collaborators c)
        => new(ctx.Factory, c.Automation, c.Documents, c.Templates, c.Sources, c.Placeholders,
               Substitute.For<ILogger<ApplicationCaseService>>());

    private static Task FiledOnApplication(Collaborators c, int times, string documentId)
        => c.Sources.Received(times).CreateAsync(nameof(Bewerbung), "b1",
            Arg.Is<SourceInput>(i => i.Type == SourceType.Document
                && i.TargetType == nameof(Document)
                && i.TargetId == documentId
                && i.Title == "Sicherheitsüberprüfung | Max Mustermann"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

    private static Task NoDocumentCreated(Collaborators c)
        => c.Documents.DidNotReceive().CreateAsync(Arg.Any<DocumentInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

    private static void SeedFiledSecurityCheck(SqliteTestContext ctx, string entityId = "existing", string documentId = "old-doc",
        string entityType = nameof(Case))
    {
        using var db = ctx.NewContext();
        db.Sources.Add(new Source
        {
            EntityType = entityType,
            EntityId = entityId,
            Type = SourceType.Document,
            Title = "Sicherheitsüberprüfung | Max Mustermann",
            TargetType = nameof(Document),
            TargetId = documentId,
        });
        db.SaveChanges();
    }

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
        c.Templates.GetAsync("tmpl-1", Arg.Any<CancellationToken>())
            .Returns(new DocumentTemplate { Id = "tmpl-1", ContentHtml = "<p>{{Name}} {{aktenzeichen}} {{Datum}}</p>" });
        c.Placeholders.ApplyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns("<p>expanded</p>");
        c.Documents.CreateAsync(Arg.Any<DocumentInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(di => new Document { Id = "doc1", Title = ((DocumentInput)di[0]).Title });
        return c;
    }

    [Fact]
    public async Task Creates_only_the_document_and_files_it_on_the_application()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        // the application fills name and case number, the placeholder service the rest
        await c.Placeholders.Received(1).ApplyAsync("<p>Max Mustermann FIB-B-2026-0001 {{Datum}}</p>", null, null,
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        await c.Documents.Received(1).CreateAsync(
            Arg.Is<DocumentInput>(i => i.Title == "Sicherheitsüberprüfung | Max Mustermann"
                && i.ContentHtml == "<p>expanded</p>"
                && i.Classification == DocumentClassification.Hrb),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        await FiledOnApplication(c, 1, "doc1");
        await c.Sources.DidNotReceive().CreateAsync(nameof(Case), Arg.Any<string>(), Arg.Any<SourceInput>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());

        using var db = ctx.NewContext();
        Assert.Empty(db.Cases);
        Assert.Null(db.Bewerbungen.Single(x => x.Id == "b1").LinkedCaseId);
    }

    [Fact]
    public async Task Encodes_the_applicant_name_in_the_body()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx, name: "<b>Max</b>");
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await c.Placeholders.Received(1).ApplyAsync("<p>&lt;b&gt;Max&lt;/b&gt; FIB-B-2026-0001 {{Datum}}</p>", null, null,
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Idempotent_when_the_document_is_already_on_the_application()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        SeedFiledSecurityCheck(ctx, entityId: "b1", entityType: nameof(Bewerbung));
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await NoDocumentCreated(c);
        await FiledOnApplication(c, 0, "old-doc");
    }

    [Fact]
    public async Task A_second_trigger_does_not_create_a_second_document()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();
        // the source service is a fake, so persist what it was asked to file
        c.Sources.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SourceInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                SeedFiledSecurityCheck(ctx, entityId: (string)ci[1], entityType: (string)ci[0], documentId: ((SourceInput)ci[2]).TargetId!);
                return new Source();
            });
        var service = Build(ctx, c);

        await Task.WhenAll(service.EnsureSecurityCheckCaseAsync("b1", Hrb()), service.EnsureSecurityCheckCaseAsync("b1", Hrb()));

        await c.Documents.Received(1).CreateAsync(Arg.Any<DocumentInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Files_the_existing_security_check_of_an_older_case_on_the_application()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx, linkedCaseId: "existing");
        SeedFiledSecurityCheck(ctx);
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await NoDocumentCreated(c);
        await FiledOnApplication(c, 1, "old-doc");
    }

    [Fact]
    public async Task Leaves_a_security_check_already_filed_on_the_application_alone()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx, linkedCaseId: "existing");
        SeedFiledSecurityCheck(ctx);
        SeedFiledSecurityCheck(ctx, entityId: "b1", entityType: nameof(Bewerbung));
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await NoDocumentCreated(c);
        await FiledOnApplication(c, 0, "old-doc");
    }

    [Fact]
    public async Task Creates_the_document_when_an_older_case_has_none()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx, linkedCaseId: "existing");
        var c = EnabledCollaborators();

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await FiledOnApplication(c, 1, "doc1");
    }

    [Fact]
    public async Task NoOp_when_disabled()
    {
        using var ctx = new SqliteTestContext();
        SeedBewerbung(ctx);
        var c = EnabledCollaborators();
        c.Automation.GetAsync(Arg.Any<CancellationToken>()).Returns(new RecruitingAutomationConfig(false, "tmpl-1"));

        await Build(ctx, c).EnsureSecurityCheckCaseAsync("b1", Hrb());

        await NoDocumentCreated(c);
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

        await NoDocumentCreated(c);
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

        await c.Placeholders.Received(1).ApplyAsync(
            Arg.Is<string>(s => s.Contains("Bewerbung: Max Mustermann") && s.Contains("Aktenzeichen: FIB-B-2026-0001")),
            Arg.Is<string?>(t => t == null), Arg.Is<string?>(i => i == null), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }
}
