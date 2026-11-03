using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DocTemplateService"/> against in-memory SQLite.</summary>
public sealed class DocTemplateServiceTests
{
    private static DocTemplateService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static DocTemplate NewTemplate(string id, string name, bool active = true, int sorting = 0)
        => new() { Id = id, Name = name, IsActive = active, Sorting = sorting };

    private static DocTemplateInput ValidInput(string name = "Verhoer") => new()
    {
        Name = name,
        Description = "Beschreibung",
        IsActive = true,
        Sorting = 5,
        DefaultReason = "Grund",
        DefaultFaction = "Ballas",
        DefaultReceivedInformation = "Info",
        DefaultTruthSerum = true,
        DefaultOutcome = MeasureOutcome.Injection,
    };

    // ---- GetAllAsync -------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_IncludingInactive_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t3", "Zeta", active: true, sorting: 2));
            db.DocTemplates.Add(NewTemplate("t1", "Alpha", active: false, sorting: 1));
            db.DocTemplates.Add(NewTemplate("t2", "Beta", active: true, sorting: 1));
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
            db.DocTemplates.Add(NewTemplate("t1", "Alpha", active: false, sorting: 1));
            db.DocTemplates.Add(NewTemplate("t2", "Beta", active: true, sorting: 2));
            db.DocTemplates.Add(NewTemplate("t3", "Gamma", active: true, sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetActiveAsync();

        Assert.Equal(new[] { "t3", "t2" }, result.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(result, t => t.Id == "t1");
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsTemplate_AndAppliesFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var created = await svc.CreateAsync(ValidInput("Verhoer"), Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Verhoer", stored.Name);
        Assert.Equal("Beschreibung", stored.Description);
        Assert.True(stored.IsActive);
        Assert.Equal(5, stored.Sorting);
        Assert.Equal("Grund", stored.DefaultReason);
        Assert.Equal("Ballas", stored.DefaultFaction);
        Assert.Equal("Info", stored.DefaultReceivedInformation);
        Assert.True(stored.DefaultTruthSerum);
        Assert.Equal(MeasureOutcome.Injection, stored.DefaultOutcome);
    }

    [Fact]
    public async Task CreateAsync_TrimsName_AndNullsBlankOptionalFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("  Getrimmt  ");
        input.Description = "   ";
        input.DefaultReason = "   ";
        input.DefaultFaction = null;
        input.DefaultReceivedInformation = "";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync();
        Assert.Equal("Getrimmt", stored.Name);
        Assert.Null(stored.Description);
        Assert.Null(stored.DefaultReason);
        Assert.Null(stored.DefaultFaction);
        Assert.Null(stored.DefaultReceivedInformation);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.DocTemplates.AnyAsync());
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
        Assert.False(await check.DocTemplates.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNameDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("  Verhoer  "); // trims to the existing name

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.DocTemplates.CountAsync());
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(new DocTemplate
            {
                Id = "t1",
                Name = "Alt",
                Description = "AlteBeschreibung",
                IsActive = true,
                Sorting = 1,
                DefaultReason = "AlterGrund",
                DefaultOutcome = MeasureOutcome.RunningStill,
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new DocTemplateInput
        {
            Name = "  Neu  ",
            Description = "   ", // becomes null
            IsActive = false,
            Sorting = 9,
            DefaultReason = "NeuerGrund",
            DefaultFaction = "Vagos",
            DefaultReceivedInformation = "NeueInfo",
            DefaultTruthSerum = true,
            DefaultOutcome = MeasureOutcome.Shot,
        };

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Neu", stored.Name);
        Assert.Null(stored.Description);
        Assert.False(stored.IsActive);
        Assert.Equal(9, stored.Sorting);
        Assert.Equal("NeuerGrund", stored.DefaultReason);
        Assert.Equal("Vagos", stored.DefaultFaction);
        Assert.Equal("NeueInfo", stored.DefaultReceivedInformation);
        Assert.True(stored.DefaultTruthSerum);
        Assert.Equal(MeasureOutcome.Shot, stored.DefaultOutcome);
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
            db.DocTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t1", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNameDuplicateOfAnotherTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Erste"));
            db.DocTemplates.Add(NewTemplate("t2", "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Erste"); // collides with t1

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("t2", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync(t => t.Id == "t2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_AllowsKeepingOwnName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Behalten", sorting: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Behalten"); // same name, different id => allowed
        input.Sorting = 42;

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Behalten", stored.Name);
        Assert.Equal(42, stored.Sorting);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("t1", ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        var stored = await check.DocTemplates.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesTemplate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.DocTemplates.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsSilently_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Service returns without throwing when the id does not exist.
        await svc.DeleteAsync("ghost", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.DocTemplates.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.DocTemplates.Add(NewTemplate("t1", "Verhoer"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("t1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.DocTemplates.AnyAsync(t => t.Id == "t1"));
    }
}
