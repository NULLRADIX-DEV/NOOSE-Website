using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The published situation level: an editorial statement with its own one-step history.</summary>
public sealed class PublicSituationServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger").WithStatus(AgentStatus.Civilian).Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(PublicSituationService Service, IMemoryCache Cache, TestDbContextFactory Factory);

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);
        return new Host(new PublicSituationService(factory, modules, cache), cache, factory);
    }

    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.HazardLevel)).IsEnabled = moduleOn;
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task PutAsync(SqliteTestContext ctx, string key, string? value)
    {
        await using var db = ctx.NewContext();
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<string?> ReadAsync(SqliteTestContext ctx, string key)
    {
        await using var db = ctx.NewContext();
        return await db.SystemSettings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
    }

    // --- silence -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingEverSet_SaysNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Null(await host.Service.GetPublishedAsync());
    }

    [Fact]
    public async Task ModuleOff_SaysNothing_EvenWithALevelStored()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());

        Assert.Null(await host.Service.GetPublishedAsync());
        // and the panel still sees it: switching the module off is how a level is taken back, not how it is deleted
        Assert.Equal(PublicSituationLevel.Hoch, (await host.Service.GetForEditAsync(Leader()))!.Level);
    }

    [Fact]
    public async Task AStrayStoredLevel_SaysNothingInsteadOfThrowing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        // the row is hand-editable, and an [AllowAnonymous] page must not answer an unknown value with an exception
        await PutAsync(ctx, SystemSettingKeys.PublicSituationLevel, "Panisch");

        Assert.Null(await host.Service.GetPublishedAsync());
    }

    [Fact]
    public async Task AnUnreachableDatabase_SaysNothing()
    {
        var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Kritisch }, Leader());
        // warm the module snapshot so the failure lands on the content read, not on the switch
        Assert.NotNull(await host.Service.GetPublishedAsync());
        host.Cache.Remove("OeffentlicheGefahrenlage");

        ctx.Dispose();

        Assert.Null(await host.Service.GetPublishedAsync());
    }

    // --- the one-step history ------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstSet_StampsTheDateAndLeavesThePredecessorEmpty()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SetAsync(
            new PublicSituationInput { Level = PublicSituationLevel.Erhoeht, Note = "Lage angespannt." }, Leader());

        var state = await host.Service.GetPublishedAsync();
        Assert.NotNull(state);
        Assert.Equal(PublicSituationLevel.Erhoeht, state.Level);
        Assert.Equal("Lage angespannt.", state.Note);
        Assert.NotNull(state.Since);
        Assert.Null(state.Previous);
    }

    [Fact]
    public async Task CorrectingOnlyTheAssessment_DoesNotMoveTheDate()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(
            new PublicSituationInput { Level = PublicSituationLevel.Hoch, Note = "Lage angespant." }, Leader());
        var before = (await host.Service.GetPublishedAsync())!.Since;

        await host.Service.SetAsync(
            new PublicSituationInput { Level = PublicSituationLevel.Hoch, Note = "Lage angespannt." }, Leader());

        var after = await host.Service.GetPublishedAsync();
        Assert.Equal("Lage angespannt.", after!.Note);
        // the page shows this date as "seit"; a typo fix must not claim the situation changed today
        Assert.Equal(before, after.Since);
        Assert.Null(after.Previous);
    }

    [Fact]
    public async Task ChangingTheLevel_RecordsThePredecessorAndRedatesIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Niedrig }, Leader());
        // an older date, so the redate is visible rather than a same-tick coincidence
        await PutAsync(ctx, SystemSettingKeys.PublicSituationSince,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
        host.Cache.Remove("OeffentlicheGefahrenlage");

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Kritisch }, Leader());

        var state = await host.Service.GetPublishedAsync();
        Assert.Equal(PublicSituationLevel.Kritisch, state!.Level);
        Assert.Equal(PublicSituationLevel.Niedrig, state.Previous);
        Assert.True(state.Since > new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SettingTheSameValuesAgain_WritesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var input = new PublicSituationInput { Level = PublicSituationLevel.Erhoeht, Note = "Unverändert." };
        await host.Service.SetAsync(input, Leader());

        int before;
        await using (var db = ctx.NewContext())
        {
            before = await db.AuditLogs.CountAsync();
        }

        await host.Service.SetAsync(input, Leader());

        await using var after = ctx.NewContext();
        Assert.Equal(before, await after.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task AnUnparsableStoredDate_ReadsAsNoDate()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());
        await PutAsync(ctx, SystemSettingKeys.PublicSituationSince, "gestern");
        host.Cache.Remove("OeffentlicheGefahrenlage");

        var state = await host.Service.GetPublishedAsync();
        Assert.NotNull(state);
        Assert.Null(state.Since);
    }

    [Fact]
    public async Task AStrayStoredPredecessor_IsDroppedButKeepsTheLevel()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());
        await PutAsync(ctx, SystemSettingKeys.PublicSituationPrevious, "Panisch");
        host.Cache.Remove("OeffentlicheGefahrenlage");

        var state = await host.Service.GetPublishedAsync();
        Assert.Equal(PublicSituationLevel.Hoch, state!.Level);
        Assert.Null(state.Previous);
    }

    // --- the assessment ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAssessment_IsTrimmedAndCapped()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SetAsync(new PublicSituationInput
        {
            Level = PublicSituationLevel.Hoch,
            Note = "  " + new string('x', SituationRules.MaxNote + 100) + "  ",
        }, Leader());

        var state = await host.Service.GetPublishedAsync();
        Assert.Equal(SituationRules.MaxNote, state!.Note.Length);
    }

    [Fact]
    public async Task TheAssessment_IsPlainTextAndIsNotCleaned()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        const string typed = "<b>fett</b> & \"zitiert\"\nZweite Zeile.";

        await host.Service.SetAsync(
            new PublicSituationInput { Level = PublicSituationLevel.Erhoeht, Note = typed }, Leader());

        // stored verbatim: it is plain text, and the page renders it escaped with the line breaks preserved
        Assert.Equal(typed, (await host.Service.GetPublishedAsync())!.Note);
    }

    [Fact]
    public async Task AnEmptyAssessment_IsAllowed()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Niedrig }, Leader());

        var state = await host.Service.GetPublishedAsync();
        Assert.Equal(PublicSituationLevel.Niedrig, state!.Level);
        Assert.Equal(string.Empty, state.Note);
    }

    // --- storage shape and bookkeeping ---------------------------------------------------------------------------

    [Theory]
    [InlineData(PublicSituationLevel.Niedrig, "Niedrig")]
    [InlineData(PublicSituationLevel.Erhoeht, "Erhoeht")]
    [InlineData(PublicSituationLevel.Hoch, "Hoch")]
    [InlineData(PublicSituationLevel.Schwer, "Schwer")]
    [InlineData(PublicSituationLevel.Kritisch, "Kritisch")]
    public async Task TheLevel_IsStoredByItsKeyAndNotByItsDefconLabel(PublicSituationLevel level, string key)
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        // a bare number would say nothing to whoever reads the settings table
        await host.Service.SetAsync(new PublicSituationInput { Level = level }, Leader());

        Assert.Equal(key, await ReadAsync(ctx, SystemSettingKeys.PublicSituationLevel));
        Assert.NotEqual(PublicSituationLevelDisplay.Name(level),
            await ReadAsync(ctx, SystemSettingKeys.PublicSituationLevel));
    }

    [Fact]
    public async Task TheFifthLevel_RoundTripsAsLevelAndAsPredecessor()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Schwer }, Leader());

        var state = await host.Service.GetPublishedAsync();
        Assert.Equal(PublicSituationLevel.Schwer, state!.Level);
        Assert.Equal(PublicSituationLevel.Hoch, state.Previous);

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Kritisch }, Leader());

        state = await host.Service.GetPublishedAsync();
        Assert.Equal(PublicSituationLevel.Kritisch, state!.Level);
        Assert.Equal(PublicSituationLevel.Schwer, state.Previous);
        Assert.Equal("Schwer", await ReadAsync(ctx, SystemSettingKeys.PublicSituationPrevious));
    }

    [Fact]
    public async Task AFourLevelRowStoredBeforeTheFifth_StillReadsTheSame()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        // rows written before DEFCON 2 existed: the four old keys and nothing else
        await PutAsync(ctx, SystemSettingKeys.PublicSituationLevel, "Kritisch");
        await PutAsync(ctx, SystemSettingKeys.PublicSituationPrevious, "Hoch");

        var state = await host.Service.GetPublishedAsync();

        Assert.Equal(PublicSituationLevel.Kritisch, state!.Level);
        Assert.Equal(PublicSituationLevel.Hoch, state.Previous);
    }

    [Fact]
    public async Task ThePredecessor_IsStoredByItsKeyToo()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Erhoeht }, Leader());

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());

        // written by one branch and read back by another; a label here would parse as unknown and lose the trend
        Assert.Equal("Erhoeht", await ReadAsync(ctx, SystemSettingKeys.PublicSituationPrevious));
        Assert.Equal(PublicSituationLevel.Erhoeht, (await host.Service.GetPublishedAsync())!.Previous);
    }

    [Fact]
    public async Task SetAsync_WritesAReadableAuditRow()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Service.SetAsync(
            new PublicSituationInput { Level = PublicSituationLevel.Hoch, Note = "Grund." }, Leader());

        await using var db = ctx.NewContext();
        var row = await db.AuditLogs
            .SingleAsync(a => a.EntityType == PublicSituationService.AuditType);
        Assert.Equal("gefahrenlage", row.EntityId);
        // read back as JSON rather than matched as a substring: the serialiser escapes non-ASCII, so the German
        // field name is stored escaped and a raw string match would fail on a row that is perfectly correct
        var changes = JsonSerializer.Deserialize<Dictionary<string, string?[]>>(row.ChangesJson!)!;
        var level = changes["Gefahrenlage"];
        Assert.Null(level[0]);
        // the wiring, not the wording: the audit row carries the German label, which is free to be reworded
        Assert.Equal(PublicSituationLevelDisplay.Name(PublicSituationLevel.Hoch), level[1]);
        var note = changes["Einschätzung"];
        Assert.Equal(string.Empty, note[0]);
        Assert.Equal("Grund.", note[1]);
    }

    [Fact]
    public async Task SavingDropsTheSnapshot_SoTheNextReadIsCurrent()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Niedrig }, Leader());
        Assert.Equal(PublicSituationLevel.Niedrig, (await host.Service.GetPublishedAsync())!.Level);

        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Kritisch }, Leader());

        Assert.Equal(PublicSituationLevel.Kritisch, (await host.Service.GetPublishedAsync())!.Level);
    }

    [Fact]
    public async Task SettingWorksWhileTheModuleIsOff_AndAppearsWhenItIsSwitchedOn()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var host = NewHost(ctx);
        // there is no draft step, so the write must not be gated on the switch — otherwise the first level ever set
        // would have to go live before anyone could write it
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Erhoeht }, Leader());
        Assert.Null(await host.Service.GetPublishedAsync());

        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.HazardLevel)).IsEnabled = true;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        Assert.Equal(PublicSituationLevel.Erhoeht, (await host.Service.GetPublishedAsync())!.Level);
    }

    // --- rights --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyReader_MayReadButNotSet()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Leader());

        Assert.NotNull(await host.Service.GetForEditAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Niedrig }, OnlyReader()));
    }

    [Fact]
    public async Task SeniorWithoutLeadership_MayNeitherReadNorSet()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetForEditAsync(Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Senior()));
    }

    [Fact]
    public async Task ACitizenAccount_MayNotSet()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        // a citizen carries no rank claim at all, which is why the internal check runs before the rank check
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Citizen()));
    }

    [Fact]
    public async Task APartner_MayNotSet()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Hoch }, Partner()));
    }

    [Fact]
    public async Task AnAnonymousReader_NeedsNoPrincipal()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetAsync(new PublicSituationInput { Level = PublicSituationLevel.Erhoeht }, Leader());

        // the public read path takes no actor: /lage is reachable logged out
        Assert.NotNull(await host.Service.GetPublishedAsync());
    }
}
