using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="WatchlistService" /> against in-memory SQLite.</summary>
public sealed class WatchlistServiceTests
{
    private const string PersonType = nameof(Person);

    private static WatchlistService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    private static WatchlistEntry Entry(string agentId, string entityType, string entityId,
        DateTime createdAt, bool deleted = false) => new()
    {
        AgentId = agentId,
        EntityType = entityType,
        EntityId = entityId,
        CreatedAt = createdAt,
        IsDeleted = deleted,
        DeletedAt = deleted ? createdAt : null,
    };

    // ---- FollowAsync -----------------------------------------------------

    [Fact]
    public async Task FollowAsync_AddsEntry_WhenRecordVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.People.Add(Seed.Person("person-1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        await svc.FollowAsync(PersonType, "person-1", actor);

        using var read = ctx.NewContext();
        var row = read.Watchlists.Single();
        Assert.Equal("agent-1", row.AgentId);
        Assert.Equal(PersonType, row.EntityType);
        Assert.Equal("person-1", row.EntityId);
        Assert.False(row.IsDeleted);
    }

    [Fact]
    public async Task FollowAsync_NoOp_WhenAlreadyFollowing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.People.Add(Seed.Person("person-1"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        await svc.FollowAsync(PersonType, "person-1", actor);

        using var read = ctx.NewContext();
        Assert.Equal(1, read.Watchlists.IgnoreQueryFilters().Count());
    }

    [Fact]
    public async Task FollowAsync_ReactivatesSoftDeletedEntry_InsteadOfAddingSecond()
    {
        using var ctx = new SqliteTestContext();
        var soft = Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1), deleted: true);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.People.Add(Seed.Person("person-1"));
            db.Watchlists.Add(soft);
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        await svc.FollowAsync(PersonType, "person-1", actor);

        using var read = ctx.NewContext();
        // no duplicate row; the existing one is un-deleted in place
        Assert.Equal(1, read.Watchlists.IgnoreQueryFilters().Count());
        var row = read.Watchlists.IgnoreQueryFilters().Single(w => w.Id == soft.Id);
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DeletedById);
    }

    [Fact]
    public async Task FollowAsync_NoOp_WhenAnonymous()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("person-1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // no NameIdentifier claim -> GetAgentId() is null -> silent no-op (returns before the visibility gate)
        await svc.FollowAsync(PersonType, "person-1", ClaimsPrincipalBuilder.Anonymous());

        using var read = ctx.NewContext();
        Assert.Equal(0, read.Watchlists.IgnoreQueryFilters().Count());
    }

    [Fact]
    public async Task FollowAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1", rank: Rank.Trainee));
            db.People.Add(Seed.Person("person-x", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        // classified record + non-leadership caller (Trainee, no admin) -> visibility gate rejects
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.Trainee).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.FollowAsync(PersonType, "person-x", actor));

        using var read = ctx.NewContext();
        Assert.Equal(0, read.Watchlists.IgnoreQueryFilters().Count());
    }

    // ---- UnfollowAsync ---------------------------------------------------

    [Fact]
    public async Task UnfollowAsync_RemovesActiveEntry()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        await svc.UnfollowAsync(PersonType, "person-1", actor);

        // The production soft-delete rewrite (Deleted -> Modified/IsDeleted=true) lives in
        // AuditSaveChangesInterceptor, which is DI-wired and NOT attached to the plain test
        // AppDbContext, so Remove() hard-deletes here. Either way the row leaves the active set.
        using var read = ctx.NewContext();
        Assert.False(read.Watchlists.Any(w => w.AgentId == "agent-1" && w.EntityId == "person-1"));
    }

    [Fact]
    public async Task UnfollowAsync_NoOp_WhenNotFollowing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        // no matching entry -> silent no-op, must not throw
        await svc.UnfollowAsync(PersonType, "person-1", actor);

        using var read = ctx.NewContext();
        Assert.Equal(0, read.Watchlists.IgnoreQueryFilters().Count());
    }

    // ---- IsFollowedAsync -------------------------------------------------

    [Fact]
    public async Task IsFollowedAsync_True_WhenActiveEntryExists()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        Assert.True(await svc.IsFollowedAsync(PersonType, "person-1", actor));
    }

    [Fact]
    public async Task IsFollowedAsync_False_WhenSoftDeletedOrMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            // soft-deleted follow must not count as active
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1), deleted: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        Assert.False(await svc.IsFollowedAsync(PersonType, "person-1", actor));
        Assert.False(await svc.IsFollowedAsync(PersonType, "unknown", actor));
    }

    // ---- GetFollowedAsync ------------------------------------------------

    [Fact]
    public async Task GetFollowedAsync_ReturnsActiveEntries_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "old", new DateTime(2026, 1, 1)));
            db.Watchlists.Add(Entry("agent-1", PersonType, "new", new DateTime(2026, 3, 1)));
            // soft-deleted + another agent's follow are excluded
            db.Watchlists.Add(Entry("agent-1", PersonType, "gone", new DateTime(2026, 2, 1), deleted: true));
            db.Users.Add(Seed.Agent("agent-2"));
            db.Watchlists.Add(Entry("agent-2", PersonType, "other", new DateTime(2026, 4, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").Build();

        var result = await svc.GetFollowedAsync(actor);

        Assert.Equal(2, result.Count);
        Assert.Equal("new", result[0].EntityId);
        Assert.Equal("old", result[1].EntityId);
    }

    // ---- GetFollowedResolvedAsync ---------------------------------------

    [Fact]
    public async Task GetFollowedResolvedAsync_ResolvesVisibleRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1", rank: Rank.SpecialAgentInCharge));
            db.People.Add(Seed.Person("person-1", name: "Max Mustermann"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.SpecialAgentInCharge).Build();

        var result = await svc.GetFollowedResolvedAsync(actor);

        var record = Assert.Single(result);
        Assert.True(record.Accessible);
        Assert.Equal(PersonType, record.Type);
        Assert.Equal("person-1", record.Id);
        Assert.Contains("Max Mustermann", record.Display);
        Assert.NotNull(record.Href);
    }

    [Fact]
    public async Task GetFollowedResolvedAsync_MarksClassifiedRecordInaccessible_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1", rank: Rank.Trainee));
            db.People.Add(Seed.Person("person-c", configure: p => p.IsClassified = true));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-c", new DateTime(2026, 1, 1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.Trainee).Build();

        var result = await svc.GetFollowedResolvedAsync(actor);

        var record = Assert.Single(result);
        Assert.False(record.Accessible);
        Assert.Equal("(nicht mehr zugänglich)", record.Display);
        Assert.Null(record.Href);
        // still listed (so it can be unfollowed) despite being hidden
        Assert.Equal("person-c", record.Id);
    }

    // ---- GetFollowerIdsAsync --------------------------------------------

    [Fact]
    public async Task GetFollowerIdsAsync_ReturnsDistinctActiveFollowers()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-1"));
            db.Users.Add(Seed.Agent("agent-2"));
            db.Users.Add(Seed.Agent("agent-3"));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-1", new DateTime(2026, 1, 1)));
            db.Watchlists.Add(Entry("agent-2", PersonType, "person-1", new DateTime(2026, 1, 2)));
            // soft-deleted follower and a follower of a different record are excluded
            db.Watchlists.Add(Entry("agent-3", PersonType, "person-1", new DateTime(2026, 1, 3), deleted: true));
            db.Watchlists.Add(Entry("agent-1", PersonType, "person-2", new DateTime(2026, 1, 4)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var ids = await svc.GetFollowerIdsAsync(PersonType, "person-1");

        Assert.Equal(2, ids.Count);
        Assert.Contains("agent-1", ids);
        Assert.Contains("agent-2", ids);
        Assert.DoesNotContain("agent-3", ids);
    }
}
