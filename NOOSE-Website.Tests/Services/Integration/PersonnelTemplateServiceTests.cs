using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PersonnelTemplateService"/> against in-memory SQLite.</summary>
public sealed class PersonnelTemplateServiceTests
{
    private static PersonnelTemplateService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static PersonnelTemplate NewTemplate(
        string id,
        PersonnelTemplateKind kind,
        string name,
        int sorting = 0,
        bool active = true,
        string content = "<p>Body</p>",
        string? description = null)
        => new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            Sorting = sorting,
            IsActive = active,
            ContentHtml = content,
            Description = description,
        };

    private static PersonnelTemplateInput ValidInput() => new()
    {
        Kind = PersonnelTemplateKind.Commendation,
        Name = "Vorlage",
        Description = "Beschreibung",
        ContentHtml = "<p>Inhalt</p>",
        IsActive = true,
        Sorting = 3,
    };

    // ---- GetAllAsync -------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_OrderedByKindThenSortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Disciplinary, "A", sorting: 0));
            db.PersonnelTemplates.Add(NewTemplate("t2", PersonnelTemplateKind.Commendation, "Z", sorting: 5));
            db.PersonnelTemplates.Add(NewTemplate("t3", PersonnelTemplateKind.Commendation, "B", sorting: 1));
            db.PersonnelTemplates.Add(NewTemplate("t4", PersonnelTemplateKind.Commendation, "A", sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        // Commendation kind first; within it Sorting then Name; Disciplinary last.
        Assert.Equal(new[] { "t4", "t3", "t2", "t1" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_IncludesInactiveTemplates()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("a", PersonnelTemplateKind.Commendation, "Aktiv", active: true));
            db.PersonnelTemplates.Add(NewTemplate("i", PersonnelTemplateKind.Commendation, "Inaktiv", active: false));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        Assert.Equal(2, result.Count);
    }

    // ---- GetActiveAsync ----------------------------------------------------

    [Fact]
    public async Task GetActiveAsync_ReturnsOnlyActiveOfKind_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("a1", PersonnelTemplateKind.Commendation, "B", sorting: 2, active: true));
            db.PersonnelTemplates.Add(NewTemplate("a2", PersonnelTemplateKind.Commendation, "C", sorting: 1, active: true));
            db.PersonnelTemplates.Add(NewTemplate("a3", PersonnelTemplateKind.Commendation, "A", sorting: 0, active: false)); // inactive
            db.PersonnelTemplates.Add(NewTemplate("a4", PersonnelTemplateKind.Disciplinary, "X", sorting: 0, active: true)); // wrong kind
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetActiveAsync(PersonnelTemplateKind.Commendation);

        Assert.Equal(new[] { "a2", "a1" }, result.Select(t => t.Id).ToArray());
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Promotion, "Beförderung"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("t1");

        Assert.NotNull(result);
        Assert.Equal("Beförderung", result!.Name);
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
    public async Task CreateAsync_PersistsTemplate_TrimsName_SanitizesHtml_AndCopiesFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new PersonnelTemplateInput
        {
            Kind = PersonnelTemplateKind.Disciplinary,
            Name = "  Verwarnung  ",
            Description = "  Notiz  ",
            ContentHtml = "<strong>Wichtig</strong><script>evil()</script>",
            IsActive = false,
            Sorting = 7,
        };

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(PersonnelTemplateKind.Disciplinary, stored.Kind);
        Assert.Equal("Verwarnung", stored.Name);
        Assert.Equal("Notiz", stored.Description);
        Assert.Contains("Wichtig", stored.ContentHtml);
        Assert.DoesNotContain("script", stored.ContentHtml);
        Assert.False(stored.IsActive);
        Assert.Equal(7, stored.Sorting);
    }

    [Fact]
    public async Task CreateAsync_BlankDescription_StoredAsNull()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Description = "   ";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync();
        Assert.Null(stored.Description);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.PersonnelTemplates.AnyAsync());
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
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenDuplicateNameInSameKind()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Vorlage"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput(); // Kind=Commendation, Name="Vorlage"

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.PersonnelTemplates.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_AllowsSameName_InDifferentKind()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Vorlage"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Kind = PersonnelTemplateKind.Promotion; // same name, different kind -> allowed

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.PersonnelTemplates.CountAsync());
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Alt", sorting: 1, active: true, content: "<p>Alt</p>", description: "AltNotiz"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonnelTemplateInput
        {
            Kind = PersonnelTemplateKind.Promotion,
            Name = "  Neu  ",
            Description = "  ", // becomes null
            ContentHtml = "<p>Neu</p>",
            IsActive = false,
            Sorting = 9,
        };

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal(PersonnelTemplateKind.Promotion, stored.Kind);
        Assert.Equal("Neu", stored.Name);
        Assert.Null(stored.Description);
        Assert.Contains("Neu", stored.ContentHtml);
        Assert.False(stored.IsActive);
        Assert.Equal(9, stored.Sorting);
    }

    [Fact]
    public async Task RefreshAsync_AllowsKeepingOwnName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Vorlage", sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput(); // same Kind + Name as the existing row (self excluded from dup check)
        input.Sorting = 42;

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Vorlage", stored.Name);
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
    public async Task RefreshAsync_Throws_WhenNameBlank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t1", input, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenDuplicateNameInSameKind()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Erste"));
            db.PersonnelTemplates.Add(NewTemplate("t2", PersonnelTemplateKind.Commendation, "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "Erste"; // collides with t1 within the same kind

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t2", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync(t => t.Id == "t2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("t1", ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        var stored = await check.PersonnelTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Vorlage"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.PersonnelTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_NoThrow_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Service returns silently for a missing id (does not throw).
        await svc.DeleteAsync("ghost", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.PersonnelTemplates.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonnelTemplates.Add(NewTemplate("t1", PersonnelTemplateKind.Commendation, "Vorlage"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("t1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.PersonnelTemplates.AnyAsync(t => t.Id == "t1"));
    }
}
