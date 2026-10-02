using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Threat;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="ThreatScoreService"/> against in-memory SQLite.</summary>
public sealed class ThreatScoreServiceTests
{
    // --- actors ---
    // Director: leadership (rank >= SupervisorySpecialAgent) => passes RequireLeadership.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership => fails RequireLeadership.
    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    // Demo visitor: rank Director like DemoIdentity, but read-only => fails RequireThreatScoreAdministration.
    private static ClaimsPrincipal DemoVisitor()
        => ClaimsPrincipalBuilder.Agent("demo-agent").WithRank(Rank.Director).AsDemo().Build();

    // --- collaborator factory ---
    private static IThreatScoreConfigService Config()
    {
        var c = Substitute.For<IThreatScoreConfigService>();
        c.GetAsync(Arg.Any<CancellationToken>()).Returns(ThreatScoreConfiguration.Default());
        return c;
    }

    private static ThreatScoreService NewService(SqliteTestContext ctx, IThreatScoreConfigService? config = null,
        INotificationService? notifications = null)
        => new(ctx.Factory, config ?? Config(), notifications ?? Substitute.For<INotificationService>());

    // ==================== Calculate (pure static) ====================

    [Fact]
    public void Calculate_ExcludesStateFaction_WithNullScore()
    {
        var input = new ThreatScoreInput { IsStateFaction = true, Classification = Classification.SuspicionCase };

        var result = ThreatScoreService.Calculate(input, DateTime.UtcNow, ThreatScoreConfiguration.Default());

        Assert.Null(result.Score);
        Assert.Null(result.Confidence);
        Assert.Equal("Staatsfraktion", result.Detail.Excluded);
    }

    [Fact]
    public void Calculate_AppliesClassificationBaseBand_ForEmptyInput()
    {
        // no content data => content 0 => score lands exactly on the classification base band (50).
        var input = new ThreatScoreInput { Classification = Classification.SuspicionCase };

        var result = ThreatScoreService.Calculate(input, DateTime.UtcNow, ThreatScoreConfiguration.Default());

        Assert.Equal(50, result.Score);
    }

    [Fact]
    public void Calculate_ActivityHeat_RaisesScore_AboveUnknownBaseline()
    {
        var now = DateTime.UtcNow;
        var cfg = ThreatScoreConfiguration.Default();
        var quiet = new ThreatScoreInput { Classification = Classification.Unknown };
        var active = new ThreatScoreInput
        {
            Classification = Classification.Unknown,
            Activities = new[] { new ThreatActivity("Mord", now), new ThreatActivity("Raub", now) },
        };

        var quietScore = ThreatScoreService.Calculate(quiet, now, cfg).Score;
        var activeScore = ThreatScoreService.Calculate(active, now, cfg).Score;

        Assert.Equal(0, quietScore);
        Assert.True(activeScore > 0);
    }

    [Fact]
    public void CalculatePerson_FugitiveRaisesScore_AboveAliveBaseline()
    {
        var now = DateTime.UtcNow;
        var cfg = ThreatScoreConfiguration.Default();
        var alive = new PersonThreatScoreInput { Classification = Classification.Unknown, LifeStatus = LifeStatus.Alive };
        var fugitive = new PersonThreatScoreInput { Classification = Classification.Unknown, LifeStatus = LifeStatus.Fugitive };

        var aliveScore = ThreatScoreService.CalculatePerson(alive, now, cfg).Score;
        var fugitiveScore = ThreatScoreService.CalculatePerson(fugitive, now, cfg).Score;

        Assert.Equal(0, aliveScore);
        Assert.True(fugitiveScore > aliveScore);
    }

    // ==================== NewCalculateAsync ====================

    [Fact]
    public async Task NewCalculateAsync_PersistsScore_ForNormalFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.Classification = Classification.SuspicionCase));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.NewCalculateAsync("f1");

        using var check = ctx.NewContext();
        var f = await check.Factions.SingleAsync(x => x.Id == "f1");
        Assert.NotNull(f.ThreatScore);
        Assert.NotNull(f.ThreatConfidence);
        Assert.NotNull(f.ScoreCalculatedAt);
        Assert.False(string.IsNullOrEmpty(f.ThreatDetailJson));
    }

    [Fact]
    public async Task NewCalculateAsync_SetsNullScore_ForStateFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f =>
            {
                f.IsStateFaction = true;
                f.ThreatScore = 99;       // stale value that must be cleared
                f.ThreatConfidence = 42;
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.NewCalculateAsync("f1");

        using var check = ctx.NewContext();
        var f = await check.Factions.SingleAsync(x => x.Id == "f1");
        Assert.Null(f.ThreatScore);
        Assert.Null(f.ThreatConfidence);
        Assert.NotNull(f.ScoreCalculatedAt);
        Assert.Contains("Staatsfraktion", f.ThreatDetailJson);
    }

    [Fact]
    public async Task NewCalculateAsync_NoOp_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // missing faction is silently skipped (no throw).
        await svc.NewCalculateAsync("nope");
    }

    // ==================== NewCalculateForPersonAsync ====================

    [Fact]
    public async Task NewCalculateForPersonAsync_RecomputesEveryMemberFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.Classification = Classification.SuspicionCase));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos", configure: f => f.Classification = Classification.ReviewCase));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.NewCalculateForPersonAsync("p1");

        using var check = ctx.NewContext();
        Assert.NotNull((await check.Factions.SingleAsync(x => x.Id == "f1")).ScoreCalculatedAt);
        Assert.NotNull((await check.Factions.SingleAsync(x => x.Id == "f2")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task NewCalculateForPersonAsync_NoOp_WhenNoMemberships()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // person is a member of nothing => nothing to recompute, no throw.
        await svc.NewCalculateForPersonAsync("p1");
    }

    // ==================== NewCalculateAllAsync ====================

    [Fact]
    public async Task NewCalculateAllAsync_ComputesAll_AndReturnsCount()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos", configure: f => f.Classification = Classification.SuspicionCase));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var count = await svc.NewCalculateAllAsync();

        Assert.Equal(2, count);
        using var check = ctx.NewContext();
        Assert.NotNull((await check.Factions.SingleAsync(x => x.Id == "f1")).ScoreCalculatedAt);
        Assert.NotNull((await check.Factions.SingleAsync(x => x.Id == "f2")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task NewCalculateAllAsync_ReturnsZero_WhenEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Equal(0, await svc.NewCalculateAllAsync());
    }

    [Fact]
    public async Task NewCalculateAllAsync_SkipsSoftDeletedFactions()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "live", name: "Live"));
            db.Factions.Add(Seed.Faction(id: "dead", name: "Dead", configure: f =>
            {
                f.IsDeleted = true;
                f.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // global soft-delete filter excludes the deleted faction.
        Assert.Equal(1, await svc.NewCalculateAllAsync());
    }

    // ==================== NewCalculatePersonScoreAsync ====================

    [Fact]
    public async Task NewCalculatePersonScoreAsync_PersistsScore()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", configure: p => p.Classification = Classification.SuspicionCase));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.NewCalculatePersonScoreAsync("p1");

        using var check = ctx.NewContext();
        var p = await check.People.SingleAsync(x => x.Id == "p1");
        Assert.NotNull(p.ThreatScore);
        Assert.NotNull(p.ThreatConfidence);
        Assert.NotNull(p.ScoreCalculatedAt);
        Assert.False(string.IsNullOrEmpty(p.ThreatDetailJson));
    }

    [Fact]
    public async Task NewCalculatePersonScoreAsync_ReflectsMeasureDocs()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));   // Unknown classification => base band 0
            db.PersonDocs.Add(new PersonDoc { PersonId = "p1", Outcome = MeasureOutcome.Shot, Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.NewCalculatePersonScoreAsync("p1");

        using var check = ctx.NewContext();
        var p = await check.People.SingleAsync(x => x.Id == "p1");
        // a recent "Shot" measure produces measure heat => a positive score despite the base band of 0.
        Assert.NotNull(p.ThreatScore);
        Assert.True(p.ThreatScore > 0);
    }

    [Fact]
    public async Task NewCalculatePersonScoreAsync_NoOp_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.NewCalculatePersonScoreAsync("nope");
    }

    // ==================== NewCalculateAllPeopleScoresAsync ====================

    [Fact]
    public async Task NewCalculateAllPeopleScoresAsync_ComputesAll_AndReturnsCount()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.People.Add(Seed.Person(id: "p2", name: "Moritz", configure: p => p.Classification = Classification.SuspicionCase));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var count = await svc.NewCalculateAllPeopleScoresAsync();

        Assert.Equal(2, count);
        using var check = ctx.NewContext();
        Assert.NotNull((await check.People.SingleAsync(x => x.Id == "p1")).ScoreCalculatedAt);
        Assert.NotNull((await check.People.SingleAsync(x => x.Id == "p2")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task NewCalculateAllPeopleScoresAsync_ReturnsZero_WhenEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Equal(0, await svc.NewCalculateAllPeopleScoresAsync());
    }

    // ==================== PreviewFactionDistributionAsync ====================

    [Fact]
    public async Task PreviewFactionDistributionAsync_ReturnsDistribution_WithoutPersisting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.Classification = Classification.SuspicionCase));
            db.Factions.Add(Seed.Faction(id: "f2", name: "State", configure: f => f.IsStateFaction = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var dist = await svc.PreviewFactionDistributionAsync(ThreatScoreConfiguration.Default(), Leader());

        Assert.Equal(2, dist.Total);
        Assert.Equal(1, dist.Scored);
        Assert.Equal(1, dist.Excluded);   // state faction excluded
        Assert.Equal(1, dist.High);       // SuspicionCase => score 50 => High band

        // dry run: nothing persisted.
        using var check = ctx.NewContext();
        Assert.Null((await check.Factions.SingleAsync(x => x.Id == "f1")).ScoreCalculatedAt);
        Assert.Null((await check.Factions.SingleAsync(x => x.Id == "f1")).ThreatScore);
    }

    [Fact]
    public async Task PreviewFactionDistributionAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PreviewFactionDistributionAsync(ThreatScoreConfiguration.Default(), LowRank()));
    }

    // ==================== PreviewPersonDistributionAsync ====================

    [Fact]
    public async Task PreviewPersonDistributionAsync_ReturnsDistribution_WithoutPersisting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max", configure: p => p.Classification = Classification.SuspicionCase));
            db.People.Add(Seed.Person(id: "p2", name: "Moritz")); // Unknown => score 0 => No band
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var dist = await svc.PreviewPersonDistributionAsync(ThreatScoreConfiguration.Default(), Leader());

        Assert.Equal(2, dist.Total);
        Assert.Equal(2, dist.Scored);   // persons are never excluded
        Assert.Equal(0, dist.Excluded);
        Assert.Equal(1, dist.High);     // SuspicionCase => 50 => High
        Assert.Equal(1, dist.No);       // Unknown, no data => 0 => No

        // dry run: nothing persisted.
        using var check = ctx.NewContext();
        Assert.Null((await check.People.SingleAsync(x => x.Id == "p1")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task PreviewPersonDistributionAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PreviewPersonDistributionAsync(ThreatScoreConfiguration.Default(), LowRank()));
    }

    [Fact]
    public async Task PreviewDistributions_Throw_ForDemoVisitor()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PreviewFactionDistributionAsync(ThreatScoreConfiguration.Default(), DemoVisitor()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PreviewPersonDistributionAsync(ThreatScoreConfiguration.Default(), DemoVisitor()));
    }

    // ==================== RecalculateAllAsync (button in the settings) ====================

    private static void SeedOneFactionAndPerson(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
        db.People.Add(Seed.Person(id: "p1", name: "Max"));
        db.SaveChanges();
    }

    private static ThreatScoreService NewService(SqliteTestContext ctx, ThreatScoreRecalculationGate gate)
        => new(ctx.Factory, Config(), Substitute.For<INotificationService>(), gate);

    [Fact]
    public async Task RecalculateAllAsync_ComputesFactionsAndPeople_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedOneFactionAndPerson(ctx);
        var svc = NewService(ctx);

        var result = await svc.RecalculateAllAsync(Leader());

        Assert.Equal(new ThreatScoreRecalculation(Factions: 1, People: 1), result);
        using var check = ctx.NewContext();
        Assert.NotNull((await check.Factions.SingleAsync(x => x.Id == "f1")).ScoreCalculatedAt);
        Assert.NotNull((await check.People.SingleAsync(x => x.Id == "p1")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task RecalculateAllAsync_Throws_ForDemoVisitor_AndWritesNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedOneFactionAndPerson(ctx);
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RecalculateAllAsync(DemoVisitor()));

        using var check = ctx.NewContext();
        Assert.Null((await check.Factions.SingleAsync(x => x.Id == "f1")).ScoreCalculatedAt);
        Assert.Null((await check.People.SingleAsync(x => x.Id == "p1")).ScoreCalculatedAt);
    }

    [Fact]
    public async Task RecalculateAllAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RecalculateAllAsync(LowRank()));
    }

    [Fact]
    public async Task RecalculateAllAsync_Throws_WithinTheCooldown_AndReleasesTheGate()
    {
        using var ctx = new SqliteTestContext();
        var gate = new ThreatScoreRecalculationGate();
        var svc = NewService(ctx, gate);
        await svc.RecalculateAllAsync(Leader());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecalculateAllAsync(Leader()));

        Assert.Contains("warten", refused.Message);
        Assert.True(gate.TryEnter()); // the refused call did not keep the gate
    }

    [Fact]
    public async Task RecalculateAllAsync_Throws_WhileAnotherRunHoldsTheGate()
    {
        using var ctx = new SqliteTestContext();
        var gate = new ThreatScoreRecalculationGate();
        Assert.True(gate.TryEnter()); // another run is going on
        var svc = NewService(ctx, gate);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecalculateAllAsync(Leader()));

        Assert.Contains("läuft bereits", refused.Message);
        gate.Exit(); // the other run's release still works: the refused call did not release it
    }

    [Fact]
    public void Gate_CountsTheCooldownDown_FromTheLastFinishedRun()
    {
        var gate = new ThreatScoreRecalculationGate();
        var finished = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        gate.MarkFinished(finished);

        Assert.Equal(TimeSpan.FromSeconds(30), gate.RemainingCooldown(finished.AddSeconds(30)));
        Assert.Equal(TimeSpan.Zero, gate.RemainingCooldown(finished + ThreatScoreRecalculationGate.Cooldown));
        Assert.Equal(TimeSpan.Zero, new ThreatScoreRecalculationGate().RemainingCooldown(finished)); // never ran
    }
}
