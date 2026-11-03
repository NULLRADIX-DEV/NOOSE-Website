using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Short address and poster code of a wanted notice: only what is outside anyway may lead anywhere.</summary>
public sealed class WantedShortLinkServiceTests
{
    private const string Live = "FIB-FA-2026-0042";
    private const string LiveCode = "2026-0042";
    private const string Vehicle = "FIB-FA-2026-0050";
    private const string Site = "https://demo.example.org";

    private sealed record Host(PublicWantedService Service, IMemoryCache Cache);

    private static Host NewHost(SqliteTestContext ctx, string? siteBaseUrl = Site)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(ctx.Factory, cache);
        var discord = Substitute.For<IDiscordWebhookService>();
        discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(false,
            siteBaseUrl ?? DiscordWebhookConfig.DefaultBaseUrl, new Dictionary<NotificationType, string?>(),
            new Dictionary<NotificationType, string?>(), true));

        var service = new PublicWantedService(ctx.Factory, modules, Substitute.For<ICaseNumberService>(),
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            Substitute.For<INotificationService>(), new TipPriorityService(ctx.Factory), discord,
            Substitute.For<IPressReleaseService>(), cache);
        return new Host(service, cache);
    }

    /// <summary>Wanted and the item notices on, tips off; one live person notice and one live vehicle notice.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var row in await db.OeffentlicheModule.ToListAsync())
        {
            row.IsEnabled = row.Key is PublicModules.Wanted or PublicModules.WantedVehicles or PublicModules.WantedArchive;
        }

        db.People.Add(Seed.Person("p1", "Max Mustermann", p => p.CaseNumber = "FIB-P-2026-0001"));
        db.People.Add(Seed.Person("p2", "Vera Verdeckt", p =>
        {
            p.CaseNumber = "FIB-P-2026-0002";
            p.IsClassified = true;
        }));
        db.People.Add(Seed.Person("p3", "Gerd Gelöscht", p =>
        {
            p.CaseNumber = "FIB-P-2026-0003";
            p.IsDeleted = true;
        }));

        db.OeffentlicheFahndungen.AddRange(
            Notice(Live, "p1", PublicWantedStatus.Veroeffentlicht),
            Notice(Vehicle, "p1", PublicWantedStatus.Veroeffentlicht, PublicWantedKind.Fahrzeug),
            Notice("FIB-FA-2026-0001", "p1", PublicWantedStatus.Entwurf),
            Notice("FIB-FA-2026-0002", "p1", PublicWantedStatus.Beantragt),
            Notice("FIB-FA-2026-0003", "p1", PublicWantedStatus.Zurueckgezogen),
            Notice("FIB-FA-2026-0004", "p1", PublicWantedStatus.Gefasst, captured: true),
            Notice("FIB-FA-2026-0005", "p1", PublicWantedStatus.Abgelaufen),
            Notice("FIB-FA-2026-0006", "p1", PublicWantedStatus.Veroeffentlicht, expired: true),
            Notice("FIB-FA-2026-0007", "p1", PublicWantedStatus.Veroeffentlicht, deleted: true),
            // suppression belt
            Notice("FIB-FA-2026-0008", "p2", PublicWantedStatus.Veroeffentlicht),
            Notice("FIB-FA-2026-0009", "p3", PublicWantedStatus.Veroeffentlicht));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static OeffentlicheFahndung Notice(string caseNumber, string personId, PublicWantedStatus status,
        PublicWantedKind kind = PublicWantedKind.Fahndung, bool captured = false, bool expired = false,
        bool deleted = false)
        => new()
        {
            CaseNumber = caseNumber,
            PersonId = personId,
            Status = status,
            Kind = kind,
            DisplayName = kind == PublicWantedKind.Fahrzeug ? "4XYZ123" : "Max Mustermann",
            ChargeHtml = "<p>Vorwurf</p>",
            PublishedAt = DateTime.UtcNow.AddDays(-1),
            CapturedAt = captured ? DateTime.UtcNow : null,
            ExpiresAt = expired ? DateTime.UtcNow.AddHours(-1) : null,
            IsDeleted = deleted,
        };

    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, string key, bool on)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
        host.Cache.Remove("OeffentlicheFahndungen");
    }

    // ---- the short route ----

    [Theory]
    [InlineData(LiveCode)]
    [InlineData("2026-42")]
    [InlineData("FA-2026-0042")]
    [InlineData(Live)]
    // printed before the rename
    [InlineData("NOOSE-FA-2026-0042")]
    [InlineData("noose-fa-2026-42")]
    public async Task AShortCodeOfALiveNotice_LeadsToItsProfile(string code)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Equal("/gesucht/" + Live, await host.Service.ResolveShortLinkAsync(code));
    }

    [Fact]
    public async Task AnOldPrintedCaseNumber_StillOpensTheProfileAndThePoster()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Equal(Live, (await host.Service.GetByCaseNumberAsync("NOOSE-FA-2026-0042"))!.CaseNumber);
        Assert.Equal("demo.example.org/gesucht/" + Live, (await host.Service.GetPosterLinkAsync("NOOSE-FA-2026-0042"))!.ProfileText);
    }

    [Theory]
    [InlineData("2026-0001")] // draft
    [InlineData("2026-0002")] // requested
    [InlineData("2026-0003")] // retracted
    [InlineData("2026-0004")] // captured
    [InlineData("2026-0005")] // expired by the worker
    [InlineData("2026-0006")] // expired, worker not yet run
    [InlineData("2026-0007")] // in the recycle bin
    [InlineData("2026-0008")] // classified file
    [InlineData("2026-0009")] // deleted file
    [InlineData("2026-0099")] // never minted
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public async Task EveryOtherState_IsTheSameMiss(string? code)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Null(await host.Service.ResolveShortLinkAsync(code));
        Assert.Null(await host.Service.GetPosterLinkAsync(WantedShortLink.CaseNumberFor(code)));
    }

    [Fact]
    public async Task WithTheBoardSwitchedOff_NothingResolves()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.Wanted, on: false);

        Assert.Null(await host.Service.ResolveShortLinkAsync(LiveCode));
        Assert.Null(await host.Service.ResolveShortLinkAsync("2026-0050"));
        Assert.Null(await host.Service.GetPosterLinkAsync(Live));
    }

    [Fact]
    public async Task AVehicleNotice_ResolvesWhileTheItemModuleIsOn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Equal("/gesucht/" + Vehicle, await host.Service.ResolveShortLinkAsync("2026-0050"));
        Assert.Equal("demo.example.org/g/2026-0050", (await host.Service.GetPosterLinkAsync(Vehicle))!.ShortText);

        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, on: false);

        Assert.Null(await host.Service.ResolveShortLinkAsync("2026-0050"));
        Assert.Null(await host.Service.GetPosterLinkAsync(Vehicle));
        // person notice stays
        Assert.Equal("/gesucht/" + Live, await host.Service.ResolveShortLinkAsync(LiveCode));
    }

    // ---- the poster ----

    [Fact]
    public async Task ThePoster_UsesTheConfiguredSite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var link = await host.Service.GetPosterLinkAsync(Live);

        Assert.NotNull(link);
        Assert.Equal("demo.example.org/g/" + LiveCode, link.ShortText);
        Assert.Equal("demo.example.org/gesucht/" + Live, link.ProfileText);
        Assert.Equal(WantedShortLink.Poster(Site, Live, tipsOn: false).QrSvg, link.QrSvg);
    }

    [Fact]
    public async Task ThePoster_InvitesATipOnlyWhileTheTipsModuleIsOn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var off = await host.Service.GetPosterLinkAsync(Live);
        await ModuleAsync(ctx, host, PublicModules.Tips, on: true);
        var on = await host.Service.GetPosterLinkAsync(Live);

        Assert.DoesNotContain("Hinweis", off!.Heading + off.Caption, StringComparison.Ordinal);
        Assert.Contains("Hinweis", on!.Heading + on.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKillSwitch_TakesThePosterCodeAndTheShortRouteOffline()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true" });
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        Assert.Null(await host.Service.ResolveShortLinkAsync(LiveCode));
        Assert.Null(await host.Service.GetPosterLinkAsync(Live));
    }
}
