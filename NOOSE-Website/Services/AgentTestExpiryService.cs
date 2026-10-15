using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IAgentTestExpiryService" />
/// <remarks>
/// No actor and no permission guard, like the recruiting sweep: nobody is acting, it only advances what the stored
/// deadline or due date already declares. Not a security control either - the open, draft and submit paths re-check
/// the deadline themselves.
/// </remarks>
public class AgentTestExpiryService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentTestBroadcaster broadcaster,
    INotificationService notifications,
    ILogger<AgentTestExpiryService> logger) : IAgentTestExpiryService
{
    /// <summary>Rows per pass; a countdown is minutes long, so there is never a real backlog.</summary>
    private const int Batch = 100;

    /// <summary>How far ahead of the due date the reminder goes out.</summary>
    public static readonly TimeSpan DueSoonLead = TimeSpan.FromHours(24);

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.AgentTestAssignments
            .Where(a => a.CompletedAt == null && a.DeadlineAt != null && a.DeadlineAt <= now)
            .OrderBy(a => a.DeadlineAt)
            .Take(Batch)
            .ToListAsync(cancellationToken);

        var closed = 0;
        foreach (var assignment in due)
        {
            // claim before filling: a reset in between would otherwise get empty rows for the old attempt
            if (!await TestAttemptClose.ClaimAgentAsync(db, assignment.Id, now, timedOut: true,
                    onlyWhenOverdue: true, cancellationToken))
            {
                continue;
            }
            await TestAttemptClose.FillMissingAnswersAsync(db, assignment, cancellationToken);
            db.AuditLogs.Add(ManualAudit.SystemRow(nameof(AgentTestAssignment), assignment.Id,
                AuditAction.Modified, ManualAudit.Change("Zeit abgelaufen", null, now)));
            await db.SaveChangesAsync(cancellationToken);
            broadcaster.Report(assignment.Id, assignment.AgentId);
            closed++;

            try
            {
                var codename = await db.Users.AsNoTracking().Where(u => u.Id == assignment.AgentId)
                    .Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken);
                var graders = await AgentTestService.GraderRecipientsAsync(db, assignment, cancellationToken);
                await notifications.NotifyManyAsync(graders, NotificationType.TestSubmitted,
                    $"Testzeit abgelaufen: {codename ?? "Agent"}", $"/tests/korrektur/{assignment.Id}", null,
                    cancellationToken);
            }
            catch { /* best effort */ }
        }
        return closed;
    }

    public async Task<int> RemindDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var soon = now + DueSoonLead;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // only assignments that existed before the window opened: a fresh one with a short due date already got
        // its assignment ping and needs no second one minutes later
        var dueSoon = await db.AgentTestAssignments.AsNoTracking()
            .Where(a => a.CompletedAt == null && a.DueAt != null && a.DueAt > now && a.DueAt <= soon
                && a.DueReminderSentAt == null && a.CreatedAt <= a.DueAt.Value.AddHours(-24))
            .OrderBy(a => a.DueAt).Take(Batch)
            .Select(a => new { a.Id, a.AgentId, a.TestId, a.DueAt })
            .ToListAsync(cancellationToken);
        var overdue = await db.AgentTestAssignments.AsNoTracking()
            .Where(a => a.CompletedAt == null && a.DueAt != null && a.DueAt <= now && a.OverdueReminderSentAt == null)
            .OrderBy(a => a.DueAt).Take(Batch)
            .Select(a => new { a.Id, a.AgentId, a.TestId, a.DueAt })
            .ToListAsync(cancellationToken);
        if (dueSoon.Count == 0 && overdue.Count == 0)
        {
            return 0;
        }

        var testIds = dueSoon.Select(a => a.TestId).Concat(overdue.Select(a => a.TestId)).Distinct().ToList();
        var titles = await db.BewerbungTests.IgnoreQueryFilters().AsNoTracking()
            .Where(t => testIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Title, cancellationToken);

        var sent = 0;
        foreach (var a in dueSoon)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // claim the stamp first, then notify: once per assignment, also with several instances. Past the claim
            // nothing is cancelled any more, or a shutdown in between would burn the stamp without a reminder
            var claimed = await db.AgentTestAssignments
                .Where(x => x.Id == a.Id && x.DueReminderSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DueReminderSentAt, now), CancellationToken.None);
            if (claimed != 1)
            {
                continue;
            }
            var local = a.DueAt!.Value.ToLocalTime();
            sent += await RemindAsync(a.AgentId, a.Id,
                $"Test „{titles.GetValueOrDefault(a.TestId) ?? "Test"}“ ist bis {local:dd.MM. HH:mm} Uhr fällig.");
        }
        foreach (var a in overdue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await db.AgentTestAssignments
                .Where(x => x.Id == a.Id && x.OverdueReminderSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OverdueReminderSentAt, now), CancellationToken.None);
            if (claimed != 1)
            {
                continue;
            }
            sent += await RemindAsync(a.AgentId, a.Id,
                $"Test „{titles.GetValueOrDefault(a.TestId) ?? "Test"}“ ist überfällig.");
        }
        return sent;
    }

    /// <summary>One reminder; a failure is logged and the stamp stays used.</summary>
    private async Task<int> RemindAsync(string agentId, string assignmentId, string title)
    {
        try
        {
            await notifications.NotifyManyAsync([agentId], NotificationType.TestDueReminder, title,
                $"/tests/{assignmentId}", triggerId: null, CancellationToken.None);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Due reminder for test assignment {AssignmentId} failed.", assignmentId);
            return 0;
        }
    }
}
