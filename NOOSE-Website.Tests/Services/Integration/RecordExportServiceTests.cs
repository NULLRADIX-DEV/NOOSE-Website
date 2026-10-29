using System.Security.Claims;
using System.Text;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Export;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Both exports carry data out of the rights system: who may, what leaves, and that every one is logged.</summary>
public sealed class RecordExportServiceTests
{
    private static readonly ClaimsPrincipal Leader =
        ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SupervisorySpecialAgent).WithCodename("Hawk");

    private static readonly ClaimsPrincipal Supervision =
        ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.SpecialAgent).AsTeamLead().WithCodename("Owl");

    private static readonly ClaimsPrincipal Senior =
        ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent);

    private static readonly ClaimsPrincipal Partner =
        ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief);

    private static readonly ClaimsPrincipal Applicant =
        ClaimsPrincipalBuilder.Agent("applicant").WithStatus(AgentStatus.Applicant);

    private static (RecordExportService Service, IAccessLogService Log) Create(SqliteTestContext ctx)
    {
        var log = Substitute.For<IAccessLogService>();
        return (new RecordExportService(ctx.Factory, log), log);
    }

    private static string SeedPerson(SqliteTestContext ctx, Action<Person>? configure = null)
    {
        using var db = ctx.NewContext();
        var person = Seed.Person(name: "Max Mustermann", configure: configure);
        db.People.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    // ---- list ----

    [Fact]
    public async Task List_IsASemicolonCsvWithBom()
    {
        using var ctx = new SqliteTestContext();
        var (service, _) = Create(ctx);

        var file = await service.ListAsync(nameof(Person), ["Name", "Score"], [["Max", "80"], ["Lena; L.", ""]], Senior);

        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("﻿Name;Score\r\nMax;80\r\n\"Lena; L.\";\r\n", Encoding.UTF8.GetString(file.Content));
    }

    [Fact]
    public async Task List_IsNamedAfterTheListAndTheDay()
    {
        using var ctx = new SqliteTestContext();
        var (service, _) = Create(ctx);

        var file = await service.ListAsync(nameof(Data.Entities.Cases.Case), ["Titel"], [], Senior);

        Assert.Matches(@"^vorgaenge-\d{4}-\d{2}-\d{2}\.csv$", file.FileName);
    }

    [Fact]
    public async Task List_IsLoggedAsAnExportWithItsRowCount()
    {
        using var ctx = new SqliteTestContext();
        var (service, log) = Create(ctx);

        await service.ListAsync(nameof(Person), ["Name"], [["Max"], ["Lena"]], Senior);

        await log.Received(1).LogViewAsync("Export", "Personen-Liste (2)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_RefusesAPartner()
    {
        using var ctx = new SqliteTestContext();
        var (service, log) = Create(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ListAsync(nameof(Person), ["Name"], [["Max"]], Partner));
        await log.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    [Fact]
    public async Task List_RefusesAnApplicant()
    {
        using var ctx = new SqliteTestContext();
        var (service, _) = Create(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ListAsync(nameof(Person), ["Name"], [["Max"]], Applicant));
    }

    [Fact]
    public async Task List_RejectsAListOutsideTheSix()
    {
        using var ctx = new SqliteTestContext();
        var (service, log) = Create(ctx);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ListAsync("Taskforce", ["Name"], [["X"]], Leader));
        await log.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    // ---- dossier ----

    [Fact]
    public async Task Dossier_GivesLeadershipAMarkdownFile()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        var (service, _) = Create(ctx);

        var file = await service.DossierAsync(nameof(Person), id, Leader);

        Assert.NotNull(file);
        Assert.Equal("text/markdown; charset=utf-8", file!.ContentType);
        Assert.StartsWith("dossier-max-mustermann-noose-p-", file.FileName);
        var text = Encoding.UTF8.GetString(file.Content);
        Assert.StartsWith("# Max Mustermann (NOOSE-P-", text);
        Assert.Contains("> Exportiert von Hawk am ", text);
        Assert.Contains("- **Name:** Max Mustermann", text);
    }

    [Fact]
    public async Task Dossier_IsComplete()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        using (var db = ctx.NewContext())
        {
            for (var i = 1; i <= 60; i++)
            {
                db.Comments.Add(new Comment
                {
                    EntityType = nameof(Person),
                    EntityId = id,
                    AuthorName = "Codename-x",
                    Text = $"Notiz {i:00}",
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                });
            }
            db.SaveChanges();
        }
        var (service, _) = Create(ctx);

        var file = await service.DossierAsync(nameof(Person), id, Leader);

        Assert.Contains("- Codename-x: Notiz 60", Encoding.UTF8.GetString(file!.Content));
    }

    [Fact]
    public async Task Dossier_MarksAClassifiedRecord()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx, p => p.IsClassified = true);
        var (service, _) = Create(ctx);

        var file = await service.DossierAsync(nameof(Person), id, Leader);

        Assert.Contains("> Personenakte · Verschlusssache", Encoding.UTF8.GetString(file!.Content));
    }

    [Fact]
    public async Task Dossier_IsLoggedAsAnExportOfThatRecord()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        var (service, log) = Create(ctx);

        await service.DossierAsync(nameof(Person), id, Leader);

        await log.Received(1).LogViewAsync("Export", $"Person:{id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dossier_IsOpenToTheReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        var (service, _) = Create(ctx);

        var file = await service.DossierAsync(nameof(Person), id, Supervision);

        Assert.Contains("> Exportiert von Owl am ", Encoding.UTF8.GetString(file!.Content));
    }

    [Fact]
    public async Task Dossier_RefusesAnAgentBelowLeadership()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        var (service, log) = Create(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DossierAsync(nameof(Person), id, Senior));
        await log.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    [Fact]
    public async Task Dossier_RefusesAPartner()
    {
        using var ctx = new SqliteTestContext();
        var id = SeedPerson(ctx);
        var (service, _) = Create(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DossierAsync(nameof(Person), id, Partner));
    }

    [Fact]
    public async Task Dossier_OfAnUnknownRecord_IsNullAndUnlogged()
    {
        using var ctx = new SqliteTestContext();
        var (service, log) = Create(ctx);

        Assert.Null(await service.DossierAsync(nameof(Person), "missing", Leader));
        await log.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    [Fact]
    public async Task Dossier_RejectsATypeOutsideTheSix()
    {
        using var ctx = new SqliteTestContext();
        var (service, _) = Create(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => service.DossierAsync("Taskforce", "t1", Leader));
    }
}
