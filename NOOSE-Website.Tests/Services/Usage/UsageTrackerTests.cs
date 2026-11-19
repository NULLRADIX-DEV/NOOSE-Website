using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

public sealed class UsageTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static (UsageTracker Tracker, UsageCollector Collector, NavPreferencesService Prefs) Build(SqliteTestContext ctx)
    {
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.SpecialAgent));
            db.SaveChanges();
        }
        var collector = new UsageCollector(new TestClock(Start));
        var prefs = new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));
        return (new UsageTracker(collector, prefs, NullLogger<UsageTracker>.Instance), collector, prefs);
    }

    private static ClaimsPrincipalBuilder Agent() => ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.SpecialAgent);

    [Fact]
    public async Task A_counted_agent_is_counted()
    {
        using var ctx = new SqliteTestContext();
        var (tracker, collector, _) = Build(ctx);

        await tracker.TrackAsync(Agent(), UsageCatalog.SearchRun);

        Assert.Equal(1, Assert.Single(collector.Snapshot()).Events);
    }

    [Fact]
    public async Task An_unknown_key_is_dropped()
    {
        using var ctx = new SqliteTestContext();
        var (tracker, collector, _) = Build(ctx);

        await tracker.TrackAsync(Agent(), "search:Michael Townley");
        await tracker.TrackAsync(Agent(), "page:personen/3f2a");

        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public async Task Whoever_is_not_counted_is_not_counted()
    {
        using var ctx = new SqliteTestContext();
        var (tracker, collector, _) = Build(ctx);

        await tracker.TrackAsync(Agent().AsDemo(), UsageCatalog.SearchRun);
        await tracker.TrackAsync(Agent().AsTeamLead(), UsageCatalog.SearchRun);
        await tracker.TrackAsync(Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Member), UsageCatalog.SearchRun);
        await tracker.TrackAsync(ClaimsPrincipalBuilder.Anonymous(), UsageCatalog.SearchRun);
        await tracker.TrackAsync(null, UsageCatalog.SearchRun);

        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public async Task The_opt_out_stops_counting_and_the_opt_in_restores_it()
    {
        using var ctx = new SqliteTestContext();
        var (tracker, collector, prefs) = Build(ctx);

        await prefs.SetUsageStatsOptOutAsync("a1", true);
        await tracker.TrackAsync(Agent(), UsageCatalog.SearchRun);
        Assert.Empty(collector.Snapshot());

        await prefs.SetUsageStatsOptOutAsync("a1", false);
        await tracker.TrackAsync(Agent(), UsageCatalog.SearchRun);
        Assert.Single(collector.Snapshot());
    }

    [Fact]
    public async Task The_choice_is_kept_in_the_stored_preferences()
    {
        using var ctx = new SqliteTestContext();
        var (_, _, prefs) = Build(ctx);
        Assert.False((await prefs.GetAsync("a1")).UsageStatsOptOut);

        await prefs.SetUsageStatsOptOutAsync("a1", true);

        var fresh = new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));
        Assert.True((await fresh.GetAsync("a1")).UsageStatsOptOut);
    }
}
