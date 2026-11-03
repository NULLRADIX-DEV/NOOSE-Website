using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="BewerbungTemplateService"/> against in-memory SQLite.</summary>
public sealed class BewerbungTemplateServiceTests
{
    // The service scopes everything to RecruitingSeeder.TemplateCategory ("Bewerbung").
    private const string Category = "Bewerbung";

    private static BewerbungTemplateService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // HRB flag => IsHrbOrLeadership => passes RequireHrbOrLeadership.
    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").AsHrb().Build();

    // Director => IsLeadership => also passes the guard.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee, no flags: not HRB, not leadership, not admin.
    private static ClaimsPrincipal Outsider()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static DocumentTemplate NewTemplate(
        string id, string name, string? category = Category, bool active = true, int sorting = 0)
        => new()
        {
            Id = id,
            Name = name,
            Category = category,
            IsActive = active,
            Sorting = sorting,
            ContentHtml = "<p>x</p>",
        };

    // ---- ListAsync ---------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsOnlyCategory_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t3", "Zeta", sorting: 2));
            db.DocumentTemplates.Add(NewTemplate("t1", "Alpha", sorting: 1));
            db.DocumentTemplates.Add(NewTemplate("t2", "Beta", sorting: 1));
            db.DocumentTemplates.Add(NewTemplate("other", "Other", category: "Verhoer", sorting: 0));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ListAsync(Hrb());

        // Only "Bewerbung" rows; sorting 1 (Alpha, Beta) then sorting 2 (Zeta), tie broken by name.
        Assert.Equal(new[] { "t1", "t2", "t3" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task ListAsync_AllowsLeadership_NotJustHrb()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alpha"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ListAsync(Leader());

        Assert.Single(result);
    }

    [Fact]
    public async Task ListAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ListAsync(Outsider()));
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsTemplate_WhenInCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alpha"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("t1", Hrb());

        Assert.NotNull(result);
        Assert.Equal("Alpha", result!.Name);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenWrongCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alpha", category: "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("t1", Hrb());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Null(await svc.GetAsync("ghost", Hrb()));
    }

    [Fact]
    public async Task GetAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAsync("t1", Outsider()));
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsTemplate_TrimsName_SetsCategoryAndSorting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("existing", "Bestand", sorting: 7));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var created = await svc.CreateAsync("  Willkommen  ", "  Grussformel  ", "<p>Hallo</p>", isActive: true, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == created.Id);
        Assert.Equal("Willkommen", stored.Name);
        Assert.Equal("Grussformel", stored.Description);
        Assert.Equal(Category, stored.Category);
        Assert.True(stored.IsActive);
        Assert.Equal("<p>Hallo</p>", stored.ContentHtml);
        Assert.Equal(8, stored.Sorting); // max(7) + 1
    }

    [Fact]
    public async Task CreateAsync_FirstTemplate_GetsSortingOne()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync("Erste", null, "<p>x</p>", isActive: false, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == created.Id);
        Assert.Equal(1, stored.Sorting); // (max ?? 0) + 1
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task CreateAsync_NullsBlankDescription_AndSanitizesHtml()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync("Vorlage", "   ", "<p>ok</p><script>evil()</script>", isActive: true, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == created.Id);
        Assert.Null(stored.Description);
        Assert.DoesNotContain("script", stored.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok", stored.ContentHtml);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("   ", null, "<p>x</p>", isActive: true, Hrb()));

        using var check = ctx.NewContext();
        Assert.False(await check.DocumentTemplates.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Willkommen"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("  Willkommen  ", null, "<p>x</p>", isActive: true, Hrb())); // trims to existing

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.DocumentTemplates.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Vorlage", null, "<p>x</p>", isActive: true, Outsider()));

        using var check = ctx.NewContext();
        Assert.False(await check.DocumentTemplates.AnyAsync());
    }

    // ---- UpdateAsync -------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt", active: true, sorting: 3));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.UpdateAsync("t1", "  Neu  ", "   ", "<p>neu</p><script>x()</script>", isActive: false, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Neu", stored.Name);
        Assert.Null(stored.Description); // blank -> null
        Assert.False(stored.IsActive);
        Assert.DoesNotContain("script", stored.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neu", stored.ContentHtml);
        Assert.Equal(3, stored.Sorting); // untouched
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("ghost", "Neu", null, "<p>x</p>", isActive: true, Hrb()));
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenWrongCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt", category: "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Only "Bewerbung" rows are visible to this service -> treated as not found.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("t1", "Neu", null, "<p>x</p>", isActive: true, Hrb()));
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("t1", "   ", null, "<p>x</p>", isActive: true, Hrb()));

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenNameDuplicateOfAnother()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Erste"));
            db.DocumentTemplates.Add(NewTemplate("t2", "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("t2", "Erste", null, "<p>x</p>", isActive: true, Hrb()));

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task UpdateAsync_AllowsKeepingOwnName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Behalten", active: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.UpdateAsync("t1", "Behalten", "Neu", "<p>x</p>", isActive: false, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Behalten", stored.Name);
        Assert.Equal("Neu", stored.Description);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateAsync("t1", "Neu", null, "<p>x</p>", isActive: true, Outsider()));

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Willkommen"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", Hrb());

        using var check = ctx.NewContext();
        // No soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.DocumentTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsSilently_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.DeleteAsync("ghost", Hrb());

        using var check = ctx.NewContext();
        Assert.False(await check.DocumentTemplates.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherCategoryUntouched()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Fremd", category: "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Not in "Bewerbung" -> service finds nothing, returns silently, row survives.
        await svc.DeleteAsync("t1", Hrb());

        using var check = ctx.NewContext();
        Assert.True(await check.DocumentTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Willkommen"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("t1", Outsider()));

        using var check = ctx.NewContext();
        Assert.True(await check.DocumentTemplates.AnyAsync(t => t.Id == "t1"));
    }
}
