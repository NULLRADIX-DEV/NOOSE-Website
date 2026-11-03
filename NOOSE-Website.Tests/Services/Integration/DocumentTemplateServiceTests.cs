using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DocumentTemplateService"/> against in-memory SQLite.</summary>
public sealed class DocumentTemplateServiceTests
{
    private static DocumentTemplateService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Admin flag short-circuits the leadership guard even at a low rank.
    private static ClaimsPrincipal AdminActor()
        => ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Trainee).AsAdmin().Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static DocumentTemplate NewTemplate(
        string id, string name, bool active = true, int sorting = 0, string? category = null)
        => new()
        {
            Id = id,
            Name = name,
            IsActive = active,
            Sorting = sorting,
            Category = category,
            ContentHtml = "<p>Body</p>",
        };

    private static DocumentTemplateInput ValidInput(string name = "Vorlage") => new()
    {
        Name = name,
        Description = "Beschreibung",
        Category = "Kategorie",
        ContentHtml = "<p>Inhalt</p>",
        IsActive = true,
        Sorting = 5,
    };

    // ---- GetAllAsync -------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Bravo", sorting: 10));
            db.DocumentTemplates.Add(NewTemplate("t2", "Alpha", sorting: 5));
            db.DocumentTemplates.Add(NewTemplate("t3", "Charlie", active: false, sorting: 5));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        // Sorting 5 group first (Alpha < Charlie by name), then sorting 10.
        Assert.Equal(new[] { "t2", "t3", "t1" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_IncludesInactiveTemplates()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Aktiv", active: true));
            db.DocumentTemplates.Add(NewTemplate("t2", "Inaktiv", active: false));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        Assert.Equal(2, result.Count);
    }

    // ---- GetActiveAsync ----------------------------------------------------

    [Fact]
    public async Task GetActiveAsync_ReturnsOnlyActive_Ordered()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Zulu", active: true, sorting: 2));
            db.DocumentTemplates.Add(NewTemplate("t2", "Alpha", active: true, sorting: 1));
            db.DocumentTemplates.Add(NewTemplate("t3", "Inaktiv", active: false, sorting: 0));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetActiveAsync();

        Assert.Equal(new[] { "t2", "t1" }, result.Select(t => t.Id).ToArray());
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsTemplate_WhenPresent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Vorlage"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("t1");

        Assert.NotNull(result);
        Assert.Equal("Vorlage", result!.Name);
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
    public async Task CreateAsync_PersistsTemplate_TrimsName_AndSanitizesHtml()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("  Neue Vorlage  ");
        input.ContentHtml = "<p>Hallo</p><script>alert(1)</script>";

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Neue Vorlage", stored.Name);
        Assert.Equal("Beschreibung", stored.Description);
        Assert.Equal("Kategorie", stored.Category);
        Assert.True(stored.IsActive);
        Assert.Equal(5, stored.Sorting);
        // Sanitizer keeps allowed <p> content, strips <script>.
        Assert.Contains("Hallo", stored.ContentHtml);
        Assert.DoesNotContain("script", stored.ContentHtml);
    }

    [Fact]
    public async Task CreateAsync_NullsBlankDescriptionAndCategory()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Description = "   ";
        input.Category = "";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync();
        Assert.Null(stored.Description);
        Assert.Null(stored.Category);
    }

    [Fact]
    public async Task CreateAsync_AllowsAdmin()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync(ValidInput("Admin-Vorlage"), AdminActor());

        Assert.Equal("Admin-Vorlage", created.Name);
        using var check = ctx.NewContext();
        Assert.True(await check.DocumentTemplates.AnyAsync(t => t.Id == created.Id));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.DocumentTemplates.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("   ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Dublette"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(ValidInput("Dublette"), Leader()));

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.DocumentTemplates.CountAsync());
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndSanitizesHtml()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt", active: true, sorting: 1, category: "AltKat"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new DocumentTemplateInput
        {
            Name = "  Neu  ",
            Description = "  ", // becomes null
            Category = "NeuKat",
            ContentHtml = "<p>Text</p><script>x()</script>",
            IsActive = false,
            Sorting = 9,
        };

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Neu", stored.Name);
        Assert.Null(stored.Description);
        Assert.Equal("NeuKat", stored.Category);
        Assert.False(stored.IsActive);
        Assert.Equal(9, stored.Sorting);
        Assert.Contains("Text", stored.ContentHtml);
        Assert.DoesNotContain("script", stored.ContentHtml);
    }

    [Fact]
    public async Task RefreshAsync_AllowsKeepingSameName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Behalten"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Behalten");
        input.Sorting = 42;

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Behalten", stored.Name);
        Assert.Equal(42, stored.Sorting);
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
    public async Task RefreshAsync_Throws_WhenNameCollidesWithOther()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Erste"));
            db.DocumentTemplates.Add(NewTemplate("t2", "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Erste"); // collides with t1

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t2", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.DocumentTemplates.SingleAsync(t => t.Id == "t2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("   ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t1", input, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("t1", ValidInput("Neu"), NonLeader()));

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
            db.DocumentTemplates.Add(NewTemplate("t1", "Weg"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", Leader());

        using var check = ctx.NewContext();
        // No soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.DocumentTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Service returns silently (no throw) when the template is missing.
        await svc.DeleteAsync("ghost", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.DocumentTemplates.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocumentTemplates.Add(NewTemplate("t1", "Bleibt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("t1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.DocumentTemplates.AnyAsync(t => t.Id == "t1"));
    }
}
