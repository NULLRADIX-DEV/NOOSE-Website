using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The processing-time clock of a recruiting test: start, draft, hand-in, sweep, extend, reset.</summary>
/// <remarks>
/// There is no injectable clock in this codebase, so the deadline is seeded as a parameter rather than "now" being
/// faked, and a stamp the service writes itself is asserted with the before/after bracket. Where the point is that
/// the clock does NOT restart, the assertion is exact equality of the two deadlines — that needs no clock at all.
/// </remarks>
public sealed class BewerbungTestZeitTests
{
    private static (BewerbungTestService Svc, BewerbungBroadcaster Broadcaster, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new BewerbungBroadcaster();
        return (new BewerbungTestService(ctx.Factory, broadcaster, notifications, Substitute.For<IApplicantDmNotifier>()), broadcaster, notifications);
    }

    private static (BewerbungTestExpiryService Svc, INotificationService Notifications) BuildSweep(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new BewerbungTestExpiryService(ctx.Factory, new BewerbungBroadcaster(), notifications, Substitute.For<IApplicantDmNotifier>()), notifications);
    }

    private static ClaimsPrincipal Hrb(string id = "hrb1")
        => ClaimsPrincipalBuilder.Agent(id).AsHrb().WithCodename("HRB-Alpha").Build();

    // passes RequireHrbOrLeadership but fails MayWrite: the read-only supervision
    private static ClaimsPrincipal OnlyReader(string id = "aufsicht")
        => ClaimsPrincipalBuilder.Agent(id).AsHrb().AsTeamLead().WithCodename("Aufsicht").Build();

    // also passes RequireHrbOrLeadership and also fails MayWrite
    private static ClaimsPrincipal Demo(string id = "demo")
        => ClaimsPrincipalBuilder.Agent(id).AsHrb().AsDemo().WithCodename("Demo").Build();

    private static ClaimsPrincipal Applicant(string id = "app1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Applicant).Build();

    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------- seed helpers ----------

    private static void AddTest(SqliteTestContext ctx, string id, int? timeLimitMinutes = null, bool isActive = true)
    {
        using var db = ctx.NewContext();
        db.BewerbungTests.Add(new BewerbungTest
        {
            Id = id, Title = "Test " + id, Sorting = 1, IsActive = isActive,
            TimeLimitMinutes = timeLimitMinutes, CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static void AddQuestion(SqliteTestContext ctx, string id, string testId,
        TestQuestionType type = TestQuestionType.FreeText, bool required = true, bool allowMultiple = false, int sorting = 1)
    {
        using var db = ctx.NewContext();
        db.BewerbungTestQuestions.Add(new BewerbungTestQuestion
        {
            Id = id, TestId = testId, Type = type, Prompt = "Frage " + id, Sorting = sorting,
            Required = required, AllowMultiple = allowMultiple, Points = 1, CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static void AddOption(SqliteTestContext ctx, string id, string questionId, bool isCorrect = false, int sorting = 1)
    {
        using var db = ctx.NewContext();
        db.BewerbungTestOptions.Add(new BewerbungTestOption
        {
            Id = id, QuestionId = questionId, Label = "Option " + id, IsCorrect = isCorrect, Sorting = sorting, CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static void AddBewerbung(SqliteTestContext ctx, string id, string applicantUserId,
        BewerbungStatus status = BewerbungStatus.ImTest, string? assignedAgentId = null)
    {
        using var db = ctx.NewContext();
        db.Bewerbungen.Add(new Bewerbung
        {
            Id = id, ApplicantUserId = applicantUserId, Name = "Bewerber " + id, CaseNumber = "FIB-B-2026-" + id,
            Status = status, AssignedAgentId = assignedAgentId, SubmittedAt = Ts, CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static void AddAssignment(SqliteTestContext ctx, string id, string bewerbungId, string testId,
        DateTime? startedAt = null, DateTime? deadlineAt = null, int? timeLimitMinutes = null, int extraMinutes = 0,
        DateTime? completedAt = null, bool timedOut = false, DateTime? gradedAt = null, int attemptCount = 1)
    {
        using var db = ctx.NewContext();
        db.BewerbungTestAssignments.Add(new BewerbungTestAssignment
        {
            Id = id, BewerbungId = bewerbungId, TestId = testId, StartedAt = startedAt, DeadlineAt = deadlineAt,
            TimeLimitMinutes = timeLimitMinutes, ExtraMinutes = extraMinutes, CompletedAt = completedAt,
            TimedOut = timedOut, GradedAt = gradedAt, AttemptCount = attemptCount, CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static void AddAnswer(SqliteTestContext ctx, string id, string assignmentId, string questionId,
        string? freeText = null, string? optionIds = null, int? manualPoints = null, bool? manualCorrect = null,
        DateTime? createdAt = null)
    {
        using var db = ctx.NewContext();
        db.BewerbungTestAnswers.Add(new BewerbungTestAnswer
        {
            Id = id, AssignmentId = assignmentId, QuestionId = questionId, FreeTextAnswer = freeText,
            SelectedOptionIds = optionIds, ManualPoints = manualPoints, ManualCorrect = manualCorrect,
            CreatedAt = createdAt ?? Ts,
        });
        db.SaveChanges();
    }

    /// <summary>A minimal one-question free-text test assigned to one applicant.</summary>
    private static void Scenario(SqliteTestContext ctx, int? limit = 30, DateTime? startedAt = null,
        DateTime? deadlineAt = null, int? frozen = null, DateTime? completedAt = null, bool timedOut = false,
        BewerbungStatus status = BewerbungStatus.ImTest, bool required = true, bool attemptReleased = false)
    {
        AddTest(ctx, "t1", limit);
        AddQuestion(ctx, "q1", "t1", required: required);
        AddBewerbung(ctx, "b1", "app1", status, assignedAgentId: "agent7");
        AddAssignment(ctx, "a1", "b1", "t1",
            attemptReleased ? null : startedAt, attemptReleased ? null : deadlineAt, frozen,
            completedAt: attemptReleased ? null : completedAt, timedOut: timedOut,
            attemptCount: attemptReleased ? 2 : 1);
    }

    private static BewerbungTestAssignment Reload(SqliteTestContext ctx, string id = "a1")
    {
        using var db = ctx.NewContext();
        return db.BewerbungTestAssignments.AsNoTracking().Single(a => a.Id == id);
    }

    private static List<BewerbungTestAnswer> Answers(SqliteTestContext ctx, string assignmentId = "a1", bool all = false)
    {
        using var db = ctx.NewContext();
        var query = all ? db.BewerbungTestAnswers.IgnoreQueryFilters() : db.BewerbungTestAnswers;
        return query.AsNoTracking().Where(a => a.AssignmentId == assignmentId).ToList();
    }

    private static List<TestAnswerInput> One(string questionId, string? text = null, params string[] optionIds)
        => [new TestAnswerInput { QuestionId = questionId, FreeText = text, SelectedOptionIds = optionIds }];

    // ---------- start of the clock ----------

    [Fact]
    public async Task GetAssignedForApplicantAsync_stampsStartAndDeadline_onTheFirstOpen()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx);
        var (svc, _, _) = Build(ctx);

        var before = DateTime.UtcNow;
        await svc.GetAssignedForApplicantAsync(Applicant());

        var stored = Reload(ctx);
        Assert.NotNull(stored.StartedAt);
        Assert.True(stored.StartedAt >= before && stored.StartedAt <= DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(30, stored.TimeLimitMinutes);
        Assert.Equal(stored.StartedAt!.Value.AddMinutes(30), stored.DeadlineAt);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_keepsTheFirstDeadline_onEveryLaterOpen()
    {
        // the F5 / second tab / prerender-double-init regression: exact equality, so no clock is needed
        using var ctx = new SqliteTestContext();
        Scenario(ctx);
        var (svc, _, _) = Build(ctx);

        await svc.GetAssignedForApplicantAsync(Applicant());
        var first = Reload(ctx);
        await svc.GetAssignedForApplicantAsync(Applicant());
        await svc.GetAssignedForApplicantAsync(Applicant());
        var later = Reload(ctx);

        Assert.Equal(first.StartedAt, later.StartedAt);
        Assert.Equal(first.DeadlineAt, later.DeadlineAt);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_stampsTheStartButNoDeadline_whenTheTestHasNoLimit()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: null);
        var (svc, _, _) = Build(ctx);

        await svc.GetAssignedForApplicantAsync(Applicant());

        var stored = Reload(ctx);
        Assert.NotNull(stored.StartedAt);
        Assert.Null(stored.DeadlineAt);
        Assert.Null(stored.TimeLimitMinutes);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_addsAGrantMadeBeforeTheStart()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", 30);
        AddQuestion(ctx, "q1", "t1");
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "a1", "b1", "t1", extraMinutes: 10);
        var (svc, _, _) = Build(ctx);

        await svc.GetAssignedForApplicantAsync(Applicant());

        var stored = Reload(ctx);
        // the base stays frozen at 30 and the grant is added on top exactly once
        Assert.Equal(30, stored.TimeLimitMinutes);
        Assert.Equal(stored.StartedAt!.Value.AddMinutes(40), stored.DeadlineAt);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_doesNotStampAClock_whenTheApplicationIsAlreadyDecided()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, status: BewerbungStatus.Abgelehnt);
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant());

        Assert.Null(Reload(ctx).StartedAt);
        Assert.NotNull(view);
        // a closed view rather than a questionnaire that would only fail on submit
        Assert.True(view.Completed);
        Assert.Empty(view.Questions);
    }

    [Fact]
    public async Task GetTestStatusForApplicantAsync_neverStampsTheClock()
    {
        // the status page calls this, and merely reading a status must not burn the attempt
        using var ctx = new SqliteTestContext();
        Scenario(ctx);
        var (svc, _, _) = Build(ctx);

        var status = await svc.GetTestStatusForApplicantAsync(Applicant());

        Assert.NotNull(status);
        Assert.Null(status.StartedAt);
        Assert.Null(status.DeadlineAt);
        Assert.Equal(30, status.TimeLimitMinutes);
        var stored = Reload(ctx);
        Assert.Null(stored.StartedAt);
        Assert.Null(stored.DeadlineAt);
    }

    [Fact]
    public async Task GetTestStatusForApplicantAsync_throws_whenNotApplicant()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetTestStatusForApplicantAsync(Hrb()));
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_returnsTheDraft_soAReloadLosesNothing()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "mein Entwurf");
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant());

        var question = Assert.Single(view!.Questions);
        Assert.Equal("mein Entwurf", question.SavedFreeText);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_closesTheAttempt_whenTheDeadlineHasPassed()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "so weit gekommen");
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant());

        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(stored.TimedOut);
        Assert.True(view!.Completed);
        Assert.True(view.TimedOut);
        // the draft is the submission; nothing was rewritten
        Assert.Equal("so weit gekommen", Assert.Single(Answers(ctx)).FreeTextAnswer);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_reshufflesTheOptions_onASecondAttempt()
    {
        // the shuffle is seeded on the attempt as well, or a reset would show the identical order
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", 30);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice);
        for (var i = 1; i <= 8; i++)
        {
            AddOption(ctx, $"o{i}", "q1", sorting: i);
        }
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "a1", "b1", "t1", startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), timeLimitMinutes: 30);
        var (svc, _, _) = Build(ctx);

        var first = (await svc.GetAssignedForApplicantAsync(Applicant()))!.Questions[0].Options.Select(o => o.OptionId).ToList();

        using (var db = ctx.NewContext())
        {
            var a = db.BewerbungTestAssignments.Single(x => x.Id == "a1");
            a.AttemptCount = 2;
            db.SaveChanges();
        }
        var second = (await svc.GetAssignedForApplicantAsync(Applicant()))!.Questions[0].Options.Select(o => o.OptionId).ToList();

        Assert.Equal(first.Order(StringComparer.Ordinal), second.Order(StringComparer.Ordinal));
        Assert.NotEqual(first, second);
    }

    // ---------- drafts ----------

    [Fact]
    public async Task SaveDraftAsync_writesOneRowPerQuestion_andUpdatesItInPlace()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SaveDraftAsync("a1", 1, One("q1", "erst"), Applicant());
        await svc.SaveDraftAsync("a1", 1, One("q1", "dann"), Applicant());

        var rows = Answers(ctx, all: true);
        Assert.Single(rows);
        Assert.Equal("dann", rows[0].FreeTextAnswer);
        Assert.Null(Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task SaveDraftAsync_doesNotRequireMandatoryQuestions()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SaveDraftAsync("a1", 1, One("q1"), Applicant());

        Assert.Null(Assert.Single(Answers(ctx)).FreeTextAnswer);
    }

    [Fact]
    public async Task SaveDraftAsync_dropsAnOptionThatBelongsToAnotherQuestion()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", 30);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice);
        AddQuestion(ctx, "q2", "t1", TestQuestionType.MultipleChoice, sorting: 2);
        AddOption(ctx, "o1", "q1");
        AddOption(ctx, "o2", "q2");
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "a1", "b1", "t1", startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), timeLimitMinutes: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SaveDraftAsync("a1", 1, One("q1", null, "o2"), Applicant());

        var row = Answers(ctx).Single(a => a.QuestionId == "q1");
        Assert.Null(row.SelectedOptionId);
    }

    [Fact]
    public async Task SaveDraftAsync_neverTouchesTheGradingColumns()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "alt", manualPoints: 1, manualCorrect: true);
        var (svc, _, _) = Build(ctx);

        await svc.SaveDraftAsync("a1", 1, One("q1", "neu"), Applicant());

        var row = Assert.Single(Answers(ctx));
        Assert.Equal("neu", row.FreeTextAnswer);
        Assert.Equal(1, row.ManualPoints);
        Assert.True(row.ManualCorrect);
    }

    [Fact]
    public async Task SaveDraftAsync_reportsClosed_afterTheAttemptWasHandedIn()
    {
        // no exception for a race the applicant did not cause
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30, completedAt: Ts);
        var (svc, _, _) = Build(ctx);

        var result = await svc.SaveDraftAsync("a1", 1, One("q1", "zu spät"), Applicant());

        Assert.Equal(TestDraftOutcome.Closed, result.Outcome);
        Assert.Empty(Answers(ctx));
    }

    [Fact]
    public async Task SaveDraftAsync_closesTheAttempt_whenItArrivesAfterTheDeadline()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        var (svc, _, _) = Build(ctx);

        var result = await svc.SaveDraftAsync("a1", 1, One("q1", "zu spät"), Applicant());

        Assert.Equal(TestDraftOutcome.Closed, result.Outcome);
        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(stored.TimedOut);
    }

    [Fact]
    public async Task SaveDraftAsync_reportsTheServerDeadline_soAnExtensionReachesTheCountdown()
    {
        using var ctx = new SqliteTestContext();
        var deadline = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Scenario(ctx, startedAt: Ts, deadlineAt: deadline, frozen: 30);
        var (svc, _, _) = Build(ctx);

        var result = await svc.SaveDraftAsync("a1", 1, One("q1", "x"), Applicant());

        Assert.Equal(TestDraftOutcome.Saved, result.Outcome);
        Assert.Equal(deadline, result.DeadlineAt);
    }

    [Fact]
    public async Task SaveDraftAsync_throws_forAnotherApplicantsAttempt()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveDraftAsync("a1", 1, One("q1", "fremd"), Applicant("someone-else")));
    }

    [Fact]
    public async Task SaveDraftAsync_throws_whenTheApplicationIsDecided()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30,
            status: BewerbungStatus.Angenommen);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SaveDraftAsync("a1", 1, One("q1", "x"), Applicant()));
    }

    [Fact]
    public async Task SaveDraftAsync_throws_whenNotApplicant()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveDraftAsync("a1", 1, One("q1", "x"), Hrb()));
    }

    [Fact]
    public async Task SaveDraftAsync_doesNotBroadcast()
    {
        // one broadcast every few seconds would reload every open HRB panel
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, broadcaster, _) = Build(ctx);
        var seen = 0;
        broadcaster.Modified += _ => seen++;

        await svc.SaveDraftAsync("a1", 1, One("q1", "x"), Applicant());

        Assert.Equal(0, seen);
    }

    // ---------- hand-in and the race with the sweep ----------

    [Fact]
    public async Task SubmitAnswersAsync_updatesTheDraftRow_insteadOfAddingASecondOne()
    {
        // the latent duplicate-row bug: two rows per question let the grader award points on the row the
        // evaluation ignores
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SaveDraftAsync("a1", 1, One("q1", "entwurf"), Applicant());
        var outcome = await svc.SubmitAnswersAsync("a1", 1, One("q1", "endgültig"), Applicant());

        Assert.Equal(TestSubmitOutcome.Submitted, outcome);
        var rows = Answers(ctx, all: true);
        Assert.Single(rows);
        Assert.Equal("endgültig", rows[0].FreeTextAnswer);
    }

    [Fact]
    public async Task SubmitAnswersAsync_flagsTheAttempt_whenItArrivesAfterTheDeadline()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddSeconds(-5), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("a1", 1, One("q1", "knapp zu spät"), Applicant());

        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(stored.TimedOut);
    }

    [Fact]
    public async Task SubmitAnswersAsync_doesNotFlag_withTimeToSpare()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("a1", 1, One("q1", "rechtzeitig"), Applicant());

        Assert.False(Reload(ctx).TimedOut);
    }

    [Fact]
    public async Task SubmitAnswersAsync_doesNotFlag_whenTheAttemptHasNoDeadline()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: null, startedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("a1", 1, One("q1", "irgendwann"), Applicant());

        Assert.False(Reload(ctx).TimedOut);
    }

    [Fact]
    public async Task SubmitAnswersAsync_skipsTheMandatoryCheck_insideTheClosingSeconds()
    {
        // the automatic hand-in fires here; refusing a blank would throw away everything that was filled in
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddSeconds(2), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("a1", 1, One("q1"), Applicant());

        Assert.NotNull(Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task SubmitAnswersAsync_stillRefusesABlankMandatoryQuestion_withTimeToSpare()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("a1", 1, One("q1"), Applicant()));
        Assert.Null(Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task SubmitAnswersAsync_keepsTheAnswers_whenTheClockClosedTheAttemptFirst()
    {
        // the sweep won the claim microseconds earlier; the last keystrokes must still land, and no exception
        // may reach the applicant for a race they did not cause
        using var ctx = new SqliteTestContext();
        var closedAt = DateTime.UtcNow.AddSeconds(-3);
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddSeconds(-4), frozen: 30,
            completedAt: closedAt, timedOut: true);
        var (svc, _, _) = Build(ctx);

        var outcome = await svc.SubmitAnswersAsync("a1", 1, One("q1", "letzte Eingabe"), Applicant());

        Assert.Equal(TestSubmitOutcome.ClosedByTimeout, outcome);
        Assert.Equal("letzte Eingabe", Assert.Single(Answers(ctx)).FreeTextAnswer);
        // the winner's stamp stands
        Assert.Equal(closedAt, Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task SubmitAnswersAsync_ignoresThePayload_onceTheGraceWindowIsOver()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow - TestDeadline.SubmitGrace - TimeSpan.FromMinutes(1),
            frozen: 30, completedAt: Ts, timedOut: true);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("a1", 1, One("q1", "viel zu spät"), Applicant()));
        Assert.Empty(Answers(ctx));
    }

    [Fact]
    public async Task SubmitAnswersAsync_ignoresThePayload_onceTheGradingIsClosed()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddSeconds(-2), frozen: 30,
            completedAt: DateTime.UtcNow.AddSeconds(-1), timedOut: true);
        using (var db = ctx.NewContext())
        {
            var a = db.BewerbungTestAssignments.Single(x => x.Id == "a1");
            a.GradedAt = DateTime.UtcNow;
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("a1", 1, One("q1", "nachgereicht"), Applicant()));
    }

    [Fact]
    public async Task SubmitAnswersAsync_stillThrows_whenTheAttemptWasHandedInNormally()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30, completedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("a1", 1, One("q1", "nochmal"), Applicant()));
    }

    // ---------- the sweep ----------

    [Fact]
    public async Task ExpireDueAsync_handsInTheDraft_andFlagsTheAttempt()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "bis hierhin");
        var (sweep, notifications) = BuildSweep(ctx);

        Assert.Equal(1, await sweep.ExpireDueAsync());

        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(stored.TimedOut);
        Assert.Equal("bis hierhin", Assert.Single(Answers(ctx)).FreeTextAnswer);
        await notifications.Received(1).NotifyAsync("agent7", NotificationType.Recruiting,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpireDueAsync_materialisesTheEmptyRow_ofAnUntouchedQuestion()
    {
        // the evaluation keys on one row per question, and a missing row reads as "added later"
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        AddQuestion(ctx, "q2", "t1", sorting: 2);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "nur die erste");
        var (sweep, _) = BuildSweep(ctx);

        await sweep.ExpireDueAsync();

        var rows = Answers(ctx);
        Assert.Equal(2, rows.Count);
        Assert.Equal("nur die erste", rows.Single(r => r.QuestionId == "q1").FreeTextAnswer);
        Assert.Null(rows.Single(r => r.QuestionId == "q2").FreeTextAnswer);
    }

    [Fact]
    public async Task ExpireDueAsync_isIdempotent_onASecondPass()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        var (sweep, notifications) = BuildSweep(ctx);

        Assert.Equal(1, await sweep.ExpireDueAsync());
        Assert.Equal(0, await sweep.ExpireDueAsync());

        // CompletedAt is the idempotency token, so the bell rings exactly once
        await notifications.Received(1).NotifyAsync("agent7", NotificationType.Recruiting,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpireDueAsync_skipsAnAttemptWithoutADeadline()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: null, startedAt: Ts);
        var (sweep, _) = BuildSweep(ctx);

        Assert.Equal(0, await sweep.ExpireDueAsync());
        Assert.Null(Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task ExpireDueAsync_skipsAnAttemptWhoseTimeIsStillRunning()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(5), frozen: 30);
        var (sweep, _) = BuildSweep(ctx);

        Assert.Equal(0, await sweep.ExpireDueAsync());
    }

    [Fact]
    public async Task ExpireDueAsync_skipsADecidedApplication()
    {
        // closing it would be pointless and the bell would confuse its case worker
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30,
            status: BewerbungStatus.Abgelehnt);
        var (sweep, notifications) = BuildSweep(ctx);

        Assert.Equal(0, await sweep.ExpireDueAsync());
        Assert.Null(Reload(ctx).CompletedAt);
        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpireDueAsync_skipsAnAlreadyHandedInAttempt()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30, completedAt: Ts);
        var (sweep, _) = BuildSweep(ctx);

        Assert.Equal(0, await sweep.ExpireDueAsync());
        Assert.False(Reload(ctx).TimedOut);
    }

    [Fact]
    public async Task ExpireDueAsync_doesNotStarve_behindAttemptsOfDecidedApplications()
    {
        // these are never closed, so skipping them INSIDE the ordered batch left them at its head forever
        // and nothing newer was ever handed in again
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", 30);
        AddQuestion(ctx, "q1", "t1");
        var oldest = DateTime.UtcNow.AddDays(-3);
        for (var i = 1; i <= 3; i++)
        {
            AddBewerbung(ctx, $"decided{i}", $"app-d{i}", BewerbungStatus.Abgelehnt);
            AddAssignment(ctx, $"a-d{i}", $"decided{i}", "t1",
                startedAt: Ts, deadlineAt: oldest.AddMinutes(i), timeLimitMinutes: 30);
        }
        AddBewerbung(ctx, "b1", "app1", assignedAgentId: "agent7");
        AddAssignment(ctx, "a1", "b1", "t1",
            startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), timeLimitMinutes: 30);
        var (sweep, _) = BuildSweep(ctx);

        Assert.Equal(1, await sweep.ExpireDueAsync());

        Assert.NotNull(Reload(ctx).CompletedAt);
        Assert.Null(Reload(ctx, "a-d1").CompletedAt);
    }

    [Fact]
    public async Task ClaimAsync_refusesAnOverdueClose_onceTheDeadlineMovedIntoTheFuture()
    {
        // the sweep reads its batch once and closes row by row; an extension landing in between sets
        // CompletedAt back to null, which would re-arm a claim that only looked at that column
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(20), frozen: 30);
        using var db = ctx.NewContext();

        var won = await TestAttemptClose.ClaimAsync(db, "a1", DateTime.UtcNow, timedOut: true,
            onlyWhenOverdue: true, CancellationToken.None);

        Assert.False(won);
        Assert.Null(Reload(ctx).CompletedAt);
    }

    [Fact]
    public async Task SaveDraftAsync_keepsThePayload_thatArrivesAsTheClockRunsOut()
    {
        // the countdown flushes at zero precisely to save this; discarding it and only then closing threw
        // away exactly what the flush was for
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddSeconds(-2), frozen: 30);
        var (svc, _, _) = Build(ctx);

        var result = await svc.SaveDraftAsync("a1", 1, One("q1", "letzter Stand"), Applicant());

        Assert.Equal(TestDraftOutcome.Closed, result.Outcome);
        Assert.Equal("letzter Stand", Assert.Single(Answers(ctx)).FreeTextAnswer);
        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(stored.TimedOut);
    }

    [Fact]
    public async Task SaveDraftAsync_reportsClosed_afterTheTestWasReleasedAgain()
    {
        // the circuit of the previous attempt is still alive and still holds the same assignment id
        using var ctx = new SqliteTestContext();
        Scenario(ctx, attemptReleased: true);
        var (svc, _, _) = Build(ctx);

        // the page still carries attempt 1 while the row is already on attempt 2
        var result = await svc.SaveDraftAsync("a1", 1, One("q1", "alter Versuch"), Applicant());

        Assert.Equal(TestDraftOutcome.Closed, result.Outcome);
        Assert.Null(result.DeadlineAt);
        Assert.Empty(Answers(ctx));
    }

    [Fact]
    public async Task SubmitAnswersAsync_throws_afterTheTestWasReleasedAgain()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, attemptReleased: true);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("a1", 1, One("q1", "alter Versuch"), Applicant()));
        Assert.Empty(Answers(ctx));
    }

    // ---------- extend ----------

    [Fact]
    public async Task ExtendAttemptAsync_pushesTheDeadline_byTheGrantedMinutes()
    {
        using var ctx = new SqliteTestContext();
        var deadline = DateTime.UtcNow.AddMinutes(5);
        Scenario(ctx, startedAt: Ts, deadlineAt: deadline, frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.ExtendAttemptAsync("b1", 10, Hrb());

        var stored = Reload(ctx);
        Assert.Equal(deadline.AddMinutes(10), stored.DeadlineAt);
        Assert.Equal(10, stored.ExtraMinutes);
        // the base stays frozen, so "27 von 40 Min" adds up
        Assert.Equal(30, stored.TimeLimitMinutes);
        Assert.Equal(40, TestDeadline.AllowedMinutes(stored));
    }

    [Fact]
    public async Task ExtendAttemptAsync_measuresFromNow_whenTheOldDeadlineIsLongGone()
    {
        // measuring from the old deadline would hand back a deadline still in the past: zero extra seconds
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddHours(-3), frozen: 30,
            completedAt: DateTime.UtcNow.AddHours(-3), timedOut: true);
        var (svc, _, _) = Build(ctx);

        await svc.ExtendAttemptAsync("b1", 10, Hrb());

        Assert.True(Reload(ctx).DeadlineAt > DateTime.UtcNow.AddMinutes(9));
    }

    [Fact]
    public async Task ExtendAttemptAsync_reopensTheClosedAttempt_andKeepsTheAnswers()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30,
            completedAt: DateTime.UtcNow.AddMinutes(-1), timedOut: true);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "vorher");
        var (svc, _, _) = Build(ctx);

        await svc.ExtendAttemptAsync("b1", 15, Hrb());

        var stored = Reload(ctx);
        Assert.Null(stored.CompletedAt);
        Assert.False(stored.TimedOut);
        Assert.Equal("vorher", Assert.Single(Answers(ctx)).FreeTextAnswer);
    }

    [Fact]
    public async Task ExtendAttemptAsync_clearsAPartialGrade()
    {
        // it would go stale the moment the applicant edits the answer again
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30,
            completedAt: DateTime.UtcNow.AddMinutes(-1), timedOut: true);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "x", manualPoints: 1, manualCorrect: true);
        var (svc, _, _) = Build(ctx);

        await svc.ExtendAttemptAsync("b1", 15, Hrb());

        var row = Assert.Single(Answers(ctx));
        Assert.Null(row.ManualPoints);
        Assert.Null(row.ManualCorrect);
    }

    [Fact]
    public async Task ExtendAttemptAsync_raisesTheBudget_beforeTheApplicantEverStarted()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx);
        var (svc, _, _) = Build(ctx);

        await svc.ExtendAttemptAsync("b1", 10, Hrb());

        var stored = Reload(ctx);
        Assert.Equal(10, stored.ExtraMinutes);
        Assert.Null(stored.DeadlineAt);
        Assert.Equal(40, TestDeadline.AllowedMinutes(stored, 30));
    }

    [Fact]
    public async Task ExtendAttemptAsync_throws_whenTheTestWasHandedInOnTime()
    {
        // only the clock may reopen what the clock closed; granting time on a finished attempt would let the
        // applicant edit answers they had already submitted
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(5), frozen: 30,
            completedAt: DateTime.UtcNow.AddMinutes(-1));
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", 10, Hrb()));
        var stored = Reload(ctx);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(0, stored.ExtraMinutes);
    }

    [Fact]
    public async Task ExtendAttemptAsync_throws_whenTheTestCarriesNoProcessingTime()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: null, startedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", 10, Hrb()));
    }

    [Fact]
    public async Task ExtendAttemptAsync_throws_whenTheGradingIsClosed()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30, completedAt: Ts, timedOut: true);
        using (var db = ctx.NewContext())
        {
            var a = db.BewerbungTestAssignments.Single(x => x.Id == "a1");
            a.GradedAt = Ts;
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", 10, Hrb()));
    }

    [Fact]
    public async Task ExtendAttemptAsync_throws_whenTheApplicationIsDecided()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(5), frozen: 30,
            status: BewerbungStatus.Geschlossen);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", 10, Hrb()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ExtendAttemptAsync_throws_forAnUnusableAmount(int minutes)
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(5), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", minutes, Hrb()));
    }

    [Fact]
    public async Task ExtendAttemptAsync_throws_whenTheRunningAttemptHasNoClock()
    {
        // the test only got its minutes after the applicant started, so there is no deadline to push and
        // adding minutes would report success while granting nothing
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: 30, startedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExtendAttemptAsync("b1", 10, Hrb()));
        Assert.Equal(0, Reload(ctx).ExtraMinutes);
    }

    // ---------- reset ----------

    [Fact]
    public async Task ResetAttemptAsync_keepsExactlyOneAssignmentRow()
    {
        // delete-and-recreate would hit the unique index, which a soft-deleted row still occupies, and the
        // global filter would hide that row from the service's own pre-check
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30, completedAt: Ts, timedOut: true);
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb());

        using var db = ctx.NewContext();
        Assert.Equal(1, db.BewerbungTestAssignments.IgnoreQueryFilters().Count(a => a.BewerbungId == "b1"));
    }

    [Fact]
    public async Task ResetAttemptAsync_clearsTheClockAndCountsTheAttempt()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30, completedAt: Ts, timedOut: true);
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb());

        var stored = Reload(ctx);
        Assert.Null(stored.StartedAt);
        Assert.Null(stored.DeadlineAt);
        Assert.Null(stored.TimeLimitMinutes);
        Assert.Null(stored.CompletedAt);
        Assert.Equal(0, stored.ExtraMinutes);
        Assert.False(stored.TimedOut);
        Assert.Equal(2, stored.AttemptCount);
    }

    [Fact]
    public async Task ResetAttemptAsync_removesTheOldAnswers()
    {
        // production soft-deletes these; SqliteTestContext attaches no interceptors, so here they are simply gone
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30, completedAt: Ts, timedOut: true);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "alter Versuch");
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb());

        Assert.Empty(Answers(ctx));
    }

    [Fact]
    public async Task ResetAttemptAsync_clearsTheFrozenResult()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        using (var db = ctx.NewContext())
        {
            var a = db.BewerbungTestAssignments.Single(x => x.Id == "a1");
            a.GradedAt = Ts;
            a.GradedByName = "HRB-Alpha";
            a.FinalPoints = 3;
            a.FinalMaxPoints = 5;
            a.FinalPassPercent = 60;
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb());

        var stored = Reload(ctx);
        Assert.Null(stored.GradedAt);
        Assert.Null(stored.GradedByName);
        Assert.Null(stored.FinalPoints);
        Assert.Null(stored.FinalMaxPoints);
        Assert.Null(stored.FinalPassPercent);
    }

    [Fact]
    public async Task ResetAttemptAsync_putsTheApplicationBackIntoTheTestPhase()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts,
            status: BewerbungStatus.Eingereicht);
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb());

        using var db = ctx.NewContext();
        Assert.Equal(BewerbungStatus.ImTest, db.Bewerbungen.AsNoTracking().Single(b => b.Id == "b1").Status);
    }

    [Fact]
    public async Task ResetAttemptAsync_canPointTheAttemptAtAnotherTest()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        AddTest(ctx, "t2", 45);
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", "t2", "Nachweis", Hrb());

        Assert.Equal("t2", Reload(ctx).TestId);
    }

    [Fact]
    public async Task ResetAttemptAsync_throws_whenTheTargetTestIsInactive()
    {
        // otherwise the reset hands out a test the applicant read path cannot resolve, and their button
        // silently disappears
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        AddTest(ctx, "t2", 45, isActive: false);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ResetAttemptAsync("b1", "t2", "Nachweis", Hrb()));
    }

    [Fact]
    public async Task ResetAttemptAsync_throws_whenTheApplicationIsDecided()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts,
            status: BewerbungStatus.Angenommen);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ResetAttemptAsync("b1", null, "Nachweis", Hrb()));
    }

    [Fact]
    public async Task ResetAttemptAsync_recordsTheJustification()
    {
        // it is asked for as mandatory, so throwing it away would make the dialog pure friction
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await svc.ResetAttemptAsync("b1", null, "Technischer Abbruch", Hrb());

        using var db = ctx.NewContext();
        var rows = db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == nameof(BewerbungTestAssignment) && a.EntityId == "a1").ToList();
        Assert.Contains(rows, a => a.ChangesJson != null && a.ChangesJson.Contains("Technischer Abbruch"));
    }

    // ---------- guards ----------

    public static TheoryData<string> WriteBlockedActors() => ["reader", "demo"];

    [Theory]
    [MemberData(nameof(WriteBlockedActors))]
    public async Task ExtendAttemptAsync_throws_forEveryRoleThatMayNotWrite(string kind)
    {
        // both pass IsHrbOrLeadership, and the ExecuteUpdate path never reaches the read-only barrier, so the
        // guard has to be explicit — SqliteTestContext attaches no interceptors, which is what proves it
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(5), frozen: 30);
        var (svc, _, _) = Build(ctx);
        var actor = kind == "reader" ? OnlyReader() : Demo();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ExtendAttemptAsync("b1", 10, actor));
        Assert.Equal(0, Reload(ctx).ExtraMinutes);
    }

    [Theory]
    [MemberData(nameof(WriteBlockedActors))]
    public async Task ResetAttemptAsync_throws_forEveryRoleThatMayNotWrite(string kind)
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        var (svc, _, _) = Build(ctx);
        var actor = kind == "reader" ? OnlyReader() : Demo();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ResetAttemptAsync("b1", null, "Nachweis", actor));
        Assert.NotNull(Reload(ctx).CompletedAt);
    }

    // ---------- freeze and the live-attempt lock ----------

    [Fact]
    public async Task UpdateTestAsync_doesNotMoveTheDeadlineOfARunningAttempt()
    {
        using var ctx = new SqliteTestContext();
        var deadline = DateTime.UtcNow.AddMinutes(20);
        Scenario(ctx, startedAt: Ts, deadlineAt: deadline, frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateTestAsync("t1", "Test t1", null, isActive: true, passPercent: null, timeLimitMinutes: 5, Hrb());

        var stored = Reload(ctx);
        Assert.Equal(deadline, stored.DeadlineAt);
        Assert.Equal(30, stored.TimeLimitMinutes);
    }

    [Fact]
    public async Task UpdateTestAsync_clampsTheProcessingTime()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1");
        var (svc, _, _) = Build(ctx);

        await svc.UpdateTestAsync("t1", "Test", null, true, null, TestDeadline.MaxMinutes + 100, Hrb());

        using var db = ctx.NewContext();
        Assert.Equal(TestDeadline.MaxMinutes, db.BewerbungTests.AsNoTracking().Single(t => t.Id == "t1").TimeLimitMinutes);
    }

    [Fact]
    public async Task UpdateTestAsync_readsZeroAsUnlimited()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", 30);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateTestAsync("t1", "Test", null, true, null, 0, Hrb());

        using var db = ctx.NewContext();
        Assert.Null(db.BewerbungTests.AsNoTracking().Single(t => t.Id == "t1").TimeLimitMinutes);
    }

    [Fact]
    public async Task TheStructuralBuilderMethods_refuseWhileATimedAttemptIsLive()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(20), frozen: 30);
        AddQuestion(ctx, "q2", "t1", TestQuestionType.MultipleChoice, sorting: 2);
        AddOption(ctx, "o1", "q2");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddQuestionAsync("t1", TestQuestionType.FreeText, "neu", true, Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateQuestionAsync("q1", "geändert", true, 1, null, null, null, false, false, Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteQuestionAsync("q1", Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddOptionAsync("q2", "neu", false, Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateOptionAsync("o1", "geändert", false, Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteOptionAsync("o1", Hrb()));
    }

    [Fact]
    public async Task TheStructuralBuilderMethods_areAllowed_onceTheAttemptExpired()
    {
        // the lock is bounded by the deadline, so it always ends by itself
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1), frozen: 30);
        var (svc, _, _) = Build(ctx);

        await svc.AddQuestionAsync("t1", TestQuestionType.FreeText, "neu", true, Hrb());
    }

    [Fact]
    public async Task TheStructuralBuilderMethods_areAllowed_whenTheAttemptHasNoLimit()
    {
        // a started but never submitted unlimited attempt must not block the questions forever
        using var ctx = new SqliteTestContext();
        Scenario(ctx, limit: null, startedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await svc.AddQuestionAsync("t1", TestQuestionType.FreeText, "neu", true, Hrb());
    }

    // ---------- the evaluation is not readable while the attempt is open ----------

    [Fact]
    public async Task GetEvaluationAsync_returnsNothing_whileTheAttemptIsStillOpen()
    {
        // the draft lives in those rows: reading them would let HRB watch the applicant type
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(10), frozen: 30);
        AddAnswer(ctx, "ans1", "a1", "q1", freeText: "im Entstehen");
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetEvaluationAsync("b1", Hrb()));
    }

    [Fact]
    public async Task GetEvaluationAsync_readsTheOldestRow_whenDuplicatesExist()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        AddAnswer(ctx, "ans-b", "a1", "q1", freeText: "jünger", createdAt: Ts.AddMinutes(5));
        AddAnswer(ctx, "ans-a", "a1", "q1", freeText: "älter", createdAt: Ts);
        var (svc, _, _) = Build(ctx);

        var report = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.Equal("älter", Assert.Single(report!.Items).AnswerText);
    }

    [Fact]
    public async Task SetAwardedPointsAsync_targetsTheSameRowTheEvaluationReads()
    {
        using var ctx = new SqliteTestContext();
        Scenario(ctx, startedAt: Ts, deadlineAt: Ts, frozen: 30, completedAt: Ts);
        AddAnswer(ctx, "ans-b", "a1", "q1", freeText: "jünger", createdAt: Ts.AddMinutes(5));
        AddAnswer(ctx, "ans-a", "a1", "q1", freeText: "älter", createdAt: Ts);
        var (svc, _, _) = Build(ctx);

        await svc.SetAwardedPointsAsync("a1", "q1", 1, Hrb());

        var rows = Answers(ctx);
        Assert.Equal(1, rows.Single(r => r.Id == "ans-a").ManualPoints);
        Assert.Null(rows.Single(r => r.Id == "ans-b").ManualPoints);
    }
}
