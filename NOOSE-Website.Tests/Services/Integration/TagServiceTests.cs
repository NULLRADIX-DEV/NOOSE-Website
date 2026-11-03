using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TagService"/> against in-memory SQLite.</summary>
public sealed class TagServiceTests
{
    // Rank >= SpecialAgentInCharge(4) or admin => IsLeadership().
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    [Fact]
    public async Task GetAllAsync_ReturnsTags_OrderedByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(new Tag { Name = "Zulu" });
            db.Tags.Add(new Tag { Name = "Alpha" });
            db.Tags.Add(new Tag { Name = "Mike" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        var result = await svc.GetAllAsync();

        Assert.Equal(new[] { "Alpha", "Mike", "Zulu" }, result.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task GetWithUsageAsync_ReturnsCounts_PerTag()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "Alpha" };
        var t2 = new Tag { Name = "Beta" };
        using (var db = ctx.NewContext())
        {
            db.Tags.AddRange(t1, t2);
            db.TagMappings.Add(new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p1" });
            db.TagMappings.Add(new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p2" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        var result = await svc.GetWithUsageAsync();

        // ordered by name: Alpha (2 assignments) then Beta (0).
        Assert.Equal(2, result.Count);
        Assert.Equal("Alpha", result[0].Tag.Name);
        Assert.Equal(2, result[0].Count);
        Assert.Equal("Beta", result[1].Tag.Name);
        Assert.Equal(0, result[1].Count);
    }

    [Fact]
    public async Task CreateAsync_PersistsTrimmedName_AndNullsBlankColour()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        var tag = await svc.CreateAsync("  Wichtig  ", "   ", Leader());

        Assert.Equal("Wichtig", tag.Name);
        Assert.Null(tag.Colour);
        using var db = ctx.NewContext();
        var stored = await db.Tags.SingleAsync(t => t.Id == tag.Id);
        Assert.Equal("Wichtig", stored.Name);
        Assert.Null(stored.Colour);
    }

    [Fact]
    public async Task CreateAsync_KeepsColour_WhenProvided()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        var tag = await svc.CreateAsync("Info", "Primary", Leader());

        Assert.Equal("Primary", tag.Colour);
    }

    [Fact]
    public async Task CreateAsync_Throws_OnEmptyName()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("   ", null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_OnDuplicateName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(new Tag { Name = "Dup" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Dup", null, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_UpdatesNameAndColour()
    {
        using var ctx = new SqliteTestContext();
        var tag = new Tag { Name = "Old", Colour = "Info" };
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(tag);
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await svc.RefreshAsync(tag.Id, "  New  ", "  Primary  ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Tags.SingleAsync(t => t.Id == tag.Id);
        Assert.Equal("New", stored.Name);
        Assert.Equal("Primary", stored.Colour);
    }

    [Fact]
    public async Task RefreshAsync_NullsColour_WhenBlank()
    {
        using var ctx = new SqliteTestContext();
        var tag = new Tag { Name = "Old", Colour = "Info" };
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(tag);
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await svc.RefreshAsync(tag.Id, "Old", "  ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Tags.SingleAsync(t => t.Id == tag.Id);
        Assert.Null(stored.Colour);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenActorNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("any-id", "New", null, NonLeader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownTag()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", "New", null, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnDuplicateName()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "First" };
        var t2 = new Tag { Name = "Second" };
        using (var db = ctx.NewContext())
        {
            db.Tags.AddRange(t1, t2);
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        // renaming t2 to an existing name collides.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync(t2.Id, "First", null, Leader()));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTag()
    {
        using var ctx = new SqliteTestContext();
        var tag = new Tag { Name = "Doomed" };
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(tag);
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await svc.DeleteAsync(tag.Id, Leader());

        // hard delete: gone entirely (Tag has no soft-delete).
        using var check = ctx.NewContext();
        Assert.False(await check.Tags.AnyAsync(t => t.Id == tag.Id));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_OnUnknownTag()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        // returns without throwing when the tag does not exist.
        await svc.DeleteAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.Tags.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenActorNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = new TagService(ctx.Factory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("any-id", NonLeader()));
    }

    [Fact]
    public async Task GetForRecordAsync_ReturnsAssignedTags_OrderedByName()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "Zeta" };
        var t2 = new Tag { Name = "Other" };
        var t3 = new Tag { Name = "Anchor" };
        using (var db = ctx.NewContext())
        {
            db.Tags.AddRange(t1, t2, t3);
            db.TagMappings.Add(new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p1" });
            db.TagMappings.Add(new TagMapping { TagId = t3.Id, EntityType = "Person", EntityId = "p1" });
            // t2 belongs to a different record and must be excluded.
            db.TagMappings.Add(new TagMapping { TagId = t2.Id, EntityType = "Person", EntityId = "p9" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        var result = await svc.GetForRecordAsync("Person", "p1");

        Assert.Equal(new[] { "Anchor", "Zeta" }, result.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task SetAsync_DiffUpdates_AddsRemovesAndKeeps()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "One" };
        var t2 = new Tag { Name = "Two" };
        var t3 = new Tag { Name = "Three" };
        using (var db = ctx.NewContext())
        {
            db.Tags.AddRange(t1, t2, t3);
            db.TagMappings.Add(new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p1" });
            db.TagMappings.Add(new TagMapping { TagId = t2.Id, EntityType = "Person", EntityId = "p1" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        // keep t2, drop t1, add t3 (plus dupes/blanks that must be filtered).
        await svc.SetAsync("Person", "p1", new[] { t2.Id, t3.Id, t3.Id, "", "  " }, Leader());

        using var check = ctx.NewContext();
        var ids = await check.TagMappings
            .Where(z => z.EntityType == "Person" && z.EntityId == "p1")
            .Select(z => z.TagId)
            .ToListAsync();
        Assert.Equal(new HashSet<string> { t2.Id, t3.Id }, ids.ToHashSet());
    }

    [Fact]
    public async Task SetAsync_ClearsAll_OnEmptySet()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "One" };
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(t1);
            db.TagMappings.Add(new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p1" });
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await svc.SetAsync("Person", "p1", Array.Empty<string>(), Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.TagMappings.AnyAsync(z => z.EntityType == "Person" && z.EntityId == "p1"));
    }

    [Fact]
    public async Task SetAsync_NoOp_WhenSetUnchanged()
    {
        using var ctx = new SqliteTestContext();
        var t1 = new Tag { Name = "One" };
        var mapping = new TagMapping { TagId = t1.Id, EntityType = "Person", EntityId = "p1" };
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(t1);
            db.TagMappings.Add(mapping);
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        await svc.SetAsync("Person", "p1", new[] { t1.Id }, Leader());

        using var check = ctx.NewContext();
        var stored = await check.TagMappings
            .Where(z => z.EntityType == "Person" && z.EntityId == "p1")
            .ToListAsync();
        Assert.Single(stored);
        // unchanged: same mapping row survives.
        Assert.Equal(mapping.Id, stored[0].Id);
    }
}
