using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IInactivityRadarService" />
/// <remarks>
/// Three traces, the latest wins: a write in the change log, a record opened in the access log, and the page-visit
/// stamp on the account. The stamp only exists from this feature on; for the time before it the two logs carry the
/// history, and a row resting on them alone stays provisional until visits have been counted long enough. No worker
/// and no store of its own: the radar is one query in the other direction.
/// </remarks>
public class InactivityRadarService(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications)
    : IInactivityRadarService
{
    private const string SettingKey = "InaktivitaetSchwellen";

    /// <summary>When page visits started being stamped; written once by migration Phase94.</summary>
    public const string VisitsCountedFromKey = "InaktivitaetBesucheAb";

    /// <summary>Audit entity type for the thresholds row.</summary>
    public const string AuditType = "InactivityThresholds";

    /// <summary>Where a nudge leads: the agent's own absences, the quickest honest answer.</summary>
    public const string NudgeHref = "/abmeldungen?tab=meine";

    public async Task<InactivityRadar> GetAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(viewer);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var thresholds = await LoadThresholdsAsync(db, cancellationToken);
        var visitsFrom = await LoadVisitsCountedFromAsync(db, cancellationToken);
        var rows = await ComputeAsync(db, thresholds, visitsFrom, onlyAgentId: null, viewer.MayRealNameSee(),
            DateTime.UtcNow, cancellationToken);
        return new InactivityRadar(thresholds, rows
            .Where(r => r.Tier != InactivityTier.None)
            .OrderByDescending(r => r.InactiveDays)
            .ThenBy(r => r.Codename, StringComparer.OrdinalIgnoreCase)
            .ToList(), visitsFrom);
    }

    public async Task<InactivityRow?> GetRowAsync(string agentId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(viewer);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await CurrentRowAsync(db, agentId, viewer.MayRealNameSee(), DateTime.UtcNow, cancellationToken);
        return row is { Tier: not InactivityTier.None } ? row : null;
    }

    public async Task NudgeAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var actorId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId) || agentId == actorId)
        {
            throw new InvalidOperationException("Bei dir selbst kannst du nicht nachfassen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // whole microseconds: the column keeps six digits, and the release below compares on equality
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        // recomputed, not trusted from the page: the agent may have come back meanwhile
        var row = await CurrentRowAsync(db, agentId, withRealName: false, now, cancellationToken);
        if (row is null || row.Tier == InactivityTier.None)
        {
            throw new InvalidOperationException("Dieser Agent steht nicht mehr im Inaktivitäts-Radar.");
        }
        if (row.AbsentUntil is { } until)
        {
            throw new InvalidOperationException(
                $"{row.Codename} ist bis {until:dd.MM.yyyy} abgemeldet; Nachfassen geht erst danach.");
        }

        var previous = await db.Users.AsNoTracking()
            .Where(u => u.Id == agentId)
            .Select(u => new { u.InactivityNudgedAt, u.InactivityNudgedByName })
            .SingleAsync(cancellationToken);
        var by = actor.GetCodename();
        var cooledDown = now - InactivityRules.NudgeCooldown;
        // compare-and-set: of two leaders clicking together only one gets the row
        var claimed = await db.Users
            .Where(u => u.Id == agentId && (u.InactivityNudgedAt == null || u.InactivityNudgedAt <= cooledDown))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.InactivityNudgedAt, (DateTime?)now)
                .SetProperty(u => u.InactivityNudgedByName, by), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Bei diesem Agenten wurde in den letzten 24 Stunden schon nachgefasst.");
        }

        // from the claim on the caller may not cancel: a stamp without its message would block a retry for a day
        try
        {
            await notifications.NotifyManyAsync([agentId], NotificationType.InactivityNudge,
                InactivityRules.NudgeTitle(MeetingTime.Day(row.LastTraceAt)), NudgeHref, actorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                // nothing went out: hand the claim back so the next click can try again
                await db.Users
                    .Where(u => u.Id == agentId && u.InactivityNudgedAt == now)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.InactivityNudgedAt, previous.InactivityNudgedAt)
                        .SetProperty(u => u.InactivityNudgedByName, previous.InactivityNudgedByName), CancellationToken.None);
            }
            catch
            {
                /* best effort */
            }
            throw new InvalidOperationException(
                $"Die Nachricht an {row.Codename} konnte nicht zugestellt werden. Bitte versuch es noch einmal.", ex);
        }

        // logged once the message is out, so the log never claims a nudge nobody received
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agentId, AuditAction.Modified, actor,
            ManualAudit.Change("Wegen Inaktivität nachgefasst", null, $"{row.InactiveDays} Tage ohne Lebenszeichen")));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<InactivityThresholds> GetThresholdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await LoadThresholdsAsync(db, cancellationToken);
    }

    public async Task SaveThresholdsAsync(InactivityThresholds thresholds, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        InactivityRules.Validate(thresholds);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var before = await LoadThresholdsAsync(db, cancellationToken);
        var json = JsonSerializer.Serialize(thresholds);
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = json });
        }
        else
        {
            row.Value = json;
        }
        db.AuditLogs.Add(ManualAudit.Row(AuditType, "global", AuditAction.Modified, actor,
            ManualAudit.Change("Inaktivitäts-Schwellen", Summarise(before), Summarise(thresholds))));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task StampSeenAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        // only who can ever appear on the radar; oversight, demo and partners leave no stamp
        if (!user.IsInternalAgent() || !user.MayWrite())
        {
            return;
        }
        var id = user.GetAgentId();
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var due = now - InactivityRules.SeenGranularity;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Users
            .Where(u => u.Id == id && (u.LastSeenAt == null || u.LastSeenAt < due))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, (DateTime?)now), cancellationToken);
    }

    /// <summary>One agent's row as of now, tier None included; null when not selectable.</summary>
    private static async Task<InactivityRow?> CurrentRowAsync(AppDbContext db, string agentId, bool withRealName,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        var thresholds = await LoadThresholdsAsync(db, cancellationToken);
        var visitsFrom = await LoadVisitsCountedFromAsync(db, cancellationToken);
        return (await ComputeAsync(db, thresholds, visitsFrom, agentId, withRealName, nowUtc, cancellationToken))
            .SingleOrDefault();
    }

    /// <summary>One row per selectable agent, tier included; the caller filters.</summary>
    private static async Task<List<InactivityRow>> ComputeAsync(AppDbContext db, InactivityThresholds thresholds,
        DateTime? visitsFrom, string? onlyAgentId, bool withRealName, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(a => a.Id != DemoIdentity.AgentId);
        if (onlyAgentId is not null)
        {
            query = query.Where(a => a.Id == onlyAgentId);
        }
        var agents = await query
            .Select(a => new
            {
                a.Id, a.Codename, a.RealName, a.BadgeNumber, a.Rank, a.RegisteredAt, a.ReleasedAt, a.ActiveSinceAt,
                a.LastSeenAt, a.InactivityNudgedAt, a.InactivityNudgedByName,
            })
            .ToListAsync(cancellationToken);
        if (agents.Count == 0)
        {
            return [];
        }

        var ids = agents.Select(a => a.Id).ToList();
        // one MAX per agent; both logs carry an (AgentId, Timestamp) index for this
        var writes = await db.AuditLogs.AsNoTracking()
            .Where(l => l.AgentId != null && ids.Contains(l.AgentId))
            .GroupBy(l => l.AgentId!)
            .Select(g => new { AgentId = g.Key, At = g.Max(l => l.Timestamp) })
            .ToDictionaryAsync(x => x.AgentId, x => x.At, cancellationToken);
        var reads = await db.AccessLogs.AsNoTracking()
            .Where(l => l.AgentId != null && ids.Contains(l.AgentId))
            .GroupBy(l => l.AgentId!)
            .Select(g => new { AgentId = g.Key, At = g.Max(l => l.Timestamp) })
            .ToDictionaryAsync(x => x.AgentId, x => x.At, cancellationToken);

        var traces = agents.ToDictionary(a => a.Id, a => InactivityRules.LastTrace(
            a.ReleasedAt ?? a.RegisteredAt,
            a.ActiveSinceAt,
            writes.TryGetValue(a.Id, out var w) ? w : null,
            reads.TryGetValue(a.Id, out var r) ? r : null,
            a.LastSeenAt));

        var today = MeetingTime.Day(nowUtc);
        var since = traces.Values.Min(t => MeetingTime.Day(t.At));
        var absences = (await db.Absences.AsNoTracking()
                .Where(a => ids.Contains(a.AgentId) && a.ToDate > since)
                .Select(a => new { a.AgentId, a.FromDate, a.ToDate })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.AgentId, a => (a.FromDate, a.ToDate));

        var rows = new List<InactivityRow>(agents.Count);
        foreach (var a in agents)
        {
            var (at, kind) = traces[a.Id];
            var spans = absences[a.Id].ToList();
            var (silent, absent) = InactivityRules.Count(MeetingTime.Day(at), today, spans);
            var inactive = silent - absent;
            var tier = InactivityRules.Tier(inactive, thresholds);
            var provisional = tier != InactivityTier.None
                && InactivityRules.IsProvisional(at, visitsFrom, today, thresholds);
            if (provisional && tier > InactivityTier.FollowUp)
            {
                tier = InactivityTier.FollowUp;
            }
            var nudged = InactivityRules.NudgeIsCurrent(a.InactivityNudgedAt, at);
            rows.Add(new InactivityRow(
                a.Id, a.Codename, withRealName ? a.RealName : null, a.BadgeNumber, a.Rank,
                at, kind,
                writes.TryGetValue(a.Id, out var lastWrite) ? lastWrite : null,
                reads.TryGetValue(a.Id, out var lastRead) ? lastRead : null,
                a.LastSeenAt,
                silent, absent, inactive, tier,
                InactivityRules.AbsentUntil(today, spans),
                nudged ? a.InactivityNudgedAt : null,
                nudged ? a.InactivityNudgedByName : null,
                InactivityRules.NudgeLockedUntil(a.InactivityNudgedAt, nowUtc),
                provisional));
        }
        return rows;
    }

    /// <summary>The stored thresholds, or the defaults when none are stored or the row is unreadable.</summary>
    private static async Task<InactivityThresholds> LoadThresholdsAsync(AppDbContext db,
        CancellationToken cancellationToken)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return InactivityThresholds.Default;
        }
        try
        {
            var stored = JsonSerializer.Deserialize<InactivityThresholds>(raw);
            if (stored is null)
            {
                return InactivityThresholds.Default;
            }
            InactivityRules.Validate(stored);
            return stored;
        }
        catch (JsonException)
        {
            return InactivityThresholds.Default;
        }
        catch (InvalidOperationException)
        {
            return InactivityThresholds.Default;
        }
    }

    /// <summary>When the migration started counting page visits; null when the row is missing or unreadable.</summary>
    private static async Task<DateTime?> LoadVisitsCountedFromAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == VisitsCountedFromKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    private static string Summarise(InactivityThresholds t)
        => $"{t.FollowUpDays} / {t.NoteDays} / {t.TerminationDays} Tage";
}
