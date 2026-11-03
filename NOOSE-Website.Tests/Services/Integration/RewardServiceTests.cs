using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="RewardService"/>: no money without the status changes, and none the other way round.</summary>
/// <remarks>
/// The money assertions go through <see cref="KassenService"/> and the share table rather than through the reward rows
/// alone, because the whole point of the phase is that the two cannot disagree.
/// </remarks>
public sealed class RewardServiceTests
{
    private const string PersonId = "p1";
    private const string CitizenUserId = "buerger1";
    private const string ProfileId = "profil1";
    private const string OtherUserId = "buerger2";
    private const string OtherProfileId = "profil2";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    /// <summary>Rank 3: commits agency money directly, but is not leadership.</summary>
    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen(string id = CitizenUserId)
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Civilian).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        RewardService Reward,
        BountyService Bounty,
        TipService Tips,
        PublicWantedService Wanted,
        KassenService Kasse,
        IMemoryCache Cache,
        TestDbContextFactory Factory);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        // stubbed because the real one issues MySQL-only raw SQL; it counts up, or the receipt index would collide
        var seq = 0;
        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{++seq:0000}");

        var notifications = Substitute.For<INotificationService>();
        var tipPriority = new TipPriorityService(factory);
        var wanted = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            notifications, tipPriority, Substitute.For<IDiscordWebhookService>(),
            Substitute.For<IPressReleaseService>(), cache);
        var kasse = new KassenService(factory, caseNumbers);
        var bounty = new BountyService(factory, wanted, modules, kasse, notifications, tipPriority,
            Substitute.For<IDiscordWebhookService>());
        var buerger = new BuergerService(factory);

        var storage = Substitute.For<ITipAttachmentStorageService>();
        var tips = new TipService(factory, modules, buerger, wanted, caseNumbers, storage, notifications,
            tipPriority, new PublicTemplateService(factory), Substitute.For<IDiscordWebhookService>(),
            new TipsBroadcaster());
        var reward = new RewardService(factory, kasse, caseNumbers, tips, buerger, wanted, tipPriority, modules);
        return new Host(reward, bounty, tips, wanted, kasse, cache, factory);
    }

    /// <summary>Modules on, one clean person file, two complete citizen profiles.</summary>
    private static async Task<SqliteTestContext> SeededAsync(bool rewardOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in new[] { PublicModules.Wanted, PublicModules.Bounty, PublicModules.Tips, PublicModules.Reward })
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == key);
            row.IsEnabled = key != PublicModules.Reward || rewardOn;
        }

        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-9001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            p.ThreatScore = 80;
        }));
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId, UserId = CitizenUserId, FirstName = "Erika", LastName = "Musterfrau",
        });
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = OtherProfileId, UserId = OtherUserId, FirstName = "Klaus", LastName = "Zweitmeldung",
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    // ---- scenario helpers ----

    private static async Task<(string Id, string CaseNumber)> PublishedAsync(Host host)
    {
        var id = await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());
        await host.Wanted.PublishAsync(id, null, Leader());
        await using var db = host.Factory.CreateDbContext();
        var caseNumber = await db.OeffentlicheFahndungen.Where(f => f.Id == id)
            .Select(f => f.CaseNumber!).SingleAsync();
        return (id, caseNumber);
    }

    /// <summary>Files a tip on the notice and moves it out of Neu — a fresh tip is deliberately not payable.</summary>
    private static async Task<string> WorkedTipAsync(Host host, string? noticeCaseNumber, bool anonymous = false,
        ClaimsPrincipal? citizen = null)
    {
        var caseNumber = await host.Tips.SubmitAsync(new TipInput
        {
            Text = "Ich habe die gesuchte Person gestern Abend am Hafen gesehen, sie stieg in einen weißen Van.",
            WantsAnonymity = anonymous,
            WantedCaseNumber = noticeCaseNumber,
        }, null, null, null, citizen ?? Citizen());

        await using var db = host.Factory.CreateDbContext();
        var id = await db.Hinweise.Where(h => h.CaseNumber == caseNumber).Select(h => h.Id).SingleAsync();
        await host.Tips.AssignSelfAsync(id, Leader());
        return id;
    }

    private static Task FundAsync(Host host, KassenKonto account, decimal amount)
        => host.Kasse.BookAsync(new KassenBuchungInput
        {
            Account = account,
            Kind = KassenBuchungArt.Einzahlung,
            Amount = amount,
            Reason = "Startkapital",
            Timestamp = DateTime.UtcNow,
        }, Leader());

    private static RewardPayoutInput Split(string wantedId, params (string TipId, decimal Amount)[] tips)
        => new()
        {
            WantedId = wantedId,
            Tips = tips.Select(t => new RewardTipAmount { TipId = t.TipId, Amount = t.Amount }).ToList(),
        };

    /// <summary>The same split, but the operator names the account the missing money comes out of.</summary>
    private static RewardPayoutInput ToppedUp(KassenKonto account, string wantedId,
        params (string TipId, decimal Amount)[] tips)
    {
        var input = Split(wantedId, tips);
        input.TopUpAccount = account;
        return input;
    }

    private static async Task<List<FahndungKopfgeldAnteil>> SharesAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        return await db.FahndungKopfgeldAnteile.AsNoTracking().ToListAsync();
    }

    private static async Task<List<KassenBuchung>> BookingsAsync(SqliteTestContext ctx, KassenBuchungArt kind)
    {
        await using var db = ctx.NewContext();
        return await db.KassenBuchungen.AsNoTracking().Where(b => b.Kind == kind).ToListAsync();
    }

    private static async Task<List<HinweisBelohnung>> RewardsAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        return await db.HinweisBelohnungen.AsNoTracking().ToListAsync();
    }

    private static async Task<TipStatus> TipStatusAsync(SqliteTestContext ctx, string tipId)
    {
        await using var db = ctx.NewContext();
        return await db.Hinweise.Where(h => h.Id == tipId).Select(h => h.Status).SingleAsync();
    }

    // ---- the happy path ----

    [Fact]
    public async Task Agency_money_is_booked_out_and_everything_is_closed()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        var receipts = await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        var receipt = Assert.Single(receipts);
        Assert.StartsWith("FIB-BEL-", receipt, StringComparison.Ordinal);

        var payout = Assert.Single(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
        Assert.Equal(50_000m, payout.Amount);
        Assert.Equal(KassenKonto.Gruengeld, payout.Account);
        Assert.Equal(50_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));

        Assert.All(await SharesAsync(ctx), s => Assert.Equal(BountyShareStatus.Ausgezahlt, s.Status));
        Assert.Equal(TipStatus.FuehrteZurErgreifung, await TipStatusAsync(ctx, tip));
        Assert.Equal(0m, (await host.Bounty.GetSummaryAsync(id, Leader())).Advertised);

        var row = Assert.Single(await RewardsAsync(ctx));
        Assert.Equal(receipt, row.ReceiptNumber);
        Assert.NotNull(row.KassenBuchungId);
        Assert.Null(row.SelfPaidAt);
    }

    [Fact]
    public async Task Two_tipsters_each_get_their_own_receipt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var first = await WorkedTipAsync(host, caseNumber);
        var second = await WorkedTipAsync(host, caseNumber, citizen: Citizen(OtherUserId));
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        var receipts = await host.Reward.PayoutAsync(Split(id, (first, 30_000m), (second, 20_000m)), Leader());

        Assert.Equal(2, receipts.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(50_000m, (await RewardsAsync(ctx)).Sum(r => r.Amount));
        Assert.Equal(TipStatus.FuehrteZurErgreifung, await TipStatusAsync(ctx, first));
        Assert.Equal(TipStatus.FuehrteZurErgreifung, await TipStatusAsync(ctx, second));

        var own = await host.Reward.GetOwnAsync(Citizen());
        var mine = Assert.Single(own);
        Assert.Equal(30_000m, mine.Amount);
    }

    [Fact]
    public async Task A_pledged_private_share_is_handed_over_by_the_donor_without_a_booking()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 20_000m, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 20_000m)), Leader());

        Assert.Empty(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
        var row = Assert.Single(await RewardsAsync(ctx));
        Assert.Null(row.KassenBuchungId);
        Assert.NotNull(row.SelfPaidAt);
    }

    [Fact]
    public async Task A_secured_private_share_is_booked_out_of_its_account()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 20_000m, Leader());
        var share = (await SharesAsync(ctx)).Single();
        await host.Bounty.PayInAsync(share.Id, KassenKonto.Schwarzgeld, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 20_000m)), Leader());

        var payout = Assert.Single(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
        Assert.Equal(KassenKonto.Schwarzgeld, payout.Account);
        // in and straight back out again
        Assert.Equal(0m, await host.Kasse.GetBalanceAsync(KassenKonto.Schwarzgeld));
    }

    [Fact]
    public async Task Undrawn_money_is_settled_as_well_rather_than_left_advertised()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 10_000m)), Leader());

        Assert.All(await SharesAsync(ctx), s => Assert.Equal(BountyShareStatus.Ausgezahlt, s.Status));
        // the rest stays in the account; only 10.000 left it
        Assert.Equal(90_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
    }

    // ---- the free amount ----

    [Fact]
    public async Task More_than_the_bounty_is_topped_up_from_the_chosen_account()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 10_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await FundAsync(host, KassenKonto.Schwarzgeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(ToppedUp(KassenKonto.Schwarzgeld, id, (tip, 25_000m)), Leader());

        var shares = await SharesAsync(ctx);
        Assert.Equal(2, shares.Count);
        var topUp = shares.Single(s => s.Amount == 15_000m);
        Assert.Equal(BountyOrigin.NooseKasse, topUp.Origin);
        Assert.Equal(KassenKonto.Schwarzgeld, topUp.Account);
        // born paid, so it never counts towards the advertised sum for a single moment
        Assert.Equal(BountyShareStatus.Ausgezahlt, topUp.Status);

        Assert.Equal(25_000m, (await RewardsAsync(ctx)).Sum(r => r.Amount));
        Assert.Equal(90_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
        Assert.Equal(85_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Schwarzgeld));
    }

    [Fact]
    public async Task A_notice_without_a_bounty_can_still_be_rewarded()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(ToppedUp(KassenKonto.Gruengeld, id, (tip, 5_000m)), Leader());

        var share = Assert.Single(await SharesAsync(ctx));
        Assert.Equal(5_000m, share.Amount);
        Assert.Equal(BountyShareStatus.Ausgezahlt, share.Status);
        Assert.Equal(0m, (await host.Bounty.GetSummaryAsync(id, Leader())).Advertised);
        Assert.Equal(5_000m, Assert.Single(await RewardsAsync(ctx)).Amount);
        Assert.Equal(95_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
    }

    [Fact]
    public async Task A_second_payout_after_a_free_one_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        var first = await WorkedTipAsync(host, caseNumber);
        var second = await WorkedTipAsync(host, caseNumber, citizen: Citizen(OtherUserId));
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        await host.Reward.PayoutAsync(ToppedUp(KassenKonto.Gruengeld, id, (first, 5_000m)), Leader());

        // without a bounty there is no advertised share to settle, so the top-up is the idempotency token
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(ToppedUp(KassenKonto.Gruengeld, id, (second, 5_000m)), Leader()));

        Assert.Contains("bereits ausgezahlt", error.Message, StringComparison.Ordinal);
        Assert.Single(await RewardsAsync(ctx));
    }

    // ---- preconditions ----

    [Fact]
    public async Task A_notice_that_is_not_captured_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader()));

        Assert.Contains("gefasst", error.Message, StringComparison.Ordinal);
        Assert.Empty(await RewardsAsync(ctx));
        Assert.Empty(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
    }

    [Fact]
    public async Task An_anonymous_tip_without_a_resolution_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber, anonymous: true);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader()));

        Assert.Contains("Anonymität", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_tip_is_payable_once_the_promise_was_lifted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber, anonymous: true);
        await host.Tips.ResolveAnonymityAsync(tip, "Belohnung", Leader());
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        Assert.Single(await RewardsAsync(ctx));
    }

    [Fact]
    public async Task A_tip_of_another_notice_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        // a tip without any reference belongs to no notice at all
        var stray = await WorkedTipAsync(host, noticeCaseNumber: null);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (stray, 50_000m)), Leader()));

        Assert.Contains("gehört nicht", error.Message, StringComparison.Ordinal);
        Assert.Empty(await RewardsAsync(ctx));
    }

    [Fact]
    public async Task A_fresh_tip_has_to_be_worked_first()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var caseNo = await host.Tips.SubmitAsync(new TipInput
        {
            Text = "Die gesuchte Person wohnt seit einer Woche im Motel an der Route 68, Zimmer 4.",
            WantedCaseNumber = caseNumber,
        }, null, null, null, Citizen());
        string tip;
        await using (var db = ctx.NewContext())
        {
            tip = await db.Hinweise.Where(h => h.CaseNumber == caseNo).Select(h => h.Id).SingleAsync();
        }
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        // Neu -> FuehrteZurErgreifung is not a transition TipRules allows
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader()));

        var draft = await host.Reward.GetDraftAsync(id, Leader());
        Assert.Empty(draft.Payable);
        Assert.Single(draft.Blocked);
    }

    [Fact]
    public async Task A_second_payout_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var first = await WorkedTipAsync(host, caseNumber);
        var second = await WorkedTipAsync(host, caseNumber, citizen: Citizen(OtherUserId));
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        await host.Reward.PayoutAsync(Split(id, (first, 25_000m)), Leader());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (second, 25_000m)), Leader()));

        Assert.Contains("bereits ausgezahlt", error.Message, StringComparison.Ordinal);
        Assert.Single(await RewardsAsync(ctx));
    }

    [Fact]
    public async Task More_than_the_bounty_without_an_account_is_refused_and_changes_nothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 10_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 10_001m)), Leader()));

        Assert.Contains("Konto", error.Message, StringComparison.Ordinal);
        Assert.Empty(await RewardsAsync(ctx));
        Assert.All(await SharesAsync(ctx), s => Assert.Equal(BountyShareStatus.Zugesagt, s.Status));
        Assert.Equal(TipStatus.InPruefung, await TipStatusAsync(ctx, tip));
    }

    // ---- the rollback ----

    [Fact]
    public async Task A_payout_the_account_cannot_cover_leaves_nothing_behind()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        // deliberately underfunded: the cash service refuses a withdrawal into the negative
        await FundAsync(host, KassenKonto.Gruengeld, 10_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader()));

        Assert.Empty(await RewardsAsync(ctx));
        Assert.Empty(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
        Assert.All(await SharesAsync(ctx), s => Assert.Equal(BountyShareStatus.Zugesagt, s.Status));
        Assert.Equal(TipStatus.InPruefung, await TipStatusAsync(ctx, tip));
        Assert.Equal(10_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
    }

    // ---- rights ----

    [Fact]
    public async Task A_senior_agent_without_leadership_may_not_pay_out()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Senior());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Senior()));
    }

    [Fact]
    public async Task The_read_only_supervision_may_not_pay_out()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        // the guard refuses before the ReadOnlyBarrierInterceptor would, so no receipt number is ever minted
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), OnlyReader()));
    }

    [Fact]
    public async Task A_citizen_may_not_pay_out()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await host.Wanted.CapturedAsync(id, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Citizen()));
    }

    // ---- what the cash book says ----

    [Fact]
    public async Task The_booking_reason_names_case_numbers_and_no_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        // every agent reads the ledger; a name in it would be the anonymity promise circumvented
        var payout = Assert.Single(await BookingsAsync(ctx, KassenBuchungArt.Auszahlung));
        Assert.DoesNotContain("Erika", payout.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Musterfrau", payout.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FIB-H-", payout.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(caseNumber, payout.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the receipt ----

    [Fact]
    public async Task The_receipt_is_readable_by_its_owner_and_by_leadership_and_by_nobody_else()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        var receipt = (await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader())).Single();

        var own = await host.Reward.GetReceiptAsync(receipt, Citizen());
        Assert.NotNull(own);
        Assert.Equal("Erika Musterfrau", own!.RecipientName);
        Assert.Equal(50_000m, own.Amount);
        Assert.Equal(caseNumber, own.WantedCaseNumber);

        Assert.NotNull(await host.Reward.GetReceiptAsync(receipt, Leader()));
        Assert.Null(await host.Reward.GetReceiptAsync(receipt, Citizen(OtherUserId)));
        Assert.Null(await host.Reward.GetReceiptAsync(receipt, Senior()));
        Assert.Null(await host.Reward.GetReceiptAsync("FIB-BEL-2026-9999", Citizen()));
    }

    [Fact]
    public async Task A_legacy_NOOSE_receipt_number_still_opens_the_receipt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        var receipt = (await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader())).Single();

        var own = await host.Reward.GetReceiptAsync(receipt.Replace("FIB-", "NOOSE-", StringComparison.Ordinal), Citizen());

        Assert.Equal(receipt, own!.ReceiptNumber);
        Assert.Null(await host.Reward.GetReceiptAsync("NOOSE-BEL-2026-9999", Citizen()));
    }

    [Fact]
    public async Task The_module_gates_the_citizen_view_but_never_the_payout()
    {
        using var ctx = await SeededAsync(rewardOn: false);
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        // money moves regardless: the switch is about what the public area shows, not about the cash book
        var receipt = (await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader())).Single();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Reward.GetOwnAsync(Citizen()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Reward.GetReceiptAsync(receipt, Citizen()));
        Assert.Single(await host.Reward.GetForNoticeAsync(id, Leader()));
    }

    [Fact]
    public async Task The_kill_switch_does_not_reach_into_the_private_account_area()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        var receipt = (await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader())).Single();

        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true",
            });
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        // the switch takes public content offline; a receipt is the private content of one signed-in citizen
        Assert.NotNull(await host.Reward.GetReceiptAsync(receipt, Citizen()));
        Assert.Single(await host.Reward.GetOwnAsync(Citizen()));
    }

    // ---- what it does to the tipster ----

    [Fact]
    public async Task The_rewarded_tip_counts_towards_the_trust_tier()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());

        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        await using var db = ctx.NewContext();
        var confirmed = await db.BuergerProfile.Where(p => p.Id == ProfileId)
            .Select(p => p.ConfirmedTips).SingleAsync();
        Assert.Equal(1, confirmed);
    }

    [Fact]
    public async Task The_citizen_is_told_in_the_thread_without_an_agent_on_the_line()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        var receipt = (await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader())).Single();

        await using var db = ctx.NewContext();
        var message = await db.HinweisNachrichten.AsNoTracking()
            .Where(m => m.HinweisId == tip && m.Audience == TipMessageAudience.Buerger)
            .SingleAsync();
        Assert.Null(message.AuthorAgentId);
        Assert.Contains(receipt, message.Text, StringComparison.Ordinal);
    }

    // ---- the internal panel ----

    [Fact]
    public async Task The_draft_separates_payable_tips_from_the_reasons_they_are_not()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 30_000m, KassenKonto.Gruengeld, null, Leader());
        await host.Bounty.AddPrivateAsync(id, 20_000m, Leader());
        var payable = await WorkedTipAsync(host, caseNumber);
        await WorkedTipAsync(host, caseNumber, anonymous: true, citizen: Citizen(OtherUserId));
        await host.Wanted.CapturedAsync(id, Leader());

        var draft = await host.Reward.GetDraftAsync(id, Leader());

        Assert.True(draft.IsCaptured);
        Assert.Equal(50_000m, draft.Available);
        Assert.Equal(30_000m, draft.Bookable);
        Assert.Equal(20_000m, draft.Handover);
        Assert.Equal(payable, Assert.Single(draft.Payable).TipId);
        Assert.Contains("Anonymität", Assert.Single(draft.Blocked).Reason, StringComparison.Ordinal);
        Assert.False(draft.AlreadyPaid);
    }

    [Fact]
    public async Task The_notice_panel_hides_the_name_of_a_tipster_whose_promise_still_holds()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber, anonymous: true);
        await host.Tips.ResolveAnonymityAsync(tip, "Belohnung", Leader());
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        // resolved, so the name may show; the row asks TipAnonymity rather than deciding for itself
        var row = Assert.Single(await host.Reward.GetForTipAsync(tip, Leader()));
        Assert.Equal("Erika Musterfrau", row.CitizenName);
        Assert.False(row.SelfPaid);
        Assert.NotNull(row.BookingCaseNumber);
    }

    // ---- the capture status is not a payment ----

    [Fact]
    public async Task The_capture_status_cannot_be_set_by_hand()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        var tip = await WorkedTipAsync(host, caseNumber);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Tips.SetStatusAsync(tip, TipStatus.FuehrteZurErgreifung, Leader()));

        Assert.Contains("Auszahlung", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TipStatus.FuehrteZurErgreifung, TipRules.AllowedTargets(TipStatus.InPruefung));
        Assert.Equal(TipStatus.InPruefung, await TipStatusAsync(ctx, tip));
    }

    [Fact]
    public async Task A_tip_left_on_the_capture_status_without_a_receipt_is_still_payable()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        // the state the old hand-set menu left behind: captured, but no money ever moved
        await using (var db = ctx.NewContext())
        {
            var row = await db.Hinweise.SingleAsync(h => h.Id == tip);
            row.Status = TipStatus.FuehrteZurErgreifung;
            await db.SaveChangesAsync();
        }

        var draft = await host.Reward.GetDraftAsync(id, Leader());

        Assert.Equal(tip, Assert.Single(draft.Payable).TipId);
        Assert.Empty(draft.Blocked);
        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());
        Assert.Equal(50_000m, Assert.Single(await RewardsAsync(ctx)).Amount);
    }

    [Fact]
    public async Task A_paid_tip_is_blocked_by_its_receipt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (id, caseNumber) = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 50_000m, KassenKonto.Gruengeld, null, Leader());
        var tip = await WorkedTipAsync(host, caseNumber);
        await FundAsync(host, KassenKonto.Gruengeld, 100_000m);
        await host.Wanted.CapturedAsync(id, Leader());
        await host.Reward.PayoutAsync(Split(id, (tip, 50_000m)), Leader());

        var draft = await host.Reward.GetDraftAsync(id, Leader());

        Assert.Empty(draft.Payable);
        Assert.Contains("Bereits belohnt", Assert.Single(draft.Blocked).Reason, StringComparison.Ordinal);
        Assert.True(draft.AlreadyPaid);
    }
}
