using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IBewerbungTestExpiryService" />
public class BewerbungTestExpiryService(
    IDbContextFactory<AppDbContext> dbFactory,
    BewerbungBroadcaster broadcaster,
    INotificationService notifications,
    IApplicantDmNotifier applicantMessages) : IBewerbungTestExpiryService
{
    /// <summary>Rows per pass; a countdown is minutes long, so there is never a real backlog.</summary>
    private const int ExpiryBatch = 100;

    /// <summary>States that allow no further transition, spelled out because the check has to reach SQL.</summary>
    private static readonly BewerbungStatus[] Decided =
        [BewerbungStatus.Angenommen, BewerbungStatus.Abgelehnt, BewerbungStatus.Geschlossen];

    /// <inheritdoc />
    /// <remarks>
    /// No actor and no permission guard, the way the public wanted expiry has none: nobody is acting, the only
    /// state it advances is the one the stored deadline already declares, and it writes no answer the applicant
    /// did not write themselves. Not a security control either — the read, draft and submit paths each re-check
    /// the deadline, which is exactly why nobody may drop those checks because this exists.
    /// </remarks>
    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        // UtcNow, never Now: the deadline is stored in UTC and MySQL datetime carries no offset that would
        // catch the mistake
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // the decided filter belongs in the PREDICATE, not in the loop. An attempt on a decided application
        // is never closed, so skipping it inside the batch left it sitting at the head of every following
        // ordered batch forever - a handful of them starve the sweep and no later attempt is ever handed in.
        var due = await db.BewerbungTestAssignments
            .Where(a => a.CompletedAt == null && a.DeadlineAt != null && a.DeadlineAt <= now
                && db.Bewerbungen.Any(b => b.Id == a.BewerbungId && !Decided.Contains(b.Status)))
            .OrderBy(a => a.DeadlineAt)
            .Take(ExpiryBatch)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var bewerbungIds = due.Select(a => a.BewerbungId).ToList();
        var applications = await db.Bewerbungen.AsNoTracking()
            .Where(b => bewerbungIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);

        var closed = 0;
        foreach (var assignment in due)
        {
            // the predicate already excluded these; belt for a decision landing mid-pass
            if (!applications.TryGetValue(assignment.BewerbungId, out var bewerbung))
            {
                continue;
            }
            if (await CloseAsync(db, assignment, bewerbung, now, cancellationToken))
            {
                closed++;
            }
        }
        return closed;
    }

    /// <summary>Hands in one attempt as it stands; false when another writer claimed it first.</summary>
    private async Task<bool> CloseAsync(AppDbContext db, BewerbungTestAssignment assignment,
        Bewerbung bewerbung, DateTime now, CancellationToken cancellationToken)
    {
        // claim BEFORE filling: this row was read at the top of the pass, and a reset in between would move
        // it to another test - the empty rows would then be written for the questions of the old test and
        // stay there even though the claim lost. CompletedAt is the idempotency token against a second pass;
        // onlyWhenOverdue is what protects a meanwhile extended or reset attempt.
        if (!await TestAttemptClose.ClaimAsync(db, assignment.Id, now, timedOut: true,
                onlyWhenOverdue: true, cancellationToken))
        {
            return false;
        }
        // the draft rows ARE the submission: nothing is rewritten, only the untouched questions get the empty
        // row the evaluation keys on
        await TestAttemptClose.FillMissingAnswersAsync(db, assignment, cancellationToken);
        db.AuditLogs.Add(ManualAudit.SystemRow(nameof(BewerbungTestAssignment), assignment.Id,
            AuditAction.Modified, ManualAudit.Change("Zeit abgelaufen", null, now)));
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(bewerbung.Id);

        try
        {
            // wrapped so a failed bell cannot roll the stamp back
            if (!string.IsNullOrEmpty(bewerbung.AssignedAgentId))
            {
                await notifications.NotifyAsync(bewerbung.AssignedAgentId, NotificationType.Recruiting,
                    $"Testzeit abgelaufen ({bewerbung.CaseNumber})", $"/bewerbungen/{bewerbung.Id}", cancellationToken);
            }
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                "Deine Bearbeitungszeit ist abgelaufen.", "/portal/status", cancellationToken);
            await applicantMessages.NotifyAsync(bewerbung.ApplicantUserId, ApplicantDmEvent.TestExpired, cancellationToken);
        }
        catch { /* best effort */ }
        return true;
    }
}
