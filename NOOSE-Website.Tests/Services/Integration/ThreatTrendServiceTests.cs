using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for threat-score history (append/dedupe/alarm) and <see cref="ThreatTrendService"/>.</summary>
public sealed class ThreatTrendServiceTests
{
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static IThreatScoreConfigService Config()
    {
        var c = Substitute.For<IThreatScoreConfigService>();
        c.GetAsync(Arg.Any<CancellationToken>()).Returns(ThreatScoreConfiguration.Default());
        return c;
    }

    private static async Task AddSnapshotAsync(SqliteTestContext ctx, string type, string id, int score, DateTime when)
    {
        await using var db = ctx.NewContext();
        db.ThreatScoreHistory.Add(new ThreatScoreHistory
        {
            EntityType = type, EntityId = id, Score = score, Confidence = 50, Timestamp = when,
        });
        await db.SaveChangesAsync();
    }

    // ==================== recompute → history ====================

    [Fact]
    public async Task NewCalculateAsync_AppendsHistory_AndDedupesUnchanged()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var svc = new ThreatScoreService(ctx.Factory, Config(), Substitute.For<INotificationService>());

        await svc.NewCalculateAsync("f1");
        await svc.NewCalculateAsync("f1"); // unchanged score+confidence => no second row

        using var check = ctx.NewContext();
        var rows = await check.ThreatScoreHistory.Where(h => h.EntityId == "f1").ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task NewCalculateAsync_NoAlarm_OnFirstSnapshot()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.Classification = Classification.SecuredStateThreatening));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = new ThreatScoreService(ctx.Factory, Config(), notifications);

        await svc.NewCalculateAsync("f1"); // high score, but there is no prior snapshot to compare

        await notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.ThreatSpike,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewCalculateAsync_Alarms_OnSignificantRise()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1")); // Unknown => score 0
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = new ThreatScoreService(ctx.Factory, Config(), notifications);

        await svc.NewCalculateAsync("f1"); // snapshot #1: score 0

        using (var db = ctx.NewContext())
        {
            var f = db.Factions.Single(x => x.Id == "f1");
            f.Classification = Classification.SuspicionCase; // pushes score to the 50 band
            db.SaveChanges();
        }

        await svc.NewCalculateAsync("f1"); // snapshot #2: score 50, +50 rise

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.ThreatScoreHistory.CountAsync(h => h.EntityId == "f1"));
        await notifications.Received(1).NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.ThreatSpike,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ==================== trend queries ====================

    [Fact]
    public async Task GetHistoryAsync_ReturnsPointsOldestFirst()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 10, now.AddDays(-20));
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 30, now.AddDays(-5));
        var svc = new ThreatTrendService(ctx.Factory);

        var points = await svc.GetHistoryAsync(nameof(Faction), "f1");

        Assert.Equal(2, points.Count);
        Assert.Equal(10, points[0].Score);
        Assert.Equal(30, points[1].Score);
    }

    [Fact]
    public async Task GetSparklinesAsync_BundlesLastScoresPerId()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 10, now.AddDays(-3));
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 20, now.AddDays(-1));
        await AddSnapshotAsync(ctx, nameof(Faction), "f2", 55, now.AddDays(-2));
        var svc = new ThreatTrendService(ctx.Factory);

        var map = await svc.GetSparklinesAsync(nameof(Faction), new[] { "f1", "f2" });

        Assert.Equal(new[] { 10, 20 }, map["f1"]);
        Assert.Equal(new[] { 55 }, map["f2"]);
    }

    [Fact]
    public async Task GetSparklinesAsync_KeepsTheNewestPointsInChronologicalOrder()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            await AddSnapshotAsync(ctx, nameof(Faction), "f1", i, now.AddDays(i - 10));
        }
        var svc = new ThreatTrendService(ctx.Factory);

        var map = await svc.GetSparklinesAsync(nameof(Faction), new[] { "f1" }, points: 4);

        // the caption under the curve reads the first value as "where it started" and the last as "now",
        // so trimming from the wrong end would describe ancient history as the current trend
        Assert.Equal(new[] { 6, 7, 8, 9 }, map["f1"]);
    }

    [Fact]
    public async Task GetFactionRaceAsync_RanksByScore_AndHidesClassifiedFromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            db.Factions.Add(Seed.Faction(id: "fx", name: "Geheim", configure: f => f.IsClassified = true));
            db.SaveChanges();
        }
        var now = DateTime.UtcNow;
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 40, now.AddDays(-2));
        await AddSnapshotAsync(ctx, nameof(Faction), "f2", 70, now.AddDays(-2));
        await AddSnapshotAsync(ctx, nameof(Faction), "fx", 90, now.AddDays(-2));
        var svc = new ThreatTrendService(ctx.Factory);

        var leaderFrames = await svc.GetFactionRaceAsync(Leader(), months: 1);
        var topLeader = leaderFrames[^1].Entries;
        Assert.Equal("fx", topLeader[0].EntityId);   // classified ranks top for leadership
        Assert.Equal("f2", topLeader[1].EntityId);

        var lowFrames = await svc.GetFactionRaceAsync(LowRank(), months: 1);
        Assert.DoesNotContain(lowFrames[^1].Entries, e => e.EntityId == "fx");
        Assert.Equal("f2", lowFrames[^1].Entries[0].EntityId);
    }

    [Fact]
    public async Task GetTopMoversAsync_ComputesRise_AndHidesClassifiedFromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "fx", name: "Geheim", configure: f => f.IsClassified = true));
            db.SaveChanges();
        }
        var now = DateTime.UtcNow;
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 20, now.AddDays(-40));
        await AddSnapshotAsync(ctx, nameof(Faction), "f1", 65, now.AddDays(-1));
        await AddSnapshotAsync(ctx, nameof(Faction), "fx", 10, now.AddDays(-40));
        await AddSnapshotAsync(ctx, nameof(Faction), "fx", 80, now.AddDays(-1));
        var svc = new ThreatTrendService(ctx.Factory);

        var leaderMovers = await svc.GetTopMoversAsync(Leader(), windowDays: 30);
        Assert.Contains(leaderMovers, m => m.EntityId == "fx");
        var f1Mover = Assert.Single(leaderMovers, m => m.EntityId == "f1");
        Assert.Equal(45, f1Mover.Delta);

        var lowMovers = await svc.GetTopMoversAsync(LowRank(), windowDays: 30);
        Assert.DoesNotContain(lowMovers, m => m.EntityId == "fx");
    }
}
