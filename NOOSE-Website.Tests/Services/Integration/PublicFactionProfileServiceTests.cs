using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>What may leave the house about an organisation, and who may put it there.</summary>
public sealed class PublicFactionProfileServiceTests
{
    private const string FactionId = "11111111-1111-1111-1111-111111111111";
    private const string Description = "<p>Die Organisation kontrolliert mehrere Blocks im Süden.</p>";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger").WithStatus(AgentStatus.Civilian).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        PublicFactionProfileService Service,
        PublicModuleService Modules,
        IMemoryCache Cache);

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>The interceptor is what rewrites a Remove into a soft delete, so the trash tests need it.</remarks>
    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);
        return new Host(new PublicFactionProfileService(factory, modules, cache), modules, cache);
    }

    /// <summary>Seeds the module switches plus one clean faction file, and turns the organisation module on.</summary>
    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true, Action<Faction>? faction = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        if (moduleOn)
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Organisations);
            row.IsEnabled = true;
        }
        db.Factions.Add(Seed.Faction(FactionId, "Ballas", f =>
        {
            f.CaseNumber = "FIB-F-2026-0001";
            f.ThreatScore = 80;
            faction?.Invoke(f);
        }));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task<string> DraftAsync(Host host, ClaimsPrincipal? actor = null)
    {
        var id = await host.Service.CreateDraftFromFactionAsync(FactionId, actor ?? Leader());
        await host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput { Id = id, DisplayName = "Ballas", DescriptionHtml = Description },
            actor ?? Leader());
        return id;
    }

    private static async Task<string> PublishedAsync(Host host)
    {
        var id = await DraftAsync(host);
        await host.Service.PublishAsync(id, Leader());
        return id;
    }

    private static async Task ClassifyAsync(SqliteTestContext ctx, Action<Faction> flag)
    {
        await using var db = ctx.NewContext();
        var faction = await db.Factions.SingleAsync(f => f.Id == FactionId);
        flag(faction);
        await db.SaveChangesAsync();
    }

    private static void DropCache(Host host) => host.Cache.Remove("OeffentlicheFraktionsprofile");

    // ---- what is outside ----

    [Fact]
    public async Task ADraft_IsNotOutside_EvenWithAHighScore()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await DraftAsync(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task PublishingPutsItOutside_WithTheLevelAndNotTheScore()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await PublishedAsync(host);

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("Ballas", card.DisplayName);
        Assert.Equal(PublicFactionStanding.Beobachtet, card.Standing);
        // 80 maps to Critical; the number itself is on no outward type
        Assert.Equal(HazardLevel.Critical, card.HazardLevel);
        Assert.DoesNotContain("80", card.DescriptionHtml ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASoftDeletedButPublishedProfile_StaysHidden()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        // straight past the service, the way a stray write would: the read path has to hide it on its own
        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheFraktionsprofile.SingleAsync(p => p.Id == id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task AFileThatBecomesClassified_FallsOutOfTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        // the belt alone, without the retraction hook the faction service calls
        await ClassifyAsync(ctx, f => f.IsClassified = true);
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task ADeletedFile_TakesItsProfileOffTheBoard()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await ClassifyAsync(ctx, f => f.IsDeleted = true);
        DropCache(host);

        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task TheModuleSwitch_EmptiesTheBoardWithoutChangingARow()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        // publishing needs a live module
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task RenamingTheFaction_ChangesNothingOutside()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await using (var db = ctx.NewContext())
        {
            var faction = await db.Factions.SingleAsync(f => f.Id == FactionId);
            faction.Name = "Vagos";
            await db.SaveChangesAsync();
        }
        DropCache(host);

        var card = Assert.Single((await host.Service.GetBoardAsync()).Cards);
        Assert.Equal("Ballas", card.DisplayName);
    }

    // ---- the classification gate ----

    [Theory]
    [InlineData(nameof(Faction.IsClassified))]
    [InlineData(nameof(Faction.IsTRUClassified))]
    [InlineData(nameof(Faction.IsHRBClassified))]
    public async Task EverySecrecyFlag_BlocksPublication(string flag)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await ClassifyAsync(ctx, f =>
        {
            if (flag == nameof(Faction.IsClassified)) { f.IsClassified = true; }
            if (flag == nameof(Faction.IsTRUClassified)) { f.IsTRUClassified = true; }
            if (flag == nameof(Faction.IsHRBClassified)) { f.IsHRBClassified = true; }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
    }

    [Fact]
    public async Task ForSomeoneWhoMayNotReadClassifiedFiles_TheRefusalReadsLikeAMissingFile()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await ClassifyAsync(ctx, f => f.IsClassified = true);

        var forLeader = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PublishAsync(id, Leader()));
        var forSenior = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PublishAsync(id, Senior()));

        // the senior may publish but not read classified files; telling him it is one would leak the classification
        Assert.Contains("Verschlusssache", forLeader.Message, StringComparison.Ordinal);
        Assert.Equal("Akte nicht gefunden.", forSenior.Message);
    }

    [Fact]
    public async Task AClassifiedFile_HidesItsProfileFromTheManagementList()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);
        await ClassifyAsync(ctx, f => f.IsClassified = true);

        Assert.Empty(await host.Service.GetAllAsync(Senior()));
        Assert.Single(await host.Service.GetAllAsync(Leader()));
    }

    // ---- who may write ----

    [Fact]
    public async Task OnlyRankThreeAndUp_MayCreateADraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateDraftFromFactionAsync(FactionId, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateDraftFromFactionAsync(FactionId, Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateDraftFromFactionAsync(FactionId, Citizen()));
        // reads everything, writes nothing
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.CreateDraftFromFactionAsync(FactionId, Supervision()));

        Assert.NotNull(await host.Service.CreateDraftFromFactionAsync(FactionId, Senior()));
    }

    [Fact]
    public async Task TheSupervisionReadsTheList_AndAJuniorDoesNot()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);

        Assert.Single(await host.Service.GetAllAsync(Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetAllAsync(Junior()));
    }

    [Fact]
    public async Task TwoLiveProfilesPerFaction_AreImpossible()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.CreateDraftFromFactionAsync(FactionId, Leader()));
    }

    // ---- what the description may contain ----

    [Theory]
    [InlineData("<p>Siehe @{Person:11111111-1111-1111-1111-111111111111}.</p>")]
    [InlineData("<p>Anführer ist {{Name}}.</p>")]
    [InlineData("<p>Anführer ist {{Name ohne Ende.</p>")]
    [InlineData("")]
    public async Task AnUnpublishableDescription_IsRefused(string html)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromFactionAsync(FactionId, Leader());
        await host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput { Id = id, DisplayName = "Ballas", DescriptionHtml = html }, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PublishAsync(id, Leader()));
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task ADraftMayHoldAMention_ItJustCannotBePublished()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await host.Service.CreateDraftFromFactionAsync(FactionId, Leader());

        // a draft is internal; the check belongs to the moment the text becomes anonymously readable
        await host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput
            {
                Id = id,
                DisplayName = "Ballas",
                DescriptionHtml = "<p>Siehe @{Person:11111111-1111-1111-1111-111111111111}.</p>",
            }, Leader());

        var draft = await host.Service.GetDraftAsync(id, Leader());
        Assert.Contains("@{Person:", draft!.DescriptionHtml!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullDescription_LeavesTheStoredTextAlone_AndEmptyClearsIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput { Id = id, DisplayName = "Ballas Süd", DescriptionHtml = null }, Leader());
        Assert.Contains("Blocks", (await host.Service.GetDraftAsync(id, Leader()))!.DescriptionHtml!,
            StringComparison.Ordinal);

        await host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput { Id = id, DisplayName = "Ballas Süd", DescriptionHtml = "" }, Leader());
        Assert.True(string.IsNullOrEmpty((await host.Service.GetDraftAsync(id, Leader()))!.DescriptionHtml));
    }

    [Fact]
    public async Task AnUnknownStanding_IsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.UpdateSnapshotAsync(
            new PublicFactionProfileInput { Id = id, DisplayName = "Ballas", Standing = (PublicFactionStanding)99 },
            Leader()));
    }

    // ---- retracting and deleting ----

    [Fact]
    public async Task RetractingWorks_WhileTheModuleIsOff()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await using (var db = ctx.NewContext())
        {
            var module = await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Organisations);
            module.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        // taking something offline must never depend on a switch that is already hiding it
        await host.Service.RetractAsync(id, "Nicht mehr aktuell.", Leader());

        var row = await host.Service.GetForFactionAsync(FactionId, Leader());
        Assert.Equal(PublicProfileStatus.Zurueckgezogen, row!.Status);
    }

    [Fact]
    public async Task RetractingNeedsAReason()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RetractAsync(id, "  ", Leader()));
    }

    [Fact]
    public async Task APublishedProfile_CannotBeDeleted()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.DeleteAsync(id, Leader()));

        await host.Service.RetractAsync(id, "Nicht mehr aktuell.", Leader());
        await host.Service.DeleteAsync(id, Leader());
        Assert.Null(await host.Service.GetForFactionAsync(FactionId, Leader()));
    }

    [Fact]
    public async Task Deleting_IsSoftAndRestoreBringsItBackAsADraft()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);
        await host.Service.RetractAsync(id, "Nicht mehr aktuell.", Leader());
        await host.Service.DeleteAsync(id, Leader());

        var trash = await host.Service.GetTrashAsync();
        Assert.Single(trash);

        await host.Service.RestoreAsync(id, Leader());
        var row = await host.Service.GetForFactionAsync(FactionId, Leader());
        Assert.Equal(PublicProfileStatus.Entwurf, row!.Status);
        Assert.Null(row.PublishedAt);
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task RetractingDropsTheSnapshot_WithoutAnybodyClearingTheCacheByHand()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        // read once so the board is actually cached, then take it offline through the service
        Assert.Single((await host.Service.GetBoardAsync()).Cards);
        await host.Service.RetractAsync(id, "Nicht mehr aktuell.", Leader());

        // no DropCache here on purpose: this is the assertion that the one save path invalidates
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    [Fact]
    public async Task AFileClassifiedWhileTheProfileSatInTheBin_CannotBeRestored()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Service.DeleteAsync(id, Leader());

        await ClassifyAsync(ctx, f => f.IsClassified = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.RestoreAsync(id, Leader()));
    }

    [Fact]
    public async Task TheRecordHook_TakesAPublishedProfileOffline()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await PublishedAsync(host);

        await host.Service.RetractForRecordAsync(FactionId, "Akte gelöscht.", Leader());

        var row = await host.Service.GetForFactionAsync(FactionId, Leader());
        Assert.Equal(PublicProfileStatus.Zurueckgezogen, row!.Status);
        Assert.Equal("Akte gelöscht.", await ReasonAsync(ctx, row.Id));
        Assert.Empty((await host.Service.GetBoardAsync()).Cards);
    }

    private static async Task<string?> ReasonAsync(SqliteTestContext ctx, string id)
    {
        await using var db = ctx.NewContext();
        return (await db.OeffentlicheFraktionsprofile.SingleAsync(p => p.Id == id)).RetractedReason;
    }

    [Fact]
    public async Task RefreshingTheLevel_FollowsTheCurrentScore()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await PublishedAsync(host);

        await using (var db = ctx.NewContext())
        {
            var faction = await db.Factions.SingleAsync(f => f.Id == FactionId);
            faction.ThreatScore = 10;
            await db.SaveChangesAsync();
        }
        await host.Service.RefreshHazardLevelAsync(id, Leader());
        DropCache(host);

        Assert.Equal(HazardLevel.Low, (await host.Service.GetBoardAsync()).Cards.Single().HazardLevel);
    }
}
