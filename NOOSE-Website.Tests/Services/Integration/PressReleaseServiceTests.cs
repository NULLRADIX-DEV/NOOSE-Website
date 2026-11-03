using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Press releases: draft, publish, retract — and what stays inside while a draft is a draft.</summary>
public sealed class PressReleaseServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    /// <summary>Rank 2: may set a notice to captured, may not publish a release.</summary>
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
        PressReleaseService Service,
        PublicModuleService Modules,
        IDiscordWebhookService Discord,
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
        var counter = 0;
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{++counter:0000}");

        var discord = Substitute.For<IDiscordWebhookService>();
        var service = new PressReleaseService(factory, modules, caseNumbers, discord, cache);
        return new Host(service, modules, discord, cache);
    }

    /// <summary>Module rows seeded, the press module on, one leadership account for the publisher FK.</summary>
    private static async Task<SqliteTestContext> SeededAsync(bool pressOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Press)).IsEnabled = pressOn;
        // distinct DiscordId: the column defaults to "" and carries a unique index
        db.Users.Add(new Agent
        {
            Id = "lead", UserName = "lead", DiscordId = "9001", Codename = "Falcon",
            Status = AgentStatus.Active, Rank = Rank.Director,
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    /// <summary>Flips a module and drops the 10 s snapshot so the change is visible now.</summary>
    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, bool on)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Press)).IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
    }

    private static PressInput Draft(string title = "Festnahme", string teaser = "Kurzfassung",
        string html = "<p>Der Text.</p>")
        => new() { Title = title, Teaser = teaser, DraftHtml = html };

    private static async Task<string> PublishedAsync(Host host)
    {
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());
        await host.Service.PublishAsync(id, Leader());
        return id;
    }

    // ---- drafts stay inside ----

    [Fact]
    public async Task ADraft_IsNotReachableAnonymously()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SaveDraftAsync(Draft(), Leader());

        var snapshot = await host.Service.GetPublishedAsync();
        Assert.Empty(snapshot.Cards);
    }

    [Fact]
    public async Task ADraft_HasNoCaseNumberAtAll()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Null(row.CaseNumber);
    }

    [Fact]
    public async Task SavingADraft_DoesNotChangeWhatVisitorsRead()
    {
        // headline and teaser are snapshotted too, unlike an editorial page: a release is a dated statement, and a
        // saved typo fix must not silently rewrite what already went out
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;

        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Andere Schlagzeile", Teaser = "Anderer Teaser", DraftHtml = "<p>Neu.</p>" },
            Leader());

        var view = await host.Service.GetByCaseNumberAsync(number);
        Assert.Contains("Der Text.", view!.Html, StringComparison.Ordinal);
        Assert.Equal("Festnahme", view.Title);
        Assert.Equal("Kurzfassung", view.Teaser);
    }

    [Fact]
    public async Task PublishingAgain_CarriesTheCorrectedHeadlineOut()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;

        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Korrigierte Schlagzeile", Teaser = "Kurzfassung" }, Leader());
        await host.Service.PublishAsync(id, Leader());

        var view = await host.Service.GetByCaseNumberAsync(number);
        Assert.Equal("Korrigierte Schlagzeile", view!.Title);
    }

    [Fact]
    public async Task ALegacyNooseNumber_StillOpensThePublishedRelease()
    {
        // the permanent address of a release was posted to Discord before the rename
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;
        var legacy = number.Replace("FIB-", "NOOSE-", StringComparison.Ordinal);

        var view = await host.Service.GetByCaseNumberAsync($" {legacy.ToLowerInvariant()} ");

        Assert.Equal(number, view!.CaseNumber);
        Assert.Null(await host.Service.GetByCaseNumberAsync("NOOSE-PM-2026-9999"));
    }

    [Fact]
    public async Task AChangedHeadline_ShowsAsADivergingDraft()
    {
        // the chip has to notice a title-only change, otherwise the panel calls a stale headline up to date
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        Assert.False((await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).DraftDiffers);

        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Andere Schlagzeile", Teaser = "Kurzfassung" }, Leader());

        Assert.True((await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).DraftDiffers);
    }

    [Fact]
    public async Task ANullDraftHtml_LeavesTheStoredBodyAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Anderer Titel", Teaser = "Kurzfassung", DraftHtml = null }, Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.Contains("Der Text.", draft!.Html, StringComparison.Ordinal);
        Assert.Equal("Anderer Titel", draft.Title);
    }

    [Fact]
    public async Task AnEmptyDraftHtml_ClearsTheStoredBody()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Festnahme", Teaser = "Kurzfassung", DraftHtml = string.Empty },
            Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.Equal(string.Empty, draft!.Html);
    }

    [Fact]
    public async Task SavingADraft_CleansTheMarkup()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.SaveDraftAsync(
            Draft(html: "<p>Text</p><script>alert(1)</script>"), Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.DoesNotContain("<script", draft!.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADraftWithoutATitle_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SaveDraftAsync(Draft(title: "   "), Leader()));
    }

    // ---- publishing ----

    [Fact]
    public async Task Publishing_MintsAPressCaseNumberAndGoesLive()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await PublishedAsync(host);

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.StartsWith($"FIB-{PressReleaseService.CaseNumberPrefix}-", row.CaseNumber, StringComparison.Ordinal);
        Assert.Equal(PressReleaseStatus.Veroeffentlicht, row.Status);
        Assert.Single((await host.Service.GetPublishedAsync()).Cards);
        Assert.Equal("Falcon", row.PublishedByName);
    }

    [Fact]
    public async Task PublishingAgain_KeepsTheSameCaseNumber()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var first = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber;

        await host.Service.RetractAsync(id, Leader());
        await host.Service.PublishAsync(id, Leader());

        var second = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber;
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task PublishingAnEmptyDraft_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(html: "   "), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task PublishingAnImageOnlyDraft_IsAllowed()
    {
        // a release that is only a picture is content; PlainText alone would have rejected it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(
            Draft(html: "<img src=\"data:image/png;base64,AAA\" />"), Leader());

        await host.Service.PublishAsync(id, Leader());

        Assert.Single((await host.Service.GetPublishedAsync()).Cards);
    }

    [Fact]
    public async Task PublishingWithoutATeaser_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(teaser: "  "), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task Publishing_CleansTheMarkupAgain()
    {
        // publishing is the moment the HTML becomes reachable anonymously, so it is cleaned there too
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());
        await using (var db = ctx.NewContext())
        {
            (await db.Pressemitteilungen.SingleAsync(p => p.Id == id)).DraftHtml =
                "<p>Text</p><script>alert(1)</script>";
            await db.SaveChangesAsync();
        }

        await host.Service.PublishAsync(id, Leader());

        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;
        var view = await host.Service.GetByCaseNumberAsync(number);
        Assert.DoesNotContain("<script", view!.Html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Discord: once per release ----

    [Fact]
    public async Task Publishing_AnnouncesOnceInThePublicChannel()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        await host.Discord.Received(1).PushCustomAsync(NotificationType.PublicPressPublished,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepublishingAfterARetraction_DoesNotAnnounceASecondTime()
    {
        // retract, correct, publish again is a legitimate round trip, and the channel must not hear it twice
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.RetractAsync(id, Leader());
        await host.Service.PublishAsync(id, Leader());

        await host.Discord.Received(1).PushCustomAsync(NotificationType.PublicPressPublished,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheAnnouncement_NamesTheCaseNumberAndLinksToThePublicPage()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;

        await host.Discord.Received(1).PushCustomAsync(NotificationType.PublicPressPublished,
            Arg.Is<string>(m => m.Contains(number, StringComparison.Ordinal)),
            $"/presse/{number}", Arg.Any<CancellationToken>());
    }

    // ---- retracting and deleting ----

    [Fact]
    public async Task Retracting_TakesItOffTheOutsideButKeepsContentAndNumber()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;

        await host.Service.RetractAsync(id, Leader());

        Assert.Null(await host.Service.GetByCaseNumberAsync(number));
        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(PressReleaseStatus.Entwurf, row.Status);
        Assert.Equal(number, row.CaseNumber);
    }

    [Fact]
    public async Task Retracting_WorksWhileTheModuleIsOff()
    {
        // publishing needs a live module, depublishing never: the kill switch must not trap a release outside
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await ModuleAsync(ctx, host, on: false);

        await host.Service.RetractAsync(id, Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(PressReleaseStatus.Entwurf, row.Status);
    }

    [Fact]
    public async Task PublishingWhileTheModuleIsOff_IsRefused()
    {
        using var ctx = await SeededAsync(pressOn: false);
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task ThePublicReadPath_IsDarkWhileTheModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var number = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).CaseNumber!;
        await ModuleAsync(ctx, host, on: false);

        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
        Assert.Null(await host.Service.GetByCaseNumberAsync(number));
    }

    [Fact]
    public async Task DeletingAPublishedRelease_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.DeleteAsync(id, Leader()));
    }

    [Fact]
    public async Task DeletingADraft_MovesItToTheBin()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await host.Service.DeleteAsync(id, Leader());

        Assert.Empty(await host.Service.GetAllAsync(Leader()));
        Assert.Single(await host.Service.GetTrashAsync());
    }

    [Fact]
    public async Task Restoring_BringsItBackAsADraft()
    {
        // nothing goes public again as a side effect of undoing a delete
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, Leader());
        await host.Service.DeleteAsync(id, Leader());

        await host.Service.RestoreAsync(id, Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(PressReleaseStatus.Entwurf, row.Status);
        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
    }

    // ---- the automatic draft after a capture ----

    private static PublicWantedCard Card()
        => new("FIB-FA-2026-0001", PublicWantedKind.Fahndung, "Frank Miller", null, false,
            HazardLevel.High, DateTime.UtcNow, []);

    [Fact]
    public async Task ACaptureDraft_IsADraftAndNothingElse()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.CreateCaptureDraftAsync(Card(), Leader());

        var row = Assert.Single(await host.Service.GetAllAsync(Leader()));
        Assert.Equal(PressReleaseStatus.Entwurf, row.Status);
        Assert.Null(row.CaseNumber);
        Assert.Contains("Frank Miller", row.Title, StringComparison.Ordinal);
        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
    }

    [Fact]
    public async Task ACaptureDraft_IsWrittenForEveryActorWhoMayCloseANotice()
    {
        // RequirePublicWantedWrite has no rank floor, so a rank-2 agent may set a notice to captured. The draft is
        // agency output triggered by that already-authorised write, not something the actor publishes themselves —
        // gating it on leadership would silently drop the automatism for most captures
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.CreateCaptureDraftAsync(Card(), Junior());

        var row = Assert.Single(await host.Service.GetAllAsync(Leader()));
        Assert.Equal(PressReleaseStatus.Entwurf, row.Status);
    }

    [Fact]
    public async Task ACaptureDraft_IsRefusedForAnAccountThatCouldNotCloseANotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateCaptureDraftAsync(Card(), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateCaptureDraftAsync(Card(), Citizen()));
    }

    [Fact]
    public async Task ACaptureDraft_IsNotWrittenWhileTheModuleIsOff()
    {
        // a draft exists to be published; one per capture that nobody can publish is noise, not a safety net
        using var ctx = await SeededAsync(pressOn: false);
        var host = NewHost(ctx);

        await host.Service.CreateCaptureDraftAsync(Card(), Leader());

        Assert.Empty(await host.Service.GetAllAsync(Leader()));
    }

    // ---- guards ----

    [Fact]
    public async Task TheReadOnlySupervision_ReadsButDoesNotWrite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        Assert.Single(await host.Service.GetAllAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(), OnlyReader()));
    }

    [Fact]
    public async Task ASeniorAgent_MayNotWrite()
    {
        // rank 3 publishes wanted notices, but the voice of the agency stays with leadership
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(), Senior()));
    }

    [Fact]
    public async Task ACitizenAccount_MayNeitherReadNorWrite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(), Citizen()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetAllAsync(Citizen()));
    }

    // ---- the publication date is part of the dated statement ----

    [Fact]
    public async Task CorrectingAReleaseKeepsItsPublicationDate()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        var march = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        await BackdateAsync(ctx, host, id, march);

        // a typo fix on a release that is already out
        await host.Service.SaveDraftAsync(
            new PressInput { Id = id, Title = "Festnahme", Teaser = "Kurzfassung", DraftHtml = "<p>Der Text, korrigiert.</p>" },
            Leader());
        await host.Service.PublishAsync(id, Leader());

        // otherwise the hub, which sorts by this date, would move a March release to today's top spot
        Assert.Equal(march, (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).PublishedAt);
        var card = Assert.Single((await host.Service.GetPublishedAsync()).Cards);
        Assert.Equal(march, card.PublishedAt);
    }

    [Fact]
    public async Task ARetractedReleaseThatGoesOutAgain_IsDatedAnew()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await BackdateAsync(ctx, host, id, new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc));

        await host.Service.RetractAsync(id, Leader());
        Assert.Null((await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).PublishedAt);

        await host.Service.PublishAsync(id, Leader());

        // it was off the air, so "since when does this stand outside" has a new answer
        var again = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).PublishedAt;
        Assert.NotNull(again);
        Assert.True(again > new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>Moves a publication into the past, which no service call can do.</summary>
    private static async Task BackdateAsync(SqliteTestContext ctx, Host host, string id, DateTime at)
    {
        await using var db = ctx.NewContext();
        (await db.Pressemitteilungen.SingleAsync(p => p.Id == id)).PublishedAt = at;
        await db.SaveChangesAsync();
        host.Cache.Remove("Pressemitteilungen");
    }
}
