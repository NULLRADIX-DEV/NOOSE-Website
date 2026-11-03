using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
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

/// <summary>The public figures: counted from published rows only, and silent rather than zero when they are not.</summary>
public sealed class PublicStatisticsServiceTests
{
    private const string PersonId = "p1";
    private const string ProfileId = "buerger1";

    private static readonly string[] AllModules =
    [
        PublicModules.Statistics, PublicModules.Wanted, PublicModules.WantedArchive,
        PublicModules.WantedVehicles, PublicModules.Tips, PublicModules.Reward,
    ];

    private sealed record Host(PublicStatisticsService Service, PublicWantedService Wanted, IMemoryCache Cache);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        // the real notice service, not a substitute: the wanted figures have to come through the suppression belt,
        // and a fake would prove nothing about the one thing this file is here to prove
        var wanted = new PublicWantedService(factory, modules, Substitute.For<ICaseNumberService>(),
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            Substitute.For<INotificationService>(), new TipPriorityService(factory),
            Substitute.For<IDiscordWebhookService>(), Substitute.For<IPressReleaseService>(), cache);

        return new Host(new PublicStatisticsService(factory, modules, wanted, cache), wanted, cache);
    }

    /// <summary>Every module on, one clean person file and one complete citizen profile.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in AllModules)
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = true;
        }

        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p => p.CaseNumber = "FIB-P-2026-0001"));
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId, UserId = "u1", FirstName = "Erika", LastName = "Musterfrau",
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static OeffentlicheFahndung Notice(string caseNumber, PublicWantedStatus status,
        Action<OeffentlicheFahndung>? tweak = null)
    {
        var row = new OeffentlicheFahndung
        {
            Id = Guid.NewGuid().ToString(),
            CaseNumber = caseNumber,
            Status = status,
            PersonId = PersonId,
            DisplayName = "Max Mustermann",
            PublishedAt = DateTime.UtcNow.AddDays(-1),
        };
        if (status == PublicWantedStatus.Gefasst)
        {
            row.CapturedAt = DateTime.UtcNow.AddHours(-1);
        }
        tweak?.Invoke(row);
        return row;
    }

    private static Hinweis Tip(TipStatus status, Action<Hinweis>? tweak = null)
    {
        var row = new Hinweis
        {
            Id = Guid.NewGuid().ToString(),
            CaseNumber = "FIB-H-2026-" + Guid.NewGuid().ToString()[..4],
            CitizenProfileId = ProfileId,
            Text = "Ich habe die gesuchte Person am Hafen gesehen.",
            Status = status,
        };
        tweak?.Invoke(row);
        return row;
    }

    // The published tip and payout figures sit on a fixed roleplay baseline. These tests are about what gets counted
    // on top of it, so they name the offset rather than restating it — retuning the baseline must not rewrite them.
    private static int Received(int counted) => PublicStatisticsService.TipsReceivedBaseline + counted;

    private static int Confirmed(int counted) => PublicStatisticsService.TipsConfirmedBaseline + counted;

    private static int Captures(int counted) => PublicStatisticsService.TipsCaptureBaseline + counted;

    private static decimal Paid(decimal counted) => PublicStatisticsService.RewardsPaidBaseline + counted;

    private static async Task AddAsync(SqliteTestContext ctx, params object[] rows)
    {
        await using var db = ctx.NewContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, string key, bool on)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
        host.Cache.Remove("OeffentlicheFahndungen");
        host.Cache.Remove("OeffentlicheZahlen");
    }

    // --- the module decides whether a figure exists at all --------------------------------------------------------

    [Fact]
    public async Task ModuleOff_PublishesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FA-1", PublicWantedStatus.Veroeffentlicht), Tip(TipStatus.Neu));
        await ModuleAsync(ctx, host, PublicModules.Statistics, false);

        var numbers = await host.Service.GetPublishedAsync();

        Assert.False(numbers.HasAny);
        Assert.Null(numbers.OpenNotices);
        Assert.Null(numbers.TipsReceived);
    }

    [Fact]
    public async Task TheKillSwitch_PublishesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FA-1", PublicWantedStatus.Veroeffentlicht));
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true" });
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        Assert.False((await host.Service.GetPublishedAsync()).HasAny);
    }

    [Fact]
    public async Task TheWantedModuleOff_SaysNothingRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FA-1", PublicWantedStatus.Veroeffentlicht), Tip(TipStatus.Neu));
        await ModuleAsync(ctx, host, PublicModules.Wanted, false);

        var numbers = await host.Service.GetPublishedAsync();

        // "0 laufende Fahndungen" would be a claim the agency has not made; a switched-off module makes none
        Assert.Null(numbers.OpenNotices);
        Assert.Equal(Received(1), numbers.TipsReceived);
    }

    [Fact]
    public async Task TheArchiveModuleOff_SaysNothingRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FA-1", PublicWantedStatus.Gefasst));
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, false);

        Assert.Null((await host.Service.GetPublishedAsync()).CapturedNotices);
    }

    [Fact]
    public async Task TheTipModuleOff_SaysNothingRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Bestaetigt));
        await ModuleAsync(ctx, host, PublicModules.Tips, false);

        var numbers = await host.Service.GetPublishedAsync();

        Assert.Null(numbers.TipsReceived);
        Assert.Null(numbers.TipsConfirmed);
        Assert.Null(numbers.TipsLedToCapture);
    }

    [Fact]
    public async Task TheRewardModuleOff_SaysNothingRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.Reward, false);

        Assert.Null((await host.Service.GetPublishedAsync()).RewardsPaid);
    }

    // --- nothing unpublished ever reaches a figure ----------------------------------------------------------------

    [Fact]
    public async Task OnlyPublishedNoticesAreCounted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx,
            Notice("FA-1", PublicWantedStatus.Veroeffentlicht),
            Notice("FA-2", PublicWantedStatus.Entwurf),
            Notice("FA-3", PublicWantedStatus.Beantragt),
            Notice("FA-4", PublicWantedStatus.Zurueckgezogen),
            Notice("FA-5", PublicWantedStatus.Veroeffentlicht, f => f.ExpiresAt = DateTime.UtcNow.AddDays(-1)),
            Notice("FA-6", PublicWantedStatus.Veroeffentlicht, f => f.IsDeleted = true),
            // retracted after a capture: RetractAsync leaves CapturedAt set, so the status clause on the counting
            // query is the only thing keeping this row out of the captured figure
            Notice("FA-7", PublicWantedStatus.Zurueckgezogen, f => f.CapturedAt = DateTime.UtcNow.AddHours(-2)),
            Notice("FA-8", PublicWantedStatus.Gefasst));

        var numbers = await host.Service.GetPublishedAsync();

        Assert.Equal(1, numbers.OpenNotices);
        Assert.Equal(1, numbers.CapturedNotices);
    }

    [Fact]
    public async Task ANoticeWhoseFileWasSuppressedIsNotCounted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FA-1", PublicWantedStatus.Veroeffentlicht),
            Notice("FA-2", PublicWantedStatus.Gefasst));
        Assert.Equal(1, (await host.Service.GetPublishedAsync()).OpenNotices);

        // the same belt the board itself runs on: a deleted file takes its notice out of every public surface
        await using (var db = ctx.NewContext())
        {
            (await db.People.SingleAsync(p => p.Id == PersonId)).IsDeleted = true;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheFahndungen");
        host.Cache.Remove("OeffentlicheZahlen");

        var numbers = await host.Service.GetPublishedAsync();
        Assert.Equal(0, numbers.OpenNotices);
        Assert.Equal(0, numbers.CapturedNotices);
    }

    [Fact]
    public async Task ASoftDeletedTipIsNotCounted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Neu), Tip(TipStatus.Neu, h => h.IsDeleted = true));

        // a submission the agency removed is not a submission it received
        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);
    }

    // --- the figures themselves ------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmedIncludesTheOnesThatLedToACapture()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx,
            Tip(TipStatus.Neu), Tip(TipStatus.InPruefung), Tip(TipStatus.Verworfen),
            Tip(TipStatus.Bestaetigt), Tip(TipStatus.Bestaetigt),
            Tip(TipStatus.FuehrteZurErgreifung));

        var numbers = await host.Service.GetPublishedAsync();

        Assert.Equal(Received(6), numbers.TipsReceived);
        // the band labels it "davon", so the smaller figure has to be contained in the larger one — which also has to
        // survive the baseline, hence three offsets that keep the funnel in order rather than one shared number
        Assert.Equal(Confirmed(3), numbers.TipsConfirmed);
        Assert.Equal(Captures(1), numbers.TipsLedToCapture);
        Assert.True(numbers.TipsReceived >= numbers.TipsConfirmed);
        Assert.True(numbers.TipsConfirmed >= numbers.TipsLedToCapture);
    }

    [Fact]
    public async Task TheRewardFigureSumsEveryPayout()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var tip = Tip(TipStatus.FuehrteZurErgreifung);
        await AddAsync(ctx, tip);
        await AddAsync(ctx,
            new HinweisBelohnung { ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "s1", Amount = 2500m },
            new HinweisBelohnung { ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "s2", Amount = 1500m });

        Assert.Equal(Paid(4000m), (await host.Service.GetPublishedAsync()).RewardsPaid);
    }

    [Fact]
    public async Task TheRewardFigureCountsTheAmountShownAtACapture()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var tip = Tip(TipStatus.FuehrteZurErgreifung);
        await AddAsync(ctx, tip, Notice("FIB-FA-2026-0002", PublicWantedStatus.Gefasst,
            n => n.PublicPaidOut = 500_000m));
        await AddAsync(ctx,
            new HinweisBelohnung { ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "s1", Amount = 4000m });

        // the roleplay figure and the real payouts land in the same published number
        Assert.Equal(Paid(504_000m), (await host.Service.GetPublishedAsync()).RewardsPaid);
    }

    [Fact]
    public async Task TheAmountShownAtACapture_IsWithheldWithTheRewardModule()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Notice("FIB-FA-2026-0002", PublicWantedStatus.Gefasst,
            n => n.PublicPaidOut = 500_000m));
        await ModuleAsync(ctx, host, PublicModules.Reward, false);

        // it has to sit inside the gated figure, not be added on after the gate
        Assert.Null((await host.Service.GetPublishedAsync()).RewardsPaid);
    }

    [Fact]
    public async Task ADeletedNotice_TakesTheAmountItShowedWithIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = Notice("FIB-FA-2026-0002", PublicWantedStatus.Gefasst, n => n.PublicPaidOut = 500_000m);
        await AddAsync(ctx, notice);
        await using (var db = ctx.NewContext())
        {
            // set by hand: this host runs without the interceptors, so Remove would be a hard delete
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == notice.Id)).IsDeleted = true;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheZahlen");

        Assert.Equal(Paid(0m), (await host.Service.GetPublishedAsync()).RewardsPaid);
    }

    [Fact]
    public async Task NoPayout_IsTheBaselineRatherThanSilence()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var numbers = await host.Service.GetPublishedAsync();

        // the module is on, so a figure is published rather than withheld; nothing counted on top leaves the baseline
        Assert.Equal(Paid(0m), numbers.RewardsPaid);
        // the point of the baseline: a fresh installation still shows a seven-figure record on the start page
        Assert.True(numbers.RewardsPaid >= 1_000_000m, $"Sockelbetrag unter einer Million: {numbers.RewardsPaid}");
    }

    [Fact]
    public async Task TheCapturedFigureIsNotCappedByTheArchiveList()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 105; i++)
            {
                db.OeffentlicheFahndungen.Add(Notice($"FA-{i:0000}", PublicWantedStatus.Gefasst));
            }
            await db.SaveChangesAsync();
        }

        // the list is capped at the newest hundred for page weight; a counter that stopped there would read as
        // completeness, which is the whole reason it is counted apart from it
        Assert.Equal(100, (await host.Wanted.GetArchiveAsync()).Count);
        Assert.Equal(105, (await host.Service.GetPublishedAsync()).CapturedNotices);
    }

    [Fact]
    public async Task TheItemModuleOff_TakesTheItemCapturesOutOfTheFigures()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx,
            Notice("FA-1", PublicWantedStatus.Veroeffentlicht),
            Notice("FA-2", PublicWantedStatus.Veroeffentlicht, f => f.Kind = PublicWantedKind.Fahrzeug),
            Notice("FA-3", PublicWantedStatus.Gefasst),
            Notice("FA-4", PublicWantedStatus.Gefasst, f => f.Kind = PublicWantedKind.Fahrzeug));
        var before = await host.Service.GetPublishedAsync();
        Assert.Equal(2, before.OpenNotices);
        Assert.Equal(2, before.CapturedNotices);

        await ModuleAsync(ctx, host, PublicModules.WantedVehicles, false);

        // a figure that still counted the plates would disagree with the board and the archive it describes
        var after = await host.Service.GetPublishedAsync();
        Assert.Equal(1, after.OpenNotices);
        Assert.Equal(1, after.CapturedNotices);
    }

    // --- failure and caching ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnreachableDatabase_SaysNothingAboutTipsAndRewards()
    {
        var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Bestaetigt));
        // warm the module snapshot so the failure lands on the counting, not on the switch board
        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);
        host.Cache.Remove("OeffentlicheZahlen");
        host.Cache.Remove("OeffentlicheFahndungen");

        ctx.Dispose();

        var numbers = await host.Service.GetPublishedAsync();
        // the baseline must not survive on its own here: a figure nobody could count is still a figure nobody knows,
        // and publishing the bare offset would turn "we cannot say" into a number
        Assert.Null(numbers.TipsReceived);
        Assert.Null(numbers.RewardsPaid);
        // the wanted figures describe what the board is showing, and an unreadable board shows nothing
        Assert.Equal(0, numbers.OpenNotices);
    }

    [Fact]
    public async Task AFailedCountIsNotCached()
    {
        var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Neu));

        // renamed rather than dropped, so the rows come back with the table
        await using (var db = ctx.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Hinweise RENAME TO Hinweise_weg");
        }
        Assert.Null((await host.Service.GetPublishedAsync()).TipsReceived);

        await using (var db = ctx.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Hinweise_weg RENAME TO Hinweise");
        }

        // a hole must not survive a cache window: the next visitor counts again rather than reading the failure
        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);
        ctx.Dispose();
    }

    [Fact]
    public async Task AModuleSwitchIsSeenBeforeTheFigureWindowDrops()
    {
        // The counts are cached; the switches are not. Reading a switch inside the figure cache would leave the start
        // page silent for a whole window after someone turns a module on, which is why the gate sits outside it.
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Neu));
        await ModuleAsync(ctx, host, PublicModules.Tips, false);
        Assert.Null((await host.Service.GetPublishedAsync()).TipsReceived);

        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Tips)).IsEnabled = true;
            await db.SaveChangesAsync();
        }
        // only the switch snapshot, deliberately not the figures: the warm counts must still answer
        host.Cache.Remove("OeffentlicheModule");

        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);
    }

    [Fact]
    public async Task TheFiguresAreCachedUntilTheWindowDrops()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await AddAsync(ctx, Tip(TipStatus.Neu));
        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);

        await AddAsync(ctx, Tip(TipStatus.Neu));
        Assert.Equal(Received(1), (await host.Service.GetPublishedAsync()).TipsReceived);

        host.Cache.Remove("OeffentlicheZahlen");
        Assert.Equal(Received(2), (await host.Service.GetPublishedAsync()).TipsReceived);
    }
}
