using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Models.Tests;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IAgentTestService" />
public class AgentTestService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentTestBroadcaster broadcaster,
    INotificationService notifications,
    ITrainingModuleService trainingModules,
    ILogger<AgentTestService> logger) : IAgentTestService
{
    /// <summary>Rows a list returns at most; the overview is a work queue, not an archive.</summary>
    private const int ListCap = 500;

    public async Task<List<BewerbungTest>> GetAssignableTestsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.BewerbungTests.AsNoTracking()
            .Where(t => t.IsActive && t.Usage != TestUsage.Bewerbung)
            .OrderBy(t => t.Sorting).ThenBy(t => t.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentTestAssignResult> AssignAsync(string testId, AgentTestTargets targets, DateTime? dueAt,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        var now = DateTime.UtcNow;
        if (dueAt is { } due && due <= now)
        {
            throw new InvalidOperationException("Die Frist liegt in der Vergangenheit.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var test = await db.BewerbungTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == testId, cancellationToken)
            ?? throw new InvalidOperationException("Test nicht gefunden.");
        if (!test.IsActive)
        {
            throw new InvalidOperationException("Dieser Test ist inaktiv und wird nicht mehr vergeben.");
        }
        if (!TestUsageDisplay.ForAgents(test.Usage))
        {
            throw new InvalidOperationException("Dieser Test ist nur für Bewerbungen freigegeben.");
        }

        // the write path checks with the same predicate as the picker, or the SignalR path stays open
        var picked = targets.AgentIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var recipients = picked.Count == 0
            ? new List<string>()
            : await db.Users.OnlySelectable().Where(u => picked.Contains(u.Id)).Select(u => u.Id)
                .ToListAsync(cancellationToken);
        if (targets.Audience is { } audience)
        {
            if (audience == AnnouncementAudience.Taskforce && string.IsNullOrEmpty(targets.TargetId))
            {
                throw new InvalidOperationException("Bitte wähle eine Taskforce.");
            }
            if (audience == AnnouncementAudience.FromRank && targets.MinRank is null)
            {
                throw new InvalidOperationException("Bitte wähle einen Mindest-Dienstgrad.");
            }
            recipients.AddRange(await AudienceRules.RecipientIdsAsync(db, audience, targets.TargetId,
                targets.MinRank, cancellationToken));
        }
        recipients = recipients.Distinct(StringComparer.Ordinal).ToList();
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("Niemand ausgewählt, an den der Test gehen kann.");
        }

        // an ungraded assignment of the same test is still running; a second one would split the attempt
        var busy = (await db.AgentTestAssignments.AsNoTracking()
                .Where(a => a.TestId == testId && a.GradedAt == null && recipients.Contains(a.AgentId))
                .Select(a => a.AgentId).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var fresh = recipients.Where(id => !busy.Contains(id)).ToList();

        var rows = fresh.Select(agentId => new AgentTestAssignment
        {
            AgentId = agentId,
            TestId = testId,
            AssignedById = actor.GetAgentId(),
            AssignedByName = actor.GetCodename(),
            DueAt = dueAt,
        }).ToList();
        db.AgentTestAssignments.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var row in rows)
        {
            broadcaster.Report(row.Id, row.AgentId);
        }
        try
        {
            await notifications.NotifyManyAsync(fresh, NotificationType.TestAssigned,
                $"Dir wurde der Test „{test.Title}“ zugewiesen.", "/tests", actor.GetAgentId(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test assignment notice for {TestId} failed.", testId);
        }
        return new AgentTestAssignResult(rows.Count, busy.Count);
    }

    public async Task<List<AgentTestRow>> GetOverviewAsync(AgentTestFilter filter, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.AgentTestAssignments.AsNoTracking();
        query = filter switch
        {
            AgentTestFilter.Offen => query.Where(a => a.CompletedAt == null),
            AgentTestFilter.ZuKorrigieren => query.Where(a => a.CompletedAt != null && a.GradedAt == null),
            AgentTestFilter.Ueberfaellig => query.Where(a => a.CompletedAt == null && a.DueAt != null && a.DueAt <= now),
            AgentTestFilter.Erledigt => query.Where(a => a.GradedAt != null),
            _ => query,
        };
        query = filter is AgentTestFilter.Offen or AgentTestFilter.Ueberfaellig
            ? query.OrderBy(a => a.DueAt == null).ThenBy(a => a.DueAt).ThenBy(a => a.CreatedAt)
            : query.OrderByDescending(a => a.CompletedAt ?? a.CreatedAt);
        return await ToRowsAsync(db, query.Take(ListCap), cancellationToken);
    }

    public async Task<List<AgentTestRow>> GetForAgentAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AgentTestAssignments.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(ListCap);
        return await ToRowsAsync(db, query, cancellationToken);
    }

    public async Task<AgentTestAssignment?> GetAssignmentAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
    }

    public async Task<TestEvaluation?> GetEvaluationAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AgentTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        // an open attempt has draft answers in those rows: reading them would let the grader watch the agent type
        return assignment is { CompletedAt: not null }
            ? await BuildEvaluationAsync(db, assignment, cancellationToken)
            : null;
    }

    public async Task SetAwardedPointsAsync(string assignmentId, string questionId, int? points, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (assignment.CompletedAt is null)
        {
            throw new InvalidOperationException("Der Test ist noch nicht abgegeben.");
        }
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException("Die Korrektur ist abgeschlossen. Bitte sie zuerst wieder öffnen.");
        }
        var question = await db.BewerbungTestQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.TestId == assignment.TestId, cancellationToken)
            ?? throw new InvalidOperationException("Frage gehört nicht zu diesem Test.");

        // keyed on the question, not on an answer row: a question added after the hand-in has none
        var rows = await db.AgentTestAnswers
            .Where(a => a.AssignmentId == assignmentId && a.QuestionId == questionId)
            .ToListAsync(cancellationToken);
        TestEngine.PickAnswers(rows).TryGetValue(questionId, out var answer);
        if (answer is null)
        {
            answer = new AgentTestAnswer { AssignmentId = assignmentId, QuestionId = questionId };
            db.AgentTestAnswers.Add(answer);
        }
        answer.ManualPoints = points is null ? null : Math.Clamp(points.Value, 0, question.Points);
        if (points is null)
        {
            answer.ManualCorrect = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);
    }

    public async Task CompleteGradingAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (assignment.CompletedAt is null)
        {
            throw new InvalidOperationException("Der Agent hat den Test noch nicht abgegeben.");
        }
        if (assignment.GradedAt is not null)
        {
            return;
        }
        var report = await BuildEvaluationAsync(db, assignment, cancellationToken);
        if (report.OpenCount > 0)
        {
            throw new InvalidOperationException(
                $"Noch {report.OpenCount} Frage(n) ohne Wertung. Bitte zuerst alle bewerten.");
        }

        assignment.GradedAt = DateTime.UtcNow;
        assignment.GradedByName = actor.GetCodename();
        assignment.FinalPoints = report.TotalPoints;
        assignment.FinalMaxPoints = report.MaxPoints;
        assignment.FinalPassPercent = report.PassPercent;
        assignment.Passed = report.Passed;
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);

        // the follow-ups are their own writes after the commit: a failed tick or note must not undo the grade
        var test = await db.BewerbungTests.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == assignment.TestId, cancellationToken);
        var title = test?.Title ?? report.Title;
        if (report.Passed == true && !string.IsNullOrEmpty(test?.TrainingModuleId))
        {
            await TickModuleAsync(assignment.AgentId, test.TrainingModuleId, title, actor, cancellationToken);
        }
        await WritePersonnelNoteAsync(assignment.Id, title, report, actor, cancellationToken);

        try
        {
            await notifications.NotifyAsync(assignment.AgentId, NotificationType.TestGraded,
                $"Dein Test „{title}“ wurde korrigiert.", $"/tests/{assignment.Id}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Graded notice for {AssignmentId} failed.", assignment.Id);
        }
    }

    public async Task ReopenGradingAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (assignment.GradedAt is null)
        {
            return;
        }
        // the frozen figures go with it: leaving them would show a stale result next to live per-question points
        assignment.GradedAt = null;
        assignment.GradedByName = null;
        assignment.FinalPoints = null;
        assignment.FinalMaxPoints = null;
        assignment.FinalPassPercent = null;
        assignment.Passed = null;
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);
    }

    public async Task ExtendAsync(string assignmentId, int minutes, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        var now = DateTime.UtcNow;
        var granted = TestDeadline.Clamp(minutes)
            ?? throw new InvalidOperationException("Bitte gib die zusätzliche Zeit in Minuten an.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException("Die Korrektur ist abgeschlossen. Bitte sie zuerst wieder öffnen.");
        }
        // an attempt handed in on time is finished; only the clock may reopen what the clock closed
        if (assignment is { CompletedAt: not null, TimedOut: false })
        {
            throw new InvalidOperationException(
                "Der Agent hat den Test regulär abgegeben. Nutze „Test neu freigeben“ für einen neuen Versuch.");
        }
        var testMinutes = await db.BewerbungTests.AsNoTracking()
            .Where(x => x.Id == assignment.TestId).Select(x => x.TimeLimitMinutes)
            .FirstOrDefaultAsync(cancellationToken);
        if (TestDeadline.AllowedMinutes(assignment, testMinutes) is null)
        {
            throw new InvalidOperationException(
                "Für diesen Test ist keine Bearbeitungszeit hinterlegt, es gibt also keine Frist zu verlängern.");
        }
        if (assignment is { StartedAt: not null, DeadlineAt: null })
        {
            throw new InvalidOperationException(
                "Dieser Versuch läuft ohne Bearbeitungszeit. Nutze „Test neu freigeben“, damit die neue Zeit greift.");
        }

        assignment.ExtraMinutes += granted;
        if (assignment.DeadlineAt is { } due)
        {
            assignment.DeadlineAt = (due > now ? due : now).AddMinutes(granted);
            assignment.CompletedAt = null;
            assignment.TimedOut = false;
        }
        await db.SaveChangesAsync(cancellationToken);

        // a partial grade goes stale the moment the agent edits an answer again
        await db.AgentTestAnswers
            .Where(a => a.AssignmentId == assignment.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ManualCorrect, (bool?)null)
                .SetProperty(a => a.ManualPoints, (int?)null), cancellationToken);
        db.AuditLogs.Add(ManualAudit.Row(nameof(AgentTestAssignment), assignment.Id,
            AuditAction.Modified, actor, ManualAudit.Change("Zusatzminuten", null, granted)));
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);
    }

    public async Task ResetAsync(string assignmentId, string? reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        // a graded attempt is history with a personnel entry; a retake is a new assignment
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException(
                "Ein korrigierter Test wird nicht neu freigegeben. Weise ihn für eine Wiederholung erneut zu.");
        }
        if (!await db.BewerbungTests.AnyAsync(t => t.Id == assignment.TestId && t.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Der Test ist nicht mehr aktiv oder wurde gelöscht.");
        }

        // soft-deleted, not overwritten, so the previous attempt stays traceable
        var answers = await db.AgentTestAnswers
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        db.AgentTestAnswers.RemoveRange(answers);

        assignment.StartedAt = null;
        assignment.DeadlineAt = null;
        assignment.TimeLimitMinutes = null;
        assignment.ExtraMinutes = 0;
        assignment.TimedOut = false;
        assignment.CompletedAt = null;
        assignment.AttemptCount += 1;
        if (TestEngine.Trim(reason) is { } note)
        {
            db.AuditLogs.Add(ManualAudit.Row(nameof(AgentTestAssignment), assignment.Id,
                AuditAction.Modified, actor, ManualAudit.Change("Neu freigegeben", null, note)));
        }
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);

        try
        {
            await notifications.NotifyAsync(assignment.AgentId, NotificationType.TestAssigned,
                "Dein Test wurde neu freigegeben.", $"/tests/{assignment.Id}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reset notice for {AssignmentId} failed.", assignment.Id);
        }
    }

    public async Task WithdrawAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return;
        }
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException("Ein korrigierter Test bleibt in der Personalakte und wird nicht zurückgezogen.");
        }
        db.AgentTestAssignments.Remove(assignment); // soft-delete
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);
    }

    public async Task<List<AgentTestRow>> GetMineAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var me = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AgentTestAssignments.AsNoTracking()
            .Where(a => a.AgentId == me)
            .OrderBy(a => a.CompletedAt != null)
            .ThenBy(a => a.DueAt == null).ThenBy(a => a.DueAt)
            .ThenByDescending(a => a.CompletedAt)
            .Take(ListCap);
        return await ToRowsAsync(db, query, cancellationToken);
    }

    public async Task<AgentTestStatusView?> GetMyStatusAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var me = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AgentTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.AgentId == me, cancellationToken);
        if (assignment is null)
        {
            return null;
        }
        var test = await db.BewerbungTests.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == assignment.TestId, cancellationToken);
        if (test is null)
        {
            return null;
        }
        // never stamps anything: reading a status must not start the clock
        return new AgentTestStatusView(assignment.Id, test.Title, test.Description,
            assignment.CompletedAt is not null, assignment.StartedAt, assignment.DeadlineAt, assignment.DueAt,
            assignment.TimedOut, TestDeadline.AllowedMinutes(assignment, test.TimeLimitMinutes));
    }

    public async Task<AgentTestView?> OpenAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var now = DateTime.UtcNow;
        var me = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.AgentTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.AgentId == me, cancellationToken);
        if (assignment is null)
        {
            return null;
        }
        var test = await db.BewerbungTests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == assignment.TestId, cancellationToken);
        if (test is null)
        {
            return null;
        }

        if (assignment is { StartedAt: null, CompletedAt: null })
        {
            var minutes = TestDeadline.Clamp(test.TimeLimitMinutes);
            var deadline = minutes is null ? null : TestDeadline.For(now, minutes + assignment.ExtraMinutes);
            // claim first, and only the version just read: prerender, the interactive pass, a second tab and F5
            // all race here, and the clock must be stamped exactly once
            var claimed = await db.AgentTestAssignments
                .Where(a => a.Id == assignment.Id && a.StartedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.StartedAt, now)
                    .SetProperty(a => a.TimeLimitMinutes, minutes)
                    .SetProperty(a => a.DeadlineAt, deadline), cancellationToken);
            if (claimed == 1)
            {
                // ExecuteUpdate bypasses the interceptors; RequireTestTaker already carried the write check
                db.AuditLogs.Add(ManualAudit.Row(nameof(AgentTestAssignment), assignment.Id,
                    AuditAction.Modified, actor, ManualAudit.Change("Bearbeitungszeit gestartet", null, minutes)));
                await db.SaveChangesAsync(cancellationToken);
                broadcaster.Report(assignment.Id, assignment.AgentId);
            }
            assignment = await db.AgentTestAssignments.AsNoTracking().FirstAsync(a => a.Id == assignment.Id, cancellationToken);
        }
        else if (assignment.CompletedAt is null && TestDeadline.IsExpired(assignment.DeadlineAt, now))
        {
            // closed here rather than trusting the sweep: a dead worker must not hand the questionnaire back out
            await CloseExpiredAsync(db, assignment, now, cancellationToken);
            assignment = await db.AgentTestAssignments.AsNoTracking().FirstAsync(a => a.Id == assignment.Id, cancellationToken);
        }

        var completed = assignment.CompletedAt is not null;
        var questions = completed
            ? (IReadOnlyList<TestQuestionView>)Array.Empty<TestQuestionView>()
            : await BuildQuestionViewsAsync(db, assignment, cancellationToken);
        return new AgentTestView(assignment.Id, assignment.AttemptCount, test.Title, test.Description, completed,
            assignment.DeadlineAt, assignment.TimedOut, TestDeadline.AllowedMinutes(assignment, test.TimeLimitMinutes),
            questions);
    }

    public async Task<TestDraftResult> SaveDraftAsync(string assignmentId, int attemptNumber,
        IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await LoadOwnAsync(db, assignmentId, actor.GetAgentId(), cancellationToken);
        if (assignment.CompletedAt is not null)
        {
            return new TestDraftResult(TestDraftOutcome.Closed, assignment.DeadlineAt);
        }
        // the circuit still holds an older attempt; its answers must not land in the fresh one
        if (assignment.AttemptCount != attemptNumber)
        {
            return new TestDraftResult(TestDraftOutcome.Closed, null);
        }
        if (TestDeadline.IsExpired(assignment.DeadlineAt, now))
        {
            if (!TestDeadline.GraceOver(assignment.DeadlineAt, now))
            {
                await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await CloseExpiredAsync(db, assignment, now, cancellationToken);
            return new TestDraftResult(TestDraftOutcome.Closed, assignment.DeadlineAt);
        }

        await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // no broadcast: a draft is not news
        return new TestDraftResult(TestDraftOutcome.Saved, assignment.DeadlineAt);
    }

    public async Task<TestSubmitOutcome> SubmitAsync(string assignmentId, int attemptNumber,
        IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await LoadOwnAsync(db, assignmentId, actor.GetAgentId(), cancellationToken);
        if (assignment.CompletedAt is not null)
        {
            // the sweep got there first; these are still the owner's last keystrokes and nobody graded yet
            if (assignment.TimedOut && assignment.GradedAt is null
                && !TestDeadline.GraceOver(assignment.DeadlineAt, now))
            {
                await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return TestSubmitOutcome.ClosedByTimeout;
            }
            throw new InvalidOperationException("Dieser Test wurde bereits abgeschlossen.");
        }
        if (assignment.AttemptCount != attemptNumber)
        {
            throw new InvalidOperationException("Dieser Test wurde neu freigegeben. Bitte lade die Seite neu.");
        }

        var expired = TestDeadline.IsExpired(assignment.DeadlineAt, now);
        var closing = TestDeadline.IsClosing(assignment.DeadlineAt, now);
        await UpsertAnswersAsync(db, assignment, answers, requireMandatory: !closing, cancellationToken);
        // answers first, then the claim: the rows are idempotent and must survive a concurrent close
        await db.SaveChangesAsync(cancellationToken);

        if (!await TestAttemptClose.ClaimAgentAsync(db, assignment.Id, now, expired,
                onlyWhenOverdue: false, cancellationToken))
        {
            return TestSubmitOutcome.ClosedByTimeout;
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(AgentTestAssignment), assignment.Id,
            AuditAction.Modified, actor, ManualAudit.Change("Test abgegeben", null, now)));
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);

        await NotifySubmittedAsync(db, assignment, "Test abgegeben", cancellationToken);
        return TestSubmitOutcome.Submitted;
    }

    public async Task<AgentTestResult?> GetMyResultAsync(string assignmentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTestTaker(actor);
        var me = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AgentTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.AgentId == me, cancellationToken);
        if (assignment is not { GradedAt: { } gradedAt })
        {
            return null;
        }
        var title = await db.BewerbungTests.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == assignment.TestId).Select(t => t.Title).FirstOrDefaultAsync(cancellationToken);
        // the frozen figures only: never the evaluation, which carries which question was wrong and the key
        return new AgentTestResult(title ?? "Test", assignment.CompletedAt, gradedAt,
            assignment.FinalPoints ?? 0, assignment.FinalMaxPoints ?? 0, assignment.FinalPassPercent, assignment.Passed);
    }

    /// <summary>The evaluation itself; grading and the panel read it through the same path.</summary>
    private static async Task<TestEvaluation> BuildEvaluationAsync(AppDbContext db, AgentTestAssignment assignment,
        CancellationToken cancellationToken)
    {
        var template = await TestEngine.LoadTemplateAsync(db, assignment.TestId, cancellationToken);
        var answers = await db.AgentTestAnswers.AsNoTracking()
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        return TestEngine.Evaluate(template, answers, assignment);
    }

    /// <summary>The questionnaire with the saved draft folded in; the guard is the caller's.</summary>
    private static async Task<IReadOnlyList<TestQuestionView>> BuildQuestionViewsAsync(AppDbContext db,
        AgentTestAssignment assignment, CancellationToken cancellationToken)
    {
        var template = await TestEngine.LoadTemplateAsync(db, assignment.TestId, cancellationToken);
        var draft = await db.AgentTestAnswers.AsNoTracking()
            .Where(a => a.AssignmentId == assignment.Id)
            // only the agent's input crosses; the grading columns stay in the house
            .Select(a => new AgentTestAnswer
            {
                Id = a.Id,
                CreatedAt = a.CreatedAt,
                QuestionId = a.QuestionId,
                SelectedOptionId = a.SelectedOptionId,
                SelectedOptionIds = a.SelectedOptionIds,
                FreeTextAnswer = a.FreeTextAnswer,
            })
            .ToListAsync(cancellationToken);
        return TestEngine.BuildQuestionViews(template, draft, $"{assignment.Id}:{assignment.AttemptCount}");
    }

    /// <summary>Writes one answer row per question; the guard is the caller's.</summary>
    private static async Task UpsertAnswersAsync(AppDbContext db, AgentTestAssignment assignment,
        IReadOnlyList<TestAnswerInput> answers, bool requireMandatory, CancellationToken cancellationToken)
    {
        var (questions, optionIds) = await TestEngine.LoadShapeAsync(db, assignment.TestId, cancellationToken);
        var existing = await db.AgentTestAnswers
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        TestEngine.ApplyAnswers(questions, optionIds, existing, answers, requireMandatory, questionId =>
        {
            var fresh = new AgentTestAnswer { AssignmentId = assignment.Id, QuestionId = questionId };
            db.AgentTestAnswers.Add(fresh);
            return fresh;
        });
    }

    /// <summary>Loads a tracked own attempt; someone else's id reads as a refusal, not as a missing row.</summary>
    private static async Task<AgentTestAssignment> LoadOwnAsync(AppDbContext db, string assignmentId, string? me,
        CancellationToken cancellationToken)
    {
        var assignment = await db.AgentTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (string.IsNullOrEmpty(me) || assignment.AgentId != me)
        {
            throw new UnauthorizedAccessException("Das ist nicht dein Test.");
        }
        return assignment;
    }

    /// <summary>Hands in the draft as it stands; true when this caller won the claim.</summary>
    private async Task<bool> CloseExpiredAsync(AppDbContext db, AgentTestAssignment assignment, DateTime now,
        CancellationToken cancellationToken)
    {
        if (!await TestAttemptClose.ClaimAgentAsync(db, assignment.Id, now, timedOut: true,
                onlyWhenOverdue: true, cancellationToken))
        {
            return false;
        }
        await TestAttemptClose.FillMissingAnswersAsync(db, assignment, cancellationToken);
        db.AuditLogs.Add(ManualAudit.SystemRow(nameof(AgentTestAssignment), assignment.Id,
            AuditAction.Modified, ManualAudit.Change("Zeit abgelaufen", null, now)));
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(assignment.Id, assignment.AgentId);
        await NotifySubmittedAsync(db, assignment, "Testzeit abgelaufen", cancellationToken);
        return true;
    }

    /// <summary>Tells whoever assigned the test that it is ready to grade; best effort.</summary>
    private async Task NotifySubmittedAsync(AppDbContext db, AgentTestAssignment assignment, string what,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(assignment.AssignedById))
        {
            return;
        }
        try
        {
            var codename = await db.Users.AsNoTracking()
                .Where(u => u.Id == assignment.AgentId).Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken);
            await notifications.NotifyAsync(assignment.AssignedById, NotificationType.TestSubmitted,
                $"{what}: {codename ?? "Agent"}", $"/tests/korrektur/{assignment.Id}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Submitted notice for {AssignmentId} failed.", assignment.Id);
        }
    }

    /// <summary>Ticks the linked training module unless it already is; a missing or inactive module is skipped.</summary>
    private async Task TickModuleAsync(string agentId, string moduleId, string title, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var active = await db.TrainingModules.AnyAsync(m => m.Id == moduleId && m.IsActive, cancellationToken);
            // MarkCompletedAsync refuses a second tick, so a module ticked by hand earlier is simply left alone
            var done = await db.AgentModuleCompletions
                .AnyAsync(c => c.AgentId == agentId && c.ModuleId == moduleId, cancellationToken);
            if (active && !done)
            {
                await trainingModules.MarkCompletedAsync(agentId, moduleId, $"Test „{title}“ bestanden.", actor,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Training module tick for {AgentId}/{ModuleId} failed.", agentId, moduleId);
        }
    }

    /// <summary>Writes the result into the personnel file, or rewrites the entry an earlier grading left.</summary>
    /// <remarks>A system entry like the top-agent award: the personnel-file service requires leadership, and an
    /// HRB grader is entitled to close the grading. No Discord post; the agent hears about it through the bell.</remarks>
    private async Task WritePersonnelNoteAsync(string assignmentId, string title, TestEvaluation report,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var assignment = await db.AgentTestAssignments.FirstAsync(a => a.Id == assignmentId, cancellationToken);
            var text = System.Net.WebUtility.HtmlEncode(PersonnelNoteText(title, report));
            var note = string.IsNullOrEmpty(assignment.PersonnelNoteId)
                ? null
                : await db.AgentNotes.FirstOrDefaultAsync(n => n.Id == assignment.PersonnelNoteId, cancellationToken);
            if (note is null)
            {
                note = new AgentNote
                {
                    AgentId = assignment.AgentId,
                    Kind = AgentNoteKind.Training,
                    EntryDate = DateTime.Now,
                    AuthorName = actor.GetCodename(),
                };
                db.AgentNotes.Add(note);
                assignment.PersonnelNoteId = note.Id;
            }
            note.Text = $"<p>{text}</p>";
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Personnel entry for test assignment {AssignmentId} failed.", assignmentId);
        }
    }

    /// <summary>The sentence the personnel file carries.</summary>
    public static string PersonnelNoteText(string title, TestEvaluation report)
    {
        var verdict = report.Passed switch
        {
            true => "bestanden",
            false => "nicht bestanden",
            null => "abgeschlossen",
        };
        return $"Test „{title}“ {verdict}: {report.TotalPoints} von {report.MaxPoints} Punkten ({report.Percent} %).";
    }

    /// <summary>List rows with codename and title; soft-deleted tests keep their title in the history.</summary>
    private static async Task<List<AgentTestRow>> ToRowsAsync(AppDbContext db, IQueryable<AgentTestAssignment> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }
        var testIds = rows.Select(r => r.TestId).Distinct().ToList();
        var agentIds = rows.Select(r => r.AgentId).Distinct().ToList();
        var tests = await db.BewerbungTests.IgnoreQueryFilters().AsNoTracking()
            .Where(t => testIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title })
            .ToDictionaryAsync(t => t.Id, t => t.Title, cancellationToken);
        var agents = await db.Users.AsNoTracking()
            .Where(u => agentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename })
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
        return rows.Select(a => new AgentTestRow(
            a.Id, a.AgentId, agents.GetValueOrDefault(a.AgentId) ?? "—",
            a.TestId, tests.GetValueOrDefault(a.TestId) ?? "Test",
            a.CreatedAt, a.AssignedByName, a.DueAt, a.StartedAt, a.CompletedAt, a.TimedOut,
            a.TimeLimitMinutes, a.GradedAt,
            a.GradedAt is null ? null : a.FinalPoints,
            a.GradedAt is null ? null : a.FinalMaxPoints,
            a.GradedAt is null ? null : a.Passed)).ToList();
    }
}
