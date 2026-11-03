using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="CustomFieldDefinitionService"/> against in-memory SQLite.</summary>
public sealed class CustomFieldDefinitionServiceTests
{
    private static CustomFieldDefinitionService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static CustomFieldDefinition NewDefinition(
        string id, string entityType, string name, int order = 0, bool active = true)
        => new() { Id = id, EntityType = entityType, Name = name, Order = order, IsActive = active };

    private static CustomFieldDefinitionInput ValidInput(
        string name = "Deckname", string entityType = "Person") => new()
    {
        Name = name,
        EntityType = entityType,
        FieldType = CustomFieldType.Text,
        Options = null,
        Mandatory = false,
        Order = 3,
        IsActive = true,
    };

    // ---- GetAllAsync -------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_OrderedByEntityTypeThenOrderThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // Person fields (order 2 "Zeta", order 1 "Beta", order 1 "Alpha") + one Faction field.
            db.CustomFieldDefinitions.Add(NewDefinition("p3", "Person", "Zeta", order: 2));
            db.CustomFieldDefinitions.Add(NewDefinition("p2", "Person", "Beta", order: 1));
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Alpha", order: 1));
            db.CustomFieldDefinitions.Add(NewDefinition("f1", "Faction", "Gamma", order: 0));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        // "Faction" sorts before "Person"; within Person: order 1 (Alpha, Beta) then order 2 (Zeta).
        Assert.Equal(new[] { "f1", "p1", "p2", "p3" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_Empty_ReturnsEmptyList()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetAllAsync();

        Assert.Empty(result);
    }

    // ---- GetForTypeAsync ---------------------------------------------------

    [Fact]
    public async Task GetForTypeAsync_OnlyActiveTrue_ReturnsActiveForType_OrderedByOrderThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Alpha", order: 2, active: true));
            db.CustomFieldDefinitions.Add(NewDefinition("p2", "Person", "Beta", order: 1, active: true));
            db.CustomFieldDefinitions.Add(NewDefinition("p3", "Person", "Gamma", order: 1, active: false));
            db.CustomFieldDefinitions.Add(NewDefinition("f1", "Faction", "Delta", order: 0, active: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetForTypeAsync("Person", onlyActive: true);

        // Excludes inactive p3 and the Faction field; order 1 (Beta) before order 2 (Alpha).
        Assert.Equal(new[] { "p2", "p1" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetForTypeAsync_OnlyActiveFalse_ReturnsAllForType_ExcludesOtherTypes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Alpha", order: 1, active: true));
            db.CustomFieldDefinitions.Add(NewDefinition("p2", "Person", "Beta", order: 2, active: false));
            db.CustomFieldDefinitions.Add(NewDefinition("f1", "Faction", "Gamma", order: 0, active: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetForTypeAsync("Person", onlyActive: false);

        Assert.Equal(new[] { "p1", "p2" }, result.Select(d => d.Id).ToArray());
        Assert.DoesNotContain(result, d => d.Id == "f1");
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsDefinition_AndAppliesFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("Deckname", "Person");
        input.FieldType = CustomFieldType.Number;
        input.Mandatory = true;
        input.Order = 7;
        input.IsActive = false;

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("Deckname", stored.Name);
        Assert.Equal("Person", stored.EntityType);
        Assert.Equal(CustomFieldType.Number, stored.FieldType);
        Assert.True(stored.Mandatory);
        Assert.Equal(7, stored.Order);
        Assert.False(stored.IsActive);
        Assert.Null(stored.Options); // not a selection field
    }

    [Fact]
    public async Task CreateAsync_TrimsNameAndEntityType_AndSetsTrimmedOptionsForSelection()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new CustomFieldDefinitionInput
        {
            Name = "  Status  ",
            EntityType = "  Person  ",
            FieldType = CustomFieldType.Selection,
            Options = "  A\nB  ",
            Order = 1,
            IsActive = true,
        };

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync();
        Assert.Equal("Status", stored.Name);
        Assert.Equal("Person", stored.EntityType);
        Assert.Equal(CustomFieldType.Selection, stored.FieldType);
        Assert.Equal("A\nB", stored.Options);
    }

    [Fact]
    public async Task CreateAsync_NullsOptions_WhenNotSelection()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.FieldType = CustomFieldType.Text;
        input.Options = "IgnoredForText";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync();
        Assert.Null(stored.Options);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.CustomFieldDefinitions.AnyAsync());
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
        Assert.False(await check.CustomFieldDefinitions.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenEntityTypeBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.EntityType = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.False(await check.CustomFieldDefinitions.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenSelectionWithoutOptions()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.FieldType = CustomFieldType.Selection;
        input.Options = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.False(await check.CustomFieldDefinitions.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenDuplicateNameForSameEntityType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Deckname"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("  Deckname  ", "Person"); // trims to the existing name

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.CustomFieldDefinitions.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_AllowsSameName_ForDifferentEntityType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Deckname"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Deckname", "Faction"); // same name, different type => allowed

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.CustomFieldDefinitions.CountAsync());
        Assert.Equal("Faction", (await check.CustomFieldDefinitions.SingleAsync(d => d.Id == created.Id)).EntityType);
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(new CustomFieldDefinition
            {
                Id = "p1",
                EntityType = "Person",
                Name = "Alt",
                FieldType = CustomFieldType.Text,
                Mandatory = false,
                Order = 1,
                IsActive = true,
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new CustomFieldDefinitionInput
        {
            Name = "  Neu  ",
            EntityType = "  Faction  ",
            FieldType = CustomFieldType.Selection,
            Options = "  X\nY  ",
            Mandatory = true,
            Order = 9,
            IsActive = false,
        };

        await svc.RefreshAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync(d => d.Id == "p1");
        Assert.Equal("Neu", stored.Name);
        Assert.Equal("Faction", stored.EntityType);
        Assert.Equal(CustomFieldType.Selection, stored.FieldType);
        Assert.Equal("X\nY", stored.Options);
        Assert.True(stored.Mandatory);
        Assert.Equal(9, stored.Order);
        Assert.False(stored.IsActive);
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
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Name = "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("p1", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync(d => d.Id == "p1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNameDuplicateOfAnotherDefinition()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Erste"));
            db.CustomFieldDefinitions.Add(NewDefinition("p2", "Person", "Zweite"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Erste", "Person"); // collides with p1 in the same type

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("p2", input, Leader()));

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync(d => d.Id == "p2");
        Assert.Equal("Zweite", stored.Name); // untouched
    }

    [Fact]
    public async Task RefreshAsync_AllowsKeepingOwnName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Behalten", order: 1));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput("Behalten", "Person"); // same name+type, same id => allowed
        input.Order = 42;

        await svc.RefreshAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync(d => d.Id == "p1");
        Assert.Equal("Behalten", stored.Name);
        Assert.Equal(42, stored.Order);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("p1", ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        var stored = await check.CustomFieldDefinitions.SingleAsync(d => d.Id == "p1");
        Assert.Equal("Alt", stored.Name); // untouched
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesDefinition()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Deckname"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("p1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.CustomFieldDefinitions.AnyAsync(d => d.Id == "p1"));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsSilently_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Service returns without throwing when the id does not exist.
        await svc.DeleteAsync("ghost", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.CustomFieldDefinitions.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.CustomFieldDefinitions.Add(NewDefinition("p1", "Person", "Deckname"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("p1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.CustomFieldDefinitions.AnyAsync(d => d.Id == "p1"));
    }
}
