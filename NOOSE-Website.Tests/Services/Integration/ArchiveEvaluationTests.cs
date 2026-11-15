using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Archived records stop skewing the numbers — that is the whole point of the feature.</summary>
public sealed class ArchiveEvaluationTests
{
    // mirrors DashboardServiceTests.Build: the service indexes settings[nameof(T)] for all seven types
    private static DashboardService BuildDashboard(SqliteTestContext ctx)
    {
        var requests = Substitute.For<IRequestService>();
        requests.GetOpenCountAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(0);

        var settings = new RecencySettings(10, 30, false);
        var recency = Substitute.For<IRecencyService>();
        recency.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, RecencySettings>
        {
            [nameof(Person)] = settings,
            [nameof(Faction)] = settings,
            [nameof(PersonGroup)] = settings,
            [nameof(Party)] = settings,
            [nameof(DrugRoute)] = settings,
            [nameof(Operation)] = settings,
            [nameof(Taskforce)] = settings,
            [nameof(Case)] = settings,
        });
        return new DashboardService(ctx.Factory, requests, recency);
    }

    private static IThreatScoreConfigService ThreatConfig()
    {
        var config = Substitute.For<IThreatScoreConfigService>();
        config.GetAsync(Arg.Any<CancellationToken>()).Returns(ThreatScoreConfiguration.Default());
        return config;
    }

    // old enough that the recency threshold above would call it stale
    private static readonly DateTime LongAgo = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SqliteTestContext Stock()
    {
        var ctx = new SqliteTestContext();
        using var db = ctx.NewContext();
        db.People.Add(Seed.Person("aktiv", "Aktiv", p => p.CreatedAt = LongAgo));
        db.People.Add(Seed.Person("archiv", "Archiv", p => { p.CreatedAt = LongAgo; p.IsArchived = true; }));
        db.Factions.Add(Seed.Faction("f-aktiv", "Aktiv"));
        db.Factions.Add(Seed.Faction("f-archiv", "Archiv", f => f.IsArchived = true));
        db.SaveChanges();
        return ctx;
    }

    [Fact]
    public async Task The_dashboard_counts_only_the_active_stock()
    {
        using var ctx = Stock();
        var metrics = await BuildDashboard(ctx).GetMetricsAsync(isLeadership: true, meId: "lead");
        Assert.Equal(1, metrics.People);
        Assert.Equal(1, metrics.FactionsAndGroups);
    }

    [Fact]
    public async Task An_archived_record_is_never_due_for_update()
    {
        using var ctx = Stock();
        var stale = await BuildDashboard(ctx).GetUpdateNeedAsync(isLeadership: true, meId: "lead");
        Assert.Contains(stale, s => s.Name == "Aktiv");
        Assert.DoesNotContain(stale, s => s.Name == "Archiv");
    }

    [Fact]
    public async Task The_score_sweep_skips_archived_records()
    {
        using var ctx = Stock();
        var service = new ThreatScoreService(ctx.Factory, ThreatConfig(), Substitute.For<INotificationService>());

        await service.NewCalculateAllPeopleScoresAsync();
        await service.NewCalculateAllAsync();

        await using var db = ctx.NewContext();
        Assert.NotNull((await db.People.SingleAsync(p => p.Id == "aktiv")).ScoreCalculatedAt);
        Assert.Null((await db.People.SingleAsync(p => p.Id == "archiv")).ScoreCalculatedAt);
        Assert.NotNull((await db.Factions.SingleAsync(f => f.Id == "f-aktiv")).ScoreCalculatedAt);
        Assert.Null((await db.Factions.SingleAsync(f => f.Id == "f-archiv")).ScoreCalculatedAt);
    }
}
