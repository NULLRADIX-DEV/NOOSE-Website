using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Released monthly reports: text goes out, the frozen figures stay in.</summary>
public sealed class PublicReportServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger").WithStatus(AgentStatus.Civilian).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(PublicReportService Service, IMemoryCache Cache, TestDbContextFactory Factory);

    /// <summary>A recognisable figure and a named person, both of which the public snapshot must never carry.</summary>
    private const int ClassifiedCount = 4711;
    private const string TopPersonName = "Marek Kowalski";
    private const string TopPersonCaseNumber = "FIB-P-2026-0777";

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>
    /// The interceptor is what rewrites a <c>Remove</c> into a soft delete, so the recycle-bin tests would exercise a
    /// hard delete without it.
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
        // no ISituationReportService: the service must not take that dependency, because the public pages inject it
        return new Host(new PublicReportService(factory, modules, cache), cache, factory);
    }

    private static async Task<SqliteTestContext> SeededAsync(bool reportsOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.SituationReports)).IsEnabled = reportsOn;
        // distinct DiscordId: the column defaults to "" and carries a unique index
        db.Users.Add(new Agent
        {
            Id = "lead", UserName = "lead", DiscordId = "9001", Codename = "Falcon",
            Status = AgentStatus.Active, Rank = Rank.Director,
        });
        db.SituationReports.Add(new SituationReport
        {
            Id = "lb-august", Year = 2026, Month = 8, Title = "Lagebericht August 2026",
            SnapshotJson = SnapshotJson(),
        });
        db.SituationReports.Add(new SituationReport
        {
            Id = "lb-juli", Year = 2026, Month = 7, Title = "Lagebericht Juli 2026",
            SnapshotJson = SnapshotJson(),
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    /// <summary>A frozen snapshot shaped like the real one, carrying the two values that must stay inside.</summary>
    private static string SnapshotJson()
        => JsonSerializer.Serialize(new
        {
            Metrics = new { Classified = ClassifiedCount, People = 120 },
            TopPeople = new[]
            {
                new { Name = TopPersonName, CaseNumber = TopPersonCaseNumber, Href = "/personen/abc", Score = 91 },
            },
        });

    /// <summary>Flips the module and drops the 10 s snapshot so the change is visible now.</summary>
    private static async Task ModuleAsync(SqliteTestContext ctx, Host host, bool on)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.SituationReports)).IsEnabled = on;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheModule");
    }

    private static PublicReportInput Draft(string anchor = "lb-august", string title = "Lage im August",
        string html = "<p>Die Behörde hat im August drei Ausschreibungen abgeschlossen.</p>")
        => new() { SituationReportId = anchor, Title = title, DraftHtml = html };

    private static async Task<string> PublishedAsync(Host host, string anchor = "lb-august")
    {
        var id = await host.Service.SaveDraftAsync(Draft(anchor), Leader());
        await host.Service.PublishAsync(id, Leader());
        return id;
    }

    // ---- the anchor decides the address ----

    [Fact]
    public async Task Creating_TakesThePeriodFromTheAnchor()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var id = await host.Service.SaveDraftAsync(Draft(anchor: "lb-juli"), Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(2026, row.Year);
        Assert.Equal(7, row.Month);
    }

    [Fact]
    public async Task CreatingWithoutAnAnchor_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SaveDraftAsync(new PublicReportInput { Title = "Ohne Anker" }, Leader()));
    }

    [Fact]
    public async Task AnUnknownAnchor_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SaveDraftAsync(Draft(anchor: "gibt-es-nicht"), Leader()));
    }

    [Fact]
    public async Task ASecondReportForTheSameMonth_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SaveDraftAsync(Draft(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SaveDraftAsync(Draft(title: "Zweiter Versuch"), Leader()));
    }

    [Fact]
    public async Task Editing_LeavesTheAnchorAndThePeriodAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        // a caller that names another anchor while editing changes nothing but the title
        await host.Service.SaveDraftAsync(
            new PublicReportInput { Id = id, SituationReportId = "lb-juli", Title = "Umbenannt" }, Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(8, row.Month);
        Assert.Equal("lb-august", row.SituationReportId);
    }

    [Fact]
    public async Task ThePicker_OffersOnlyMonthsWithoutAText()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Equal(2, (await host.Service.GetAnchorsAsync(Leader())).Count);

        await host.Service.SaveDraftAsync(Draft(), Leader());

        var anchor = Assert.Single(await host.Service.GetAnchorsAsync(Leader()));
        Assert.Equal("lb-juli", anchor.Id);
    }

    [Fact]
    public async Task ThePicker_DoesNotOfferADeletedMonthlyReport()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await SoftDeleteAnchorAsync(ctx, host, "lb-august");

        var anchor = Assert.Single(await host.Service.GetAnchorsAsync(Leader()));
        Assert.Equal("lb-juli", anchor.Id);
    }

    [Fact]
    public async Task ADeletedMonthlyReport_CannotBeAnchoredOn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await SoftDeleteAnchorAsync(ctx, host, "lb-august");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SaveDraftAsync(Draft(), Leader()));
    }

    [Fact]
    public async Task DeletingThePublicText_FreesTheMonthInThePicker()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        Assert.DoesNotContain("lb-august", (await host.Service.GetAnchorsAsync(Leader())).Select(a => a.Id));

        await host.Service.DeleteAsync(id, Leader());

        Assert.Contains("lb-august", (await host.Service.GetAnchorsAsync(Leader())).Select(a => a.Id));
    }

    // ---- drafts stay inside ----

    [Fact]
    public async Task ADraft_IsNotReachableAnonymously()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SaveDraftAsync(Draft(), Leader());

        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
        Assert.Null(await host.Service.GetByPeriodAsync("2026-08"));
    }

    [Fact]
    public async Task Publishing_PutsTitleAndBodyOutside()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        var card = Assert.Single((await host.Service.GetPublishedAsync()).Cards);
        Assert.Equal("Lage im August", card.Title);

        var view = await host.Service.GetByPeriodAsync("2026-08");
        Assert.Contains("drei Ausschreibungen", view!.Html);
    }

    [Fact]
    public async Task SavingADraft_LeavesThePublishedCopyAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.SaveDraftAsync(
            new PublicReportInput { Id = id, Title = "Ganz anderer Titel", DraftHtml = "<p>Neuer Text.</p>" },
            Leader());

        var view = await host.Service.GetByPeriodAsync("2026-08");
        Assert.Equal("Lage im August", view!.Title);
        Assert.Contains("drei Ausschreibungen", view.Html);
    }

    [Fact]
    public async Task ADivergingDraft_IsFlaggedInThePanel()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        Assert.False((await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).DraftDiffers);

        await host.Service.SaveDraftAsync(new PublicReportInput { Id = id, Title = "Korrigiert" }, Leader());

        Assert.True((await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).DraftDiffers);
    }

    [Fact]
    public async Task ANullDraftBody_LeavesTheStoredTextAlone()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await host.Service.SaveDraftAsync(new PublicReportInput { Id = id, Title = "Nur der Titel" }, Leader());

        Assert.Contains("drei Ausschreibungen", (await host.Service.GetDraftAsync(id, Leader()))!.Html);
    }

    [Fact]
    public async Task AnEmptyDraftBody_ClearsTheStoredText()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());

        await host.Service.SaveDraftAsync(
            new PublicReportInput { Id = id, Title = "Leer", DraftHtml = "" }, Leader());

        Assert.Equal(string.Empty, (await host.Service.GetDraftAsync(id, Leader()))!.Html);
    }

    [Fact]
    public async Task AnEmptyDraft_IsNotPublishable()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(html: "<p>   </p>"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task AReportThatIsOnlyAPicture_Counts()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(
            Draft(html: "<p><img src=\"data:image/png;base64,iVBORw0KGgo=\" /></p>"), Leader());

        await host.Service.PublishAsync(id, Leader());

        Assert.Single((await host.Service.GetPublishedAsync()).Cards);
    }

    [Fact]
    public async Task Markup_IsCleanedOnSaveAndOnPublish()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(
            Draft(html: "<p>Sicher.</p><script>alert(1)</script>"), Leader());

        Assert.DoesNotContain("<script", (await host.Service.GetDraftAsync(id, Leader()))!.Html,
            StringComparison.OrdinalIgnoreCase);

        // and again at publication, which is the moment the markup becomes reachable anonymously
        await ScriptIntoDraftAsync(ctx, id);
        await host.Service.PublishAsync(id, Leader());

        Assert.DoesNotContain("<script", (await host.Service.GetByPeriodAsync("2026-08"))!.Html,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes raw markup past the service, so publishing has something left to clean.</summary>
    private static async Task ScriptIntoDraftAsync(SqliteTestContext ctx, string id)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheLageberichte.SingleAsync(r => r.Id == id)).DraftHtml =
            "<p>Sicher.</p><script>alert(1)</script>";
        await db.SaveChangesAsync();
    }

    // ---- the publication date is minted once ----

    [Fact]
    public async Task CorrectingAReport_KeepsItsPublicationDate()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        var first = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        await BackdateAsync(ctx, host, id, first);

        await host.Service.SaveDraftAsync(new PublicReportInput { Id = id, Title = "Tippfehler weg" }, Leader());
        await host.Service.PublishAsync(id, Leader());

        Assert.Equal(first, (await host.Service.GetPublishedAsync()).Cards.Single().PublishedAt);
    }

    [Fact]
    public async Task ARetractedReportThatGoesOutAgain_IsDatedAnew()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await BackdateAsync(ctx, host, id, new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc));

        await host.Service.RetractAsync(id, Leader());
        await host.Service.PublishAsync(id, Leader());

        var published = (await host.Service.GetPublishedAsync()).Cards.Single().PublishedAt;
        Assert.NotNull(published);
        Assert.True(published > new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc));
    }

    private static async Task BackdateAsync(SqliteTestContext ctx, Host host, string id, DateTime at)
    {
        await using var db = ctx.NewContext();
        (await db.OeffentlicheLageberichte.SingleAsync(r => r.Id == id)).PublishedAt = at;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheLageberichte");
    }

    // ---- retract, delete, restore ----

    [Fact]
    public async Task Retracting_TakesItOffTheAirAndKeepsTheDraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await host.Service.RetractAsync(id, Leader());

        Assert.Null(await host.Service.GetByPeriodAsync("2026-08"));
        Assert.Contains("drei Ausschreibungen", (await host.Service.GetDraftAsync(id, Leader()))!.Html);
    }

    [Fact]
    public async Task DeletingBeforeRetracting_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.DeleteAsync(id, Leader()));
    }

    [Fact]
    public async Task Restoring_ComesBackAsADraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, Leader());
        await host.Service.DeleteAsync(id, Leader());

        Assert.Single(await host.Service.GetTrashAsync());

        await host.Service.RestoreAsync(id, Leader());

        var row = (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id);
        Assert.Equal(PublicReportStatus.Entwurf, row.Status);
        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
    }

    [Fact]
    public async Task RestoringIntoAMonthThatIsTakenAgain_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.SaveDraftAsync(Draft(), Leader());
        await host.Service.DeleteAsync(id, Leader());

        // the month is free again, so a second text for it is legitimate — and the restore must not add a twin
        await host.Service.SaveDraftAsync(Draft(title: "Neuer Anlauf"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RestoreAsync(id, Leader()));
    }

    // ---- the module switch ----

    [Fact]
    public async Task ModuleOff_HidesHubAndArticle_ButRetractingStillWorks()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await ModuleAsync(ctx, host, on: false);

        Assert.Empty((await host.Service.GetPublishedAsync()).Cards);
        Assert.Null(await host.Service.GetByPeriodAsync("2026-08"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));

        // depublishing never asks the module: otherwise the kill switch would make retracting impossible
        await host.Service.RetractAsync(id, Leader());
        Assert.Equal(PublicReportStatus.Entwurf,
            (await host.Service.GetAllAsync(Leader())).Single(r => r.Id == id).Status);
    }

    // ---- the deleted anchor ----

    [Fact]
    public async Task ADeletedMonthlyReport_KeepsThePublicTextOnlineAndInTheList()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await SoftDeleteAnchorAsync(ctx, host, "lb-august");

        // the optional navigation is LEFT joined, so the row stays in the panel instead of vanishing from a list that
        // a count would still include
        var row = Assert.Single(await host.Service.GetAllAsync(Leader()));
        Assert.Equal(id, row.Id);
        Assert.False(row.HasAnchor);

        // and cleaning up the internal archive is not a silent depublication: the outward text carries no field of it
        Assert.NotNull(await host.Service.GetByPeriodAsync("2026-08"));
    }

    private static async Task SoftDeleteAnchorAsync(SqliteTestContext ctx, Host host, string anchorId)
    {
        await using var db = ctx.NewContext();
        var anchor = await db.SituationReports.SingleAsync(l => l.Id == anchorId);
        anchor.IsDeleted = true;
        anchor.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        host.Cache.Remove("OeffentlicheLageberichte");
    }

    // ---- the point of the whole phase ----

    [Fact]
    public async Task NoFieldOfTheFrozenSnapshot_ReachesThePublicSnapshot()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        // the whole outward snapshot, serialised: a leak through any field would show up here rather than only in the
        // one property a targeted assertion happened to look at
        var outward = JsonSerializer.Serialize(await host.Service.GetPublishedAsync());

        Assert.DoesNotContain(ClassifiedCount.ToString(), outward, StringComparison.Ordinal);
        Assert.DoesNotContain(TopPersonName, outward, StringComparison.Ordinal);
        Assert.DoesNotContain(TopPersonCaseNumber, outward, StringComparison.Ordinal);
        Assert.DoesNotContain("/personen/", outward, StringComparison.Ordinal);
        Assert.DoesNotContain("lb-august", outward, StringComparison.Ordinal);
        Assert.DoesNotContain("Falcon", outward, StringComparison.Ordinal);
    }

    // ---- addresses ----

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-8")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnparsablePeriod_ReadsAsNotFound(string? period)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        Assert.Null(await host.Service.GetByPeriodAsync(period));
    }

    // ---- rights ----

    [Fact]
    public async Task TheReadOnlySupervision_ReadsButDoesNotWrite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        Assert.Single(await host.Service.GetAllAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(anchor: "lb-juli"), OnlyReader()));
    }

    [Fact]
    public async Task ASeniorAgentWithoutLeadership_MayNotWrite()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(), Senior()));
    }

    [Fact]
    public async Task ACitizenAccount_ReachesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SaveDraftAsync(Draft(), Citizen()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.GetAllAsync(Citizen()));
    }
}
