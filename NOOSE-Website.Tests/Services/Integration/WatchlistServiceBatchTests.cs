using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary><see cref="WatchlistService.FollowManyAsync"/>: many picked records followed at once.</summary>
public sealed class WatchlistServiceBatchTests
{
    private static ClaimsPrincipal Lead(string id = "lead") => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static void SeedBase(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("junior", Rank.Trainee));
        db.Users.Add(Seed.Agent("colleague", Rank.SpecialAgent));
        db.People.Add(Seed.Person("p1"));
        db.People.Add(Seed.Person("p2"));
        db.People.Add(Seed.Person("p3"));
        db.Cases.Add(Seed.Case("c1"));
        db.SaveChanges();
    }

    [Fact]
    public async Task Follows_new_reactivates_unfollowed_and_leaves_followed_alone()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        using (var db = ctx.NewContext())
        {
            db.Watchlists.Add(new WatchlistEntry { AgentId = "lead", EntityType = "Person", EntityId = "p1" });
            db.Watchlists.Add(new WatchlistEntry { AgentId = "lead", EntityType = "Person", EntityId = "p2", IsDeleted = true, DeletedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        var factory = new CountingDbContextFactory(ctx);
        var svc = new WatchlistService(factory);

        var outcome = await svc.FollowManyAsync([("Person", "p1"), ("Person", "p2"), ("Person", "p3"), ("Case", "c1")], Lead());

        Assert.Equal(new(3, 1, 0), outcome);
        Assert.Equal(1, factory.Saves);
        using var check = ctx.NewContext();
        var rows = check.Watchlists.IgnoreQueryFilters().Where(w => w.AgentId == "lead").ToList();
        // reactivated, not added a second time
        Assert.Equal(4, rows.Count);
        Assert.All(rows, w => Assert.False(w.IsDeleted));
        Assert.Null(rows.Single(w => w.EntityId == "p2").DeletedAt);
    }

    [Fact]
    public async Task Only_the_callers_own_rows_are_touched()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        using (var db = ctx.NewContext())
        {
            db.Watchlists.Add(new WatchlistEntry { AgentId = "colleague", EntityType = "Person", EntityId = "p1", IsDeleted = true });
            db.SaveChanges();
        }
        var svc = new WatchlistService(ctx.Factory);

        var outcome = await svc.FollowManyAsync([("Person", "p1")], Lead());

        Assert.Equal(new(1, 0, 0), outcome);
        using var check = ctx.NewContext();
        Assert.True(check.Watchlists.IgnoreQueryFilters().Single(w => w.AgentId == "colleague").IsDeleted);
        Assert.Single(check.Watchlists.Where(w => w.AgentId == "lead" && w.EntityId == "p1"));
    }

    [Fact]
    public async Task Types_without_a_follow_button_and_hidden_records_are_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job { Id = "j1", Title = "Offen", CaseNumber = "FIB-A-2026-0001" });
            db.Documents.Add(new Document { Id = "doc1", Title = "Bericht" });
            db.Cases.Add(Seed.Case("vs", "Verschlusssache", c => c.IsClassified = true));
            db.SaveChanges();
        }
        var svc = new WatchlistService(ctx.Factory);

        var outcome = await svc.FollowManyAsync(
            [("Job", "j1"), ("Document", "doc1"), ("Case", "vs"), ("Agent", "colleague"), ("Case", "c1")], Junior());

        // a personnel file is leadership reading
        Assert.Equal(new(1, 0, 4), outcome);
    }

    [Fact]
    public async Task A_personnel_file_needs_to_exist()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var svc = new WatchlistService(ctx.Factory);

        var outcome = await svc.FollowManyAsync([("Agent", "colleague"), ("Agent", "nobody")], Lead());

        Assert.Equal(new(1, 0, 1), outcome);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("partner")]
    [InlineData("demo")]
    public async Task A_read_only_actor_is_stopped_before_anything_is_written(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var factory = new CountingDbContextFactory(ctx);
        var svc = new WatchlistService(factory);
        ClaimsPrincipal actor = kind switch
        {
            "reader" => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsTeamLead().Build(),
            "partner" => ClaimsPrincipalBuilder.Agent("lead").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
            _ => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsDemo().Build(),
        };

        // the write guard, not a visibility refusal further down
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.FollowManyAsync([("Person", "p1")], actor));
        Assert.StartsWith("Nur-Lese-Modus", ex.Message);
        Assert.Equal(0, factory.Saves);
    }
}
