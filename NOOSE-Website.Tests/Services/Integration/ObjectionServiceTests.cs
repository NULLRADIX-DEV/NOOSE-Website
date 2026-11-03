using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Citizen objections: who may file one, who may decide it, and what upholding one requires first.</summary>
public sealed class ObjectionServiceTests
{
    private const string PersonId = "p1";
    private const string ProfileId = "b1";
    private const string OtherProfileId = "b2";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    /// <summary>Senior Special Agent: reads the section, does not decide.</summary>
    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen(string id = "buerger")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Civilian).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        ObjectionService Service,
        PublicWantedService Wanted,
        IMemoryCache Cache,
        ICaseService Cases,
        INotificationService Notifications);

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

        var wanted = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<NOOSE_Website.Infrastructure.Storage.IFileStorageService>(),
            Substitute.For<NOOSE_Website.Infrastructure.Storage.IPublicWantedPhotoStorageService>(),
            Substitute.For<INotificationService>(), new TipPriorityService(factory),
            Substitute.For<IDiscordWebhookService>(), Substitute.For<IPressReleaseService>(), cache);

        var notifications = Substitute.For<INotificationService>();
        var cases = Substitute.For<ICaseService>();
        var buergerService = new BuergerService(factory);
        var service = new ObjectionService(factory, modules, buergerService, caseNumbers, notifications, wanted, cases);
        return new Host(service, wanted, cache, cases, notifications);
    }

    /// <summary>Modules on, a clean file, two citizen profiles, and one published notice.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in new[] { PublicModules.Wanted, PublicModules.Objection })
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == key)).IsEnabled = true;
        }

        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            p.ThreatScore = 60;
        }));
        // distinct DiscordId: the column defaults to "" and carries a unique index
        db.Users.Add(new Agent
        {
            Id = "buerger", UserName = "buerger", DiscordId = "1001", Status = AgentStatus.Civilian,
        });
        db.Users.Add(new Agent
        {
            Id = "buerger2", UserName = "buerger2", DiscordId = "1002", Status = AgentStatus.Civilian,
        });
        db.BuergerProfile.Add(new BuergerProfil
        {
            // the notice is published for "Max Mustermann", and only that person may dispute it
            Id = ProfileId, UserId = "buerger", FirstName = "Max", LastName = "Mustermann",
        });
        db.Users.Add(new Agent
        {
            Id = "fremd", UserName = "fremd", DiscordId = "1003", Status = AgentStatus.Civilian,
        });
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = "profil-fremd", UserId = "fremd", FirstName = "Anna", LastName = "Andere",
        });
        db.BuergerProfile.Add(new BuergerProfil
        {
            // a second account of the same name: the tests about quotas and foreign lists are about the account,
            // not about the identity gate
            Id = OtherProfileId, UserId = "buerger2", FirstName = "Max", LastName = "Mustermann",
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    /// <summary>Publishes a notice for the seeded file and returns its public case number.</summary>
    private static async Task<string> PublishedAsync(SqliteTestContext ctx, Host host)
    {
        var id = await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());
        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).ChargeHtml = "<p>Vorwurf</p>";
            await db.SaveChangesAsync();
        }
        await host.Wanted.PublishAsync(id, null, Leader());

        await using var read = ctx.NewContext();
        return (await read.OeffentlicheFahndungen.SingleAsync(f => f.Id == id)).CaseNumber!;
    }

    private static ObjectionInput Input(string caseNumber, string? text = null) => new()
    {
        WantedCaseNumber = caseNumber,
        Text = text ?? "Das bin nicht ich. Ich war zur angegebenen Zeit nachweislich in Paleto Bay.",
    };

    private static async Task RetractAsync(SqliteTestContext ctx, Host host, string wantedCaseNumber)
    {
        await using var db = ctx.NewContext();
        var id = await db.OeffentlicheFahndungen.Where(f => f.CaseNumber == wantedCaseNumber)
            .Select(f => f.Id).SingleAsync();
        await host.Wanted.RetractAsync(id, "Einspruch plausibel", Leader());
    }

    private static async Task<string> FiledAsync(SqliteTestContext ctx, Host host)
    {
        var caseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(caseNumber), Citizen());

        await using var db = ctx.NewContext();
        return (await db.FahndungEinsprueche.SingleAsync()).Id;
    }

    // ---- filing ----

    [Fact]
    public async Task OnlyTheNamedPersonMayObject()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen("fremd")));

        Assert.Equal(ObjectionRules.NotTheNamedPerson, error.Message);
    }

    [Fact]
    public async Task ARefusedObjectionLeavesNoRow()
    {
        // the guard sits before the write, so a refusal must not have spent a case number or a quota slot
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen("fremd")));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.FahndungEinsprueche.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task FilingAgainstAPublishedNotice_MintsItsOwnCaseNumber()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);

        var caseNumber = await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());

        Assert.Contains("EIN", caseNumber, StringComparison.Ordinal);
        await using var db = ctx.NewContext();
        var row = await db.FahndungEinsprueche.SingleAsync();
        Assert.Equal(ObjectionStatus.Neu, row.Status);
        Assert.Equal(ProfileId, row.CitizenProfileId);
        Assert.Null(row.DecisionNote);
    }

    [Fact]
    public async Task FilingAgainstADraft_ReadsAsNoSuchNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());

        // resolved through the public read path, which sits behind the suppression belt: a draft does not exist
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input("FIB-FA-2026-0001"), Citizen()));
    }

    [Fact]
    public async Task FilingAgainstARetractedNotice_ReadsAsNoSuchNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await RetractAsync(ctx, host, wantedCaseNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen()));
    }

    [Fact]
    public async Task AShortText_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber, "stimmt nicht"), Citizen()));
    }

    [Fact]
    public async Task ASecondOpenObjectionAgainstTheSameNotice_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen()));
    }

    [Fact]
    public async Task AnotherCitizen_MayObjectToTheSameNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());

        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen("buerger2"));

        await using var db = ctx.NewContext();
        Assert.Equal(2, await db.FahndungEinsprueche.CountAsync());
    }

    [Fact]
    public async Task TheDailyQuota_CountsDeletedObjectionsToo()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        for (var i = 0; i < ObjectionRules.PerDay; i++)
        {
            await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
            // decide it so the per-notice cap does not fire first, then delete it
            await using var db = ctx.NewContext();
            var row = await db.FahndungEinsprueche.OrderByDescending(e => e.CreatedAt).FirstAsync();
            row.Status = ObjectionStatus.Abgelehnt;
            // by hand, not Remove: this context has no audit interceptor, so Remove would delete the row for real
            // and it would leave the quota for the wrong reason
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // deleting must not refill the quota
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen()));
    }

    [Fact]
    public async Task ABlockedCitizen_MayNotFile()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await using (var db = ctx.NewContext())
        {
            (await db.BuergerProfile.SingleAsync(p => p.Id == ProfileId)).IsBlocked = true;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen()));
    }

    [Fact]
    public async Task WithTheModuleOff_FilingIsRefusedButTheOwnListStaysReadable()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Objection)).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen("buerger2")));
        Assert.Single(await host.Service.GetOwnAsync(Citizen()));
    }

    [Fact]
    public async Task TheOwnList_ShowsOnlyTheCallersObjections()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen("buerger2"));

        var mine = await host.Service.GetOwnAsync(Citizen());

        Assert.Single(mine);
        Assert.Equal(wantedCaseNumber, mine[0].WantedCaseNumber);
        Assert.Equal("Max Mustermann", mine[0].WantedDisplayName);
    }

    [Fact]
    public async Task TheOwnList_StillNamesTheNoticeAfterItWasRetracted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        await RetractAsync(ctx, host, wantedCaseNumber);

        // what the citizen objected to must stay legible to them even once it is offline
        var mine = await host.Service.GetOwnAsync(Citizen());
        Assert.Equal(wantedCaseNumber, Assert.Single(mine).WantedCaseNumber);
    }

    // ---- the desk ----

    [Fact]
    public async Task ATraineeAndACitizen_SeeNothingOfTheDesk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await FiledAsync(ctx, host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetListAsync(true, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetListAsync(true, Citizen()));
    }

    [Fact]
    public async Task ASeniorAgentAndTheSupervision_ReadTheDeskButDoNotDecide()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        Assert.Single(await host.Service.GetListAsync(true, Senior()));
        Assert.Single(await host.Service.GetListAsync(true, OnlyReader()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Abgelehnt, "Nicht plausibel.", Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Abgelehnt, "Nicht plausibel.", OnlyReader()));
    }

    [Fact]
    public async Task TheCountsAndTheTabs_ComeFromTheSamePass()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        Assert.Equal(new ObjectionCounts(1, 0), await host.Service.GetCountsAsync(Leader()));

        await host.Service.SetStatusAsync(id, ObjectionStatus.Abgelehnt, "Nicht plausibel.", Leader());

        Assert.Equal(new ObjectionCounts(0, 1), await host.Service.GetCountsAsync(Leader()));
        Assert.Empty(await host.Service.GetListAsync(true, Leader()));
        Assert.Single(await host.Service.GetListAsync(false, Leader()));
    }

    [Fact]
    public async Task RejectingWithoutAReason_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        // the citizen reads this text; a decision without one says nothing
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Abgelehnt, "   ", Leader()));
    }

    [Fact]
    public async Task AStepThatIsNotAllowed_IsRefusedRatherThanApplied()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);
        await host.Service.SetStatusAsync(id, ObjectionStatus.InPruefung, null, Leader());

        // Neu is not reachable again from anywhere
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Neu, null, Leader()));
    }

    // ---- upholding needs the notice offline ----

    [Fact]
    public async Task UpholdingWhileTheNoticeIsStillPublished_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Angenommen, "Der Einwand trifft zu.", Leader()));

        await using var db = ctx.NewContext();
        Assert.Equal(ObjectionStatus.Neu, (await db.FahndungEinsprueche.SingleAsync(e => e.Id == id)).Status);
    }

    [Fact]
    public async Task UpholdingAfterTheNoticeWasRetracted_Works()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string id;
        await using (var read = ctx.NewContext())
        {
            id = (await read.FahndungEinsprueche.SingleAsync()).Id;
        }
        await RetractAsync(ctx, host, wantedCaseNumber);

        await host.Service.SetStatusAsync(id, ObjectionStatus.Angenommen, "Der Einwand trifft zu.", Leader());

        await using var db = ctx.NewContext();
        var row = await db.FahndungEinsprueche.SingleAsync(e => e.Id == id);
        Assert.Equal(ObjectionStatus.Angenommen, row.Status);
        Assert.Equal("Der Einwand trifft zu.", row.DecisionNote);
        Assert.NotNull(row.DecidedAt);
        Assert.Equal("lead", row.DecidedById);
    }

    [Fact]
    public async Task UpholdingWhileTheNoticeIsCaptured_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string id, wantedId;
        await using (var read = ctx.NewContext())
        {
            id = (await read.FahndungEinsprueche.SingleAsync()).Id;
            wantedId = (await read.OeffentlicheFahndungen.SingleAsync()).Id;
        }
        await host.Wanted.CapturedAsync(wantedId, Leader());

        // a captured notice is still outside, in the archive
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, ObjectionStatus.Angenommen, "Der Einwand trifft zu.", Leader()));
    }

    [Fact]
    public async Task ReopeningADecision_ClearsTheAnswerTheCitizenRead()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);
        await host.Service.SetStatusAsync(id, ObjectionStatus.Abgelehnt, "Nicht plausibel.", Leader());

        await host.Service.SetStatusAsync(id, ObjectionStatus.InPruefung, null, Leader());

        await using var db = ctx.NewContext();
        var row = await db.FahndungEinsprueche.SingleAsync(e => e.Id == id);
        Assert.Null(row.DecisionNote);
        Assert.Null(row.DecidedAt);
        Assert.Null(row.DecidedById);
    }

    // ---- the case, and the bin ----

    [Fact]
    public async Task OpeningACase_RemembersItAndRefusesASecond()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);
        var @case = Seed.Case("v1", "Einspruch");
        await using (var db = ctx.NewContext())
        {
            db.Cases.Add(@case);
            await db.SaveChangesAsync();
        }
        host.Cases.CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(@case);

        var created = await host.Service.ToCaseAsync(id, null, Leader());

        Assert.Equal("v1", created);
        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal("v1", detail!.LinkedCaseId);
        Assert.Equal(@case.CaseNumber, detail.LinkedCaseNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.ToCaseAsync(id, null, Leader()));
    }

    [Fact]
    public async Task OpeningACase_IsLeadershipOnly()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.ToCaseAsync(id, null, Senior()));
    }

    [Fact]
    public async Task Deleting_IsSoftAndRestorable()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await FiledAsync(ctx, host);

        await host.Service.DeleteAsync(id, Leader());

        Assert.Empty(await host.Service.GetListAsync(true, Leader()));
        var trash = await host.Service.GetTrashAsync();
        Assert.Equal(id, Assert.Single(trash).Id);
        // and it leaves the citizen's own list as well
        Assert.Empty(await host.Service.GetOwnAsync(Citizen()));

        await host.Service.RestoreAsync(id, Leader());

        Assert.Single(await host.Service.GetListAsync(true, Leader()));
        Assert.Single(await host.Service.GetOwnAsync(Citizen()));
    }

    [Fact]
    public async Task TheDetail_NamesTheNoticeAndItsCurrentState()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string id;
        await using (var read = ctx.NewContext())
        {
            id = (await read.FahndungEinsprueche.SingleAsync()).Id;
        }

        var detail = await host.Service.GetAsync(id, Leader());

        Assert.NotNull(detail);
        Assert.Equal(wantedCaseNumber, detail!.WantedCaseNumber);
        Assert.Equal(PublicWantedStatus.Veroeffentlicht, detail.WantedStatus);
        Assert.Equal("Max Mustermann", detail.CitizenName);
        Assert.False(detail.CitizenIsBlocked);
    }

    [Fact]
    public async Task ADeletedNotice_DoesNotBreakTheDesk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string id, wantedId;
        await using (var read = ctx.NewContext())
        {
            id = (await read.FahndungEinsprueche.SingleAsync()).Id;
            wantedId = (await read.OeffentlicheFahndungen.SingleAsync()).Id;
        }
        await RetractAsync(ctx, host, wantedCaseNumber);
        await host.Wanted.DeleteAsync(wantedId, Leader());

        // the notice is gone from the filtered set; the desk must still show what was disputed
        var rows = await host.Service.GetListAsync(true, Leader());
        Assert.Equal(wantedCaseNumber, Assert.Single(rows).WantedCaseNumber);
        var detail = await host.Service.GetAsync(id, Leader());
        Assert.NotNull(detail);
        Assert.Equal(wantedCaseNumber, detail!.WantedCaseNumber);
    }

    [Fact]
    public async Task WithADeletedNotice_TheTabCountAndTheListStillAgree()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string wantedId;
        await using (var read = ctx.NewContext())
        {
            wantedId = (await read.OeffentlicheFahndungen.SingleAsync()).Id;
        }
        await RetractAsync(ctx, host, wantedCaseNumber);
        await host.Wanted.DeleteAsync(wantedId, Leader());

        // a badge that insists something is open while the list cannot find it is worse than either alone
        var counts = await host.Service.GetCountsAsync(Leader());
        Assert.Equal(counts.Open, (await host.Service.GetListAsync(true, Leader())).Count);
    }

    [Fact]
    public async Task RestoringAnOpenObjection_IsRefusedWhenAFreshOneTookItsPlace()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string first;
        await using (var read = ctx.NewContext())
        {
            first = (await read.FahndungEinsprueche.SingleAsync()).Id;
        }
        await host.Service.DeleteAsync(first, Leader());
        // the deleted one no longer occupies the notice, so a second submission goes through
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());

        // restoring is the second door onto "one open objection per notice and account"
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RestoreAsync(first, Leader()));
    }

    [Fact]
    public async Task RestoringADecidedObjection_IsNotBlockedByAnOpenOne()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var wantedCaseNumber = await PublishedAsync(ctx, host);
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());
        string first;
        await using (var read = ctx.NewContext())
        {
            first = (await read.FahndungEinsprueche.SingleAsync()).Id;
        }
        await host.Service.SetStatusAsync(first, ObjectionStatus.Abgelehnt, "Nicht plausibel.", Leader());
        await host.Service.DeleteAsync(first, Leader());
        await host.Service.SubmitAsync(Input(wantedCaseNumber), Citizen());

        // a decided objection occupies nothing; its history belongs back in the file
        await host.Service.RestoreAsync(first, Leader());

        Assert.Equal(2, (await host.Service.GetOwnAsync(Citizen())).Count);
    }
}
