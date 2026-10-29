using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>The supervision sheet of the personnel file: one agent's figures, each beside the team median.</summary>
public interface ISupervisionSheetService
{
    /// <summary>The sheet over <paramref name="days"/>, falling back to the default window when it is not one of
    /// <see cref="SupervisionWindow.Choices"/>; null for an unknown id or an applicant.</summary>
    /// <remarks>Leadership and read-only supervision; the counter-intelligence part only for leadership.</remarks>
    Task<SupervisionSheet?> GetAsync(string agentId, int days, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISupervisionSheetService" />
/// <remarks>
/// Every figure is one query grouped over all agents, so the median is read from the same numbers as the value.
/// The median runs over the statistics' roster (selectable, demo excluded): a departed agent still gets a sheet,
/// but no longer weighs on anyone else's.
/// </remarks>
public sealed class SupervisionSheetService(
    IDbContextFactory<AppDbContext> dbFactory,
    IGamificationService gamification,
    ICounterIntelService counterIntel) : ISupervisionSheetService
{
    public async Task<SupervisionSheet?> GetAsync(string agentId, int days, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(viewer);
        Permission.RequireClassifiedRead(viewer);
        days = SupervisionWindow.Normalize(days);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var agent = await db.Users.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new { a.Status, a.RegisteredAt, a.ReleasedAt, a.ActiveSinceAt, a.LastSeenAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (agent is null || agent.Status == AgentStatus.Applicant)
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var sinceUtc = nowUtc.AddDays(-days);
        var today = MeetingTime.Day(nowUtc);
        var roster = await db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(a => a.Id != DemoIdentity.AgentId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        SupervisionFigure Figure(IReadOnlyDictionary<string, int> counts)
            => new(counts.GetValueOrDefault(agentId),
                SupervisionMath.Median(roster.Select(id => counts.GetValueOrDefault(id))));

        var writes = await CountAsync(db.AuditLogs
            .Where(l => l.AgentId != null && l.Timestamp >= sinceUtc)
            .Select(l => l.AgentId!), cancellationToken);
        var followups = await FollowupsAsync(db, sinceUtc, nowUtc, today, cancellationToken);
        var meetings = await MeetingsAsync(db, sinceUtc, cancellationToken);
        var absent = await AbsenceDaysAsync(db, MeetingTime.Day(sinceUtc), today, cancellationToken);
        var points = await gamification.GetPointsAsync(days, cancellationToken);
        var badges = await CountAsync(db.AgentBadges.Select(b => b.AgentId), cancellationToken);
        var commendations = await CountAsync(db.AgentNotes
            .Where(n => n.Kind == AgentNoteKind.Commendation && n.EntryDate >= sinceUtc)
            .Select(n => n.AgentId), cancellationToken);
        var disciplinary = await CountAsync(db.AgentNotes
            .Where(n => n.Kind == AgentNoteKind.Disciplinary && n.EntryDate >= sinceUtc)
            .Select(n => n.AgentId), cancellationToken);

        var lastWrite = await db.AuditLogs.AsNoTracking()
            .Where(l => l.AgentId == agentId)
            .MaxAsync(l => (DateTime?)l.Timestamp, cancellationToken);
        var lastRead = await db.AccessLogs.AsNoTracking()
            .Where(l => l.AgentId == agentId)
            .MaxAsync(l => (DateTime?)l.Timestamp, cancellationToken);
        var (traceAt, traceKind) = InactivityRules.LastTrace(agent.ReleasedAt ?? agent.RegisteredAt,
            agent.ActiveSinceAt, lastWrite, lastRead, agent.LastSeenAt);
        var traceDay = MeetingTime.Day(traceAt);
        var spans = await db.Absences.AsNoTracking()
            .Where(a => a.AgentId == agentId && a.ToDate > traceDay)
            .Select(a => new { a.FromDate, a.ToDate })
            .ToListAsync(cancellationToken);
        var (silent, absentSince) = InactivityRules.Count(traceDay, today, spans.Select(s => (s.FromDate, s.ToDate)));

        return new SupervisionSheet(
            agentId, days, sinceUtc,
            new SupervisionActivity(traceAt, traceKind, silent, absentSince),
            Figure(writes),
            Figure(followups.OnTime), Figure(followups.Late), Figure(followups.Overdue),
            Figure(meetings[MeetingAttendanceStatus.Present]),
            Figure(meetings[MeetingAttendanceStatus.SignedOff]),
            Figure(meetings[MeetingAttendanceStatus.Missing]),
            Figure(absent), Figure(points), Figure(badges), Figure(commendations), Figure(disciplinary),
            viewer.MayCounterIntel()
                ? await CounterIntelAsync(db, agentId, days, sinceUtc, viewer, Figure, cancellationToken)
                : null);
    }

    /// <summary>The cockpit's profile and findings for this agent, beside the median of accesses.</summary>
    private async Task<SupervisionCounterIntel> CounterIntelAsync(AppDbContext db, string agentId, int days,
        DateTime sinceUtc, ClaimsPrincipal viewer, Func<IReadOnlyDictionary<string, int>, SupervisionFigure> figure,
        CancellationToken cancellationToken)
    {
        var profile = await counterIntel.GetAgentProfileAsync(viewer, agentId, days, cancellationToken);
        var flags = (await counterIntel.GetFlagsAsync(viewer, cancellationToken))
            .Where(f => f.AgentId == agentId)
            .ToList();
        var accesses = await CountAsync(db.AccessLogs
            .Where(l => l.AgentId != null && l.Timestamp >= sinceUtc)
            .Select(l => l.AgentId!), cancellationToken);
        // the agent's own value comes from the profile, so the sheet and the cockpit never disagree
        var own = figure(accesses) with { Value = profile?.Total ?? 0 };
        return new SupervisionCounterIntel(own, profile?.DistinctRecords ?? 0, profile?.OffHours ?? 0,
            profile?.ByType ?? [], flags);
    }

    private sealed record FollowupCounts(
        Dictionary<string, int> OnTime, Dictionary<string, int> Late, Dictionary<string, int> Overdue);

    /// <summary>Follow-ups due in the window, per responsible agent: done by the due day, done after it, or still open
    /// past it. One due today and still open is not overdue yet.</summary>
    private static async Task<FollowupCounts> FollowupsAsync(AppDbContext db, DateTime sinceUtc, DateTime nowUtc,
        DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await db.Followups.AsNoTracking()
            .Where(f => f.ResponsibleAgentId != null && f.DueAt >= sinceUtc && f.DueAt <= nowUtc)
            .Select(f => new { AgentId = f.ResponsibleAgentId!, f.DueAt, f.Done, f.DoneAt })
            .ToListAsync(cancellationToken);
        var counts = new FollowupCounts(new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            new(StringComparer.Ordinal));
        foreach (var row in rows)
        {
            var due = MeetingTime.Day(row.DueAt);
            var bucket = row.Done
                // an old row without a completion stamp is taken at its word
                ? row.DoneAt is { } doneAt && MeetingTime.Day(doneAt) > due ? counts.Late : counts.OnTime
                : due < today ? counts.Overdue : null;
            if (bucket is not null)
            {
                bucket[row.AgentId] = bucket.GetValueOrDefault(row.AgentId) + 1;
            }
        }
        return counts;
    }

    /// <summary>Attendance per status and agent; only meetings whose attendance is closed carry a settled state.</summary>
    private static async Task<Dictionary<MeetingAttendanceStatus, Dictionary<string, int>>> MeetingsAsync(
        AppDbContext db, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var rows = await db.MeetingAttendances.AsNoTracking()
            .Where(t => t.Meeting!.AttendanceClosedAt != null && t.Meeting.Start >= sinceUtc)
            .GroupBy(t => new { t.AgentId, t.Status })
            .Select(g => new { g.Key.AgentId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return Enum.GetValues<MeetingAttendanceStatus>().ToDictionary(s => s,
            s => rows.Where(r => r.Status == s).ToDictionary(r => r.AgentId, r => r.Count, StringComparer.Ordinal));
    }

    /// <summary>Calendar days in the window an absence covers, per agent; overlapping notices count a day once.</summary>
    private static async Task<Dictionary<string, int>> AbsenceDaysAsync(AppDbContext db, DateOnly first,
        DateOnly last, CancellationToken cancellationToken)
    {
        var spans = await db.Absences.AsNoTracking()
            .Where(a => a.ToDate >= first && a.FromDate <= last)
            .Select(a => new { a.AgentId, a.FromDate, a.ToDate })
            .ToListAsync(cancellationToken);
        return spans.GroupBy(s => s.AgentId, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            var covered = new HashSet<int>();
            foreach (var span in g)
            {
                for (var day = Math.Max(span.FromDate.DayNumber, first.DayNumber);
                     day <= Math.Min(span.ToDate.DayNumber, last.DayNumber); day++)
                {
                    covered.Add(day);
                }
            }
            return covered.Count;
        }, StringComparer.Ordinal);
    }

    private static Task<Dictionary<string, int>> CountAsync(IQueryable<string> agentIds,
        CancellationToken cancellationToken)
        => agentIds.GroupBy(id => id)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, StringComparer.Ordinal, cancellationToken);
}
