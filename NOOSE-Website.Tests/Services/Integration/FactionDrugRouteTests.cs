using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Factions;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A drug route belongs to exactly one active faction; saving asks, moves, or refuses.</summary>
public sealed class FactionDrugRouteTests
{
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static FactionService NewService(SqliteTestContext ctx, IThreatScoreService? threat = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("FIB-F-2026-0099");
        return new FactionService(ctx.Factory, caseNo, Substitute.For<IProfileSuggestionService>(),
            Substitute.For<IPersonService>(), Substitute.For<IFactionPhotoStorageService>(),
            threat ?? Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(),
            Substitute.For<IPublicFactionProfileService>());
    }

    private static FactionInput Input(string name, params string[] routes) => new()
    {
        Name = name,
        DrugRoutes = routes.Select(r => new StockInput { Designation = r }).ToList(),
    };

    /// <summary>Faction "a" without routes, faction "b" holding the given route.</summary>
    private static void SeedHolder(SqliteTestContext ctx, string route, Action<Faction>? holder = null)
    {
        using var db = ctx.NewContext();
        db.Factions.Add(Seed.Faction(id: "a", name: "Ballas"));
        var b = Seed.Faction(id: "b", name: "Vagos", configure: holder);
        db.Factions.Add(b);
        db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "b", Designation = route, Note = "Hafen" });
        db.SaveChanges();
    }

    private static List<string> RoutesOf(SqliteTestContext ctx, string factionId)
    {
        using var db = ctx.NewContext();
        return db.FactionDrugRoutes.Where(d => d.FactionId == factionId).Select(d => d.Designation).ToList();
    }

    // ---- the save asks ----

    [Fact]
    public async Task A_route_another_faction_holds_is_refused_without_confirmation()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        var svc = NewService(ctx);

        var ex = await Assert.ThrowsAsync<DrugRouteConflictException>(
            () => svc.RefreshAsync("a", Input("Ballas", "Kokain Nord"), Leader()));

        var conflict = Assert.Single(ex.Conflicts);
        Assert.Equal("b", conflict.HolderId);
        Assert.Equal("Vagos", conflict.HolderName);
        Assert.True(conflict.MayTakeOver);
        // nothing was written
        Assert.Empty(RoutesOf(ctx, "a"));
        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "b"));
    }

    [Fact]
    public async Task Case_and_spacing_do_not_make_a_different_route()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        var svc = NewService(ctx);

        var conflicts = await svc.GetDrugRouteConflictsAsync("a", ["  kokain   NORD "], Leader());

        Assert.Equal("kokain nord", Assert.Single(conflicts).Key);
    }

    [Fact]
    public async Task The_preflight_names_the_same_conflicts_the_save_refuses()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        var svc = NewService(ctx);

        var asked = await svc.GetDrugRouteConflictsAsync("a", ["Kokain Nord", "Meth Süd"], Leader());
        var refused = await Assert.ThrowsAsync<DrugRouteConflictException>(
            () => svc.RefreshAsync("a", Input("Ballas", "Kokain Nord", "Meth Süd"), Leader()));

        Assert.Equal(asked, refused.Conflicts);
    }

    // ---- the save moves ----

    [Fact]
    public async Task A_confirmed_route_moves_and_both_files_record_it()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);
        var input = Input("Ballas", "kokain nord");
        input.ConfirmedRouteTakeovers = ["Kokain Nord"];

        await svc.RefreshAsync("a", input, Leader());

        Assert.Equal(["kokain nord"], RoutesOf(ctx, "a"));
        Assert.Empty(RoutesOf(ctx, "b"));
        using var db = ctx.NewContext();
        var gave = Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Faction) && l.EntityId == "b"));
        Assert.Contains("Ballas", gave.ChangesJson);
        var took = Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Faction) && l.EntityId == "a"));
        Assert.Contains("Vagos", took.ChangesJson);
        // the faction that lost a route gets its score recomputed
        await threat.Received().NewCalculateAsync("b", Arg.Any<CancellationToken>());
        Assert.NotNull(db.Factions.Single(f => f.Id == "b").StockRefreshedAt);
    }

    [Fact]
    public async Task Creating_a_faction_is_checked_the_same_way()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<DrugRouteConflictException>(
            () => svc.CreateAsync(Input("Families", "Kokain Nord"), Leader()));
        using (var db = ctx.NewContext())
        {
            // the transaction rolled back: no half-created faction
            Assert.Equal(2, db.Factions.Count());
        }

        var input = Input("Families", "Kokain Nord");
        input.ConfirmedRouteTakeovers = ["kokain nord"];
        var created = await svc.CreateAsync(input, Leader());

        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, created.Id));
        Assert.Empty(RoutesOf(ctx, "b"));
    }

    [Fact]
    public async Task A_route_written_twice_in_one_list_is_kept_once()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "a", name: "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RefreshAsync("a", Input("Ballas", "Kokain Nord", " kokain nord"), Leader());

        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "a"));
    }

    [Fact]
    public async Task An_old_double_is_settled_at_the_next_save()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord");
        using (var db = ctx.NewContext())
        {
            // from before the rule: two factions hold the route already
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "a", Designation = "Kokain Nord" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // saving "a" with its route unchanged still asks
        await Assert.ThrowsAsync<DrugRouteConflictException>(
            () => svc.RefreshAsync("a", Input("Ballas", "Kokain Nord"), Leader()));
        var input = Input("Ballas", "Kokain Nord");
        input.ConfirmedRouteTakeovers = ["Kokain Nord"];
        await svc.RefreshAsync("a", input, Leader());

        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "a"));
        Assert.Empty(RoutesOf(ctx, "b"));
    }

    // ---- who holds ----

    [Fact]
    public async Task A_faction_in_the_trash_holds_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord", b => b.IsDeleted = true);
        var svc = NewService(ctx);

        await svc.RefreshAsync("a", Input("Ballas", "Kokain Nord"), Leader());

        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "a"));
    }

    [Fact]
    public async Task An_archived_faction_holds_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord", b => b.IsArchived = true);
        var svc = NewService(ctx);

        await svc.RefreshAsync("a", Input("Ballas", "Kokain Nord"), Leader());

        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "a"));
        // its row stays for the record; coming back decides
        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "b"));
    }

    // ---- a holder out of reach ----

    [Fact]
    public async Task A_classified_holder_stays_unnamed_and_keeps_its_route()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord", b => b.IsClassified = true);
        var svc = NewService(ctx);
        var input = Input("Ballas", "Kokain Nord");
        input.ConfirmedRouteTakeovers = ["Kokain Nord"];

        var ex = await Assert.ThrowsAsync<DrugRouteConflictException>(() => svc.RefreshAsync("a", input, LowRank()));

        var conflict = Assert.Single(ex.Conflicts);
        Assert.Null(conflict.HolderId);
        Assert.Null(conflict.HolderName);
        Assert.False(conflict.MayTakeOver);
        Assert.DoesNotContain("Vagos", ex.Message);
        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "b"));
    }

    [Fact]
    public async Task A_classified_holder_is_not_named_in_the_takers_history()
    {
        using var ctx = new SqliteTestContext();
        SeedHolder(ctx, "Kokain Nord", b => b.IsClassified = true);
        var svc = NewService(ctx);
        var input = Input("Ballas", "Kokain Nord");
        input.ConfirmedRouteTakeovers = ["Kokain Nord"];

        // leadership may see and move it, but everyone who reads the open file reads its history
        await svc.RefreshAsync("a", input, Leader());

        using var db = ctx.NewContext();
        var took = Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Faction) && l.EntityId == "a"));
        Assert.DoesNotContain("Vagos", took.ChangesJson);
    }

    // ---- coming back ----

    [Fact]
    public async Task A_restored_faction_loses_a_route_another_faction_took_meanwhile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "a", name: "Ballas", configure: f => f.IsDeleted = true));
            db.Factions.Add(Seed.Faction(id: "b", name: "Vagos"));
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "a", Designation = "Kokain Nord" });
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "a", Designation = "Meth Süd" });
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "b", Designation = "kokain nord" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("a", Leader());

        Assert.Equal(["Meth Süd"], RoutesOf(ctx, "a"));
        Assert.Equal(["kokain nord"], RoutesOf(ctx, "b"));
        using var check = ctx.NewContext();
        var row = Assert.Single(check.AuditLogs.Where(l => l.EntityType == nameof(Faction) && l.EntityId == "a"));
        Assert.DoesNotContain("Vagos", row.ChangesJson);
    }

    [Fact]
    public async Task An_unarchived_faction_loses_a_route_another_faction_took_meanwhile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "a", name: "Ballas", configure: f => f.IsArchived = true));
            db.Factions.Add(Seed.Faction(id: "b", name: "Vagos"));
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "a", Designation = "Kokain Nord" });
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "b", Designation = "Kokain Nord" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.UnarchiveAsync("a", Leader());

        Assert.Empty(RoutesOf(ctx, "a"));
        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "b"));
    }

    [Fact]
    public async Task A_restored_but_still_archived_faction_keeps_its_routes_for_now()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "a", name: "Ballas", configure: f => { f.IsDeleted = true; f.IsArchived = true; }));
            db.Factions.Add(Seed.Faction(id: "b", name: "Vagos"));
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "a", Designation = "Kokain Nord" });
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "b", Designation = "Kokain Nord" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("a", Leader());

        // it holds nothing while archived; the unarchive settles it
        Assert.Equal(["Kokain Nord"], RoutesOf(ctx, "a"));
    }
}
