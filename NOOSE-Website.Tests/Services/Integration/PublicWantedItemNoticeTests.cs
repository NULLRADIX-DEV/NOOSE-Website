using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Vehicle and weapon notices: what they carry outside, and what the file behind them still decides.</summary>
public sealed class PublicWantedItemNoticeTests
{
    private const string PersonId = "p1";
    private const string VehicleId = "v1";
    private const string PlainVehicleId = "v2";
    private const string WeaponId = "w1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(PublicWantedService Service, IMemoryCache Cache, IDiscordWebhookService Discord);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{Guid.NewGuid().ToString()[..4]}");

        var discord = Substitute.For<IDiscordWebhookService>();
        var service = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            Substitute.For<INotificationService>(), new TipPriorityService(factory),
            discord, Substitute.For<IPressReleaseService>(), cache);
        return new Host(service, cache, discord);
    }

    /// <summary>Modules on, one clean file with a plated vehicle, an unplated one, a weapon and a known area.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in new[] { PublicModules.Wanted, PublicModules.WantedVehicles, PublicModules.WantedArchive })
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = true;
        }

        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            p.ThreatScore = 80;
        }));
        db.PersonVehicles.Add(new PersonVehicle
        {
            Id = VehicleId, PersonId = PersonId, Designation = "Bravado Banshee", LicensePlate = "4XYZ123",
        });
        db.PersonVehicles.Add(new PersonVehicle
        {
            Id = PlainVehicleId, PersonId = PersonId, Designation = "Roter Pickup",
        });
        db.PersonWeapons.Add(new PersonWeapon { Id = WeaponId, PersonId = PersonId, Text = "AP Pistol, graviert" });
        db.PersonLocations.Add(new PersonLocation { PersonId = PersonId, Text = "Sandy Shores" });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task ChargeAsync(SqliteTestContext ctx, string id, string html = "<p>Tatfahrzeug</p>")
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).ChargeHtml = html;
        await db.SaveChangesAsync();
    }

    private static async Task ClassifyAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        (await db.People.SingleAsync(p => p.Id == PersonId)).IsClassified = true;
        await db.SaveChangesAsync();
    }

    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, string key, bool on)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
        host.Cache.Remove("OeffentlicheFahndungen");
    }

    /// <summary>A published vehicle notice; returns its row id.</summary>
    private static async Task<string> PublishedVehicleAsync(SqliteTestContext ctx, Host host)
    {
        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await ChargeAsync(ctx, id);
        await host.Service.PublishAsync(id, null, Leader());
        return id;
    }

    // ---- what a draft pulls from the profile row ----

    [Fact]
    public async Task ADraftFromAVehicle_IsNamedAfterThePlateAndKeepsTheFile()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal(PublicWantedKind.Fahrzeug, row.Kind);
        Assert.Equal("4XYZ123", row.DisplayName);
        Assert.Equal("Bravado Banshee", row.VehicleText);
        // the file stays on the row: that is what the suppression belt and the timeline hang on
        Assert.Equal(PersonId, row.PersonId);
        Assert.Equal(PublicWantedStatus.Entwurf, row.Status);
    }

    [Fact]
    public async Task ADraftFromAVehicleWithoutAPlate_FallsBackToTheModelAndLeavesTheLineEmpty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.CreateDraftFromVehicleAsync(PlainVehicleId, Leader());

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal("Roter Pickup", row.DisplayName);
        // no second field repeating the headline
        Assert.Null(row.VehicleText);
    }

    [Fact]
    public async Task ADraftFromAWeapon_TakesItsDesignation()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.CreateDraftFromWeaponAsync(WeaponId, Leader());

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal(PublicWantedKind.Waffe, row.Kind);
        Assert.Equal("AP Pistol, graviert", row.DisplayName);
        Assert.Null(row.VehicleText);
    }

    [Fact]
    public async Task TheAccusation_IsNotPrefilledFromTheFile()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());

        await using var db = ctx.NewContext();
        // WantedReason is an allegation against the person and usually names her; publishing is refused until the
        // author has written what the vehicle itself is wanted for
        Assert.True(string.IsNullOrEmpty((await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).ChargeHtml));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task AMissingProfileRow_ReadsAsAMissingSource()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromVehicleAsync("weg", Leader()));
    }

    // ---- one notice per subject, and the manhunt is a subject of its own ----

    [Fact]
    public async Task AFile_CarriesAManhuntAndItsVehiclesAtTheSameTime()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.CreateDraftFromPersonAsync(PersonId, Leader());
        await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await host.Service.CreateDraftFromWeaponAsync(WeaponId, Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(3, await db.OeffentlicheFahndungen.CountAsync(f => f.PersonId == PersonId));
    }

    [Fact]
    public async Task AnAdvertisedPlate_DoesNotBlockTheManhunt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);

        var id = await host.Service.CreateDraftFromPersonAsync(PersonId, Leader());

        Assert.False(string.IsNullOrEmpty(id));
    }

    [Fact]
    public async Task TheSameVehicleTwice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader()));
    }

    // ---- what the outside gets ----

    [Fact]
    public async Task APublishedPlate_IsOnTheBoardWithoutANameAndWithoutAPicture()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal(PublicWantedKind.Fahrzeug, card.Kind);
        Assert.Equal("4XYZ123", card.DisplayName);
        Assert.False(card.HasPhoto);
        // the owner is on the row, never on the card
        Assert.DoesNotContain("Mustermann", card.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurningTheItemModuleOff_DropsThePlateAndLeavesThePersonStanding()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);
        var personId = await host.Service.CreateDraftFromPersonAsync(PersonId, Leader());
        await ChargeAsync(ctx, personId, "<p>Verdacht auf Waffenhandel</p>");
        await host.Service.PublishAsync(personId, null, Leader());

        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        var cards = (await host.Service.GetBoardAsync()).Cards;
        Assert.Single(cards);
        Assert.Equal(PublicWantedKind.Fahndung, cards[0].Kind);
    }

    [Fact]
    public async Task WithTheItemModuleOff_ThePlateProfileAndItsPictureAreGone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedVehicleAsync(ctx, host);
        string caseNumber;
        await using (var db = ctx.NewContext())
        {
            caseNumber = (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).CaseNumber!;
        }
        Assert.NotNull(await host.Service.GetByCaseNumberAsync(caseNumber));

        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        Assert.Null(await host.Service.GetByCaseNumberAsync(caseNumber));
        Assert.Null(await host.Service.GetPublishedPhotoAsync(caseNumber));
    }

    [Fact]
    public async Task ASeizedPlate_LeavesTheArchiveWithItsModule()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedVehicleAsync(ctx, host);
        await host.Service.CapturedAsync(id, Leader());
        Assert.Single(await host.Service.GetArchiveAsync());

        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    // ---- the belt and the photo ----

    [Fact]
    public async Task ClassifyingTheOwner_TakesThePlateOffline()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);
        Assert.Single((await host.Service.GetBoardAsync()).Cards);

        await ClassifyAsync(ctx);
        host.Cache.Remove("OeffentlicheFahndungen");

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task AClassifiedFile_RefusesAnItemDraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ClassifyAsync(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader()));
    }

    [Fact]
    public async Task APhotoOnAnItemNotice_IsRefusedRatherThanDropped()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await using (var db = ctx.NewContext())
        {
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "foto1", PersonId = PersonId, FileNameSaved = "a.jpg", ContentType = "image/jpeg",
            });
            await db.SaveChangesAsync();
        }

        // the editor offers no picker here, so a value can only come from a manipulated post — and the only photo
        // store in the house holds mugshots of the owner
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "4XYZ123", PhotoSourceId = "foto1" }, Leader()));
    }

    [Fact]
    public async Task TheEditor_OffersNoPhotoForAnItemNoticeButKeepsTheAreas()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await using (var db = ctx.NewContext())
        {
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "foto1", PersonId = PersonId, FileNameSaved = "a.jpg", ContentType = "image/jpeg",
            });
            await db.SaveChangesAsync();
        }

        var options = await host.Service.GetOptionsAsync(id, Leader());

        Assert.Empty(options.Photos);
        Assert.Contains("Sandy Shores", options.Areas);
    }

    // ---- the file page ----

    [Fact]
    public async Task TheWarningBanner_IgnoresAnItemNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);

        // the banner says this PERSON is publicly wanted, which an advertised plate does not make true
        Assert.Null(await host.Service.GetBannerForPersonAsync(PersonId));
        Assert.Null(await host.Service.GetForPersonAsync(PersonId, Leader()));
    }

    [Fact]
    public async Task TheItemPanel_ListsExactlyTheItemNotices()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.CreateDraftFromPersonAsync(PersonId, Leader());
        await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await host.Service.CreateDraftFromWeaponAsync(WeaponId, Leader());

        var rows = await host.Service.GetItemsForPersonAsync(PersonId, Leader());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(WantedKinds.IsItem(r.Kind)));
    }

    [Fact]
    public async Task TheSourceList_OffersEveryProfileRowAndMarksWhatIsAlreadyOut()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());

        var sources = await host.Service.GetItemSourcesAsync(PersonId, Leader());

        Assert.Equal(3, sources.Count);
        // carried and flagged rather than filtered out: an author has to see that a plate is already out
        Assert.True(sources.Single(o => o.Id == VehicleId).Advertised);
        Assert.False(sources.Single(o => o.Id == PlainVehicleId).Advertised);
        Assert.False(sources.Single(o => o.Id == WeaponId).Advertised);
        Assert.Equal("Bravado Banshee – 4XYZ123", sources.Single(o => o.Id == VehicleId).Label);
    }

    [Fact]
    public async Task APartner_ReadsNeitherTheItemNoticesNorTheirSources()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedVehicleAsync(ctx, host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.GetItemsForPersonAsync(PersonId, Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.GetItemSourcesAsync(PersonId, Partner()));
    }

    [Fact]
    public async Task ARecordThatDoesNotResolve_YieldsNoSources()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        // fail closed, like every other read that hangs off a file
        Assert.Empty(await host.Service.GetItemSourcesAsync("gibtsnicht", Leader()));
        Assert.Empty(await host.Service.GetItemsForPersonAsync("gibtsnicht", Leader()));
    }

    // ---- publishing answers to both switches ----

    [Fact]
    public async Task PublishingAPlate_WhileItsOwnModuleIsOff_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromVehicleAsync(VehicleId, Leader());
        await ChargeAsync(ctx, id);
        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        // otherwise the row would say published, the board would strip it, and the Discord post — which cannot be
        // recalled — would link to a 404
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Entwurf, (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).Status);
    }

    [Fact]
    public async Task APublishedVehicle_IsAnnouncedAsASachfahndungCardWithoutAnImage()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedVehicleAsync(ctx, host);

        var card = (WantedDiscordEmbed)host.Discord.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDiscordWebhookService.PushWantedCardAsync))
            .GetArguments()[1]!;
        Assert.Equal("🚗 Sachfahndung Fahrzeug: 4XYZ123", card.Title);
        Assert.Null(card.ImagePath);
        Assert.Null(card.ThumbnailPath);
        // the accusation of the notice stays on the page
        Assert.DoesNotContain("Tatfahrzeug", WantedCardText.Of(card), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingAPerson_IsUnaffectedByTheItemModule()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromPersonAsync(PersonId, Leader());
        await ChargeAsync(ctx, id, "<p>Verdacht auf Waffenhandel</p>");
        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        await host.Service.PublishAsync(id, null, Leader());

        Assert.Single((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task EditingALivePlate_WhileItsOwnModuleIsOff_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedVehicleAsync(ctx, host);
        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "9ABC456" }, Leader()));
    }

    [Fact]
    public async Task RetractingAPlate_WorksWhileItsOwnModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedVehicleAsync(ctx, host);
        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        // publishing needs a live module, taking something offline never does
        await host.Service.RetractAsync(id, "Fahrzeug sichergestellt", Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Zurueckgezogen,
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).Status);
    }
}
