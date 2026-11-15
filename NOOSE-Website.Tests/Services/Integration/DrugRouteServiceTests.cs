using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.DrugRoutes;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A drug route is its own file: one active file per route name, one faction at a time, stations in order.</summary>
public sealed class DrugRouteServiceTests
{
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static int _caseNo;

    private static DrugRouteService NewService(SqliteTestContext ctx, IThreatScoreService? threat = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-DR-2026-{Interlocked.Increment(ref _caseNo):0000}");
        var photos = Substitute.For<IDrugRoutePhotoStorageService>();
        photos.IsAllowedType(Arg.Any<string>()).Returns(true);
        photos.MaxBytes.Returns(10 * 1024 * 1024);
        photos.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"{Guid.NewGuid():N}.jpg");
        return new DrugRouteService(ctx.Factory, caseNo, Substitute.For<IPersonService>(),
            threat ?? Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(), photos);
    }

    private static void SeedFactions(SqliteTestContext ctx, Action<Faction>? vagos = null)
    {
        using var db = ctx.NewContext();
        db.Factions.Add(Seed.Faction(id: "a", name: "Ballas"));
        db.Factions.Add(Seed.Faction(id: "b", name: "Vagos", configure: vagos));
        db.SaveChanges();
    }

    private static DrugRouteInput Input(string designation, string? factionId = null)
        => new() { Designation = designation, FactionId = factionId };

    private static List<string?> FactionAuditChanges(SqliteTestContext ctx, string factionId)
    {
        using var db = ctx.NewContext();
        // the JSON is stored with \u escapes for umlauts; unescape so the assertions can name them
        return db.AuditLogs.Where(a => a.EntityType == nameof(Faction) && a.EntityId == factionId)
            .Select(a => a.ChangesJson).ToList()
            .Select(c => c is null ? null : System.Text.RegularExpressions.Regex.Unescape(c)).ToList();
    }

    // ---- one active file per route ----

    [Fact]
    public async Task A_second_file_for_the_same_route_is_refused_whatever_the_spelling()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var first = await svc.CreateAsync(Input("Kokain Nord"), Leader());

        var ex = await Assert.ThrowsAsync<DrugRouteDuplicateException>(
            () => svc.CreateAsync(Input("  kokain   NORD "), Leader()));

        Assert.Equal(first.Id, ex.ExistingId);
        Assert.Contains(first.CaseNumber, ex.Message);
        using var db = ctx.NewContext();
        Assert.Equal(1, db.DrugRoutes.Count());
    }

    [Fact]
    public async Task A_route_the_actor_cannot_see_is_named_without_a_link()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        await svc.CreateAsync(new DrugRouteInput { Designation = "Meth Süd", SecrecyLevel = DocumentClassification.Leadership }, Leader());

        var ex = await Assert.ThrowsAsync<DrugRouteDuplicateException>(
            () => svc.CreateAsync(Input("Meth Süd"), LowRank()));

        Assert.Null(ex.ExistingId);
        Assert.DoesNotContain("FIB-DR", ex.Message);
    }

    [Fact]
    public async Task An_archived_route_gives_its_name_free_and_cannot_come_back_while_it_is_taken()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var old = await svc.CreateAsync(Input("Kokain Nord"), Leader());
        await svc.ArchiveAsync(old.Id, "aufgegeben", Leader());

        var fresh = await svc.CreateAsync(Input("Kokain Nord"), Leader());
        Assert.NotEqual(old.Id, fresh.Id);

        await Assert.ThrowsAsync<DrugRouteDuplicateException>(() => svc.UnarchiveAsync(old.Id, Leader()));
        using var db = ctx.NewContext();
        Assert.True(db.DrugRoutes.Single(r => r.Id == old.Id).IsArchived);
    }

    [Fact]
    public async Task Renaming_onto_another_active_route_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        await svc.CreateAsync(Input("Kokain Nord"), Leader());
        var other = await svc.CreateAsync(Input("Meth Süd"), Leader());

        await Assert.ThrowsAsync<DrugRouteDuplicateException>(
            () => svc.RefreshAsync(other.Id, Input("KOKAIN NORD"), Leader()));
        // the own name in another spelling is fine
        await svc.RefreshAsync(other.Id, Input("meth süd"), Leader());

        using var db = ctx.NewContext();
        Assert.Equal("meth süd", db.DrugRoutes.Single(r => r.Id == other.Id).Designation);
    }

    // ---- one faction at a time ----

    [Fact]
    public async Task Moving_a_route_notes_both_factions_and_rescores_them()
    {
        using var ctx = new SqliteTestContext();
        SeedFactions(ctx);
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);
        var route = await svc.CreateAsync(Input("Kokain Nord", "b"), Leader());

        await svc.RefreshAsync(route.Id, Input("Kokain Nord", "a"), Leader());

        using (var db = ctx.NewContext())
        {
            Assert.Equal("a", db.DrugRoutes.Single().FactionId);
        }
        var gave = FactionAuditChanges(ctx, "b");
        Assert.Contains(gave, c => c!.Contains("Übergeben an") && c.Contains("Ballas"));
        var took = FactionAuditChanges(ctx, "a");
        Assert.Contains(took, c => c!.Contains("Drogenroute übernommen") && c.Contains("Vagos") && c.Contains("Kokain Nord"));
        await threat.Received().NewCalculateAsync("a", Arg.Any<CancellationToken>());
        await threat.Received().NewCalculateAsync("b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_classified_faction_stays_unnamed_in_the_other_history()
    {
        using var ctx = new SqliteTestContext();
        SeedFactions(ctx, vagos: f => f.SecrecyLevel = DocumentClassification.Leadership);
        var svc = NewService(ctx);
        var route = await svc.CreateAsync(Input("Kokain Nord", "b"), Leader());

        await svc.RefreshAsync(route.Id, Input("Kokain Nord", "a"), Leader());

        var took = FactionAuditChanges(ctx, "a");
        Assert.Contains(took, c => c!.Contains("einer anderen Fraktion"));
        Assert.DoesNotContain(took, c => c!.Contains("Vagos"));
    }

    [Fact]
    public async Task A_classified_route_stays_unnamed_in_the_faction_history()
    {
        using var ctx = new SqliteTestContext();
        SeedFactions(ctx);
        var svc = NewService(ctx);

        await svc.CreateAsync(new DrugRouteInput
        {
            Designation = "Geheimroute",
            FactionId = "a",
            SecrecyLevel = DocumentClassification.Leadership,
        }, Leader());

        var notes = FactionAuditChanges(ctx, "a");
        Assert.Contains(notes, c => c!.Contains("eine eingestufte Route"));
        Assert.DoesNotContain(notes, c => c!.Contains("Geheimroute"));
    }

    [Fact]
    public async Task A_faction_the_actor_cannot_see_cannot_be_assigned()
    {
        using var ctx = new SqliteTestContext();
        SeedFactions(ctx, vagos: f => f.SecrecyLevel = DocumentClassification.Leadership);
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input("Kokain Nord", "b"), LowRank()));
        using var db = ctx.NewContext();
        Assert.Empty(db.DrugRoutes);
    }

    [Fact]
    public async Task The_faction_file_lists_only_routes_the_viewer_may_see()
    {
        using var ctx = new SqliteTestContext();
        SeedFactions(ctx);
        var svc = NewService(ctx);
        await svc.CreateAsync(Input("Kokain Nord", "a"), Leader());
        await svc.CreateAsync(new DrugRouteInput { Designation = "Geheimroute", FactionId = "a", SecrecyLevel = DocumentClassification.Leadership }, Leader());

        var low = await svc.GetForFactionAsync("a", ViewerScope.From(LowRank()));
        var lead = await svc.GetForFactionAsync("a", ViewerScope.From(Leader()));

        Assert.Equal(["Kokain Nord"], low.Select(r => r.Designation));
        Assert.Equal(2, lead.Count);
    }

    // ---- stations and evidence ----

    [Fact]
    public async Task Stations_keep_their_order_and_move_by_one()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var route = await svc.CreateAsync(Input("Kokain Nord"), Leader());
        await svc.StationAddAsync(route.Id, new() { Kind = DrugRouteStationKind.Gathering, Location = "Feld" }, Leader());
        await svc.StationAddAsync(route.Id, new() { Kind = DrugRouteStationKind.Processing, Location = "Labor" }, Leader());
        var sale = await svc.StationAddAsync(route.Id, new() { Kind = DrugRouteStationKind.Sale, Location = "Strand" }, Leader());

        await svc.StationMoveAsync(sale.Id, -1, Leader());
        await svc.StationMoveAsync(sale.Id, -1, Leader());
        await svc.StationMoveAsync(sale.Id, -1, Leader());

        var order = (await svc.GetStationsAsync(route.Id, ViewerScope.From(Leader()))).Select(s => s.Location);
        Assert.Equal(["Strand", "Feld", "Labor"], order);
    }

    [Fact]
    public async Task A_removed_station_hands_its_photos_to_the_gallery()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var route = await svc.CreateAsync(Input("Kokain Nord"), Leader());
        var station = await svc.StationAddAsync(route.Id, new() { Location = "Labor" }, Leader());
        var photo = await svc.PhotoAddAsync(route.Id, station.Id, "Eingang", new MemoryStream([1]), "a.jpg", "image/jpeg", 1, Leader());

        await svc.StationRemoveAsync(station.Id, Leader());

        using var db = ctx.NewContext();
        var kept = db.DrugRoutePhotos.Single();
        Assert.Equal(photo.Id, kept.Id);
        Assert.Null(kept.StationId);
        Assert.Equal("Eingang", kept.Caption);
        Assert.True(kept.IsTitleImage);
    }

    [Fact]
    public async Task A_photo_cannot_be_pinned_to_another_routes_station()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var a = await svc.CreateAsync(Input("Kokain Nord"), Leader());
        var b = await svc.CreateAsync(Input("Meth Süd"), Leader());
        var foreign = await svc.StationAddAsync(b.Id, new() { Location = "Labor" }, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PhotoAddAsync(
            a.Id, foreign.Id, null, new MemoryStream([1]), "a.jpg", "image/jpeg", 1, Leader()));
    }

    [Fact]
    public async Task A_classified_routes_photo_is_hidden_from_a_low_rank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var route = await svc.CreateAsync(new DrugRouteInput { Designation = "Geheimroute", SecrecyLevel = DocumentClassification.Leadership }, Leader());
        var photo = await svc.PhotoAddAsync(route.Id, null, null, new MemoryStream([1]), "a.jpg", "image/jpeg", 1, Leader());

        Assert.Null(await svc.GetPhotoWithRouteAsync(photo.Id, ViewerScope.From(LowRank())));
        Assert.NotNull(await svc.GetPhotoWithRouteAsync(photo.Id, ViewerScope.From(Leader())));
    }

    [Fact]
    public async Task A_low_rank_cannot_write_to_a_classified_route()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var route = await svc.CreateAsync(new DrugRouteInput { Designation = "Geheimroute", SecrecyLevel = DocumentClassification.Leadership }, Leader());

        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.StationAddAsync(route.Id, new() { Location = "Labor" }, LowRank()));
        using var db = ctx.NewContext();
        Assert.Empty(db.DrugRouteStations);
    }
}
