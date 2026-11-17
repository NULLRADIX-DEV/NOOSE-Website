using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Gamification;

namespace NOOSE_Website.Services;

/// <summary>Current top-agent announcement schedule config.</summary>
public sealed record TopAgentConfig(bool Enabled, int IntervalDays, bool CreateNote, DateTime? LastRun);

/// <summary>Editable subset of <see cref="TopAgentConfig"/>.</summary>
public sealed record TopAgentConfigInput(bool Enabled, int IntervalDays, bool CreateNote);

/// <summary>Outcome of one run: placed agents, listed leadership and whether the post actually went out.</summary>
/// <remarks>Announced without Posted means Discord is intentionally off, not that the post failed.</remarks>
public sealed record TopAgentRunResult(int Ranked, int OutOfCompetition, bool Posted, bool Announced);

/// <summary>Periodically announces the top 3 agents of the interval to the Discord announcements channel (pinging @NOOSE)
/// and optionally files a positive personnel entry for each. Leadership is listed out of competition and never placed;
/// the wording follows the configured interval. Schedule + toggles live in SystemSettings.</summary>
public interface ITopAgentAwardService
{
    Task<TopAgentConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(TopAgentConfigInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Run if enabled and the interval has elapsed since the last run; an untouched run reports zeros.</summary>
    Task<TopAgentRunResult> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Force a run now (leadership only), ignoring enabled/interval; the counts are placed agents and listed leadership, not names in the post.</summary>
    Task<TopAgentRunResult> RunNowAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITopAgentAwardService" />
public sealed class TopAgentAwardService(
    IDbContextFactory<AppDbContext> dbFactory,
    IGamificationService gamification,
    IDiscordWebhookService discord) : ITopAgentAwardService
{
    private const int DefaultIntervalDays = 7;
    // clamped on write and read: the numeric field bounds are client-side, and a hand-edited row makes AddDays throw
    private const int MaxIntervalDays = 366;
    private const int TopN = 3;
    // a fixed 24h poll can reach the boundary tick a hair under N days; a half-day slack keeps the cadence from drifting to N+1
    private const double IntervalToleranceDays = 0.5;
    // wider than the scheduler slack, or a legitimate run at N-0.5 days is suppressed as a duplicate
    private const double NoteFloorSlackDays = IntervalToleranceDays + (1.0 / 24.0);
    // only this code opens a commendation this way, so it recognises its own notes without matching human text
    private const string NotePrefix = "<p>Bester Agent ";
    private static readonly string[] Medals = { "🥇", "🥈", "🥉" };
    // disabled or not yet due: nothing ran, so nothing was announced either
    private static readonly TopAgentRunResult NotDue = new(0, 0, Posted: false, Announced: false);

    public async Task<TopAgentConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadConfigAsync(db, cancellationToken);
    }

    public async Task SaveConfigAsync(TopAgentConfigInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        var interval = input.IntervalDays > 0 ? Math.Clamp(input.IntervalDays, 1, MaxIntervalDays) : DefaultIntervalDays;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.BestAgentEnabled, input.Enabled ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.BestAgentIntervalDays, interval.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetAsync(db, SystemSettingKeys.BestAgentCreateNote, input.CreateNote ? "true" : "false", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TopAgentRunResult> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var config = await GetConfigAsync(cancellationToken);
        if (!config.Enabled)
        {
            return NotDue;
        }
        if (config.LastRun is { } last && (nowUtc - last).TotalDays < config.IntervalDays - IntervalToleranceDays)
        {
            return NotDue; // interval not yet elapsed
        }
        var result = await AwardAsync(config, actor: null, nowUtc, cancellationToken);
        // advance the cadence only when the announcement actually went out (or Discord is off); a failed
        // post leaves LastRun stale so the next daily tick retries instead of silently losing the interval
        if (result.Announced)
        {
            await StampLastRunAsync(nowUtc, cancellationToken);
        }
        return result;
    }

    public async Task<TopAgentRunResult> RunNowAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        var nowUtc = DateTime.UtcNow;
        var config = await GetConfigAsync(cancellationToken);
        var result = await AwardAsync(config, actor, nowUtc, cancellationToken);
        if (result.Announced)
        {
            await StampLastRunAsync(nowUtc, cancellationToken);
        }
        return result;
    }

    private async Task<TopAgentRunResult> AwardAsync(
        TopAgentConfig config, ClaimsPrincipal? actor, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var board = await gamification.GetLeaderboardAsync(config.IntervalDays, TopN, cancellationToken);
        if (board.Ranked.Count == 0)
        {
            // leadership cannot win the award it hands out; advance the cadence anyway
            return new TopAgentRunResult(0, board.OutOfCompetition.Count, Posted: false, Announced: true);
        }

        // one instant for wording, note text and dedup floor; two DateTime.Now calls can straddle midnight
        var localNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime();
        var period = TopAgentPeriodDisplay.For(config.IntervalDays, localNow);

        // file the (idempotent) personnel notes before the post so a post retry never re-files them
        if (config.CreateNote)
        {
            await FileNotesAsync(board.Ranked, period, config.IntervalDays, localNow,
                actor?.GetCodename() ?? "System", cancellationToken);
        }

        // link to the board that produced these medals, not to the all-time default
        var href = period.PeriodQuery is null
            ? "/personal?tab=bestenliste"
            : $"/personal?tab=bestenliste&zeitraum={period.PeriodQuery}";
        var posted = await discord.PushCustomAsync(
            NotificationType.Announcement, BuildMessage(period.Headline, board), href, cancellationToken);
        // treat "sent" and "Discord intentionally off" as done; only a configured-but-failed post holds the cadence back to retry
        var announced = posted || !await DiscordAnnouncementLiveAsync(cancellationToken);
        return new TopAgentRunResult(board.Ranked.Count, board.OutOfCompetition.Count, posted, announced);
    }

    private static string BuildMessage(string headline, LeaderboardView board)
    {
        var sb = new StringBuilder();
        sb.Append("🏆 **Beste Agenten ").Append(headline).Append("**");
        for (var i = 0; i < board.Ranked.Count; i++)
        {
            var medal = i < Medals.Length ? Medals[i] : $"{i + 1}.";
            sb.Append('\n').Append(medal).Append(' ').Append(Safe(board.Ranked[i].Codename))
                .Append(" — ").Append(board.Ranked[i].Points).Append(" Punkte");
        }
        // TopN caps both slices, so the body stays far below the 2000-character limit of a Discord message
        if (board.OutOfCompetition.Count > 0)
        {
            sb.Append("\n\n**Führungsebene (außer Wertung)**");
            foreach (var entry in board.OutOfCompetition)
            {
                sb.Append("\n• ").Append(Safe(entry.Codename)).Append(" — ").Append(entry.Points).Append(" Punkte");
            }
        }
        return sb.ToString();
    }

    // escape inline markdown: a bold header now sits after codename lines, and one stray asterisk would eat it
    private static string Safe(string codename)
    {
        var sb = new StringBuilder(codename.Length + 8);
        foreach (var ch in codename)
        {
            if (ch is '\r' or '\n')
            {
                sb.Append(' ');
                continue;
            }
            if (ch is '\\' or '*' or '_' or '~' or '`' or '|')
            {
                sb.Append('\\');
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // is the announcement channel actually reachable (enabled + a webhook URL) — distinguishes "off" from "send failed"
    private async Task<bool> DiscordAnnouncementLiveAsync(CancellationToken cancellationToken)
    {
        var cfg = await discord.GetConfigAsync(cancellationToken);
        return cfg.Enabled
            && cfg.Webhooks.TryGetValue(NotificationType.Announcement, out var url)
            && !string.IsNullOrWhiteSpace(url);
    }

    /// <summary>Files one commendation per placed agent, idempotent within the period.</summary>
    /// <remarks>
    /// Both gates only ever look at notes this code wrote (the text prefix), so a hand-filed commendation that
    /// happens to quote a period cannot cancel an automatic one. Beyond the period marker there is an EntryDate
    /// floor, which keeps a broken webhook from filing a note per agent on every daily retry: notes precede the
    /// post and a failed post deliberately leaves LastRun stale. Pre-existing and unchanged: AgentNote is
    /// ISoftDelete, so a trashed commendation is invisible here and gets re-filed, and without a unique index a
    /// worker tick racing the manual send can double-file.
    /// </remarks>
    private async Task FileNotesAsync(
        IReadOnlyList<LeaderboardEntry> ranked, TopAgentPeriodWording period, int intervalDays,
        DateTime localNow, string author, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var floor = localNow.AddDays(-(intervalDays - NoteFloorSlackDays));
        var ids = ranked.Select(r => r.AgentId).ToList();
        var already = (await db.AgentNotes
            .Where(n => ids.Contains(n.AgentId) && n.Kind == AgentNoteKind.Commendation
                && n.Text.StartsWith(NotePrefix)
                && (n.Text.Contains(period.Marker) || n.EntryDate >= floor))
            .Select(n => n.AgentId)
            .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var added = false;
        for (var i = 0; i < ranked.Count; i++)
        {
            if (!already.Add(ranked[i].AgentId))
            {
                continue;
            }
            db.AgentNotes.Add(new AgentNote
            {
                AgentId = ranked[i].AgentId,
                Kind = AgentNoteKind.Commendation,
                EntryDate = localNow,
                Text = $"<p>Bester Agent {period.NotePhrase} — Platz {i + 1} ({ranked[i].Points} Punkte).</p>",
                AuthorName = author,
            });
            added = true;
        }
        if (added)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<TopAgentConfig> ReadConfigAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            SystemSettingKeys.BestAgentEnabled, SystemSettingKeys.BestAgentIntervalDays,
            SystemSettingKeys.BestAgentCreateNote, SystemSettingKeys.BestAgentLastRun,
        };
        var map = await db.SystemSettings.Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var enabled = Eq(map.GetValueOrDefault(SystemSettingKeys.BestAgentEnabled), "true");
        // a stored "0" still means the default; anything above the ceiling is clamped, not honoured
        var interval = int.TryParse(map.GetValueOrDefault(SystemSettingKeys.BestAgentIntervalDays), out var n) && n > 0
            ? Math.Clamp(n, 1, MaxIntervalDays) : DefaultIntervalDays;
        // note filing defaults ON; only an explicit "false" disables it
        var createNote = !Eq(map.GetValueOrDefault(SystemSettingKeys.BestAgentCreateNote), "false");
        DateTime? lastRun = DateTime.TryParse(map.GetValueOrDefault(SystemSettingKeys.BestAgentLastRun),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

        return new TopAgentConfig(enabled, interval, createNote, lastRun);
    }

    private async Task StampLastRunAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.BestAgentLastRun, nowUtc.ToString("o", CultureInfo.InvariantCulture), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool Eq(string? value, string other) => string.Equals(value, other, StringComparison.OrdinalIgnoreCase);

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
