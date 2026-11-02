using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Requests;
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

/// <summary>Guard tests for <see cref="PublicWantedService"/>: who may publish, and what publishing actually exposes.</summary>
public sealed class PublicWantedServiceTests
{
    private const string PersonId = "p1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    /// <summary>Senior Special Agent: may publish directly, is not leadership.</summary>
    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    /// <summary>Rank 2: may prepare a notice, but publishing turns into a request.</summary>
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
        PublicWantedService Service,
        PublicModuleService Modules,
        IFileStorageService PeopleFiles,
        IPublicWantedPhotoStorageService PublicFiles,
        ICaseNumberService CaseNumbers,
        IDiscordWebhookService Discord,
        INotificationService Notifications,
        IPressReleaseService Press,
        IMemoryCache Cache);

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>
    /// The interceptor is what rewrites a <c>Remove</c> into a soft delete, so the recycle-bin tests would exercise a
    /// hard delete without it. <see cref="ICaseNumberService"/> is stubbed: the real one issues MySQL-only raw SQL.
    /// </remarks>
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
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-0001");

        var peopleFiles = Substitute.For<IFileStorageService>();
        var publicFiles = Substitute.For<IPublicWantedPhotoStorageService>();

        var discord = Substitute.For<IDiscordWebhookService>();
        var notifications = Substitute.For<INotificationService>();
        var press = Substitute.For<IPressReleaseService>();

        var service = new PublicWantedService(factory, modules, caseNumbers, peopleFiles, publicFiles,
            notifications, new TipPriorityService(factory), discord, press, cache);
        return new Host(service, modules, peopleFiles, publicFiles, caseNumbers, discord, notifications, press, cache);
    }

    /// <summary>Turns a module switch on or off and drops the 10 s module snapshot so the change is visible now.</summary>
    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, string key, bool on)
    {
        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == key);
        row.IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
        DropCache(host);
    }

    /// <summary>Seeds the module switches plus one clean person file, and turns the wanted module on.</summary>
    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true, Action<Person>? person = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        if (moduleOn)
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Wanted);
            row.IsEnabled = true;
        }
        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            p.ThreatScore = 80;
            person?.Invoke(p);
        }));
        await db.SaveChangesAsync();
        return ctx;
    }

    /// <summary>Creates a draft with a publishable accusation and returns its id.</summary>
    private static async Task<string> DraftAsync(Host host, ClaimsPrincipal? actor = null)
    {
        var id = await host.Service.CreateDraftFromPersonAsync(PersonId, actor ?? Leader());
        return id;
    }

    private static async Task<string> PublishedAsync(Host host)
    {
        var id = await DraftAsync(host);
        await host.Service.PublishAsync(id, null, Leader());
        return id;
    }

    private static async Task ClassifyAsync(SqliteTestContext ctx, Action<Person> flag)
    {
        await using var db = ctx.NewContext();
        var person = await db.People.SingleAsync(p => p.Id == PersonId);
        flag(person);
        await db.SaveChangesAsync();
    }

    // ---- the classification gate ----

    [Theory]
    [InlineData(Rank.Director, false)]
    [InlineData(Rank.DeputyDirector, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SpecialAgent, true)]
    public async Task Publishing_AClassifiedFile_IsRefusedRegardlessOfRank(Rank rank, bool admin)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        var builder = ClaimsPrincipalBuilder.Agent("actor").WithRank(rank).WithCodename("X");
        var actor = admin ? builder.AsAdmin().Build() : builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, "Grund", actor));
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Publishing_WithATruOrHrbFlagOnly_IsRefused(bool tru, bool hrb)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p =>
        {
            p.IsTRUClassified = tru;
            p.IsHRBClassified = hrb;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task Publishing_ASoftDeletedFile_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p => p.IsDeleted = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task Publishing_ForATrainee_DoesNotRevealThatTheFileIsClassified()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        var seen = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PublishAsync(id, "Grund", Junior()));
        var known = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PublishAsync(id, null, Leader()));

        Assert.DoesNotContain("Verschlusssache", seen.Message);
        Assert.Contains("Verschlusssache", known.Message);
    }

    [Fact]
    public async Task Publishing_ByTheReadOnlySupervision_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.PublishAsync(id, null, OnlyReader()));
    }

    [Fact]
    public async Task Publishing_ByACitizenAccount_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.PublishAsync(id, "Grund", Citizen()));
    }

    [Fact]
    public async Task Publishing_WhileTheModuleIsOff_IsRefused()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task Publishing_WhileTheKillSwitchIsOn_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Modules.KillSwitchSetAsync(true,
            ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    // ---- the rank switch ----

    [Fact]
    public async Task Publishing_AtRankThree_GoesLiveDirectly()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Senior());

        var outcome = await host.Service.PublishAsync(id, null, Senior());

        Assert.Equal(PublicWantedPublishOutcome.Published, outcome);
        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("FIB-FA-2026-0001", card.CaseNumber);
    }

    [Fact]
    public async Task Publishing_AtRankTwo_CreatesARequestAndLeavesTheEntryBeantragt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());

        var outcome = await host.Service.PublishAsync(id, "Bitte ausschreiben", Junior());

        Assert.Equal(PublicWantedPublishOutcome.Requested, outcome);
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);

        await using var db = ctx.NewContext();
        var request = await db.Requests.SingleAsync();
        Assert.Equal(RequestType.Veroeffentlichung, request.Type);
        Assert.Equal(id, request.PublicationWantedId);
        Assert.Equal(PublicWantedStatus.Beantragt, (await db.OeffentlicheFahndungen.SingleAsync()).Status);
    }

    [Fact]
    public async Task Publishing_AtRankTwo_WithoutAJustification_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, "  ", Junior()));
    }

    [Fact]
    public async Task Publishing_AtRankTwo_TwiceIsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());
        await host.Service.PublishAsync(id, "Bitte ausschreiben", Junior());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PublishAsync(id, "Nochmal", Junior()));
    }

    [Fact]
    public async Task ThePublicationRequest_CarriesNoAccusationHtml()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());
        await host.Service.PublishAsync(id, "Bitte ausschreiben", Junior());

        await using var db = ctx.NewContext();
        var request = await db.Requests.SingleAsync();
        Assert.DoesNotContain("<p>", request.Justification ?? string.Empty);
        Assert.DoesNotContain("Waffenhandel", request.TargetDesignation);
    }

    // ---- snapshot isolation, the point of the phase ----

    [Fact]
    public async Task RenamingThePersonAfterPublication_DoesNotChangeTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == PersonId);
            person.Name = "Ganz anderer Name";
            person.WantedReason = "Ganz anderer Vorwurf";
            await db.SaveChangesAsync();
        }
        DropCache(host);

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("Max Mustermann", card.DisplayName);
        var detail = await host.Service.GetByCaseNumberAsync(card.CaseNumber);
        Assert.Contains("Waffenhandel", detail!.ChargeHtml);
    }

    [Fact]
    public async Task ALegacyNooseNumber_StillOpensThePublishedNotice()
    {
        // posters printed before the rename carry NOOSE-FA-… and must keep resolving
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        var legacy = card.CaseNumber.Replace("FIB-", "NOOSE-", StringComparison.Ordinal);

        var detail = await host.Service.GetByCaseNumberAsync($" {legacy} ");

        Assert.Equal(card.CaseNumber, detail!.CaseNumber);
        Assert.Null(await host.Service.GetByCaseNumberAsync("NOOSE-FA-9999-0001"));
    }

    [Fact]
    public async Task ChangingTheThreatScore_DoesNotChangeThePublishedHazardLevel()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        var before = Assert.Single((await host.Service.GetBoardAsync()).Cards).HazardLevel;

        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == PersonId);
            person.ThreatScore = 1;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Equal(before, Assert.Single((await host.Service.GetBoardAsync()).Cards).HazardLevel);
        Assert.Equal(HazardLevel.Critical, before);
    }

    [Fact]
    public async Task CreateDraft_CopiesOnlyNameAndAccusation()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.PersonAliases.Add(new PersonAlias { PersonId = PersonId, AliasName = "Schatten" });
            db.PersonVehicles.Add(new PersonVehicle { PersonId = PersonId, Designation = "Sultan", LicensePlate = "AB123" });
            db.PersonLocations.Add(new PersonLocation { PersonId = PersonId, Text = "Sandy Shores" });
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        var id = await DraftAsync(host);
        var draft = await host.Service.GetDraftAsync(id, Leader());

        Assert.Equal("Max Mustermann", draft!.DisplayName);
        Assert.Contains("Waffenhandel", draft.ChargeHtml);
        Assert.Null(draft.AliasText);
        Assert.Null(draft.VehicleText);
        Assert.Null(draft.LastArea);
        Assert.Null(draft.PhotoSourceId);
    }

    [Fact]
    public async Task CreateDraft_ForAClassifiedFile_IsRefused()
    {
        using var ctx = await SeededAsync(person: p => p.IsClassified = true);
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromPersonAsync(PersonId, Leader()));
    }

    [Fact]
    public async Task CreateDraft_ForAFileTheActorCannotSee_ReadsAsNotFound()
    {
        using var ctx = await SeededAsync(person: p => p.IsClassified = true);
        var host = NewHost(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromPersonAsync(PersonId, Junior()));
        Assert.DoesNotContain("Verschlusssache", ex.Message);
    }

    [Fact]
    public async Task CreateDraft_Twice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromPersonAsync(PersonId, Leader()));
    }

    [Fact]
    public async Task CreateDraft_WorksWhileTheModuleIsOff()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var host = NewHost(ctx);

        var id = await DraftAsync(host);

        Assert.NotNull(await host.Service.GetDraftAsync(id, Leader()));
    }

    // ---- a file that turns classified or vanishes after publication ----

    [Fact]
    public async Task AFileClassifiedAfterPublication_DisappearsFromTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        // straight through the context: proves the read-side belt on its own, without the retract hook
        await ClassifyAsync(ctx, p => p.IsClassified = true);
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001"));
        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
        // the row itself is untouched — the belt subtracts, it does not rewrite
        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Veroeffentlicht, (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).Status);
    }

    [Fact]
    public async Task AFileSoftDeletedAfterPublication_DisappearsFromTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        await ClassifyAsync(ctx, p => p.IsDeleted = true);
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task RetractForRecord_TakesEveryLiveNoticeOffline()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.RetractForRecordAsync(PersonId, "Akte als Verschlusssache eingestuft.", Leader());

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal(PublicWantedStatus.Zurueckgezogen, row.Status);
        Assert.Equal("Akte als Verschlusssache eingestuft.", row.RetractedReason);
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task RetractForRecord_AlsoClosesAnOpenPublicationRequest()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());
        await host.Service.PublishAsync(id, "Bitte ausschreiben", Junior());

        await host.Service.RetractForRecordAsync(PersonId, "Akte gelöscht.", Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(RequestStatus.Rejected, (await db.Requests.SingleAsync()).Status);
    }

    [Fact]
    public async Task ASoftDeletedNotice_NeverReachesTheBoard()
    {
        // the suppression belt reads the person file with IgnoreQueryFilters; that flag is compilation-scoped, so a
        // subquery using it would strip !IsDeleted from the notice set as well and serve a deleted poster
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001"));
        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
    }

    [Fact]
    public async Task ANoticeWhosePersonRowIsGone_IsSuppressed()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        await using (var db = ctx.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Personen WHERE Id = {0}", PersonId);
        }
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    // ---- the notice carries the file's content, so it answers to the file's read gate ----

    [Fact]
    public async Task GetAllAsync_HidesANoticeWhoseFileTheActorMayNotRead()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);
        await ClassifyAsync(ctx, p =>
        {
            p.IsClassified = true;
            p.IsTRUClassified = true;
        });

        Assert.Empty(await host.Service.GetAllAsync(Senior()));
        Assert.Single(await host.Service.GetAllAsync(Leader()));
    }

    [Fact]
    public async Task GetDraftAndOptions_ForAFileTheActorMayNotRead_AreEmpty()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.PersonLocations.Add(new PersonLocation { PersonId = PersonId, Text = "Sandy Shores" });
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p =>
        {
            p.IsClassified = true;
            p.IsTRUClassified = true;
        });

        // these are live rows of the file, not snapshot fields
        Assert.Null(await host.Service.GetDraftAsync(id, Senior()));
        Assert.Empty((await host.Service.GetOptionsAsync(id, Senior())).Areas);

        Assert.NotNull(await host.Service.GetDraftAsync(id, Leader()));
        Assert.Single((await host.Service.GetOptionsAsync(id, Leader())).Areas);
    }

    [Fact]
    public async Task GetForPersonAsync_ForAFileTheActorMayNotRead_IsNull()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);
        await ClassifyAsync(ctx, p =>
        {
            p.IsClassified = true;
            p.IsTRUClassified = true;
        });

        Assert.Null(await host.Service.GetForPersonAsync(PersonId, Senior()));
        Assert.NotNull(await host.Service.GetForPersonAsync(PersonId, Leader()));
    }

    [Fact]
    public async Task ARankTwoAgent_CanOpenTheNoticeHePrepared()
    {
        // without this the rank switch is dead: he creates a draft in the file and can never reach the request
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host, Junior());

        Assert.NotNull(await host.Service.GetForPersonAsync(PersonId, Junior()));
        Assert.NotNull(await host.Service.GetDraftAsync(id, Junior()));
        await host.Service.GetOptionsAsync(id, Junior());
        // the cross-record list stays at rank 3
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetAllAsync(Junior()));
    }

    [Fact]
    public async Task APartnerAndACitizen_CannotOpenANoticeAtAll()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        var partner = ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetDraftAsync(id, partner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetForPersonAsync(PersonId, Citizen()));
    }

    // ---- everything that is not published looks the same from outside ----

    [Theory]
    [InlineData(PublicWantedStatus.Entwurf)]
    [InlineData(PublicWantedStatus.Beantragt)]
    [InlineData(PublicWantedStatus.Zurueckgezogen)]
    [InlineData(PublicWantedStatus.Gefasst)]
    [InlineData(PublicWantedStatus.Abgelaufen)]
    public async Task Detail_ForEveryNonPublicState_IsIndistinguishable(PublicWantedStatus status)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
            row.Status = status;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001"));
        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-9999-0001"));
    }

    [Fact]
    public async Task Detail_MatchesTheCaseNumberCaseInsensitively()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        Assert.NotNull(await host.Service.GetByCaseNumberAsync("fib-fa-2026-0001"));
    }

    [Fact]
    public async Task AnExpiredEntry_LeavesTheBoardWithoutAWorker()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task TheBoard_IsEmptyWhileTheModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        await host.Modules.SaveAsync(
            [new PublicModuleInput { Key = PublicModules.Wanted, IsEnabled = false }],
            ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build());

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001"));
    }

    // ---- the accusation text ----

    [Fact]
    public async Task Publishing_AnAccusationContainingAMention_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await SetChargeAsync(ctx, id, $"<p>Kontakt zu {MentionParser.Token("Person", Guid.NewGuid().ToString())}</p>");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task Publishing_AnAccusationContainingAPlaceholder_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await SetChargeAsync(ctx, id, "<p>Gesucht wegen {{Aktenzeichen}}</p>");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task Publishing_AnEmptyAccusation_IsRefused()
    {
        using var ctx = await SeededAsync(person: p => p.WantedReason = null);
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, null, Leader()));
    }

    [Fact]
    public async Task AnAccusationThatIsOnlyAnImage_IsPublishable()
    {
        using var ctx = await SeededAsync(person: p => p.WantedReason = null);
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await SetChargeAsync(ctx, id, "<p><img src=\"data:image/png;base64,iVBORw0KGgo=\" /></p>");

        await host.Service.PublishAsync(id, null, Leader());

        Assert.Single((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task Publishing_ReCleansTheStoredAccusation_EvenWhenItWasWrittenAroundTheService()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await SetChargeAsync(ctx, id, "<p>Gefährlich</p><script>alert(1)</script>");

        await host.Service.PublishAsync(id, null, Leader());

        var detail = await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001");
        Assert.DoesNotContain("<script", detail!.ChargeHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- lifecycle ----

    [Fact]
    public async Task Retracting_KeepsTheCaseNumberAndTheAccusation()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.RetractAsync(id, "Hinweis war falsch", Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.Equal("FIB-FA-2026-0001", draft!.CaseNumber);
        Assert.Contains("Waffenhandel", draft.ChargeHtml);
        Assert.Equal(PublicWantedStatus.Zurueckgezogen, draft.Status);
    }

    [Fact]
    public async Task Retracting_RequiresAReason()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RetractAsync(id, "   ", Leader()));
    }

    [Fact]
    public async Task Retracting_WorksWhileTheModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Modules.KillSwitchSetAsync(true,
            ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build());

        await host.Service.RetractAsync(id, "Not-Aus, trotzdem sauber schließen", Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Zurueckgezogen, (await db.OeffentlicheFahndungen.SingleAsync()).Status);
    }

    [Fact]
    public async Task Captured_DropsTheEntryFromTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal(PublicWantedStatus.Gefasst, row.Status);
        Assert.NotNull(row.CapturedAt);
    }

    [Fact]
    public async Task Delete_WhilePublished_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.DeleteAsync(id, Leader()));
    }

    [Fact]
    public async Task Delete_ThenRestore_BringsTheEntryBackAsADraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Erledigt", Leader());
        await host.Service.DeleteAsync(id, Leader());

        var trash = await host.Service.GetTrashAsync();
        Assert.Equal(id, Assert.Single(trash).Id);

        await host.Service.RestoreAsync(id, Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.Equal(PublicWantedStatus.Entwurf, draft!.Status);
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task Restore_AfterTheFileBecameClassified_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Service.DeleteAsync(id, Leader());
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RestoreAsync(id, Leader()));
    }

    [Fact]
    public async Task Publishing_MintsTheCaseNumberOnlyOnce()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Kurz offline", Leader());

        await host.Service.PublishAsync(id, null, Leader());

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("FIB-FA-2026-0001", card.CaseNumber);
        // the substitute answers with a constant, so only the call count can tell ??= from =
        await host.CaseNumbers.Received(1)
            .NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnExpiryPickedInTheEditor_LastsToTheEndOfThatDayLocally()
    {
        // MudDatePicker hands over local midnight; compared against UtcNow raw, the notice would drop off early
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var today = DateTime.Now.Date;

        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", ExpiresAt = today }, Leader());

        Assert.Single((await host.Service.GetBoardAsync()).Cards);
        await using var db = ctx.NewContext();
        var stored = (await db.OeffentlicheFahndungen.SingleAsync()).ExpiresAt;
        Assert.NotNull(stored);
        Assert.True(stored > DateTime.UtcNow, "Ein heute gewähltes Ablaufdatum gilt bis zum Tagesende.");
    }

    // ---- the photo copy ----

    [Fact]
    public async Task Publishing_CopiesTheFilePhoto_AndNeverStoresTheInternalFileName()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "foto-1",
                PersonId = PersonId,
                FileNameSaved = "intern.jpg",
                OriginalName = "o.jpg",
                ContentType = "image/jpeg",
            });
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);
        host.PeopleFiles.OpenRead("intern.jpg").Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("bild")));
        host.PublicFiles.SaveAsync(Arg.Any<Stream>(), "image/jpeg", Arg.Any<CancellationToken>())
            .Returns("oeffentlich.jpg");

        var id = await DraftAsync(host);
        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = "foto-1" }, Leader());
        await host.Service.PublishAsync(id, null, Leader());

        var photo = await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001");
        Assert.Equal("oeffentlich.jpg", photo!.FileNameSaved);
        Assert.Equal("image/jpeg", photo.ContentType);
        Assert.NotEqual("intern.jpg", photo.FileNameSaved);
    }

    [Fact]
    public async Task ClearingThePhotoOfALiveNotice_TakesTheCopyOffline()
    {
        // the copy used to follow the choice only at publish time, so removing a mugshot reported success while it
        // stayed anonymously downloadable
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        var id = await DraftAsync(host);
        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = "foto-1" }, Leader());
        await host.Service.PublishAsync(id, null, Leader());
        Assert.NotNull(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));

        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = null }, Leader());

        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
        Assert.False(Assert.Single((await host.Service.GetBoardAsync()).Cards).HasPhoto);
        host.PublicFiles.Received().Delete("oeffentlich.jpg");
    }

    [Fact]
    public async Task SwappingThePhotoOfALiveNotice_ReplacesTheCopy()
    {
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        host.PublicFiles.SaveAsync(Arg.Any<Stream>(), "image/png", Arg.Any<CancellationToken>())
            .Returns("zweite.png");
        var id = await DraftAsync(host);
        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = "foto-1" }, Leader());
        await host.Service.PublishAsync(id, null, Leader());

        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = "foto-2" }, Leader());

        var photo = await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001");
        Assert.Equal("zweite.png", photo!.FileNameSaved);
        host.PublicFiles.Received().Delete("oeffentlich.jpg");
    }

    [Fact]
    public async Task GetPublishedPhoto_ForADraftOrAnUnknownCaseNumber_IsNull()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);

        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
        Assert.Null(await host.Service.GetPublishedPhotoAsync("gibt-es-nicht"));
        Assert.Null(await host.Service.GetPublishedPhotoAsync(null));
    }

    // ---- caching ----

    [Fact]
    public async Task TheBoard_IsCachedAndDroppedOnEveryWrite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        Assert.Single((await host.Service.GetBoardAsync()).Cards);

        // a write around the service is not seen — that is the cache doing its job
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
            row.DisplayName = "Direkt geändert";
            await db.SaveChangesAsync();
        }
        Assert.Equal("Max Mustermann", Assert.Single((await host.Service.GetBoardAsync()).Cards).DisplayName);

        // a write through the service invalidates it immediately
        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Über den Dienst" }, Leader());
        Assert.Equal("Über den Dienst", Assert.Single((await host.Service.GetBoardAsync()).Cards).DisplayName);
    }

    // ---- guards on the internal read paths ----

    [Fact]
    public async Task GetAllAsync_AdmitsRankThreeAndTheSupervision_ButNotATrainee()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);

        Assert.Single(await host.Service.GetAllAsync(Senior()));
        Assert.Single(await host.Service.GetAllAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetAllAsync(Junior()));
    }

    [Fact]
    public async Task GetAllAsync_ProjectsTheCodename_NotTheIdentityUser()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead", configure: a =>
            {
                a.Codename = "Falcon";
                a.RealName = "Klarname Klar";
            }));
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);
        await PublishedAsync(host);

        var row = Assert.Single(await host.Service.GetAllAsync(Leader()));
        Assert.Equal("Falcon", row.PublishedByName);
    }

    // ---- deciding a publication request ----

    private static async Task<(string WantedId, string RequestId)> RequestedAsync(Host host, SqliteTestContext ctx)
    {
        var id = await DraftAsync(host, Junior());
        await host.Service.PublishAsync(id, "Bitte ausschreiben", Junior());
        await using var db = ctx.NewContext();
        return (id, (await db.Requests.SingleAsync()).Id);
    }

    [Fact]
    public async Task Approving_PublishesThroughTheSameBodyAsADirectPublish()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, requestId) = await RequestedAsync(host, ctx);

        await host.Service.ApprovePublicationRequestAsync(requestId, "Passt", Leader());

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("FIB-FA-2026-0001", card.CaseNumber);
        await using var db = ctx.NewContext();
        Assert.Equal(RequestStatus.Approved, (await db.Requests.SingleAsync()).Status);
        Assert.Equal(PublicWantedStatus.Veroeffentlicht,
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == wantedId)).Status);
    }

    [Fact]
    public async Task Approving_AfterTheFileBecameClassified_IsRefusedAndTheRowStaysBeantragt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, requestId) = await RequestedAsync(host, ctx);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ApprovePublicationRequestAsync(requestId, null, Leader()));

        await using var db = ctx.NewContext();
        Assert.Equal(RequestStatus.Requested, (await db.Requests.SingleAsync()).Status);
        Assert.Equal(PublicWantedStatus.Beantragt,
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == wantedId)).Status);
    }

    [Fact]
    public async Task Approving_IntoASwitchedOffModule_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (_, requestId) = await RequestedAsync(host, ctx);
        await host.Modules.KillSwitchSetAsync(true,
            ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ApprovePublicationRequestAsync(requestId, null, Leader()));
    }

    [Fact]
    public async Task Approving_BelowRankThree_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (_, requestId) = await RequestedAsync(host, ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.ApprovePublicationRequestAsync(requestId, null, Junior()));
    }

    [Fact]
    public async Task Rejecting_LeavesTheEntryAsADraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, requestId) = await RequestedAsync(host, ctx);

        await host.Service.RejectPublicationRequestAsync(requestId, "Nicht jetzt", Leader());

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        var draft = await host.Service.GetDraftAsync(wantedId, Leader());
        Assert.Equal(PublicWantedStatus.Entwurf, draft!.Status);
    }

    [Fact]
    public async Task Approving_ContentThatBecameUnpublishable_IsRefused_WithoutCopyingAPhoto()
    {
        // the content rules live in the shared publish body, not only in PublishAsync — a Beantragt row can be edited
        // between filing and decision, and the copy must not be written before the refusal
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, requestId) = await RequestedAsync(host, ctx);
        await SetChargeAsync(ctx, wantedId, "<p>Gesucht wegen {{Aktenzeichen}}</p>");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ApprovePublicationRequestAsync(requestId, null, Leader()));

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
        await host.PublicFiles.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approving_ByTheReadOnlySupervision_IsRefusedBeforeAnythingIsWritten()
    {
        // RequireHighestClassification alone admits the supervision, and the page that renders the button does too;
        // without the write guard the case number and the photo copy happen before the interceptor vetoes the save
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (_, requestId) = await RequestedAsync(host, ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.ApprovePublicationRequestAsync(requestId, null, OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.RejectPublicationRequestAsync(requestId, null, OnlyReader()));
        await host.PublicFiles.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_ABeantragtNotice_ClosesItsRequest()
    {
        // otherwise the badge counts a request the inbox cannot show and nobody can decide
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, requestId) = await RequestedAsync(host, ctx);

        await host.Service.DeleteAsync(wantedId, Leader());

        Assert.Equal(0, await host.Service.GetPendingRequestCountAsync());
        Assert.Empty(await host.Service.GetPendingRequestsAsync());
        await using var db = ctx.NewContext();
        Assert.Equal(RequestStatus.Rejected, (await db.Requests.SingleAsync(a => a.Id == requestId)).Status);
    }

    [Fact]
    public async Task ThePendingCountAndTheInbox_AreBuiltFromTheSameJoin()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (wantedId, _) = await RequestedAsync(host, ctx);
        // a notice that vanished around the service must not leave the badge counting
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == wantedId);
            row.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal((await host.Service.GetPendingRequestsAsync()).Count,
            await host.Service.GetPendingRequestCountAsync());
    }

    [Fact]
    public async Task GetPendingRequests_CountAndRowsCoverOnlyOpenPublicationRequests()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var (_, requestId) = await RequestedAsync(host, ctx);

        Assert.Equal(1, await host.Service.GetPendingRequestCountAsync());
        var row = Assert.Single(await host.Service.GetPendingRequestsAsync());
        Assert.Equal(requestId, row.RequestId);
        Assert.Equal("Max Mustermann", row.DisplayName);

        await host.Service.RejectPublicationRequestAsync(requestId, null, Leader());
        Assert.Equal(0, await host.Service.GetPendingRequestCountAsync());
    }

    // ---- audit ----

    [Fact]
    public async Task PublishAndRetract_AreAuditedByTheInterceptor_WithoutAManualRow()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Erledigt", Leader());

        await using var db = ctx.NewContext();
        var rows = await db.AuditLogs.Where(a => a.EntityType == nameof(OeffentlicheFahndung)).ToListAsync();
        Assert.Contains(rows, a => a.Action == AuditAction.Created);
        Assert.Contains(rows, a => a.Action == AuditAction.Modified);
        // nothing is logged against the person file itself; the timeline fans in over the snapshot type
        Assert.Empty(await db.AuditLogs.Where(a => a.EntityType == nameof(Person)).ToListAsync());
    }

    /// <summary>A host whose person file carries two photos and whose storage substitutes answer.</summary>
    private static async Task<Host> PhotoHostAsync(SqliteTestContext ctx)
    {
        await using (var db = ctx.NewContext())
        {
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "foto-1", PersonId = PersonId, FileNameSaved = "intern.jpg",
                OriginalName = "o.jpg", ContentType = "image/jpeg",
            });
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "foto-2", PersonId = PersonId, FileNameSaved = "intern2.png",
                OriginalName = "o2.png", ContentType = "image/png",
            });
            await db.SaveChangesAsync();
        }

        var host = NewHost(ctx);
        host.PeopleFiles.OpenRead(Arg.Any<string>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("bild")));
        host.PublicFiles.SaveAsync(Arg.Any<Stream>(), "image/jpeg", Arg.Any<CancellationToken>())
            .Returns("oeffentlich.jpg");
        return host;
    }

    // ---- the archive (/gefasst) ----

    [Fact]
    public async Task TheArchive_ShowsOnlyCapturedNotices()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        Assert.Empty(await host.Service.GetArchiveAsync());

        await host.Service.CapturedAsync(id, Leader());

        var card = Assert.Single(await host.Service.GetArchiveAsync());
        Assert.Equal("FIB-FA-2026-0001", card.CaseNumber);
        Assert.Equal("Max Mustermann", card.DisplayName);
        // and it left the live board in the same snapshot
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task Capturing_StoresThePubliclyShownPayout()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader(), 750_000m);

        await using var db = ctx.NewContext();
        Assert.Equal(750_000m, (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).PublicPaidOut);
        // a display figure and nothing else: no cash left the till and no citizen was issued a receipt
        Assert.Empty(await db.KassenBuchungen.ToListAsync());
        Assert.Empty(await db.HinweisBelohnungen.ToListAsync());
    }

    [Fact]
    public async Task Capturing_WithoutAnAmount_LeavesThePubliclyShownPayoutUnset()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        await using var db = ctx.NewContext();
        // null rather than 0: "not stated" is a different claim from "nothing was paid"
        Assert.Null((await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).PublicPaidOut);
    }

    [Fact]
    public async Task ANegativePubliclyShownPayout_IsRefusedAndLeavesTheNoticeLive()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CapturedAsync(id, Leader(), -1m));

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Veroeffentlicht,
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).Status);
    }

    [Fact]
    public async Task AThirdDecimalInThePubliclyShownPayout_IsRefused()
    {
        // the column holds two decimals; MySQL would swallow the third without a word
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CapturedAsync(id, Leader(), 1.005m));
    }

    [Fact]
    public async Task ThePubliclyShownPayout_StaysCorrectableAfterTheCapture()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader(), 750_000m);

        await host.Service.SetPublicPaidOutAsync(id, 500_000m, Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(500_000m, (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).PublicPaidOut);
    }

    [Fact]
    public async Task ThePubliclyShownPayout_StaysCorrectableAfterARetraction()
    {
        // a retraction does not take the figure out of the public total, so it must not lock the correction either
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader(), 750_000m);
        await host.Service.RetractAsync(id, "Zahlendreher", Leader());

        await host.Service.SetPublicPaidOutAsync(id, null, Leader());

        await using var db = ctx.NewContext();
        Assert.Null((await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).PublicPaidOut);
    }

    [Fact]
    public async Task ThePubliclyShownPayout_CannotBeSetOnANoticeNobodyWasCaughtOn()
    {
        // otherwise the start page reports money paid for an arrest that never happened
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetPublicPaidOutAsync(id, 500_000m, Leader()));
    }

    [Fact]
    public async Task ThePubliclyShownPayout_IsRefusedForTheReadOnlySupervision()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader(), 750_000m);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetPublicPaidOutAsync(id, 1m, OnlyReader()));
    }

    [Fact]
    public async Task Capturing_LeavesAPressDraftBehind()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        // the card is all the draft may know: it cannot carry a PersonId, the internal case number or a score
        await host.Press.Received(1).CreateCaptureDraftAsync(
            Arg.Is<PublicWantedCard>(c => c.CaseNumber == "FIB-FA-2026-0001" && c.DisplayName == "Max Mustermann"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingPressService_DoesNotUndoTheCapture()
    {
        // the draft is a convenience; losing it must not leave a caught person on the live board
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        host.Press.CreateCaptureDraftAsync(Arg.Any<PublicWantedCard>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("kaputt")));
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Gefasst,
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).Status);
    }

    [Fact]
    public async Task TheProfileRoute_StillAnswersNotFoundForACapturedNotice()
    {
        // the archive is a list, not a second way into the profile: the accusation is over with the arrest
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        Assert.Null(await host.Service.GetByCaseNumberAsync("FIB-FA-2026-0001"));
    }

    [Fact]
    public async Task TheArchive_IsEmptyWhileItsOwnModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    [Fact]
    public async Task TheArchive_StaysOnlineWhileTheWantedModuleIsOff()
    {
        // two switches, one snapshot: turning the board off must not take the archive with it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await ModuleAsync(ctx, host, PublicModules.Wanted, false);

        Assert.Single(await host.Service.GetArchiveAsync());
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task TheArchive_IsEmptyWhileTheKillSwitchIsOn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await host.Modules.KillSwitchSetAsync(true,
            ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build());

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    [Theory]
    [InlineData("vs")]
    [InlineData("tru")]
    [InlineData("hrb")]
    public async Task TheArchive_HidesACapturedNoticeWhoseRecordBecameClassified(string flag)
    {
        // the belt, not the hook: this test never touches the notice row
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());
        Assert.Single(await host.Service.GetArchiveAsync());

        await ClassifyAsync(ctx, p =>
        {
            switch (flag)
            {
                case "vs": p.IsClassified = true; break;
                case "tru": p.IsTRUClassified = true; break;
                default: p.IsHRBClassified = true; break;
            }
        });
        DropCache(host);

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    [Fact]
    public async Task TheArchive_HidesACapturedNoticeWhoseRecordWasDeleted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == PersonId);
            person.IsDeleted = true;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    // ---- the retraction hook now covers Gefasst ----

    [Fact]
    public async Task RetractForRecord_PullsACapturedNoticeToo()
    {
        // before the archive existed this was harmless: a person caught in August and classified in September would
        // otherwise keep photo, name and date on /gefasst forever
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await host.Service.RetractForRecordAsync(PersonId, "Akte eingestuft", Leader());

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        Assert.Equal(PublicWantedStatus.Zurueckgezogen, row.Status);
        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    [Fact]
    public async Task Retracting_ACapturedNotice_IsAllowed()
    {
        // otherwise the only way off /gefasst would be deleting it — a silent depublication with no reason
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await host.Service.RetractAsync(id, "Falsche Person", Leader());

        Assert.Empty(await host.Service.GetArchiveAsync());
    }

    // ---- the photo endpoint, widened by one status ----

    [Fact]
    public async Task ThePhoto_OfACapturedNotice_IsServedWhileTheArchiveIsOn()
    {
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedWithPhotoAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        Assert.NotNull(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
    }

    [Fact]
    public async Task ThePhoto_OfACapturedNotice_IsGoneWhileTheArchiveIsOff()
    {
        // a per-entry gate, not "either module is on": switching the archive off has to take its pictures with it
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        var id = await PublishedWithPhotoAsync(host);

        await host.Service.CapturedAsync(id, Leader());

        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
    }

    [Fact]
    public async Task ThePhoto_OfALiveNotice_IsGoneWhileTheBoardIsOff_EvenWithTheArchiveOn()
    {
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        await PublishedWithPhotoAsync(host);
        Assert.NotNull(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));

        await ModuleAsync(ctx, host, PublicModules.Wanted, false);

        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
    }

    [Fact]
    public async Task ThePhoto_OfACapturedNoticeOfAClassifiedRecord_IsGone()
    {
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        await ModuleAsync(ctx, host, PublicModules.WantedArchive, true);
        var id = await PublishedWithPhotoAsync(host);
        await host.Service.CapturedAsync(id, Leader());

        await ClassifyAsync(ctx, p => p.IsTRUClassified = true);
        DropCache(host);

        Assert.Null(await host.Service.GetPublishedPhotoAsync("FIB-FA-2026-0001"));
    }

    // ---- the view counter ----

    [Fact]
    public async Task CountView_IncrementsThePublishedRow()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await host.Service.CountViewAsync("FIB-FA-2026-0001");
        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var db = ctx.NewContext();
        Assert.Equal(2, (await db.OeffentlicheFahndungen.SingleAsync()).ViewCount);
    }

    [Fact]
    public async Task CountView_LeavesTheModifiedStampAndTheAuditLogAlone()
    {
        // a tracked increment would stamp GeaendertAm, write one audit row per anonymous view and push the notice
        // onto the person file's timeline on every page load
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        DateTime? before;
        int auditBefore;
        await using (var db = ctx.NewContext())
        {
            before = (await db.OeffentlicheFahndungen.SingleAsync()).ModifiedAt;
            auditBefore = await db.AuditLogs.CountAsync();
        }

        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var check = ctx.NewContext();
        var row = await check.OeffentlicheFahndungen.SingleAsync();
        Assert.Equal(before, row.ModifiedAt);
        Assert.Equal(auditBefore, await check.AuditLogs.CountAsync());
        Assert.Equal(1, row.ViewCount);
    }

    [Fact]
    public async Task CountView_LeavesADraftAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
            row.CaseNumber = "FIB-FA-2026-0001";
            await db.SaveChangesAsync();
        }

        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var check = ctx.NewContext();
        Assert.Equal(0, (await check.OeffentlicheFahndungen.SingleAsync()).ViewCount);
    }

    [Fact]
    public async Task CountView_LeavesARetractedNoticeAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Grund", Leader());

        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var db = ctx.NewContext();
        Assert.Equal(0, (await db.OeffentlicheFahndungen.SingleAsync()).ViewCount);
    }

    [Fact]
    public async Task CountView_LeavesAnExpiredNoticeAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);

        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var db = ctx.NewContext();
        Assert.Equal(0, (await db.OeffentlicheFahndungen.SingleAsync()).ViewCount);
    }

    [Fact]
    public async Task CountView_LeavesASoftDeletedNoticeAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Grund", Leader());
        await host.Service.DeleteAsync(id, Leader());

        await host.Service.CountViewAsync("FIB-FA-2026-0001");

        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(0, row.ViewCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gibt-es-nicht")]
    public async Task CountView_WithAnUnknownCaseNumber_DoesNothing(string caseNumber)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await host.Service.CountViewAsync(caseNumber);

        await using var db = ctx.NewContext();
        Assert.Equal(0, (await db.OeffentlicheFahndungen.SingleAsync()).ViewCount);
    }

    // ---- warning chips ----

    [Fact]
    public async Task TheBoard_RendersTheActiveHintsOfAPublishedNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await PublishedAsync(host);

        await host.Service.SetHintsAsync(id, [hint], Leader());

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        var chip = Assert.Single(card.Hints);
        Assert.Equal("bewaffnet", chip.Label);
        Assert.Equal("Error", chip.Colour);
    }

    [Fact]
    public async Task DeactivatingAWarning_TakesItOffTheBoardWithoutTouchingTheNotice()
    {
        // read live, not copied onto the assignment: a warning that no longer applies must be retractable without
        // editing forty notices by hand
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await PublishedAsync(host);
        await host.Service.SetHintsAsync(id, [hint], Leader());

        await using (var db = ctx.NewContext())
        {
            (await db.Warnhinweise.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Empty(Assert.Single((await host.Service.GetBoardAsync()).Cards).Hints);
    }

    [Fact]
    public async Task SetHints_WritesOneManualAuditRowAgainstTheNotice()
    {
        // FahndungWarnhinweis is not IAuditable, so the interceptor writes nothing on its own
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);

        await host.Service.SetHintsAsync(id, [hint], Leader());

        await using var db = ctx.NewContext();
        var rows = await db.AuditLogs
            .Where(a => a.EntityType == nameof(OeffentlicheFahndung) && a.EntityId == id)
            .ToListAsync();
        // filtered in memory, and on the prefix: the serializer escapes the umlaut to ü
        Assert.Single(rows, a => a.ChangesJson is not null
            && a.ChangesJson.Contains("Warnhinweise hinzugef", StringComparison.Ordinal)
            && a.ChangesJson.Contains("bewaffnet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetHints_WithNoChange_WritesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);
        await host.Service.SetHintsAsync(id, [hint], Leader());
        int before;
        await using (var db = ctx.NewContext())
        {
            before = await db.AuditLogs.CountAsync();
        }

        await host.Service.SetHintsAsync(id, [hint], Leader());

        await using var check = ctx.NewContext();
        Assert.Equal(before, await check.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task SetHints_IgnoresAnInactiveWarning()
    {
        // a deactivated row must not reach the outside through a tampered dialog post
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "alt", "Info", active: false);
        var id = await DraftAsync(host);

        await host.Service.SetHintsAsync(id, [hint], Leader());

        await using var db = ctx.NewContext();
        Assert.Empty(db.FahndungWarnhinweise);
    }

    [Fact]
    public async Task SetHints_KeepsAnAssignmentWhoseWarningWasDeactivatedLater()
    {
        // narrowing the target to active rows alone would drop it unseen: the picker does not offer it and the
        // editor marks it as inactive, so pressing "Übernehmen" without touching anything would destroy it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);
        await host.Service.SetHintsAsync(id, [hint], Leader());
        await using (var db = ctx.NewContext())
        {
            (await db.Warnhinweise.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }

        // the round trip the editor makes: read the ids back, hand the same set in again
        var current = await host.Service.GetHintIdsAsync(id, Leader());
        await host.Service.SetHintsAsync(id, current, Leader());

        Assert.Single(await host.Service.GetHintIdsAsync(id, Leader()));
    }

    [Fact]
    public async Task SetHints_ForAFileTheActorMayNotRead_IsRefused()
    {
        // the write path used to check the file only for a live notice, while the read path always did — so an agent
        // who knew a draft's id could chip a Verschlusssache and write an audit row against it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetHintsAsync(id, [hint], Junior()));

        await using var db = ctx.NewContext();
        Assert.Empty(db.FahndungWarnhinweise);
    }

    [Fact]
    public async Task SetHints_OnALiveNoticeOfAClassifiedRecord_IsRefused()
    {
        // chipping a live notice changes what is outside, so it answers to the same gates as an edit
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await PublishedAsync(host);
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetHintsAsync(id, [hint], Leader()));
    }

    [Fact]
    public async Task SetHints_ByACitizen_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetHintsAsync(id, [hint], Citizen()));
    }

    [Fact]
    public async Task GetHintIds_ForAFileTheActorMayNotRead_IsEmpty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var hint = await HintAsync(ctx, "bewaffnet", "Error");
        var id = await DraftAsync(host);
        await host.Service.SetHintsAsync(id, [hint], Leader());
        await ClassifyAsync(ctx, p => p.IsClassified = true);

        Assert.Empty(await host.Service.GetHintIdsAsync(id, Junior()));
        Assert.Single(await host.Service.GetHintIdsAsync(id, Leader()));
    }

    // ---- expiry sweep ----

    [Fact]
    public async Task Expire_FlipsADueNoticeToAbgelaufen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await LeadershipAsync(ctx);
        await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);

        Assert.Equal(1, await host.Service.ExpireDueAsync());

        await using var db = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Abgelaufen, (await db.OeffentlicheFahndungen.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expire_LeavesEveryOtherStateAlone()
    {
        // Veroeffentlicht only, not "everything but Gefasst": a Beantragt row would die silently, without its open
        // request being closed, and the nav badge would keep counting it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Service.PublishAsync(id, "Begründung", Junior());
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFahndungen.SingleAsync();
            row.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await host.Service.ExpireDueAsync());

        await using var check = ctx.NewContext();
        Assert.Equal(PublicWantedStatus.Beantragt, (await check.OeffentlicheFahndungen.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expire_LeavesASoftDeletedNoticeAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);
        await host.Service.RetractAsync(id, "Grund", Leader());
        await host.Service.DeleteAsync(id, Leader());

        Assert.Equal(0, await host.Service.ExpireDueAsync());
    }

    [Fact]
    public async Task Expire_TellsLeadershipOncePerSweep_NotOncePerNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await LeadershipAsync(ctx);
        await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);

        await host.Service.ExpireDueAsync();

        await host.Notifications.Received(1).NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.PublicWantedExpired,
            Arg.Any<string>(), Arg.Is("/fahndung?tab=oeffentlich"), Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Expire_ASecondSweep_TellsNobodyAgain()
    {
        // the status change is the idempotency token; no NotifiedAt column needed
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await LeadershipAsync(ctx);
        await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);
        await host.Service.ExpireDueAsync();
        host.Notifications.ClearReceivedCalls();

        Assert.Equal(0, await host.Service.ExpireDueAsync());

        await host.Notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Expire_KeepsThePhotoCopy()
    {
        // expiry is reversible like a retraction: republishing is one click and must not silently come back blind
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);
        await LeadershipAsync(ctx);
        await PublishedWithPhotoAsync(host);
        await ExpireInThePastAsync(ctx);

        await host.Service.ExpireDueAsync();

        await using var db = ctx.NewContext();
        Assert.Equal("oeffentlich.jpg", (await db.OeffentlicheFahndungen.SingleAsync()).PhotoFileName);
        host.PublicFiles.DidNotReceive().Delete("oeffentlich.jpg");
    }

    [Fact]
    public async Task AnExpiredNotice_LeavesTheBoardEvenBeforeTheSweepRuns()
    {
        // the worker is not a security control: the read path already filters by date
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);
        await ExpireInThePastAsync(ctx);
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    // ---- the Discord announcement ----

    [Fact]
    public async Task Publishing_PushesOneCardToThePublicChannel()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        await host.Discord.Received(1).PushWantedCardAsync(NotificationType.PublicWantedPublished,
            Arg.Is<WantedDiscordEmbed>(c => c.ProfilePath == "/gesucht/FIB-FA-2026-0001"), Arg.Any<CancellationToken>());
        // the old one-line text is gone, or Discord would show two posts for one notice
        await host.Discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publishing_WritesNoInAppNotification()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        await host.Notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.PublicWantedPublished, Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCard_NamesNeitherTheRecordCaseNumberNorACodenameNorTheCharge()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        var text = WantedCardText.Of(PushedCard(host));
        Assert.Contains("Max Mustermann", text, StringComparison.Ordinal);
        Assert.Contains("FIB-FA-2026-0001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FIB-P-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOOSE-P-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Falcon", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Waffenhandel", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCard_ShowsTheActiveHintsInTheirSortOrder()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        // created in the wrong order and with an inactive one in between: only sort order and the switch count
        var late = await HintAsync(ctx, "fluchtgefährdet", "Warning", sortOrder: 20);
        var off = await HintAsync(ctx, "abgeschaltet", "Info", active: false, sortOrder: 15);
        var early = await HintAsync(ctx, "bewaffnet", "Error", sortOrder: 10);
        var id = await DraftAsync(host);
        await host.Service.SetHintsAsync(id, [late, off, early], Leader());

        await host.Service.PublishAsync(id, null, Leader());

        Assert.Equal("🔴 bewaffnet\n🟠 fluchtgefährdet", WantedCardText.Field(PushedCard(host), "Warnhinweise"));
    }

    [Fact]
    public async Task TheCard_WithoutHints_HasNoHintField()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        Assert.Null(WantedCardText.Field(PushedCard(host), "Warnhinweise"));
    }

    [Fact]
    public async Task TheCard_ColoursByTheStoredHazardLevel()
    {
        // the seeded file scores 80, which is Kritisch
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        var card = PushedCard(host);
        Assert.Equal(0xE53935, card.Color);
        Assert.Equal("Kritisch", WantedCardText.Field(card, "Gefahrenstufe"));
    }

    [Fact]
    public async Task TheCard_ShowsThePublishedPhotoAsTheImage()
    {
        using var ctx = await SeededAsync();
        var host = await PhotoHostAsync(ctx);

        await PublishedWithPhotoAsync(host);

        var card = PushedCard(host);
        Assert.Equal("/gesucht/FIB-FA-2026-0001/foto", card.ImagePath);
        Assert.Null(card.ThumbnailPath);
    }

    [Fact]
    public async Task TheCard_WithoutAPhoto_HasNoImage()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        var card = PushedCard(host);
        Assert.Null(card.ImagePath);
        Assert.Null(card.ThumbnailPath);
    }

    [Fact]
    public async Task TheCard_CarriesThePublicationMoment()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await PublishedAsync(host);

        var stamp = PushedCard(host).TimestampUtc;
        Assert.NotNull(stamp);
        Assert.InRange(stamp!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ANoticeTheBoardDoesNotList_IsNotPosted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await using (var db = ctx.NewContext())
        {
            // expired before it went out
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).ExpiresAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        await host.Service.PublishAsync(id, null, Leader());

        await host.Discord.DidNotReceive().PushWantedCardAsync(Arg.Any<NotificationType>(),
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARankTwoRequest_PushesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await host.Service.PublishAsync(id, "Begründung", Junior());

        await host.Discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await host.Discord.DidNotReceive().PushWantedCardAsync(Arg.Any<NotificationType>(),
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApprovingARequest_PushesTheSameOneCard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Service.PublishAsync(id, "Begründung", Junior());
        var requestId = await OpenRequestIdAsync(ctx);

        await host.Service.ApprovePublicationRequestAsync(requestId, null, Leader());

        await host.Discord.Received(1).PushWantedCardAsync(NotificationType.PublicWantedPublished,
            Arg.Is<WantedDiscordEmbed>(c => c.ProfilePath == "/gesucht/FIB-FA-2026-0001"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetractingCapturingAndEditing_PushNothing()
    {
        // a depublication notice in the public channel would point at a page that is no longer there
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann" }, Leader());
        await host.Service.CapturedAsync(id, Leader());
        await host.Service.RetractAsync(id, "Grund", Leader());

        await host.Discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await host.Discord.DidNotReceive().PushWantedCardAsync(Arg.Any<NotificationType>(),
            Arg.Any<WantedDiscordEmbed>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ThePushComposer_TakesOnlyOutwardTypes()
    {
        // the riddle is the parameter type, not the care taken: PublicWantedCard structurally cannot carry a
        // PersonId, the internal case number, a codename or a score, so the message cannot either
        var method = typeof(PublicWantedService)
            .GetMethod("PushPublishedAsync", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var offenders = method!.GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t != typeof(CancellationToken) && t != typeof(PublicWantedCard)
                && t != typeof(PublicWantedDetail))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der Discord-Text darf nur Nach-außen-Typen sehen: " + string.Join(", ", offenders));
    }

    // ---- phase 5 helpers ----

    /// <summary>Draft with the first file photo selected, published; the copy is named oeffentlich.jpg.</summary>
    private static async Task<string> PublishedWithPhotoAsync(Host host)
    {
        var id = await DraftAsync(host);
        await host.Service.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = "foto-1" }, Leader());
        await host.Service.PublishAsync(id, null, Leader());
        return id;
    }

    /// <summary>Adds one warning to the value list and returns its id.</summary>
    private static async Task<string> HintAsync(SqliteTestContext ctx, string name, string colour, bool active = true,
        int sortOrder = 10)
    {
        await using var db = ctx.NewContext();
        var row = new Warnhinweis { Name = name, Colour = colour, SortOrder = sortOrder, IsActive = active };
        db.Warnhinweise.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Publishes and then backdates the expiry, which the editor path would round to the end of a day.</summary>
    private static async Task ExpireInThePastAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>One leadership account, so the expiry sweep has somebody to tell.</summary>
    private static async Task LeadershipAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("chef", Rank.Director));
        await db.SaveChangesAsync();
    }

    private static async Task<string> OpenRequestIdAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        return (await db.Requests.SingleAsync(a => a.Type == RequestType.Veroeffentlichung)).Id;
    }

    private static async Task SetChargeAsync(SqliteTestContext ctx, string id, string html)
    {
        await using var db = ctx.NewContext();
        var row = await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id);
        row.ChargeHtml = html;
        await db.SaveChangesAsync();
    }

    /// <summary>Drops the 10-second snapshot; a test that changes the database around the service has to say so.</summary>
    private static void DropCache(Host host) => host.Cache.Remove("OeffentlicheFahndungen");

    /// <summary>The one card the host pushed to the public channel.</summary>
    private static WantedDiscordEmbed PushedCard(Host host)
        => (WantedDiscordEmbed)host.Discord.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDiscordWebhookService.PushWantedCardAsync))
            .GetArguments()[1]!;
}
