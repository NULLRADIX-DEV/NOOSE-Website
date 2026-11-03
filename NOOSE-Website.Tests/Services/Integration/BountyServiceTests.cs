using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="BountyService"/>: who may put money on a head, and what of it leaves the house.</summary>
/// <remarks>
/// The outward assertion runs through <see cref="PublicWantedService.GetBoardAsync"/> rather than over the share
/// table, because that is the path an anonymous visitor takes — belt, module switch and cache included.
/// </remarks>
public sealed class BountyServiceTests
{
    private const string PersonId = "p1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    /// <summary>Senior Special Agent: commits agency money directly, is not leadership.</summary>
    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    /// <summary>Rank 2: may pledge his own money, but agency money turns into a request.</summary>
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger").WithStatus(AgentStatus.Civilian).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        BountyService Bounty,
        PublicWantedService Wanted,
        KassenService Kasse,
        IDiscordWebhookService Discord,
        INotificationService Notifications,
        IMemoryCache Cache);

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
        var counter = 0;
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{++counter:0000}");

        var discord = Substitute.For<IDiscordWebhookService>();
        var notifications = Substitute.For<INotificationService>();

        var tipPriority = new TipPriorityService(factory);
        var wanted = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            notifications, tipPriority, discord, Substitute.For<IPressReleaseService>(), cache);
        var kasse = new KassenService(factory, caseNumbers);
        var bounty = new BountyService(factory, wanted, modules, kasse, notifications, tipPriority, discord);
        return new Host(bounty, wanted, kasse, discord, notifications, cache);
    }

    /// <summary>Seeds the module switches plus one clean person file, with the wanted and bounty modules on.</summary>
    private static async Task<SqliteTestContext> SeededAsync(bool bountyOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in new[] { PublicModules.Wanted, PublicModules.Bounty })
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == key);
            row.IsEnabled = key != PublicModules.Bounty || bountyOn;
        }
        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            p.ThreatScore = 80;
        }));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, string key, bool on)
    {
        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == key);
        row.IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
        host.Cache.Remove("OeffentlicheFahndungen");
    }

    private static async Task<string> PublishedAsync(Host host)
    {
        var id = await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());
        await host.Wanted.PublishAsync(id, null, Leader());
        return id;
    }

    private static async Task<string> DraftAsync(Host host)
        => await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());

    private static async Task ClassifyAsync(SqliteTestContext ctx, Action<Person> flag)
    {
        await using var db = ctx.NewContext();
        var person = await db.People.SingleAsync(p => p.Id == PersonId);
        flag(person);
        await db.SaveChangesAsync();
    }

    /// <summary>The public case number of a notice, resolved rather than guessed from the stubbed counter.</summary>
    private static async Task<string> CaseAsync(SqliteTestContext ctx, string wantedId)
    {
        await using var db = ctx.NewContext();
        return (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == wantedId)).CaseNumber!;
    }

    /// <summary>The number an anonymous visitor sees, read the way an anonymous visitor reads it.</summary>
    private static async Task<decimal?> AdvertisedAsync(Host host, string caseNumber)
        => (await host.Wanted.GetBountyAsync(caseNumber))?.Total;

    // ---- rights ----

    [Fact]
    public async Task ATrainee_MayPledgeHisOwnMoney()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Bounty.AddPrivateAsync(id, 1_000_000m, Junior());

        Assert.Equal(1_000_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task ATrainee_CommittingAgencyMoney_FilesARequestInstead()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        var outcome = await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, "Grund", Junior());

        Assert.Equal(BountyAddOutcome.Requested, outcome);
        Assert.Equal(1, await host.Bounty.GetPendingRequestCountAsync());
        // a pending share is an open internal decision and must not show outside
        Assert.Null(await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task ATrainee_FilingARequestWithoutAReason_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, "  ", Junior()));
    }

    [Fact]
    public async Task ASeniorAgent_CommitsAgencyMoneyDirectly()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        var outcome = await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, null, Senior());

        Assert.Equal(BountyAddOutcome.Committed, outcome);
        Assert.Equal(500_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
        Assert.Equal(0, await host.Bounty.GetPendingRequestCountAsync());
    }

    [Fact]
    public async Task TheReadOnlySupervision_MayNotSetABounty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Bounty.AddPrivateAsync(id, 100m, OnlyReader()));
    }

    [Fact]
    public async Task ACitizen_MayNotSetABounty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Bounty.AddPrivateAsync(id, 100m, Citizen()));
    }

    [Fact]
    public async Task AnAmountOfZeroOrLess_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Bounty.AddPrivateAsync(id, 0m, Senior()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Bounty.AddPrivateAsync(id, -5m, Senior()));
    }

    [Fact]
    public async Task AClassifiedFile_TakesNoBounty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Bounty.AddPrivateAsync(id, 100m, Leader()));
    }

    [Fact]
    public async Task AnAgentWhoMayNotReadTheFile_SeesNoShares()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 100m, Leader());
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        // not-found and not-allowed answer the same, or the panel becomes an existence oracle
        Assert.Empty(await host.Bounty.GetSharesAsync(id, Junior()));
        Assert.Equal(0m, (await host.Bounty.GetSummaryAsync(id, Junior())).Advertised);
    }

    // ---- what the outside sees ----

    [Fact]
    public async Task TheAdvertisedSum_CountsPledgedAndSecuredOnly()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, null, Leader());
        await host.Bounty.AddPrivateAsync(id, 1_000_000m, Senior());
        // one pending and one withdrawn share, neither of which may count
        await host.Bounty.AddOfficialAsync(id, 250_000m, KassenKonto.Gruengeld, "Grund", Junior());
        await host.Bounty.AddPrivateAsync(id, 700m, Junior());
        var withdrawn = (await host.Bounty.GetSharesAsync(id, Leader())).Single(s => s.Amount == 700m);
        await host.Bounty.WithdrawAsync(withdrawn.Id, "Irrtum", Junior());

        Assert.Equal(1_500_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task ANoticeWithoutMoney_CarriesNoBountyAtAll()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        // no entry rather than an advertised "0 $"
        Assert.Null(await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task TheCeilingFlag_ReachesTheOutside()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await host.Wanted.SetBountyIsCapAsync(id, true, Leader());

        var bounty = await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id));
        Assert.NotNull(bounty);
        Assert.True(bounty!.IsCap);
    }

    [Fact]
    public async Task WithTheBountyModuleOff_TheBoardStaysAndTheMoneyGoes()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await ModuleAsync(ctx, host, PublicModules.Bounty, false);

        Assert.Single((await host.Wanted.GetBoardAsync()).Cards);
        Assert.Null(await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task WithTheKillSwitchOn_NoMoneyIsAdvertised()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.PublicAreaKillSwitch,
                Value = "true",
            });
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        Assert.Null(await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task AClassifiedFile_TakesItsAdvertisedMoneyWithIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());
        Assert.Equal(250_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));

        await ClassifyAsync(ctx, p => p.IsTRUClassified = true);
        host.Cache.Remove("OeffentlicheFahndungen");

        // the belt hides card and money together; the bounty is summed behind it, not beside it
        Assert.Empty((await host.Wanted.GetBoardAsync()).Cards);
        Assert.Null(await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task ADraft_AdvertisesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        Assert.Empty((await host.Wanted.GetBoardAsync()).Cards);
        Assert.Empty((await host.Wanted.GetBoardAsync()).BountyByCaseNumber);
    }

    [Fact]
    public async Task ARetractedNotice_AdvertisesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await host.Wanted.RetractAsync(id, "Irrtum", Leader());

        Assert.Null(await host.Wanted.GetBountyAsync(await CaseAsync(ctx, id)));
    }

    // ---- the treasury ----

    [Fact]
    public async Task PayingInAPrivateShare_BooksExactlyOneDepositAndSecuresIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 1_000_000m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Senior());

        await using var db = ctx.NewContext();
        var booking = Assert.Single(await db.KassenBuchungen.ToListAsync());
        Assert.Equal(KassenBuchungArt.Einzahlung, booking.Kind);
        Assert.Equal(1_000_000m, booking.Amount);
        Assert.Equal(1_000_000m, await host.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));

        var after = Assert.Single(await db.FahndungKopfgeldAnteile.ToListAsync());
        Assert.Equal(BountyShareStatus.Gesichert, after.Status);
        Assert.Equal(booking.Id, after.KassenBuchungId);
        // the advertised sum is unchanged: pledged and secured both count
        Assert.Equal(1_000_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    [Fact]
    public async Task PayingInTheSameShareTwice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();
        await host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Senior());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Senior()));

        await using var db = ctx.NewContext();
        Assert.Single(await db.KassenBuchungen.ToListAsync());
    }

    [Fact]
    public async Task PayingInAgencyMoney_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, null, Leader());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Leader()));
    }

    [Fact]
    public async Task PayingInSomeoneElsesShare_NeedsLeadership()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Junior()));
        await host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Leader());
    }

    [Fact]
    public async Task WithdrawingSecuredMoney_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();
        await host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Senior());

        // giving it back is a withdrawal from the treasury, not an edit of a pledge
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.WithdrawAsync(share.Id, "doch nicht", Senior()));
    }

    [Fact]
    public async Task WithdrawingWithoutAReason_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.WithdrawAsync(share.Id, "   ", Senior()));
    }

    [Fact]
    public async Task NoWritePath_OverwritesTheAmountOfAnExistingShare()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        await host.Bounty.AddPrivateAsync(id, 700m, Senior());

        // append-only: a second pledge is a second row, never a bigger first one
        await using var db = ctx.NewContext();
        // ordered in memory: SQLite refuses a decimal ORDER BY, and the ordering is the assertion's business anyway
        var amounts = (await db.FahndungKopfgeldAnteile.Select(k => k.Amount).ToListAsync()).Order().ToList();
        Assert.Equal(new[] { 500m, 700m }, amounts);
    }

    // ---- coverage ----

    [Fact]
    public async Task Coverage_CountsAgencyPledgesAndSecuredPrivateMoney()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 400m, KassenKonto.Gruengeld, null, Leader());
        await host.Bounty.AddPrivateAsync(id, 100m, Senior());
        var pledge = (await host.Bounty.GetSharesAsync(id, Leader())).Single(s => s.Amount == 100m);
        await host.Bounty.PayInAsync(pledge.Id, KassenKonto.Gruengeld, Senior());

        var coverage = (await host.Bounty.GetCoverageAsync(Leader()))
            .Single(c => c.Account == KassenKonto.Gruengeld);

        // the deposit raised the balance to 100 and is itself owed out, so 400 + 100 stand against 100
        Assert.Equal(500m, coverage.Owed);
        Assert.Equal(100m, coverage.Balance);
        Assert.Equal(400m, coverage.Shortfall);
        Assert.True(coverage.IsShort);
    }

    [Fact]
    public async Task Coverage_WarnsButNeverBlocks()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        // nothing in the till, half a million promised — and it goes through
        await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Schwarzgeld, null, Leader());

        Assert.Equal(500_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
        Assert.True((await host.Bounty.GetCoverageAsync(Leader()))
            .Single(c => c.Account == KassenKonto.Schwarzgeld).IsShort);
    }

    // ---- requests ----

    [Fact]
    public async Task ApprovingARequest_MakesTheMoneyPublic()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();

        await host.Bounty.ApproveRequestAsync(request.RequestId, "ok", Leader());

        Assert.Equal(500_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
        Assert.Equal(0, await host.Bounty.GetPendingRequestCountAsync());
    }

    [Fact]
    public async Task RejectingARequest_WithdrawsTheShare()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500_000m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();

        await host.Bounty.RejectRequestAsync(request.RequestId, "nein", Leader());

        Assert.Null(await AdvertisedAsync(host, await CaseAsync(ctx, id)));
        Assert.Equal(0, await host.Bounty.GetPendingRequestCountAsync());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();
        Assert.Equal(BountyShareStatus.Zurueckgezogen, share.Status);
    }

    [Fact]
    public async Task ASecondRequestForTheSameNotice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.AddOfficialAsync(id, 900m, KassenKonto.Gruengeld, "Grund", Junior()));
    }

    [Fact]
    public async Task WithdrawingAPendingShare_ClosesItsRequest()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await host.Bounty.WithdrawAsync(share.Id, "doch nicht", Junior());

        // a request nobody can decide any more would keep the badge counting
        Assert.Equal(0, await host.Bounty.GetPendingRequestCountAsync());
        Assert.Empty(await host.Bounty.GetPendingRequestsAsync());
    }

    [Fact]
    public async Task DeletingTheNotice_TakesItsRequestOutOfTheInbox()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        Assert.Equal(1, await host.Bounty.GetPendingRequestCountAsync());

        await host.Wanted.DeleteAsync(id, Leader());

        // approving would answer "not found" from here on, so a badge that keeps counting it promises a decision
        // nobody can make
        Assert.Equal(0, await host.Bounty.GetPendingRequestCountAsync());
        Assert.Empty(await host.Bounty.GetPendingRequestsAsync());
    }

    [Fact]
    public async Task TheReadOnlySupervision_MayNotDecideARequest()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Bounty.ApproveRequestAsync(request.RequestId, null, OnlyReader()));
    }

    [Fact]
    public async Task ATrainee_MayNotDecideHisOwnRequest()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Bounty.ApproveRequestAsync(request.RequestId, null, Junior()));
    }

    [Fact]
    public async Task DecidingTwice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();
        await host.Bounty.ApproveRequestAsync(request.RequestId, null, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Bounty.ApproveRequestAsync(request.RequestId, null, Leader()));
    }

    [Fact]
    public async Task TheGenericRequestService_RefusesABountyRequest()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddOfficialAsync(id, 500m, KassenKonto.Gruengeld, "Grund", Junior());
        var request = (await host.Bounty.GetPendingRequestsAsync()).Single();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).Options;
        var requests = new RequestService(new TestDbContextFactory(options),
            Substitute.For<INotificationService>());

        // approving there means "set the classification on the target", which would silently downgrade the file
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => requests.DecideAsync(request.RequestId, true, null, Leader()));
    }

    // ---- Discord ----

    /// <summary>Nothing went to the public channel, neither as a card nor as the old one-line text.</summary>
    private static async Task AssertNothingAnnouncedAsync(Host host)
    {
        await host.Discord.DidNotReceive().PushWantedCardAsync(Arg.Any<NotificationType>(),
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());
        await host.Discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The one card the host pushed since the last <c>ClearReceivedCalls</c>.</summary>
    private static WantedDiscordEmbed PushedCard(Host host)
        => (WantedDiscordEmbed)host.Discord.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDiscordWebhookService.PushWantedCardAsync))
            .GetArguments()[1]!;

    private static async Task<string> HintAsync(SqliteTestContext ctx, string name, string colour)
    {
        await using var db = ctx.NewContext();
        var row = new Warnhinweis { Name = name, Colour = colour, SortOrder = 10, IsActive = true };
        db.Warnhinweise.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task ARaiseOnALiveNotice_IsAnnouncedExactlyOnce()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await host.Discord.Received(1).PushWantedCardAsync(NotificationType.PublicWantedBountyRaised,
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());
        // the old one-line text is gone, or Discord would show two posts for one raise
        await host.Discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheAnnouncement_NamesNoCodenameAndNoInternalCaseNumberAndNoCharge()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        var text = WantedCardText.Of(PushedCard(host));
        Assert.DoesNotContain("Falcon", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FIB-P-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOOSE-P-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Waffenhandel", text, StringComparison.Ordinal);
        Assert.Contains("250.000", text, StringComparison.Ordinal);
        Assert.Contains("Max Mustermann", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRaiseCard_IsGoldAndCarriesTheNewTotalAndTheCaseNumber()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var caseNumber = await CaseAsync(ctx, id);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        var card = PushedCard(host);
        Assert.Equal("💰 Kopfgeld erhöht: Max Mustermann", card.Title);
        Assert.Equal(0xC9A227, card.Color);
        Assert.Equal("/gesucht/" + caseNumber, card.ProfilePath);
        Assert.Equal("250.000 $", WantedCardText.Field(card, "Neues Kopfgeld"));
        Assert.Equal(caseNumber, WantedCardText.Field(card, "Aktenzeichen"));
        Assert.NotNull(card.TimestampUtc);
    }

    [Fact]
    public async Task ASecondRaise_ShowsTheWholeTotalNotTheIncrement()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 50_000m, Leader());

        Assert.Equal("300.000 $", WantedCardText.Field(PushedCard(host), "Neues Kopfgeld"));
    }

    [Fact]
    public async Task ACapBounty_IsAnnouncedWithBis()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).BountyIsCap = true;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheFahndungen");
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        Assert.Equal("bis 250.000 $", WantedCardText.Field(PushedCard(host), "Neues Kopfgeld"));
    }

    [Fact]
    public async Task TheRaiseCard_ShowsThePhotoAsAThumbnailNotAsTheLargeImage()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var caseNumber = await CaseAsync(ctx, id);
        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).PhotoFileName = "oeffentlich.jpg";
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheFahndungen");
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        var card = PushedCard(host);
        Assert.Equal($"/gesucht/{caseNumber}/foto", card.ThumbnailPath);
        Assert.Null(card.ImagePath);
    }

    [Fact]
    public async Task TheRaiseCard_TakesHintsAndHazardFromTheOutwardBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Wanted.SetHintsAsync(id, [await HintAsync(ctx, "bewaffnet", "Error")], Leader());
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        var card = PushedCard(host);
        Assert.Equal("🔴 bewaffnet", WantedCardText.Field(card, "Warnhinweise"));
        // the seeded file scores 80
        Assert.Equal("Kritisch", WantedCardText.Field(card, "Gefahrenstufe"));
    }

    [Fact]
    public async Task ThePublishCard_ShowsTheBountyThatWasPledgedOnTheDraft()
    {
        // a raise on a draft is silent, so the publish card is the first announcement of that money
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());
        host.Discord.ClearReceivedCalls();

        await host.Wanted.PublishAsync(id, null, Leader());

        Assert.Equal("250.000 $", WantedCardText.Field(PushedCard(host), "Kopfgeld"));
    }

    [Fact]
    public async Task ThePublishCard_HidesTheBountyWhileTheBountyModuleIsOff()
    {
        using var ctx = await SeededAsync(bountyOn: false);
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());
        host.Discord.ClearReceivedCalls();

        await host.Wanted.PublishAsync(id, null, Leader());

        Assert.Null(WantedCardText.Field(PushedCard(host), "Kopfgeld"));
    }

    [Fact]
    public async Task ANoticeTheBoardDoesNotList_IsNotAnnounced()
    {
        // no page to link to means nothing to announce, however the row looks
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var blind = Substitute.For<IPublicWantedService>();
        blind.GetBoardAsync(Arg.Any<CancellationToken>()).Returns(PublicWantedBoard.Empty);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).Options;
        var factory = new TestDbContextFactory(options);
        var bounty = new BountyService(factory, blind, new PublicModuleService(factory, host.Cache), host.Kasse,
            host.Notifications, new TipPriorityService(factory), host.Discord);
        host.Discord.ClearReceivedCalls();

        await bounty.AddPrivateAsync(id, 250_000m, Leader());

        await AssertNothingAnnouncedAsync(host);
        // the stubbed service dropped no snapshot, so the real one is dropped here
        host.Cache.Remove("OeffentlicheFahndungen");
        Assert.Equal(250_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    [Fact]
    public void TheComposer_TakesOnlyOutwardTypes()
    {
        // the bolt is the parameter type, not the care taken: none of these can hold a PersonId, the internal case
        // number, a codename, a breakdown or the charge
        var method = typeof(BountyService)
            .GetMethod("Compose", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var offenders = method!.GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t != typeof(PublicWantedCard) && t != typeof(PublicBounty) && t != typeof(DateTime))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Die Karte darf nur Nach-außen-Typen sehen: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task ARequest_IsNotAnnounced()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddOfficialAsync(id, 250_000m, KassenKonto.Gruengeld, "Grund", Junior());

        await AssertNothingAnnouncedAsync(host);
    }

    [Fact]
    public async Task AWithdrawal_IsNotAnnounced()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();
        host.Discord.ClearReceivedCalls();

        await host.Bounty.WithdrawAsync(share.Id, "Irrtum", Leader());

        await AssertNothingAnnouncedAsync(host);
    }

    [Fact]
    public async Task ARaiseOnADraft_IsNotAnnounced()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await AssertNothingAnnouncedAsync(host);
    }

    [Fact]
    public async Task WithTheBountyModuleOff_ARaiseIsNotAnnounced()
    {
        using var ctx = await SeededAsync(bountyOn: false);
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Bounty.AddPrivateAsync(id, 250_000m, Leader());

        await AssertNothingAnnouncedAsync(host);
    }

    [Fact]
    public async Task AFailingWebhook_LeavesTheBountyStanding()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.PushWantedCardAsync(Arg.Any<NotificationType>(), Arg.Any<WantedDiscordEmbed>(),
            Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new HttpRequestException("tot"));
        host.Discord.ClearReceivedCalls();

        await Assert.ThrowsAnyAsync<Exception>(() => host.Bounty.AddPrivateAsync(id, 250_000m, Leader()));

        // it was the card that failed, so the commit really did come first
        await host.Discord.Received(1).PushWantedCardAsync(NotificationType.PublicWantedBountyRaised,
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());

        // the money is committed before the announcement; a dead webhook must not undo it
        Assert.Equal(250_000m, await AdvertisedAsync(host, await CaseAsync(ctx, id)));
    }

    // ---- the audit trail ----

    [Fact]
    public async Task PayingIn_WritesItsOwnAuditRow()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Bounty.AddPrivateAsync(id, 500m, Senior());
        var share = (await host.Bounty.GetSharesAsync(id, Leader())).Single();

        await host.Bounty.PayInAsync(share.Id, KassenKonto.Gruengeld, Senior());

        await using var db = ctx.NewContext();
        // ExecuteUpdate bypasses the interceptor, so the money would otherwise leave no trace
        Assert.Contains(await db.AuditLogs.ToListAsync(),
            a => a.EntityType == nameof(FahndungKopfgeldAnteil) && a.EntityId == share.Id);
    }
}

