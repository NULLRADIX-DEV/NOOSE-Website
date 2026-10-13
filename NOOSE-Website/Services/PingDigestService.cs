using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Models.Notifications;

namespace NOOSE_Website.Services;

/// <summary>Bundles the personal Discord pings agents chose to collect into one daily post.</summary>
public interface IPingDigestService
{
    /// <summary>Sends today's digest once the digest hour has passed and it has not gone out yet; returns how many agents it listed.</summary>
    Task<int> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPingDigestService" />
public sealed class PingDigestService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordWebhookService discord,
    ILogger<PingDigestService> logger) : IPingDigestService
{
    // Discord takes 2000 characters and 100 mentions per message; the heading and the link need room
    private const int MaxChars = 1800;
    private const int MaxMentions = 90;
    private const string Heading = "🗞️ **Tagesüberblick** – ungelesen seit dem letzten Überblick:";
    private const string InboxRoute = "/benachrichtigungen";

    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lastRun = await ReadLastRunAsync(db, cancellationToken);
        if (!IsDue(nowUtc, lastRun))
        {
            return 0;
        }

        var listed = 0;
        if (await ChannelLiveAsync(cancellationToken))
        {
            // a day at most, so an outage never sends a stale pile; never what the last digest already counted
            var since = nowUtc.AddDays(-1);
            if (lastRun is { } last && last > since)
            {
                since = last;
            }

            var entries = await CollectAsync(db, since, nowUtc, cancellationToken);
            var delivered = true;
            foreach (var message in Compose(entries))
            {
                delivered &= await discord.PushDigestAsync(message.Content, message.DiscordIds, InboxRoute, cancellationToken);
            }
            if (!delivered)
            {
                logger.LogWarning("Ping digest not fully delivered ({Agents} agents listed).", entries.Count);
            }
            listed = entries.Count;
        }

        // stamped even after a failed post: a retry would ping the delivered part twice, the inbox keeps everything
        await StampAsync(db, nowUtc, cancellationToken);
        return listed;
    }

    /// <summary>Due once a day, at the first look after the digest hour (local time).</summary>
    public static bool IsDue(DateTime nowUtc, DateTime? lastRunUtc)
    {
        var local = MeetingTime.Local(nowUtc);
        if (local.Hour < DiscordPings.DigestHour)
        {
            return false;
        }
        return lastRunUtc is not { } last || MeetingTime.Day(last) < DateOnly.FromDateTime(local);
    }

    /// <summary>Splits the lines into messages Discord accepts, each opening with the heading.</summary>
    public static List<PingDigestMessage> Compose(IReadOnlyList<PingDigestEntry> entries)
    {
        var messages = new List<PingDigestMessage>();
        var body = new StringBuilder();
        var ids = new List<string>();
        foreach (var entry in entries)
        {
            var line = Line(entry);
            if (ids.Count > 0 && (ids.Count >= MaxMentions || body.Length + 1 + line.Length > MaxChars))
            {
                messages.Add(new PingDigestMessage(body.ToString(), ids));
                body.Clear();
                ids = [];
            }
            if (body.Length == 0)
            {
                body.Append(Heading);
            }
            body.Append('\n').Append(line);
            ids.Add(entry.DiscordId);
        }
        if (ids.Count > 0)
        {
            messages.Add(new PingDigestMessage(body.ToString(), ids));
        }
        return messages;
    }

    /// <summary>One mention plus a count per category; category names only, never a title.</summary>
    public static string Line(PingDigestEntry entry)
        => $"<@{entry.DiscordId}> "
            + string.Join(", ", entry.Counts.Select(c => $"{c.Count}× {NotificationTypeDisplay.Name(c.Type)}"));

    private async Task<bool> ChannelLiveAsync(CancellationToken cancellationToken)
    {
        var config = await discord.GetConfigAsync(cancellationToken);
        return config.Enabled && !string.IsNullOrWhiteSpace(config.Webhooks.GetValueOrDefault(NotificationType.PingDigest));
    }

    /// <summary>Unread notices per agent in the categories that agent collects, created inside the window.</summary>
    private static async Task<List<PingDigestEntry>> CollectAsync(AppDbContext db, DateTime sinceUtc, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var agents = await db.Users.AsNoTracking()
            .Where(u => u.Status == AgentStatus.Active && u.NavPreferencesJson != null)
            .Select(u => new { u.Id, u.Codename, u.DiscordId, u.NavPreferencesJson })
            .ToListAsync(cancellationToken);

        var collecting = new Dictionary<string, (string DiscordId, string Codename, HashSet<NotificationType> Types)>();
        foreach (var agent in agents)
        {
            // the shared demo account keeps its choices in the visitor's session; an unlinked account cannot be pinged
            if (agent.Id == DemoIdentity.AgentId || !IsSnowflake(agent.DiscordId))
            {
                continue;
            }
            NavPreferences prefs;
            try
            {
                prefs = NavPreferences.FromJson(agent.NavPreferencesJson);
            }
            catch
            {
                /* ignore */
                continue;
            }
            var types = DiscordPings.Personal
                .Where(t => DiscordPings.ModeFor(prefs, t) == DiscordPingMode.Digest)
                .ToHashSet();
            if (types.Count > 0)
            {
                collecting[agent.Id] = (agent.DiscordId, agent.Codename, types);
            }
        }
        if (collecting.Count == 0)
        {
            return [];
        }

        var ids = collecting.Keys.ToList();
        var personal = DiscordPings.Personal.ToList();
        var counts = await db.Notifications.AsNoTracking()
            .Where(n => ids.Contains(n.RecipientId) && n.ReadAt == null && personal.Contains(n.Type)
                && n.CreatedAt > sinceUtc && n.CreatedAt <= nowUtc)
            .GroupBy(n => new { n.RecipientId, n.Type })
            .Select(g => new { g.Key.RecipientId, g.Key.Type, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var entries = new List<PingDigestEntry>();
        foreach (var (id, agent) in collecting.OrderBy(c => c.Value.Codename, StringComparer.CurrentCulture))
        {
            var mine = DiscordPings.Personal
                .Where(agent.Types.Contains)
                .Select(t => new PingDigestCount(t,
                    counts.Where(c => c.RecipientId == id && c.Type == t).Sum(c => c.Count)))
                .Where(c => c.Count > 0)
                .ToList();
            if (mine.Count > 0)
            {
                entries.Add(new PingDigestEntry(agent.DiscordId, mine));
            }
        }
        return entries;
    }

    // only numeric snowflakes are pingable (guards "" and the seeded "demo" user)
    private static bool IsSnowflake(string? value)
        => !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);

    private static async Task<DateTime?> ReadLastRunAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.PingDigestLastRun)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    private static async Task StampAsync(AppDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var value = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.PingDigestLastRun, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PingDigestLastRun, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
