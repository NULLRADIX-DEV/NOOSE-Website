using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="ActivityTemplateService"/> against in-memory SQLite.</summary>
public sealed class ActivityTemplateServiceTests
{
    private static ActivityTemplateService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ActivityTemplate NewTemplate(string id, string name, bool active = true, int sorting = 0)
        => new() { Id = id, Name = name, IsActive = active, Sorting = sorting };

    private static ActivityTemplateInput ValidInput(string name = "Streife") => new()
    {
        Name = name,
        Description = "Beschreibung",
        Kind = "Patrouille",
        ContentHtml = "<p>Inhalt</p>",
        IsActive = true,
        Sorting = 5,
    };

    // ---- GetAllAsync -------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_IncludingInactive_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t3", "Zeta", active: true, sorting: 2));
            db.ActivityTemplates.Add(NewTemplate("t1", "Alpha", active: false, sorting: 1));
            db.ActivityTemplates.Add(NewTemplate("t2", "Beta", active: true, sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        // sorting 1 (Alpha, Beta) then sorting 2 (Zeta); tie broken by name.
        Assert.Equal(new[] { "t1", "t2", "t3" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_Empty_ReturnsEmptyList()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        Assert.Empty(result);
    }

    // ---- GetActiveAsync ----------------------------------------------------

    [Fact]
    public async Task GetActiveAsync_ReturnsOnlyActive_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Alpha", active: false, sorting: 1));
            db.ActivityTemplates.Add(NewTemplate("t2", "Beta", active: true, sorting: 2));
            db.ActivityTemplates.Add(NewTemplate("t3", "Gamma", active: true, sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetActiveAsync();

        Assert.Equal(new[] { "t3", "t2" }, result.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(result, t => t.Id == "t1");
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsTemplate_WhenExists()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Streife"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("t1");

        Assert.NotNull(result);
        Assert.Equal("Streife", result!.Name);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetAsync("ghost");

        Assert.Null(result);
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsTemplate_AndAppliesFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync(ValidInput("Streife"), Leader());

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Streife", stored.Name);
        Assert.Equal("Beschreibung", stored.Description);
        Assert.Equal("Patrouille", stored.Kind);
        Assert.Contains("Inhalt", stored.ContentHtml);
        Assert.True(stored.IsActive);
        Assert.Equal(5, stored.Sorting);
    }

    [Fact]
    public async Task CreateAsync_TrimsName_AndNullsBlankOptionalFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("  Getrimmt  ");
        input.Description = "   ";
        input.Kind = "";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync();
        Assert.Equal("Getrimmt", stored.Name);
        Assert.Null(stored.Description);
        Assert.Null(stored.Kind);
    }

    [Fact]
    public async Task CreateAsync_SanitizesContentHtml_StrippingScript()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("MitSkript");
        input.ContentHtml = "<p>Safe</p><script>steal()</script>";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync();
        Assert.Contains("Safe", stored.ContentHtml);
        Assert.DoesNotContain("<script", stored.ContentHtml);
        Assert.DoesNotContain("steal", stored.ContentHtml);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.ActivityTemplates.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.False(await check.ActivityTemplates.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Streife"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("  Streife  "); // trims to the existing name

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.ActivityTemplates.CountAsync());
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(new ActivityTemplate
            {
                Id = "t1",
                Name = "Alt",
                Description = "AlteBeschreibung",
                Kind = "AlteArt",
                ContentHtml = "<p>Alt</p>",
                IsActive = true,
                Sorting = 1,
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new ActivityTemplateInput
        {
            Name = "  Neu  ",
            Description = "   ", // becomes null
            Kind = "NeueArt",
            ContentHtml = "<p>Neu</p>",
            IsActive = false,
            Sorting = 9,
        };

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Neu", stored.Name);
        Assert.Null(stored.Description);
        Assert.Equal("NeueArt", stored.Kind);
        Assert.Contains("Neu", stored.ContentHtml);
        Assert.False(stored.IsActive);
        Assert.Equal(9, stored.Sorting);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("ghost", ValidInput(), Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t1", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNameDuplicateOfAnotherTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Erste"));
            db.ActivityTemplates.Add(NewTemplate("t2", "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Erste"); // collides with t1

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t2", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync(t => t.Id == "t2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_AllowsKeepingOwnName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Behalten", sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Behalten"); // same name, different id => allowed
        input.Sorting = 42;

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Behalten", stored.Name);
        Assert.Equal(42, stored.Sorting);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("t1", ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        var stored = await check.ActivityTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Streife"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.ActivityTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsSilently_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Service returns without throwing when the id does not exist.
        await svc.DeleteAsync("ghost", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.ActivityTemplates.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ActivityTemplates.Add(NewTemplate("t1", "Streife"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("t1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.ActivityTemplates.AnyAsync(t => t.Id == "t1"));
    }
}
