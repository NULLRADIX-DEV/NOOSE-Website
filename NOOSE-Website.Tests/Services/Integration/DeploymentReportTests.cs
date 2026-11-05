using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Deployments as operations: report fields, faction heat and freshness, the faction tab, closed activities.</summary>
public sealed class DeploymentReportTests
{
    private static readonly DateTime T0 = DateTime.UtcNow.AddDays(-1);

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static IThreatScoreConfigService Config()
    {
        var c = Substitute.For<IThreatScoreConfigService>();
        c.GetAsync(Arg.Any<CancellationToken>()).Returns(ThreatScoreConfiguration.Default());
        return c;
    }

    private static OperationService Operations(SqliteTestContext ctx, IThreatScoreService? threat = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-OP-2026-0001");
        return new OperationService(ctx.Factory, caseNo, Substitute.For<IProfileSuggestionService>(),
            Substitute.For<INotificationService>(), threat);
    }

    private static Operation Op(string id, OperationStatus status, string? type = "Geiselnahme", bool classified = false)
        => new()
        {
            Id = id,
            CaseNumber = "FIB-OP-2026-" + id,
            Title = "Einsatz " + id,
            Type = type,
            Status = status,
            Start = T0,
            IsClassified = classified,
            CreatedAt = T0,
        };

    private static Link Involved(string operationId, string factionId)
        => new() { SourceType = nameof(Operation), SourceId = operationId, TargetType = nameof(Faction), TargetId = factionId };

    private static void Seed2(SqliteTestContext ctx, Action<AppDbContext>? extra = null)
    {
        using var db = ctx.NewContext();
        foreach (var id in new[] { "f1", "f2" })
        {
            db.Factions.Add(Seed.Faction(id: id, name: "Fraktion " + id,
                configure: f => f.Classification = Classification.SuspicionCase));
        }
        extra?.Invoke(db);
        db.SaveChanges();
    }

    private static async Task<int?> ScoreAsync(SqliteTestContext ctx, string id)
    {
        await using var db = ctx.NewContext();
        return (await db.Factions.SingleAsync(f => f.Id == id)).ThreatScore;
    }

    // ---------- faction heat ----------

    // both factions carry one link, so the network part is equal and only the heat differs
    [Fact]
    public async Task LinkedDeployment_AddsHeat_OnceItTookPlace()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db =>
        {
            db.Operations.AddRange(Op("done", OperationStatus.Completed), Op("planned", OperationStatus.Planned));
            db.Links.AddRange(Involved("done", "f1"), Involved("planned", "f2"));
        });
        var threat = new ThreatScoreService(ctx.Factory, Config(), Substitute.For<INotificationService>());

        await threat.NewCalculateAsync("f1");
        await threat.NewCalculateAsync("f2");
        var withHeat = await ScoreAsync(ctx, "f1");
        var planned = await ScoreAsync(ctx, "f2");

        await using (var db = ctx.NewContext())
        {
            await db.Operations.Where(o => o.Id == "planned")
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OperationStatus.Running));
        }
        await threat.NewCalculateAsync("f2");

        Assert.True(withHeat > planned);
        Assert.Equal(withHeat, await ScoreAsync(ctx, "f2"));
    }

    [Fact]
    public async Task StatusChange_RecomputesAndRefreshesLinkedFaction()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db =>
        {
            db.Operations.Add(Op("op1", OperationStatus.Planned));
            db.Links.Add(Involved("op1", "f1"));
        });
        var threat = Substitute.For<IThreatScoreService>();
        var svc = Operations(ctx, threat);

        await svc.RefreshAsync("op1", new OperationInput { Title = "Einsatz op1", Type = "Geiselnahme", Status = OperationStatus.Running,
            Start = T0.ToLocalTime() }, Leader());

        await threat.Received(1).NewCalculateAsync("f1", Arg.Any<CancellationToken>());
        await threat.DidNotReceive().NewCalculateAsync("f2", Arg.Any<CancellationToken>());
        await using var db = ctx.NewContext();
        Assert.NotNull((await db.Factions.SingleAsync(f => f.Id == "f1")).ActivitiesRefreshedAt);
    }

    [Fact]
    public async Task UnchangedHeatFields_DoNotRecompute()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db =>
        {
            db.Operations.Add(Op("op1", OperationStatus.Running));
            db.Links.Add(Involved("op1", "f1"));
        });
        var threat = Substitute.For<IThreatScoreService>();

        await Operations(ctx, threat).RefreshAsync("op1", new OperationInput { Title = "Neuer Titel", Type = "Geiselnahme",
            Status = OperationStatus.Running, Start = T0.ToLocalTime(), Evidence = "1x Pistole" }, Leader());

        await threat.DidNotReceive().NewCalculateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkingRunningDeployment_RefreshesFactionActivities()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db => db.Operations.Add(Op("op1", OperationStatus.Running)));
        var links = new LinkService(ctx.Factory, Substitute.For<IThreatScoreService>());

        await links.CreateAsync(nameof(Operation), "op1", nameof(Faction), "f1", null, Leader(),
            allowedTargetTypes: LinkService.InvolvedTypes);

        await using var db = ctx.NewContext();
        Assert.NotNull((await db.Factions.SingleAsync(f => f.Id == "f1")).ActivitiesRefreshedAt);
    }

    [Fact]
    public async Task LinkingPlannedDeployment_LeavesFreshnessAlone()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db => db.Operations.Add(Op("op1", OperationStatus.Planned)));
        var links = new LinkService(ctx.Factory, Substitute.For<IThreatScoreService>());

        await links.CreateAsync(nameof(Operation), "op1", nameof(Faction), "f1", null, Leader(),
            allowedTargetTypes: LinkService.InvolvedTypes);

        await using var db = ctx.NewContext();
        Assert.Null((await db.Factions.SingleAsync(f => f.Id == "f1")).ActivitiesRefreshedAt);
    }

    // ---------- faction tab ----------

    [Fact]
    public async Task GetLinkedAsync_ReturnsInvolvedDeployments_HidesClassifiedFromJunior()
    {
        using var ctx = new SqliteTestContext();
        Seed2(ctx, db =>
        {
            db.Operations.AddRange(Op("open", OperationStatus.Completed), Op("vs", OperationStatus.Completed, classified: true),
                Op("other", OperationStatus.Completed));
            db.Links.AddRange(Involved("open", "f1"), Involved("vs", "f1"), Involved("other", "f2"),
                new Link { SourceType = nameof(Faction), SourceId = "f1", TargetType = nameof(Operation), TargetId = "other",
                    Automatic = true });
        });
        var svc = Operations(ctx);

        var leader = await svc.GetLinkedAsync(nameof(Faction), "f1", ViewerScope.From(Leader()));
        var junior = await svc.GetLinkedAsync(nameof(Faction), "f1", ViewerScope.From(Junior()));

        Assert.Equal(new[] { "open", "vs" }, leader.Select(o => o.Id).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "open" }, junior.Select(o => o.Id).ToArray());
    }

    // ---------- report fields ----------

    [Fact]
    public async Task CreateAsync_StoresReportFields_AndStartAsUtc()
    {
        using var ctx = new SqliteTestContext();
        var local = new DateTime(2026, 11, 2, 20, 15, 0, DateTimeKind.Unspecified);

        var created = await Operations(ctx).CreateAsync(new OperationInput
        {
            Title = "Bankraub – Bloc13",
            Type = "Bankraub",
            Status = OperationStatus.Completed,
            Start = local,
            Legitimation = "  Geiselnahme mit Verdacht auf Chief of Police  ",
            InvolvedAgencies = " LSPD ",
            Evidence = "1x Schweres Gewehr",
        }, Leader());

        await using var db = ctx.NewContext();
        var stored = await db.Operations.SingleAsync(o => o.Id == created.Id);
        Assert.Equal("Geiselnahme mit Verdacht auf Chief of Police", stored.Legitimation);
        Assert.Equal("LSPD", stored.InvolvedAgencies);
        Assert.Equal("1x Schweres Gewehr", stored.Evidence);
        Assert.Equal(local.ToUniversalTime(), DateTime.SpecifyKind(stored.Start!.Value, DateTimeKind.Utc));
    }

    // ---------- roles and types ----------

    [Theory]
    [InlineData("Beschuldigter", OperationRoleGroup.Accused)]
    [InlineData("Tatverdächtiger", OperationRoleGroup.Accused)]
    [InlineData("Mittäter", OperationRoleGroup.Accused)]
    [InlineData("Geisel", OperationRoleGroup.Victim)]
    [InlineData("Opfer", OperationRoleGroup.Victim)]
    [InlineData("Zeuge", OperationRoleGroup.Other)]
    [InlineData(null, OperationRoleGroup.Other)]
    public void RoleGroup_SortsPersonsForTheReport(string? label, OperationRoleGroup expected)
        => Assert.Equal(expected, OperationInvolvedRoles.Group(label));

    [Fact]
    public void RolesFor_CaseAndOperationOnly()
    {
        Assert.Same(CasePersonRoles.Suggestions, OperationInvolvedRoles.For(nameof(NOOSE_Website.Data.Entities.Cases.Case)));
        Assert.Same(OperationInvolvedRoles.Suggestions, OperationInvolvedRoles.For(nameof(Operation)));
        Assert.Null(OperationInvolvedRoles.For(nameof(Faction)));
    }

    [Fact]
    public void TypeSuggestions_MergeCatalogAndActivityKinds_WithoutTemplateKind()
    {
        var merged = OperationTypeSuggestions.Merge(new[] { "Razzia", "bankraub" }, new[] { "Bankraub", "Vorlage", " ", "Geiselnahme" });

        Assert.Equal(new[] { "bankraub", "Geiselnahme", "Razzia" }, merged);
    }

    // ---------- closed activities ----------

    [Fact]
    public async Task ActivityCreate_IsClosed()
    {
        using var ctx = new SqliteTestContext();
        var threat = Substitute.For<IThreatScoreService>();
        var svc = new AgentActivityService(ctx.Factory, threat);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(new AgentActivityInput
        {
            Title = "Einsatz",
            ActivityDate = DateTime.Now,
            OrgLinks = { new AgentActivityOrgRef { TargetType = nameof(Faction), TargetId = "f1" } },
        }, Leader()));

        Assert.Contains("Operation", ex.Message);
        await using var db = ctx.NewContext();
        Assert.Empty(db.AgentActivities);
        await threat.DidNotReceive().NewCalculateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
