using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IBewerbungTestService" />
public class BewerbungTestService(
    IDbContextFactory<AppDbContext> dbFactory,
    BewerbungBroadcaster broadcaster,
    INotificationService notifications) : IBewerbungTestService
{
    public async Task<List<BewerbungTest>> GetTestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.BewerbungTests.AsNoTracking()
            .OrderBy(t => t.Sorting).ThenBy(t => t.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<BewerbungTest> CreateTestAsync(string title, string? description, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Der Titel darf nicht leer sein.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var max = await db.BewerbungTests.MaxAsync(t => (int?)t.Sorting, cancellationToken) ?? 0;
        var test = new BewerbungTest { Title = title.Trim(), Description = Trim(description), Sorting = max + 1 };
        db.BewerbungTests.Add(test);
        await db.SaveChangesAsync(cancellationToken);
        return test;
    }

    public async Task UpdateTestAsync(string id, string title, string? description, bool isActive, int? passPercent, int? timeLimitMinutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Der Titel darf nicht leer sein.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var test = await db.BewerbungTests.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Test nicht gefunden.");
        test.Title = title.Trim();
        test.Description = Trim(description);
        test.IsActive = isActive;
        test.PassPercent = passPercent is null ? null : Math.Clamp(passPercent.Value, 0, 100);
        // a running attempt keeps the deadline frozen at its start, so this only reaches later attempts
        test.TimeLimitMinutes = TestDeadline.Clamp(timeLimitMinutes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateUsageAsync(string id, TestUsage usage, string? trainingModuleId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var test = await db.BewerbungTests.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Test nicht gefunden.");
        var module = Trim(trainingModuleId);
        if (module is not null && !await db.TrainingModules.AnyAsync(m => m.Id == module, cancellationToken))
        {
            throw new InvalidOperationException("Das gewählte Ausbildungsmodul wurde nicht gefunden.");
        }
        test.Usage = usage;
        // only an internal test can tick a module; an applicant has no personnel file
        test.TrainingModuleId = TestUsageDisplay.ForAgents(usage) ? module : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTestAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var test = await db.BewerbungTests.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (test is null)
        {
            return;
        }
        // an open assignment points at this test, and there is no way to un-assign one: deleting it left the
        // applicant unable to finish and the HRB unable to decide, with no route out for either
        if (await db.BewerbungTestAssignments
                .AnyAsync(a => a.TestId == id && a.CompletedAt == null, cancellationToken)
            || await db.AgentTestAssignments
                .AnyAsync(a => a.TestId == id && a.GradedAt == null, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Test ist noch zugewiesen und nicht abgeschlossen. "
                + "Setze ihn auf inaktiv, damit er nicht mehr vergeben wird.");
        }

        db.BewerbungTests.Remove(test); // soft-delete
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TestEditModel?> GetEditModelAsync(string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var test = await db.BewerbungTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == testId, cancellationToken);
        if (test is null)
        {
            return null;
        }
        var questions = await db.BewerbungTestQuestions.AsNoTracking()
            .Where(q => q.TestId == testId).OrderBy(q => q.Sorting).ToListAsync(cancellationToken);
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await db.BewerbungTestOptions.AsNoTracking()
            .Where(o => questionIds.Contains(o.QuestionId)).OrderBy(o => o.Sorting).ToListAsync(cancellationToken);

        var edits = questions
            .Select(q => new TestQuestionEdit(q, options.Where(o => o.QuestionId == q.Id).ToList()))
            .ToList();
        return new TestEditModel(test, edits);
    }

    public async Task<BewerbungTestQuestion> AddQuestionAsync(string testId, TestQuestionType type, string prompt, bool required, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireNoLiveAttemptAsync(db, testId, cancellationToken);
        var max = await db.BewerbungTestQuestions.Where(q => q.TestId == testId)
            .MaxAsync(q => (int?)q.Sorting, cancellationToken) ?? 0;
        var question = new BewerbungTestQuestion
        {
            TestId = testId,
            Type = type,
            Prompt = (prompt ?? string.Empty).Trim(),
            Required = required,
            Sorting = max + 1,
        };
        db.BewerbungTestQuestions.Add(question);
        await db.SaveChangesAsync(cancellationToken);
        return question;
    }

    public async Task UpdateQuestionAsync(string questionId, string prompt, bool required, int points, bool? correctYesNo, string? keywords, int? minKeywordHits, bool keepOptionOrder, bool allowMultiple, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var question = await db.BewerbungTestQuestions.FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken)
            ?? throw new InvalidOperationException("Frage nicht gefunden.");
        await RequireNoLiveAttemptAsync(db, question.TestId, cancellationToken);
        question.Prompt = (prompt ?? string.Empty).Trim();
        question.Required = required;
        question.Points = Math.Max(0, points);
        question.CorrectYesNo = question.Type == TestQuestionType.YesNo ? correctYesNo : null;
        question.Keywords = question.Type == TestQuestionType.FreeText ? Trim(keywords) : null;
        question.MinKeywordHits = question.Type == TestQuestionType.FreeText && minKeywordHits is > 0 ? minKeywordHits : null;
        question.KeepOptionOrder = question.Type == TestQuestionType.MultipleChoice && keepOptionOrder;
        question.AllowMultiple = question.Type == TestQuestionType.MultipleChoice && allowMultiple;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteQuestionAsync(string questionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var question = await db.BewerbungTestQuestions.FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken);
        if (question is null)
        {
            return;
        }
        await RequireNoLiveAttemptAsync(db, question.TestId, cancellationToken);
        db.BewerbungTestQuestions.Remove(question);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BewerbungTestOption> AddOptionAsync(string questionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireNoLiveAttemptForQuestionAsync(db, questionId, cancellationToken);
        var max = await db.BewerbungTestOptions.Where(o => o.QuestionId == questionId)
            .MaxAsync(o => (int?)o.Sorting, cancellationToken) ?? 0;
        var option = new BewerbungTestOption
        {
            QuestionId = questionId,
            Label = (label ?? string.Empty).Trim(),
            IsCorrect = isCorrect,
            Sorting = max + 1,
        };
        db.BewerbungTestOptions.Add(option);
        await db.SaveChangesAsync(cancellationToken);
        return option;
    }

    public async Task UpdateOptionAsync(string optionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var option = await db.BewerbungTestOptions.FirstOrDefaultAsync(o => o.Id == optionId, cancellationToken)
            ?? throw new InvalidOperationException("Antwortoption nicht gefunden.");
        await RequireNoLiveAttemptForQuestionAsync(db, option.QuestionId, cancellationToken);
        option.Label = (label ?? string.Empty).Trim();
        option.IsCorrect = isCorrect;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteOptionAsync(string optionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var option = await db.BewerbungTestOptions.FirstOrDefaultAsync(o => o.Id == optionId, cancellationToken);
        if (option is null)
        {
            return;
        }
        await RequireNoLiveAttemptForQuestionAsync(db, option.QuestionId, cancellationToken);
        db.BewerbungTestOptions.Remove(option);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BewerbungTestAssignment?> GetAssignmentAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.BewerbungTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken);
    }

    public async Task AssignAsync(string bewerbungId, string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var bewerbung = await db.Bewerbungen.FirstOrDefaultAsync(b => b.Id == bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Bewerbung nicht gefunden.");
        if (await db.BewerbungTestAssignments.AnyAsync(a => a.BewerbungId == bewerbungId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Bewerbung ist bereits ein Test zugewiesen.");
        }
        if (!await db.BewerbungTests.AnyAsync(t => t.Id == testId && t.Usage != TestUsage.Intern, cancellationToken))
        {
            throw new InvalidOperationException("Test nicht gefunden oder nur für interne Tests freigegeben.");
        }

        db.BewerbungTestAssignments.Add(new BewerbungTestAssignment
        {
            BewerbungId = bewerbungId,
            TestId = testId,
            AssignedByName = actor.GetCodename(),
        });

        // entering the test phase
        if (bewerbung.Status is BewerbungStatus.Eingereicht or BewerbungStatus.InSicherheitspruefung)
        {
            bewerbung.Status = BewerbungStatus.ImTest;
        }
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(bewerbungId);

        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                "Dir wurde ein Test zugewiesen.", "/portal/test", cancellationToken);
        }
        catch { /* best effort */ }
    }

    public async Task<TestEvaluation?> GetEvaluationAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.BewerbungTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken);
        // an open attempt has draft answers in those rows: reading them would let HRB watch the applicant type,
        // and would let a grade be frozen mid-attempt
        return assignment is { CompletedAt: not null }
            ? await BuildEvaluationAsync(db, assignment, cancellationToken)
            : null;
    }

    /// <summary>The evaluation itself; both grading transitions read it through the same path the panel does.</summary>
    private static async Task<TestEvaluation> BuildEvaluationAsync(
        AppDbContext db, BewerbungTestAssignment assignment, CancellationToken cancellationToken)
    {
        var template = await TestEngine.LoadTemplateAsync(db, assignment.TestId, cancellationToken);
        var answers = await db.BewerbungTestAnswers.AsNoTracking()
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        return TestEngine.Evaluate(template, answers, assignment);
    }

    public async Task SetAwardedPointsAsync(string assignmentId, string questionId, int? points,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.BewerbungTestAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException("Die Korrektur ist abgeschlossen. Bitte sie zuerst wieder öffnen.");
        }
        var question = await db.BewerbungTestQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.TestId == assignment.TestId, cancellationToken)
            ?? throw new InvalidOperationException("Frage gehört nicht zu diesem Test.");

        // keyed on the question, not on an answer row: a question added after the applicant submitted has none.
        // Picked the same way the evaluation picks, so a duplicate row cannot make the grader edit the row the
        // evaluation ignores.
        var rows = await db.BewerbungTestAnswers
            .Where(a => a.AssignmentId == assignmentId && a.QuestionId == questionId)
            .ToListAsync(cancellationToken);
        TestEngine.PickAnswers(rows).TryGetValue(questionId, out var answer);
        if (answer is null)
        {
            answer = new BewerbungTestAnswer { AssignmentId = assignmentId, QuestionId = questionId };
            db.BewerbungTestAnswers.Add(answer);
        }
        answer.ManualPoints = points is null ? null : Math.Clamp(points.Value, 0, question.Points);
        // "Auto" clears the old verdict too, otherwise a row graded before points existed would keep overriding
        if (points is null)
        {
            answer.ManualCorrect = null;
        }
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(assignment.BewerbungId);
    }

    public async Task CompleteGradingAsync(string bewerbungId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        var evaluation = await GetEvaluationInternalAsync(bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Kein Test zugewiesen.");
        if (evaluation.Assignment.CompletedAt is null)
        {
            throw new InvalidOperationException("Der Bewerber hat den Test noch nicht abgegeben.");
        }
        if (evaluation.Report.OpenCount > 0)
        {
            throw new InvalidOperationException(
                $"Noch {evaluation.Report.OpenCount} Frage(n) ohne Wertung. Bitte zuerst alle bewerten.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.BewerbungTestAssignments
            .FirstOrDefaultAsync(a => a.Id == evaluation.Assignment.Id, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        assignment.GradedAt = DateTime.UtcNow;
        assignment.GradedByName = actor.GetCodename();
        assignment.FinalPoints = evaluation.Report.TotalPoints;
        assignment.FinalMaxPoints = evaluation.Report.MaxPoints;
        assignment.FinalPassPercent = evaluation.Report.PassPercent;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(bewerbungId);

        try
        {
            var recipient = await db.Bewerbungen.AsNoTracking()
                .Where(b => b.Id == bewerbungId).Select(b => b.AssignedAgentId).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrEmpty(recipient))
            {
                await notifications.NotifyAsync(recipient, NotificationType.Recruiting,
                    "Test korrigiert", $"/bewerbungen/{bewerbungId}", cancellationToken);
            }
        }
        catch { /* best effort */ }
    }

    public async Task ReopenGradingAsync(string bewerbungId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.BewerbungTestAssignments
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken)
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
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(bewerbungId);
    }

    /// <summary>Evaluation plus its assignment, for the two grading transitions; the guard is the caller's.</summary>
    private async Task<(BewerbungTestAssignment Assignment, TestEvaluation Report)?> GetEvaluationInternalAsync(
        string bewerbungId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.BewerbungTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken);
        if (assignment is null)
        {
            return null;
        }
        var report = await BuildEvaluationAsync(db, assignment, cancellationToken);
        return (assignment, report);
    }

    public async Task<TestStatusView?> GetTestStatusForApplicantAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var userId = applicant.GetAgentId();
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (bewerbung, assignment) = await FindOwnAssignmentAsync(db, userId, cancellationToken);
        if (bewerbung is null || assignment is null)
        {
            return null;
        }
        var test = await db.BewerbungTests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == assignment.TestId, cancellationToken);
        if (test is null)
        {
            return null;
        }
        // never stamps anything: the status page reads this, and reading a status must not start the clock
        return new TestStatusView(assignment.Id, test.Title, test.Description,
            assignment.CompletedAt is not null, assignment.StartedAt, assignment.DeadlineAt,
            assignment.TimedOut, TestDeadline.AllowedMinutes(assignment, test.TimeLimitMinutes));
    }

    public async Task<TestView?> GetAssignedForApplicantAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var now = DateTime.UtcNow;
        var userId = applicant.GetAgentId();
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (bewerbung, assignment) = await FindOwnAssignmentAsync(db, userId, cancellationToken);
        if (bewerbung is null || assignment is null)
        {
            return null;
        }
        var test = await db.BewerbungTests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == assignment.TestId, cancellationToken);
        if (test is null)
        {
            return null;
        }

        var decided = BewerbungStatusDisplay.IsTerminal(bewerbung.Status);
        if (assignment is { StartedAt: null, CompletedAt: null } && !decided)
        {
            var minutes = TestDeadline.Clamp(test.TimeLimitMinutes);
            // the base is frozen, a grant made before the first open is added on top of it
            var deadline = minutes is null ? null : TestDeadline.For(now, minutes + assignment.ExtraMinutes);
            // claim first, and only the version just read: the prerender pass, the interactive pass, a second
            // tab and F5 all race here, and the clock must be stamped exactly once
            var claimed = await db.BewerbungTestAssignments
                .Where(a => a.Id == assignment.Id && a.StartedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.StartedAt, now)
                    .SetProperty(a => a.TimeLimitMinutes, minutes)
                    .SetProperty(a => a.DeadlineAt, deadline), cancellationToken);
            if (claimed == 1)
            {
                // ExecuteUpdate bypasses the interceptors, so this row is written by hand. RequireApplicant
                // already carries the write check: an applicant is never read-only supervision, partner or demo.
                db.AuditLogs.Add(ManualAudit.Row(nameof(BewerbungTestAssignment), assignment.Id,
                    AuditAction.Modified, applicant,
                    ManualAudit.Change("Bearbeitungszeit gestartet", null, minutes)));
                await db.SaveChangesAsync(cancellationToken);
                broadcaster.Report(bewerbung.Id);
            }
            // re-read either way: the loser must serve the winning deadline, not its own
            assignment = await db.BewerbungTestAssignments.AsNoTracking()
                .FirstAsync(a => a.Id == assignment.Id, cancellationToken);
        }
        else if (assignment.CompletedAt is null && !decided && TestDeadline.IsExpired(assignment.DeadlineAt, now))
        {
            // the read path closes it itself rather than trusting the sweep: a closed browser plus a dead
            // worker must not hand the questionnaire back out with the clock already at zero
            await CloseExpiredAsync(db, assignment, bewerbung, now, cancellationToken);
            assignment = await db.BewerbungTestAssignments.AsNoTracking()
                .FirstAsync(a => a.Id == assignment.Id, cancellationToken);
        }

        // a decided application gets a closed view rather than a questionnaire that only fails on submit
        var completed = assignment.CompletedAt is not null || decided;
        var questions = completed
            ? (IReadOnlyList<TestQuestionView>)Array.Empty<TestQuestionView>()
            : await BuildQuestionViewsAsync(db, assignment, cancellationToken);

        return new TestView(assignment.Id, assignment.AttemptCount, bewerbung.CaseNumber, test.Title, test.Description,
            completed, assignment.DeadlineAt, assignment.TimedOut,
            TestDeadline.AllowedMinutes(assignment, test.TimeLimitMinutes), questions);
    }

    public async Task<TestDraftResult> SaveDraftAsync(string assignmentId, int attemptNumber,
        IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var now = DateTime.UtcNow;
        var userId = applicant.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (assignment, bewerbung) = await LoadOwnAttemptAsync(db, assignmentId, userId, cancellationToken);
        // a stale circuit must not rewrite what the clock already handed in and HRB may already be grading
        if (assignment.CompletedAt is not null)
        {
            return new TestDraftResult(TestDraftOutcome.Closed, assignment.DeadlineAt);
        }
        // the circuit still holds an older attempt: HRB released the test again, and answers typed for the
        // previous attempt must not be written into the fresh one
        if (assignment.AttemptCount != attemptNumber)
        {
            return new TestDraftResult(TestDraftOutcome.Closed, null);
        }
        if (TestDeadline.IsExpired(assignment.DeadlineAt, now))
        {
            // this payload is the last input of the applicant, arriving as the clock ran out - the countdown
            // flushes at zero precisely to save it, so it is written before the attempt is closed
            if (!TestDeadline.GraceOver(assignment.DeadlineAt, now))
            {
                await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await CloseExpiredAsync(db, assignment, bewerbung, now, cancellationToken);
            return new TestDraftResult(TestDraftOutcome.Closed, assignment.DeadlineAt);
        }

        await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // no broadcast: a draft is not news, and one every few seconds would reload every open HRB panel
        return new TestDraftResult(TestDraftOutcome.Saved, assignment.DeadlineAt);
    }

    public async Task<TestSubmitOutcome> SubmitAnswersAsync(string assignmentId, int attemptNumber,
        IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default)
    {
        Permission.RequireApplicant(applicant);
        var now = DateTime.UtcNow;
        var userId = applicant.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (assignment, bewerbung) = await LoadOwnAttemptAsync(db, assignmentId, userId, cancellationToken);

        if (assignment.CompletedAt is not null)
        {
            // the sweep or another tab got there microseconds earlier; these are still the last keystrokes of
            // the owner of this attempt, and nobody has started grading
            if (assignment.TimedOut && assignment.GradedAt is null
                && !TestDeadline.GraceOver(assignment.DeadlineAt, now))
            {
                await UpsertAnswersAsync(db, assignment, answers, requireMandatory: false, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return TestSubmitOutcome.ClosedByTimeout;
            }
            throw new InvalidOperationException("Dieser Test wurde bereits abgeschlossen.");
        }
        // answers typed for an older attempt must not land in the fresh one
        if (assignment.AttemptCount != attemptNumber)
        {
            throw new InvalidOperationException("Dieser Test wurde neu freigegeben. Bitte lade die Seite neu.");
        }

        // past the deadline the answers still count - the decision was auto-submit, not discard - and the
        // attempt carries the marker so the grading panel can see why it is thin
        var expired = TestDeadline.IsExpired(assignment.DeadlineAt, now);
        // the mandatory check is a safety net for the applicant, not a gate: in the closing seconds the
        // countdown may already have fired, and refusing a blank then throws away everything that was filled in
        var closing = TestDeadline.IsClosing(assignment.DeadlineAt, now);

        await UpsertAnswersAsync(db, assignment, answers, requireMandatory: !closing, cancellationToken);
        // answers first, then the claim: unlike a reminder bell these rows are idempotent, and they must
        // survive even when another writer closed the attempt in between
        await db.SaveChangesAsync(cancellationToken);

        if (!await TestAttemptClose.ClaimAsync(db, assignment.Id, now, expired,
                onlyWhenOverdue: false, cancellationToken))
        {
            return TestSubmitOutcome.ClosedByTimeout;
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(BewerbungTestAssignment), assignment.Id,
            AuditAction.Modified, applicant, ManualAudit.Change("Test abgegeben", null, now)));
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(bewerbung.Id);

        try
        {
            var recipient = bewerbung.AssignedAgentId;
            if (!string.IsNullOrEmpty(recipient))
            {
                await notifications.NotifyAsync(recipient, NotificationType.Recruiting,
                    $"Test abgeschlossen ({bewerbung.CaseNumber})", $"/bewerbungen/{bewerbung.Id}", cancellationToken);
            }
        }
        catch { /* best effort */ }
        return TestSubmitOutcome.Submitted;
    }

    public async Task ExtendAttemptAsync(string bewerbungId, int minutes, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTestAttemptWrite(actor);
        var now = DateTime.UtcNow;
        var granted = TestDeadline.Clamp(minutes)
            ?? throw new InvalidOperationException("Bitte gib die zusätzliche Zeit in Minuten an.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.BewerbungTestAssignments
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        var bewerbung = await db.Bewerbungen.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Bewerbung nicht gefunden.");
        if (BewerbungStatusDisplay.IsTerminal(bewerbung.Status))
        {
            throw new InvalidOperationException("Über diese Bewerbung ist bereits entschieden.");
        }
        if (assignment.GradedAt is not null)
        {
            throw new InvalidOperationException("Die Korrektur ist abgeschlossen. Bitte sie zuerst wieder öffnen.");
        }
        // an attempt handed in on time is finished; granting time would silently reopen it and let the
        // applicant edit answers they had already submitted. Only the clock may reopen what the clock closed.
        if (assignment is { CompletedAt: not null, TimedOut: false })
        {
            throw new InvalidOperationException(
                "Der Bewerber hat den Test regulär abgegeben. Nutze „Test neu freigeben“, wenn er einen "
                + "neuen Versuch bekommen soll.");
        }
        var testMinutes = await db.BewerbungTests.AsNoTracking()
            .Where(x => x.Id == assignment.TestId).Select(x => x.TimeLimitMinutes)
            .FirstOrDefaultAsync(cancellationToken);
        if (TestDeadline.AllowedMinutes(assignment, testMinutes) is null)
        {
            throw new InvalidOperationException(
                "Für diesen Test ist keine Bearbeitungszeit hinterlegt, es gibt also keine Frist zu verlängern.");
        }
        // started without a clock: the test only got its minutes afterwards, so there is no deadline to push
        // and adding minutes here would report success while granting nothing
        if (assignment is { StartedAt: not null, DeadlineAt: null })
        {
            throw new InvalidOperationException(
                "Dieser Versuch läuft ohne Bearbeitungszeit. Nutze „Test neu freigeben“, damit die neue Zeit greift.");
        }

        // only the grant column grows; the base stays frozen so the two never add up twice
        assignment.ExtraMinutes += granted;
        if (assignment.DeadlineAt is { } due)
        {
            // measured from now when the old deadline is long gone, otherwise the grant buys no time at all
            assignment.DeadlineAt = (due > now ? due : now).AddMinutes(granted);
            // the grant reopens the attempt: the flag describes the outcome, and time was given
            assignment.CompletedAt = null;
            assignment.TimedOut = false;
        }
        await db.SaveChangesAsync(cancellationToken);

        // a partial grade goes stale the moment the applicant edits an answer again
        await db.BewerbungTestAnswers
            .Where(a => a.AssignmentId == assignment.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ManualCorrect, (bool?)null)
                .SetProperty(a => a.ManualPoints, (int?)null), cancellationToken);
        db.AuditLogs.Add(ManualAudit.Row(nameof(BewerbungTestAssignment), assignment.Id,
            AuditAction.Modified, actor, ManualAudit.Change("Zusatzminuten", null, granted)));
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(bewerbungId);
        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                "Deine Bearbeitungszeit wurde verlängert.", "/portal/test", cancellationToken);
        }
        catch { /* best effort */ }
    }

    public async Task ResetAttemptAsync(string bewerbungId, string? testId, string? reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTestAttemptWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignment = await db.BewerbungTestAssignments
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        var bewerbung = await db.Bewerbungen.FirstOrDefaultAsync(b => b.Id == bewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Bewerbung nicht gefunden.");
        if (BewerbungStatusDisplay.IsTerminal(bewerbung.Status))
        {
            throw new InvalidOperationException("Über diese Bewerbung ist bereits entschieden.");
        }
        var target = testId ?? assignment.TestId;
        if (!await db.BewerbungTests.AnyAsync(t => t.Id == target && t.IsActive && t.Usage != TestUsage.Intern,
                cancellationToken))
        {
            throw new InvalidOperationException("Der Test ist nicht mehr aktiv oder wurde gelöscht.");
        }

        // soft-deleted, not overwritten: the interceptor keeps the old values in the audit log, so the
        // previous attempt stays readable. A hard delete would destroy exactly that.
        var answers = await db.BewerbungTestAnswers
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        db.BewerbungTestAnswers.RemoveRange(answers);

        // in place on this one row: the unique index on BewerbungId is still held by a soft-deleted row, and
        // the global filter would hide it from the pre-check, so delete-and-recreate fails with a raw 1062
        assignment.TestId = target;
        assignment.StartedAt = null;
        assignment.DeadlineAt = null;
        assignment.TimeLimitMinutes = null;
        assignment.ExtraMinutes = 0;
        assignment.TimedOut = false;
        assignment.CompletedAt = null;
        assignment.AttemptCount += 1;
        // the frozen figures go with it, same reason as reopening a grading
        assignment.GradedAt = null;
        assignment.GradedByName = null;
        assignment.FinalPoints = null;
        assignment.FinalMaxPoints = null;
        assignment.FinalPassPercent = null;

        // entering the test phase, the same nudge the assignment makes
        if (bewerbung.Status is BewerbungStatus.Eingereicht or BewerbungStatus.InSicherheitspruefung)
        {
            bewerbung.Status = BewerbungStatus.ImTest;
        }
        // the interceptor logs the cleared fields, but not WHY: that is the whole point of asking for it
        if (Trim(reason) is { } note)
        {
            db.AuditLogs.Add(ManualAudit.Row(nameof(BewerbungTestAssignment), assignment.Id,
                AuditAction.Modified, actor, ManualAudit.Change("Neu freigegeben", null, note)));
        }
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(bewerbungId);
        try
        {
            await notifications.NotifyAsync(bewerbung.ApplicantUserId, NotificationType.Recruiting,
                "Dein Test wurde neu freigegeben.", "/portal/test", cancellationToken);
        }
        catch { /* best effort */ }
    }

    /// <summary>The newest own application and its single assignment; read-only, stamps nothing.</summary>
    private static async Task<(Bewerbung? Bewerbung, BewerbungTestAssignment? Assignment)> FindOwnAssignmentAsync(
        AppDbContext db, string userId, CancellationToken cancellationToken)
    {
        var bewerbung = await db.Bewerbungen.AsNoTracking()
            .Where(b => b.ApplicantUserId == userId)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (bewerbung is null)
        {
            return (null, null);
        }
        var assignment = await db.BewerbungTestAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BewerbungId == bewerbung.Id, cancellationToken);
        return (bewerbung, assignment);
    }

    /// <summary>Loads a tracked attempt and proves it belongs to this applicant and is still decidable.</summary>
    /// <remarks>Bundled so a new applicant write path cannot forget one of the three checks. Answers arriving
    /// after the decision change nothing and read as if it were still open.</remarks>
    private static async Task<(BewerbungTestAssignment Assignment, Bewerbung Bewerbung)> LoadOwnAttemptAsync(
        AppDbContext db, string assignmentId, string? userId, CancellationToken cancellationToken)
    {
        var assignment = await db.BewerbungTestAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new InvalidOperationException("Testzuweisung nicht gefunden.");
        var bewerbung = await db.Bewerbungen
            .FirstOrDefaultAsync(b => b.Id == assignment.BewerbungId, cancellationToken)
            ?? throw new InvalidOperationException("Bewerbung nicht gefunden.");
        if (bewerbung.ApplicantUserId != userId)
        {
            throw new UnauthorizedAccessException("Das ist nicht dein Test.");
        }
        if (BewerbungStatusDisplay.IsTerminal(bewerbung.Status))
        {
            throw new InvalidOperationException("Über diese Bewerbung ist bereits entschieden.");
        }
        return (assignment, bewerbung);
    }

    /// <summary>The questionnaire with the saved draft folded in; the guard is the caller's.</summary>
    private static async Task<IReadOnlyList<TestQuestionView>> BuildQuestionViewsAsync(
        AppDbContext db, BewerbungTestAssignment assignment, CancellationToken cancellationToken)
    {
        var template = await TestEngine.LoadTemplateAsync(db, assignment.TestId, cancellationToken);
        var draft = await db.BewerbungTestAnswers.AsNoTracking()
            .Where(a => a.AssignmentId == assignment.Id)
            // only the applicant input crosses; the two grading columns on the same row stay in the house
            .Select(a => new BewerbungTestAnswer
            {
                Id = a.Id,
                CreatedAt = a.CreatedAt,
                QuestionId = a.QuestionId,
                SelectedOptionId = a.SelectedOptionId,
                SelectedOptionIds = a.SelectedOptionIds,
                FreeTextAnswer = a.FreeTextAnswer,
            })
            .ToListAsync(cancellationToken);

        // seeded on the attempt as well as the assignment: a reset keeps the row, so without the counter the
        // second attempt would show the identical shuffle
        return TestEngine.BuildQuestionViews(template, draft, $"{assignment.Id}:{assignment.AttemptCount}");
    }

    /// <summary>Hands in the draft as it stands and stamps the marker; true when this caller won the claim.</summary>
    private async Task<bool> CloseExpiredAsync(AppDbContext db, BewerbungTestAssignment assignment,
        Bewerbung bewerbung, DateTime now, CancellationToken cancellationToken)
    {
        // claim first, and only while the row really is overdue: an extension or a reset landing in between
        // sets CompletedAt back to null and would re-arm a plain claim
        if (!await TestAttemptClose.ClaimAsync(db, assignment.Id, now, timedOut: true,
                onlyWhenOverdue: true, cancellationToken))
        {
            return false;
        }
        // the draft rows ARE the submission: nothing is rewritten, only the untouched questions get the
        // empty row the evaluation keys on
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
        }
        catch { /* best effort */ }
        return true;
    }

    /// <summary>Writes one answer row per question, creating what is missing; the guard is the caller's.</summary>
    /// <remarks>The validation rules live in <see cref="TestEngine.ApplyAnswers{T}"/>.</remarks>
    private static async Task UpsertAnswersAsync(AppDbContext db, BewerbungTestAssignment assignment,
        IReadOnlyList<TestAnswerInput> answers, bool requireMandatory, CancellationToken cancellationToken)
    {
        var (questions, optionIds) = await TestEngine.LoadShapeAsync(db, assignment.TestId, cancellationToken);
        var existing = await db.BewerbungTestAnswers
            .Where(a => a.AssignmentId == assignment.Id).ToListAsync(cancellationToken);
        TestEngine.ApplyAnswers(questions, optionIds, existing, answers, requireMandatory, questionId =>
        {
            var fresh = new BewerbungTestAnswer { AssignmentId = assignment.Id, QuestionId = questionId };
            db.BewerbungTestAnswers.Add(fresh);
            return fresh;
        });
    }

    /// <summary>Refuse a structural edit while a timed attempt on that test is running.</summary>
    /// <remarks>
    /// Otherwise an applicant is graded on a question they never saw, or an option they already ticked
    /// disappears. Deliberately bounded by the deadline: an attempt without a limit is never live here,
    /// because a started but never submitted one would block the questions forever.
    /// </remarks>
    private static async Task RequireNoLiveAttemptAsync(AppDbContext db, string testId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var live = await db.BewerbungTestAssignments.AsNoTracking()
            .AnyAsync(a => a.TestId == testId && a.StartedAt != null && a.CompletedAt == null
                && a.DeadlineAt != null && a.DeadlineAt > now, cancellationToken)
            // an agent mid-attempt is graded on the same questions
            || await db.AgentTestAssignments.AsNoTracking()
                .AnyAsync(a => a.TestId == testId && a.StartedAt != null && a.CompletedAt == null
                    && a.DeadlineAt != null && a.DeadlineAt > now, cancellationToken);
        if (live)
        {
            throw new InvalidOperationException(
                "Jemand bearbeitet diesen Test gerade. Fragen und Antwortoptionen sind bis zum Ablauf "
                + "der Bearbeitungszeit gesperrt; Titel, Bestehensgrenze und Bearbeitungszeit gehen weiter.");
        }
    }

    /// <summary>Same guard, reached from a question or option id.</summary>
    private static async Task RequireNoLiveAttemptForQuestionAsync(AppDbContext db, string questionId,
        CancellationToken cancellationToken)
    {
        var testId = await db.BewerbungTestQuestions.AsNoTracking()
            .Where(q => q.Id == questionId).Select(q => q.TestId).FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrEmpty(testId))
        {
            await RequireNoLiveAttemptAsync(db, testId, cancellationToken);
        }
    }

    private static string? Trim(string? value) => TestEngine.Trim(value);
}
