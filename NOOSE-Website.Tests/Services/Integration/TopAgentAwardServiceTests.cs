using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Gamification;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TopAgentAwardService"/> (schedule guard, Discord post, personnel entries).</summary>
public sealed class TopAgentAwardServiceTests
{
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("low").WithRank(Rank.Trainee).Build();

    private static List<LeaderboardEntry> Top(params string[] ids)
        => ids.Select((id, i) => new LeaderboardEntry(i + 1, id, $"CN-{id}", 100 - i, 5, 2, 1, 1, 1, 1)).ToList();

    /// <summary>Out-of-competition rows as the service returns them: no place, so Position 0.</summary>
    private static List<LeaderboardEntry> Benched(params string[] ids)
        => ids.Select((id, i) => new LeaderboardEntry(0, id, $"CN-{id}", 100 - i, 5, 2, 1, 1, 1, 1)).ToList();

    private static void SeedConfig(SqliteTestContext ctx, bool enabled, int interval, bool note, DateTime? lastRun = null)
    {
        using var db = ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.BestAgentEnabled, Value = enabled ? "true" : "false" });
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.BestAgentIntervalDays, Value = interval.ToString(CultureInfo.InvariantCulture) });
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.BestAgentCreateNote, Value = note ? "true" : "false" });
        if (lastRun is not null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.BestAgentLastRun, Value = lastRun.Value.ToString("o", CultureInfo.InvariantCulture) });
        }
        db.SaveChanges();
    }

    private static (TopAgentAwardService Svc, IDiscordWebhookService Discord) New(
        SqliteTestContext ctx, IReadOnlyList<LeaderboardEntry> ranked,
        IReadOnlyList<LeaderboardEntry>? benched = null, bool posts = true, bool discordLive = true)
    {
        var gam = Substitute.For<IGamificationService>();
        gam.GetLeaderboardAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new LeaderboardView(ranked, benched ?? []));
        var discord = Substitute.For<IDiscordWebhookService>();
        discord.PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(posts);
        // a real config is required, not decorative: without it the failed-post branch reads a null config.
        // discordLive false is the "intentionally off" state, which the service treats as announced.
        discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(
            discordLive, DiscordWebhookConfig.DefaultBaseUrl,
            discordLive
                ? new Dictionary<NotificationType, string?> { [NotificationType.Announcement] = "https://discord.test/hook" }
                : new Dictionary<NotificationType, string?>(),
            new Dictionary<NotificationType, string?>(),
            false));
        return (new TopAgentAwardService(ctx.Factory, gam, discord), discord);
    }

    private static List<string> PostedContents(IDiscordWebhookService discord)
        => discord.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDiscordWebhookService.PushCustomAsync))
            .Select(c => (string)c.GetArguments()[1]!)
            .ToList();

    private static List<string?> PostedHrefs(IDiscordWebhookService discord)
        => discord.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDiscordWebhookService.PushCustomAsync))
            .Select(c => (string?)c.GetArguments()[2])
            .ToList();

    [Fact]
    public async Task RunDue_Disabled_DoesNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: false, interval: 7, note: true);
        var (svc, discord) = New(ctx, Top("a1", "a2", "a3"));

        var result = await svc.RunDueAsync(DateTime.UtcNow);

        Assert.Equal(0, result.Ranked);
        Assert.False(result.Posted);
        await discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDue_IntervalNotElapsed_Skips()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true, lastRun: DateTime.UtcNow.AddDays(-2));
        var (svc, discord) = New(ctx, Top("a1", "a2", "a3"));

        var result = await svc.RunDueAsync(DateTime.UtcNow);

        Assert.Equal(0, result.Ranked);
        Assert.False(result.Posted);
        await discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDue_Elapsed_PostsAnnouncement_FilesNotes_StampsLastRun()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true, lastRun: DateTime.UtcNow.AddDays(-10));
        var (svc, discord) = New(ctx, Top("a1", "a2", "a3"));

        var result = await svc.RunDueAsync(DateTime.UtcNow);

        Assert.Equal(3, result.Ranked);
        Assert.True(result.Posted);
        await discord.Received(1).PushCustomAsync(NotificationType.Announcement, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var check = ctx.NewContext();
        var notes = await check.AgentNotes.ToListAsync();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Equal(AgentNoteKind.Commendation, n.Kind));
        Assert.All(notes, n => Assert.Contains("KW", n.Text));
        Assert.NotNull((await check.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.BestAgentLastRun)).Value);
    }

    [Fact]
    public async Task RunDue_NoteToggleOff_PostsButFilesNoNotes()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: false, lastRun: DateTime.UtcNow.AddDays(-10));
        var (svc, discord) = New(ctx, Top("a1", "a2", "a3"));

        await svc.RunDueAsync(DateTime.UtcNow);

        await discord.Received(1).PushCustomAsync(NotificationType.Announcement, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        using var check = ctx.NewContext();
        Assert.Equal(0, await check.AgentNotes.CountAsync());
    }

    [Fact]
    public async Task RunNow_RequiresLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, _) = New(ctx, Top("a1"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RunNowAsync(Junior()));
    }

    [Fact]
    public async Task RunNow_TwiceInSameWeek_DoesNotDuplicateNotes()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, _) = New(ctx, Top("a1"));

        await svc.RunNowAsync(Leader());
        await svc.RunNowAsync(Leader());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.AgentNotes.CountAsync(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task RunNow_Interval7_KeepsWeeklyWordingAndLinksTheWeeklyBoard()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, discord) = New(ctx, Top("a1"));

        await svc.RunNowAsync(Leader());

        Assert.StartsWith("🏆 **Beste Agenten der Woche (KW ", PostedContents(discord)[0]);
        Assert.Equal("/bestenliste?zeitraum=Week", PostedHrefs(discord)[0]);
    }

    [Fact]
    public async Task RunNow_Interval30_PostsMonthlyWording_AndLinksTheMonthlyBoard()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 30, note: true);
        var (svc, discord) = New(ctx, Top("a1"));

        await svc.RunNowAsync(Leader());

        var content = PostedContents(discord)[0];
        Assert.StartsWith("🏆 **Beste Agenten des Monats**", content);
        Assert.DoesNotContain("KW", content);
        Assert.Equal("/bestenliste?zeitraum=Month", PostedHrefs(discord)[0]);

        using var check = ctx.NewContext();
        Assert.Contains("des Monats (bis ", (await check.AgentNotes.SingleAsync()).Text);
    }

    [Fact]
    public async Task RunDue_LeadershipBlock_IsAppendedBelowTheMedals()
    {
        using var ctx = new SqliteTestContext();
        // RunDueAsync takes the instant, so the expected week cannot drift from the one the service used
        var nowUtc = DateTime.UtcNow;
        var localNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime();
        SeedConfig(ctx, enabled: true, interval: 7, note: true, lastRun: nowUtc.AddDays(-10));
        var (svc, discord) = New(ctx, Top("a1", "a2", "a3"), Benched("l1", "l2"));

        await svc.RunDueAsync(nowUtc);

        // asserted as one whole string so a builder refactor cannot quietly drop the separating blank line
        var expected = string.Join('\n',
            $"🏆 **Beste Agenten der Woche (KW {ISOWeek.GetWeekOfYear(localNow)})**",
            "🥇 CN-a1 — 100 Punkte",
            "🥈 CN-a2 — 99 Punkte",
            "🥉 CN-a3 — 98 Punkte",
            "",
            "**Führungsebene (außer Wertung)**",
            "• CN-l1 — 100 Punkte",
            "• CN-l2 — 99 Punkte");
        Assert.Equal(expected, PostedContents(discord)[0]);
    }

    [Fact]
    public async Task RunNow_Leadership_GetsNoCommendation()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, _) = New(ctx, Top("a1"), Benched("l1"));

        await svc.RunNowAsync(Leader());

        using var check = ctx.NewContext();
        Assert.Equal("a1", (await check.AgentNotes.SingleAsync()).AgentId);
    }

    [Fact]
    public async Task RunNow_OnlyLeadershipScored_PostsNothingButAdvancesTheCadence()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, discord) = New(ctx, ranked: [], benched: Benched("l1"));

        var result = await svc.RunNowAsync(Leader());

        Assert.Equal(0, result.Ranked);
        Assert.Equal(1, result.OutOfCompetition);
        Assert.False(result.Posted);
        Assert.True(result.Announced);
        await discord.DidNotReceive().PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        using var check = ctx.NewContext();
        Assert.Equal(0, await check.AgentNotes.CountAsync());
        Assert.NotNull((await check.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.BestAgentLastRun)).Value);
    }

    [Fact]
    public async Task RunDue_RetryAfterFailedPost_FilesOneNotePerAgentPerPeriod()
    {
        using var ctx = new SqliteTestContext();
        var start = DateTime.UtcNow;
        SeedConfig(ctx, enabled: true, interval: 14, note: true, lastRun: start.AddDays(-20));
        var (svc, discord) = New(ctx, Top("a1"), posts: false);

        await svc.RunDueAsync(start);
        await svc.RunDueAsync(start.AddDays(1)); // the daily retry: a failed post left LastRun stale on purpose

        await discord.Received(2).PushCustomAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        using var check = ctx.NewContext();
        Assert.Equal(1, await check.AgentNotes.CountAsync(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task RunDue_NextPeriod_FilesASecondNote()
    {
        using var ctx = new SqliteTestContext();
        var start = DateTime.UtcNow;
        SeedConfig(ctx, enabled: true, interval: 14, note: true, lastRun: start.AddDays(-20));
        var (svc, _) = New(ctx, Top("a1"));

        await svc.RunDueAsync(start);
        await svc.RunDueAsync(start.AddDays(15));

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.AgentNotes.CountAsync(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task RunDue_LegacyWeeklyNote_StillDeduplicates()
    {
        using var ctx = new SqliteTestContext();
        var nowUtc = DateTime.UtcNow;
        var localNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime();
        SeedConfig(ctx, enabled: true, interval: 7, note: true, lastRun: nowUtc.AddDays(-10));
        using (var db = ctx.NewContext())
        {
            // the note format that shipped before the wording became period-aware, and old enough to miss the date floor
            db.AgentNotes.Add(new AgentNote
            {
                AgentId = "a1",
                Kind = AgentNoteKind.Commendation,
                EntryDate = localNow.AddDays(-30),
                Text = $"<p>Bester Agent der Woche KW {ISOWeek.GetWeekOfYear(localNow)}/{ISOWeek.GetYear(localNow)} — Platz 1 (10 Punkte).</p>",
            });
            db.SaveChanges();
        }
        var (svc, _) = New(ctx, Top("a1"));

        await svc.RunDueAsync(nowUtc);

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.AgentNotes.CountAsync(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task RunNow_EscapesMarkdownInCodenames()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var ranked = new List<LeaderboardEntry> { new(1, "a1", "Bo*nd_x", 10, 0, 0, 0, 0, 0, 0) };
        var (svc, discord) = New(ctx, ranked, Benched("l1"));

        await svc.RunNowAsync(Leader());

        var content = PostedContents(discord)[0];
        Assert.Contains(@"Bo\*nd\_x", content);
        // the bold header sits after the codename lines, so an unescaped name would swallow it
        Assert.Contains("**Führungsebene (außer Wertung)**", content);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(-5, 7)]
    [InlineData(100000, 366)]
    [InlineData(30, 30)]
    public async Task SaveConfig_ClampsIntervalDays(int input, int expected)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = New(ctx, Top("a1"));

        await svc.SaveConfigAsync(new TopAgentConfigInput(true, input, true), Leader());

        Assert.Equal(expected, (await svc.GetConfigAsync()).IntervalDays);
    }

    [Fact]
    public async Task ReadConfig_ClampsAStoredIntervalAboveTheCeiling()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 100000, note: true);
        var (svc, _) = New(ctx, Top("a1"));

        Assert.Equal(366, (await svc.GetConfigAsync()).IntervalDays);
    }

    [Fact]
    public async Task RunDue_OnScheduleRun_IsNotSuppressedByTheNoteFloor()
    {
        using var ctx = new SqliteTestContext();
        var start = DateTime.UtcNow;
        SeedConfig(ctx, enabled: true, interval: 14, note: true, lastRun: start.AddDays(-20));
        var (svc, _) = New(ctx, Top("a1"));

        await svc.RunDueAsync(start);
        // the earliest run the scheduler lets through is interval minus its tolerance, so the note floor must be wider
        await svc.RunDueAsync(start.AddDays(13.5));

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.AgentNotes.CountAsync(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task RunNow_DiscordOff_CountsAsAnnounced_AndAdvancesTheCadence()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, _) = New(ctx, Top("a1"), posts: false, discordLive: false);

        var result = await svc.RunNowAsync(Leader());

        Assert.Equal(1, result.Ranked);
        Assert.False(result.Posted);
        Assert.True(result.Announced); // off on purpose, so retrying would never help
        using var check = ctx.NewContext();
        Assert.NotNull((await check.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.BestAgentLastRun)).Value);
    }

    [Fact]
    public async Task RunNow_SendFailed_HoldsTheCadenceForARetry()
    {
        using var ctx = new SqliteTestContext();
        SeedConfig(ctx, enabled: true, interval: 7, note: true);
        var (svc, _) = New(ctx, Top("a1"), posts: false);

        var result = await svc.RunNowAsync(Leader());

        Assert.False(result.Posted);
        Assert.False(result.Announced);
        using var check = ctx.NewContext();
        Assert.False(await check.SystemSettings.AnyAsync(s => s.Key == SystemSettingKeys.BestAgentLastRun));
    }
}
