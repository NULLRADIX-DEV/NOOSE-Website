using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Gamification;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="GamificationService"/> (stats, badges, leaderboard, sweep).</summary>
public sealed class GamificationServiceTests
{
    private static GamificationService Svc(SqliteTestContext ctx) => new(ctx.Factory);

    private static AuditLog CaseStatusChange(string agentId, string changesJson, string entityType = "Case", string? entityId = null)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            EntityType = entityType,
            EntityId = entityId ?? Guid.NewGuid().ToString(),
            Action = AuditAction.Modified,
            AgentId = agentId,
            AgentName = agentId,
            ChangesJson = changesJson,
        };

    [Fact]
    public async Task GetStats_CountsContributions_ExcludesSoftDeletedAndOtherAgents()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", configure: p => p.CreatedById = "a1"));
            db.People.Add(Seed.Person(id: "p2", configure: p => p.CreatedById = "a1"));
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.CreatedById = "a1"));
            db.People.Add(Seed.Person(id: "pDel", configure: p => { p.CreatedById = "a1"; p.IsDeleted = true; }));
            db.People.Add(Seed.Person(id: "pOther", configure: p => p.CreatedById = "a2"));
            db.Observations.Add(new Observation { PersonId = "p1", ObservingAgentId = "a1", Start = DateTime.UtcNow, CreatedById = "a1" });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase, AgentId = "a1", Timestamp = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var stats = await Svc(ctx).GetStatsAsync("a1");

        Assert.Equal(3, stats.Records); // 2 people + 1 faction; soft-deleted and a2's record excluded
        Assert.Equal(1, stats.Observations);
        Assert.Equal(1, stats.Classifications);
    }

    [Fact]
    public async Task GetStats_CountsSolvedCases_DistinctExistingCompletedOnly()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Seed.Case(id: "cSolved"));
            db.Cases.Add(Seed.Case(id: "cReopened"));
            db.Cases.Add(Seed.Case(id: "cDeleted", configure: c => c.IsDeleted = true));
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[1,3]}", entityId: "cSolved"));         // -> Completed: counts
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[1,3]}", entityId: "cReopened"));       // completed
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[3,1]}", entityId: "cReopened"));       // reopened: ignored
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[1,3]}", entityId: "cReopened"));       // re-completed: same case, dedup
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[1,3]}", entityId: "cDeleted"));        // completed but soft-deleted: excluded
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[0,1]}", entityId: "cSolved"));         // -> InProcessing: no
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Titel\":[\"a\",\"b\"]}", entityId: "cSolved"));  // no Status field: no
            db.AuditLogs.Add(CaseStatusChange("a1", "{\"Status\":[1,3]}", entityType: "Person", entityId: "cSolved")); // wrong type: no
            db.SaveChanges();
        }

        var stats = await Svc(ctx).GetStatsAsync("a1");

        Assert.Equal(2, stats.SolvedCases); // cSolved + cReopened counted once each; re-completion deduped, deleted excluded
    }

    [Fact]
    public async Task Sweep_AwardsEarnedBadges_AndIsIdempotent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", configure: p => p.CreatedById = "a1"));
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        var first = await svc.SweepAsync();
        var second = await svc.SweepAsync();

        Assert.Equal(1, first);  // erste-akte
        Assert.Equal(0, second); // idempotent
        using var check = ctx.NewContext();
        Assert.Equal(1, await check.AgentBadges.CountAsync(b => b.AgentId == "a1"));
        Assert.Equal("erste-akte", (await check.AgentBadges.SingleAsync(b => b.AgentId == "a1")).BadgeKey);
    }

    [Fact]
    public async Task Sweep_AwardsMultipleTiers_WhenThresholdsMet()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 25; i++)
            {
                db.People.Add(Seed.Person(id: $"p{i}", configure: p => p.CreatedById = "a1"));
            }
            db.SaveChanges();
        }

        await Svc(ctx).SweepAsync();

        using var check = ctx.NewContext();
        var keys = await check.AgentBadges.Where(b => b.AgentId == "a1").Select(b => b.BadgeKey).ToListAsync();
        Assert.Contains("erste-akte", keys);
        Assert.Contains("aktenfuchs", keys);
    }

    [Fact]
    public async Task GetBadges_ResolvesCatalog_SkipsUnknownKeys()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentBadges.Add(new AgentBadge { AgentId = "a1", BadgeKey = "erste-akte", AwardedAt = DateTime.UtcNow });
            db.AgentBadges.Add(new AgentBadge { AgentId = "a1", BadgeKey = "bogus-removed-badge", AwardedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var badges = await Svc(ctx).GetBadgesAsync("a1");

        Assert.Single(badges);
        Assert.Equal("Erste Akte", badges[0].Label);
    }

    [Fact]
    public async Task GetLeaderboard_RanksByPoints_ExcludesInactivePartnerAndZeroActivity()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("a2", rank: Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("a3", rank: Rank.SpecialAgent, status: AgentStatus.Pending)); // inactive -> excluded
            db.Users.Add(Seed.Agent("a4", rank: Rank.SpecialAgent));                              // active but no activity -> excluded
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.Cases.Add(Seed.Case(id: "c2", configure: c => c.CreatedById = "a1"));
            db.People.Add(Seed.Person(id: "p1", configure: p => p.CreatedById = "a2"));
            db.People.Add(Seed.Person(id: "p2", configure: p => p.CreatedById = "a3"));
            db.SaveChanges();
        }

        var rows = (await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime)).Ranked;

        Assert.Equal(2, rows.Count);
        Assert.Equal("a1", rows[0].AgentId);
        Assert.Equal(1, rows[0].Position);
        Assert.True(rows[0].Points > rows[1].Points);
        Assert.Equal("a2", rows[1].AgentId);
        Assert.DoesNotContain(rows, r => r.AgentId is "a3" or "a4");
    }

    [Fact]
    public async Task GetLeaderboard_ExcludesTeamLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("tl-adm", configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            // scoring rows for everyone, so a zero-points filter cannot mask the exclusion
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.Cases.Add(Seed.Case(id: "c2", configure: c => c.CreatedById = "tl"));
            db.Cases.Add(Seed.Case(id: "c3", configure: c => c.CreatedById = "tl-adm"));
            db.Cases.Add(Seed.Case(id: "c4", configure: c => c.CreatedById = "partner"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal("a1", Assert.Single(board.Ranked).AgentId);
        // the leadership slice must not become a back door: those three are leadership-ranked by default
        Assert.Empty(board.OutOfCompetition);
    }

    [Fact]
    public async Task GetLeaderboard_Period_ExcludesRecordsOutsideWindow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.People.Add(Seed.Person(id: "old", configure: p => { p.CreatedById = "a1"; p.CreatedAt = DateTime.UtcNow.AddDays(-60); }));
            db.People.Add(Seed.Person(id: "new", configure: p => { p.CreatedById = "a1"; p.CreatedAt = DateTime.UtcNow.AddDays(-3); }));
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        var week = await svc.GetLeaderboardAsync(GamificationPeriod.Week);
        var all = await svc.GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal(1, week.Ranked[0].Records);
        Assert.Equal(2, all.Ranked[0].Records);
    }

    [Fact]
    public async Task GetLeaderboard_SurfacesClassificationAndObservationColumns()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.Observations.Add(new Observation { PersonId = "p1", ObservingAgentId = "a1", Start = DateTime.UtcNow, CreatedById = "a1" });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase, AgentId = "a1", Timestamp = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var rows = (await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime)).Ranked;

        var a1 = Assert.Single(rows);
        Assert.Equal(1, a1.Classifications);
        Assert.Equal(1, a1.Observations);
        Assert.Equal(3, a1.Points); // Classifications*2 + Observations*1, so the score reconciles with the shown columns
    }

    [Fact]
    public async Task GetLeaderboard_Leadership_IsListedButHoldsNoPlace()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", rank: Rank.Director));
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            // the top scorer is leadership, so the medal has to go to the weaker agent
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "boss"));
            db.Cases.Add(Seed.Case(id: "c2", configure: c => c.CreatedById = "boss"));
            db.Cases.Add(Seed.Case(id: "c3", configure: c => c.CreatedById = "a1"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal("a1", Assert.Single(board.Ranked).AgentId);
        Assert.Equal(1, board.Ranked[0].Position);
        var boss = Assert.Single(board.OutOfCompetition);
        Assert.Equal("boss", boss.AgentId);
        Assert.Equal(0, boss.Position);
        Assert.True(boss.Points > board.Ranked[0].Points);
    }

    [Theory]
    [InlineData(Rank.Trainee, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SpecialAgentInCharge, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public async Task GetLeaderboard_FloorStartsAtSpecialAgentInCharge(Rank rank, bool outOfCompetition)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: rank));
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal(outOfCompetition ? 0 : 1, board.Ranked.Count);
        Assert.Equal(outOfCompetition ? 1 : 0, board.OutOfCompetition.Count);
    }

    [Fact]
    public async Task GetLeaderboard_AdminBelowTheFloor_StillCompetes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // the floor is rank-only on purpose; the admin flag is out-of-character and benches nobody
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent, configure: a => a.IsAdmin = true));
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal("a1", Assert.Single(board.Ranked).AgentId);
        Assert.Empty(board.OutOfCompetition);
    }

    [Fact]
    public async Task GetLeaderboard_RanklessAgent_CompetesAndAppearsOnce()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // a null rank compares false against the floor either way, so it must not fall out of both slices
            db.Users.Add(Seed.Agent("a1", configure: a => a.Rank = null));
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal("a1", Assert.Single(board.Ranked).AgentId);
        Assert.Empty(board.OutOfCompetition);
    }

    [Fact]
    public async Task GetLeaderboard_TopN_CapsEachSliceOnItsOwn()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 3; i++)
            {
                db.Users.Add(Seed.Agent($"a{i}", rank: Rank.SpecialAgent));
                db.Users.Add(Seed.Agent($"l{i}", rank: Rank.Director));
                db.Cases.Add(Seed.Case(id: $"ca{i}", configure: c => c.CreatedById = $"a{i}"));
                db.Cases.Add(Seed.Case(id: $"cl{i}", configure: c => c.CreatedById = $"l{i}"));
            }
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime, topN: 2);

        Assert.Equal(2, board.Ranked.Count);
        Assert.Equal(2, board.OutOfCompetition.Count);
    }

    [Fact]
    public async Task GetLeaderboard_ZeroPoints_IsInNeitherSlice()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // the points filter runs before the split, or the post grows a leadership block of zero-point names
            db.Users.Add(Seed.Agent("boss", rank: Rank.Director));
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal("a1", Assert.Single(board.Ranked).AgentId);
        Assert.Empty(board.OutOfCompetition);
    }

    [Fact]
    public async Task GetLeaderboard_OutOfCompetition_IsOrderedByPoints()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("low", rank: Rank.Director));
            db.Users.Add(Seed.Agent("high", rank: Rank.DeputyDirector));
            db.Cases.Add(Seed.Case(id: "c1", configure: c => c.CreatedById = "a1"));
            db.Cases.Add(Seed.Case(id: "c2", configure: c => c.CreatedById = "low"));
            db.Cases.Add(Seed.Case(id: "c3", configure: c => c.CreatedById = "high"));
            db.Cases.Add(Seed.Case(id: "c4", configure: c => c.CreatedById = "high"));
            db.SaveChanges();
        }

        var board = await Svc(ctx).GetLeaderboardAsync(GamificationPeriod.AllTime);

        Assert.Equal(new[] { "high", "low" }, board.OutOfCompetition.Select(r => r.AgentId));
    }

    [Fact]
    public async Task GetLeaderboard_WindowDays_HonoursTheDayCount()
    {
        // the day-count overload is the one the announcement calls; a sign flip here would be invisible otherwise
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", rank: Rank.SpecialAgent));
            db.People.Add(Seed.Person(id: "old", configure: p => { p.CreatedById = "a1"; p.CreatedAt = DateTime.UtcNow.AddDays(-60); }));
            db.People.Add(Seed.Person(id: "new", configure: p => { p.CreatedById = "a1"; p.CreatedAt = DateTime.UtcNow.AddDays(-3); }));
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.Equal(1, (await svc.GetLeaderboardAsync(7)).Ranked[0].Records);
        Assert.Equal(2, (await svc.GetLeaderboardAsync(90)).Ranked[0].Records);
        Assert.Equal(2, (await svc.GetLeaderboardAsync(0)).Ranked[0].Records); // 0 or less means all time
    }
}
