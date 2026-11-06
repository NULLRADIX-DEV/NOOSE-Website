using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="BewerbungTestService"/> against in-memory SQLite.</summary>
public sealed class BewerbungTestServiceTests
{
    private static (BewerbungTestService Svc, BewerbungBroadcaster Broadcaster, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new BewerbungBroadcaster();
        var svc = new BewerbungTestService(ctx.Factory, broadcaster, notifications, Substitute.For<IApplicantDmNotifier>());
        return (svc, broadcaster, notifications);
    }

    // Passes RequireHrbOrLeadership via the HRB flag; carries a codename for AssignedByName stamping.
    private static ClaimsPrincipal Hrb(string id = "hrb1")
        => ClaimsPrincipalBuilder.Agent(id).AsHrb().WithCodename("HRB-Alpha").Build();

    // Active junior agent: not HRB, not leadership, not admin => fails RequireHrbOrLeadership.
    private static ClaimsPrincipal NonHrb(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Public applicant: passes RequireApplicant.
    private static ClaimsPrincipal Applicant(string id = "app1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Applicant).Build();

    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------- seed helpers ----------

    private static BewerbungTest AddTest(SqliteTestContext ctx, string id, string title, int sorting, int? passPercent = null, bool isActive = true)
    {
        using var db = ctx.NewContext();
        var t = new BewerbungTest { Id = id, Title = title, Sorting = sorting, PassPercent = passPercent, IsActive = isActive, CreatedAt = Ts };
        db.BewerbungTests.Add(t);
        db.SaveChanges();
        return t;
    }

    private static BewerbungTestQuestion AddQuestion(SqliteTestContext ctx, string id, string testId, TestQuestionType type, int sorting,
        int points = 1, bool? correctYesNo = null, string? keywords = null, int? minKeywordHits = null, bool keepOptionOrder = false,
        bool allowMultiple = false, bool required = true)
    {
        using var db = ctx.NewContext();
        var q = new BewerbungTestQuestion
        {
            Id = id, TestId = testId, Type = type, Prompt = $"Frage {id}", Sorting = sorting, Required = required,
            Points = points, CorrectYesNo = correctYesNo, Keywords = keywords, MinKeywordHits = minKeywordHits,
            KeepOptionOrder = keepOptionOrder, AllowMultiple = allowMultiple, CreatedAt = Ts,
        };
        db.BewerbungTestQuestions.Add(q);
        db.SaveChanges();
        return q;
    }

    private static BewerbungTestOption AddOption(SqliteTestContext ctx, string id, string questionId, string label, bool isCorrect, int sorting)
    {
        using var db = ctx.NewContext();
        var o = new BewerbungTestOption { Id = id, QuestionId = questionId, Label = label, IsCorrect = isCorrect, Sorting = sorting, CreatedAt = Ts };
        db.BewerbungTestOptions.Add(o);
        db.SaveChanges();
        return o;
    }

    private static Bewerbung AddBewerbung(SqliteTestContext ctx, string id, string applicantUserId,
        BewerbungStatus status = BewerbungStatus.Eingereicht, string? assignedAgentId = null, string caseNumber = "FIB-B-2026-0001")
    {
        using var db = ctx.NewContext();
        var b = new Bewerbung
        {
            Id = id, ApplicantUserId = applicantUserId, Name = "Bewerber " + id, CaseNumber = caseNumber,
            Status = status, AssignedAgentId = assignedAgentId, SubmittedAt = Ts, CreatedAt = Ts,
        };
        db.Bewerbungen.Add(b);
        db.SaveChanges();
        return b;
    }

    private static BewerbungTestAssignment AddAssignment(SqliteTestContext ctx, string id, string bewerbungId, string testId, DateTime? completedAt = null)
    {
        using var db = ctx.NewContext();
        var a = new BewerbungTestAssignment { Id = id, BewerbungId = bewerbungId, TestId = testId, CompletedAt = completedAt, CreatedAt = Ts };
        db.BewerbungTestAssignments.Add(a);
        db.SaveChanges();
        return a;
    }

    private static BewerbungTestAnswer AddAnswer(SqliteTestContext ctx, string id, string assignmentId, string questionId,
        string? selectedOptionId = null, string? freeText = null, bool? manualCorrect = null)
    {
        using var db = ctx.NewContext();
        var ans = new BewerbungTestAnswer
        {
            Id = id, AssignmentId = assignmentId, QuestionId = questionId,
            SelectedOptionId = selectedOptionId, FreeTextAnswer = freeText, ManualCorrect = manualCorrect, CreatedAt = Ts,
        };
        db.BewerbungTestAnswers.Add(ans);
        db.SaveChanges();
        return ans;
    }

    // ---------- GetTestsAsync ----------

    [Fact]
    public async Task GetTestsAsync_ReturnsAll_OrderedBySortingThenTitle()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Zulu", sorting: 2);
        AddTest(ctx, "t2", "Mike", sorting: 1);
        AddTest(ctx, "t3", "Alpha", sorting: 1);
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetTestsAsync(Hrb());

        Assert.Equal(new[] { "t3", "t2", "t1" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetTestsAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetTestsAsync(NonHrb()));
    }

    // ---------- CreateTestAsync ----------

    [Fact]
    public async Task CreateTestAsync_PersistsTrimmed_AndAssignsFirstSorting()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var test = await svc.CreateTestAsync("  Einstellungstest  ", "  desc  ", Hrb());

        Assert.Equal("Einstellungstest", test.Title);
        Assert.Equal("desc", test.Description);
        Assert.Equal(1, test.Sorting);

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTests.SingleAsync();
        Assert.Equal("Einstellungstest", stored.Title);
        Assert.Equal(1, stored.Sorting);
    }

    [Fact]
    public async Task CreateTestAsync_IncrementsSorting_AboveExistingMax()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Bestehend", sorting: 5);
        var (svc, _, _) = Build(ctx);

        var test = await svc.CreateTestAsync("Neu", null, Hrb());

        Assert.Equal(6, test.Sorting);
        Assert.Null(test.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTestAsync_Throws_OnEmptyTitle(string? title)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateTestAsync(title!, null, Hrb()));
    }

    [Fact]
    public async Task CreateTestAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateTestAsync("Neu", null, NonHrb()));
    }

    // ---------- UpdateTestAsync ----------

    [Fact]
    public async Task UpdateTestAsync_UpdatesFields_AndClampsPassPercent()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Alt", sorting: 1, isActive: true);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateTestAsync("t1", "  Neu  ", "  info  ", isActive: false, passPercent: 150, timeLimitMinutes: null, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTests.SingleAsync(t => t.Id == "t1");
        Assert.Equal("Neu", stored.Title);
        Assert.Equal("info", stored.Description);
        Assert.False(stored.IsActive);
        Assert.Equal(100, stored.PassPercent); // clamped to [0,100]
    }

    [Fact]
    public async Task UpdateTestAsync_AllowsNullPassPercent()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Alt", sorting: 1, passPercent: 60);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateTestAsync("t1", "Neu", null, isActive: true, passPercent: null, timeLimitMinutes: null, Hrb());

        using var check = ctx.NewContext();
        Assert.Null((await check.BewerbungTests.SingleAsync(t => t.Id == "t1")).PassPercent);
    }

    [Fact]
    public async Task UpdateTestAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateTestAsync("nope", "Neu", null, true, null, null, Hrb()));
    }

    [Fact]
    public async Task UpdateTestAsync_Throws_OnEmptyTitle()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Alt", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateTestAsync("t1", "  ", null, true, null, null, Hrb()));
    }

    [Fact]
    public async Task UpdateTestAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Alt", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateTestAsync("t1", "Neu", null, true, null, null, NonHrb()));
    }

    // ---------- DeleteTestAsync ----------

    [Fact]
    public async Task DeleteTestAsync_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Weg", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteTestAsync("t1", Hrb());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row gone from the filtered set
        Assert.False(await check.BewerbungTests.AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteTestAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Bleibt", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteTestAsync("missing", Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.BewerbungTests.CountAsync());
    }

    [Fact]
    public async Task DeleteTestAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Bleibt", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteTestAsync("t1", NonHrb()));

        using var check = ctx.NewContext();
        Assert.True(await check.BewerbungTests.AnyAsync(t => t.Id == "t1"));
    }

    // ---------- GetEditModelAsync ----------

    [Fact]
    public async Task GetEditModelAsync_ReturnsTestQuestionsAndOptions_Ordered()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q2", "t1", TestQuestionType.FreeText, sorting: 2);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "oB", "q1", "B", isCorrect: false, sorting: 2);
        AddOption(ctx, "oA", "q1", "A", isCorrect: true, sorting: 1);
        var (svc, _, _) = Build(ctx);

        var model = await svc.GetEditModelAsync("t1", Hrb());

        Assert.NotNull(model);
        Assert.Equal("t1", model!.Test.Id);
        Assert.Equal(new[] { "q1", "q2" }, model.Questions.Select(q => q.Question.Id).ToArray());
        var mc = model.Questions.First(q => q.Question.Id == "q1");
        Assert.Equal(new[] { "oA", "oB" }, mc.Options.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task GetEditModelAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetEditModelAsync("nope", Hrb()));
    }

    [Fact]
    public async Task GetEditModelAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetEditModelAsync("t1", NonHrb()));
    }

    // ---------- AddQuestionAsync ----------

    [Fact]
    public async Task AddQuestionAsync_Persists_TrimsPrompt_AndAssignsNextSorting()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q0", "t1", TestQuestionType.YesNo, sorting: 3);
        var (svc, _, _) = Build(ctx);

        var q = await svc.AddQuestionAsync("t1", TestQuestionType.FreeText, "  Warum?  ", required: false, Hrb());

        Assert.Equal("Warum?", q.Prompt);
        Assert.Equal(TestQuestionType.FreeText, q.Type);
        Assert.False(q.Required);
        Assert.Equal(4, q.Sorting); // max(3)+1

        using var check = ctx.NewContext();
        Assert.True(await check.BewerbungTestQuestions.AnyAsync(x => x.Id == q.Id && x.TestId == "t1"));
    }

    [Fact]
    public async Task AddQuestionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AddQuestionAsync("t1", TestQuestionType.YesNo, "Q", true, NonHrb()));
    }

    // ---------- UpdateQuestionAsync ----------

    [Fact]
    public async Task UpdateQuestionAsync_FreeText_SetsKeywords_ClearsYesNo_ClampsPoints()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateQuestionAsync("q1", "  Neuer Text  ", required: true, points: -5,
            correctYesNo: true, keywords: "  a; b  ", minKeywordHits: 2, keepOptionOrder: false, allowMultiple: false, Hrb());

        using var check = ctx.NewContext();
        var q = await check.BewerbungTestQuestions.SingleAsync(x => x.Id == "q1");
        Assert.Equal("Neuer Text", q.Prompt);
        Assert.Equal(0, q.Points);              // clamped to >= 0
        Assert.Null(q.CorrectYesNo);            // not applicable to FreeText
        Assert.Equal("a; b", q.Keywords);
        Assert.Equal(2, q.MinKeywordHits);
    }

    [Fact]
    public async Task UpdateQuestionAsync_YesNo_SetsCorrectYesNo_ClearsKeywords()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.YesNo, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateQuestionAsync("q1", "Ja oder nein?", required: true, points: 3,
            correctYesNo: false, keywords: "ignored", minKeywordHits: 5, keepOptionOrder: false, allowMultiple: false, Hrb());

        using var check = ctx.NewContext();
        var q = await check.BewerbungTestQuestions.SingleAsync(x => x.Id == "q1");
        Assert.Equal(3, q.Points);
        Assert.False(q.CorrectYesNo);
        Assert.Null(q.Keywords);                // not applicable to YesNo
        Assert.Null(q.MinKeywordHits);
    }

    [Fact]
    public async Task UpdateQuestionAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateQuestionAsync("nope", "Q", true, 1, null, null, null, false, false, Hrb()));
    }

    [Fact]
    public async Task UpdateQuestionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.YesNo, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateQuestionAsync("q1", "Q", true, 1, null, null, null, false, false, NonHrb()));
    }

    // ---------- DeleteQuestionAsync ----------

    [Fact]
    public async Task DeleteQuestionAsync_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.YesNo, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteQuestionAsync("q1", Hrb());

        using var check = ctx.NewContext();
        Assert.False(await check.BewerbungTestQuestions.AnyAsync(x => x.Id == "q1"));
    }

    [Fact]
    public async Task DeleteQuestionAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.YesNo, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteQuestionAsync("missing", Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.BewerbungTestQuestions.CountAsync());
    }

    [Fact]
    public async Task DeleteQuestionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.YesNo, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteQuestionAsync("q1", NonHrb()));
    }

    // ---------- AddOptionAsync ----------

    [Fact]
    public async Task AddOptionAsync_Persists_TrimsLabel_AndAssignsNextSorting()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o0", "q1", "A", isCorrect: false, sorting: 7);
        var (svc, _, _) = Build(ctx);

        var opt = await svc.AddOptionAsync("q1", "  Antwort B  ", isCorrect: true, Hrb());

        Assert.Equal("Antwort B", opt.Label);
        Assert.True(opt.IsCorrect);
        Assert.Equal(8, opt.Sorting); // max(7)+1

        using var check = ctx.NewContext();
        Assert.True(await check.BewerbungTestOptions.AnyAsync(o => o.Id == opt.Id && o.QuestionId == "q1"));
    }

    [Fact]
    public async Task AddOptionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AddOptionAsync("q1", "A", false, NonHrb()));
    }

    // ---------- UpdateOptionAsync ----------

    [Fact]
    public async Task UpdateOptionAsync_UpdatesLabelAndCorrectness()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Alt", isCorrect: false, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateOptionAsync("o1", "  Neu  ", isCorrect: true, Hrb());

        using var check = ctx.NewContext();
        var o = await check.BewerbungTestOptions.SingleAsync(x => x.Id == "o1");
        Assert.Equal("Neu", o.Label);
        Assert.True(o.IsCorrect);
    }

    [Fact]
    public async Task UpdateOptionAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateOptionAsync("nope", "Neu", true, Hrb()));
    }

    [Fact]
    public async Task UpdateOptionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Alt", isCorrect: false, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateOptionAsync("o1", "Neu", true, NonHrb()));
    }

    // ---------- DeleteOptionAsync ----------

    [Fact]
    public async Task DeleteOptionAsync_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Weg", isCorrect: false, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteOptionAsync("o1", Hrb());

        using var check = ctx.NewContext();
        Assert.False(await check.BewerbungTestOptions.AnyAsync(o => o.Id == "o1"));
    }

    [Fact]
    public async Task DeleteOptionAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Bleibt", isCorrect: false, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.DeleteOptionAsync("missing", Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.BewerbungTestOptions.CountAsync());
    }

    [Fact]
    public async Task DeleteOptionAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Bleibt", isCorrect: false, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteOptionAsync("o1", NonHrb()));
    }

    // ---------- GetAssignmentAsync ----------

    [Fact]
    public async Task GetAssignmentAsync_ReturnsAssignment_WhenPresent()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetAssignmentAsync("b1", Hrb());

        Assert.NotNull(result);
        Assert.Equal("as1", result!.Id);
        Assert.Equal("t1", result.TestId);
    }

    [Fact]
    public async Task GetAssignmentAsync_ReturnsNull_WhenNone()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetAssignmentAsync("b1", Hrb()));
    }

    [Fact]
    public async Task GetAssignmentAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAssignmentAsync("b1", NonHrb()));
    }

    // ---------- AssignAsync ----------

    [Fact]
    public async Task AssignAsync_CreatesAssignment_TransitionsStatus_Broadcasts_AndNotifiesApplicant()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "applicantUser", status: BewerbungStatus.Eingereicht);
        var (svc, broadcaster, notifications) = Build(ctx);
        var fired = new List<string>();
        broadcaster.Modified += fired.Add;

        await svc.AssignAsync("b1", "t1", Hrb());

        using var check = ctx.NewContext();
        var assignment = await check.BewerbungTestAssignments.SingleAsync(a => a.BewerbungId == "b1");
        Assert.Equal("t1", assignment.TestId);
        Assert.Equal("HRB-Alpha", assignment.AssignedByName);
        Assert.Equal(BewerbungStatus.ImTest, (await check.Bewerbungen.SingleAsync(b => b.Id == "b1")).Status);
        Assert.Contains("b1", fired);
        await notifications.Received(1).NotifyAsync("applicantUser", NotificationType.Recruiting,
            Arg.Any<string>(), "/portal/test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignAsync_Throws_WhenBewerbungMissing()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("nope", "t1", Hrb()));
    }

    [Fact]
    public async Task AssignAsync_Throws_WhenAlreadyAssigned()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("b1", "t1", Hrb()));
    }

    [Fact]
    public async Task AssignAsync_Throws_WhenTestMissing()
    {
        using var ctx = new SqliteTestContext();
        AddBewerbung(ctx, "b1", "userA");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("b1", "nope", Hrb()));

        using var check = ctx.NewContext();
        Assert.False(await check.BewerbungTestAssignments.AnyAsync());
    }

    [Fact]
    public async Task AssignAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AssignAsync("b1", "t1", NonHrb()));
    }

    // ---------- GetEvaluationAsync ----------

    [Fact]
    public async Task GetEvaluationAsync_GradesAllTypes_AndComputesTotals()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Prüfung", sorting: 1, passPercent: 50);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        // MultipleChoice, 2 points
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, points: 2);
        AddOption(ctx, "o1", "q1", "Richtig", isCorrect: true, sorting: 1);
        AddOption(ctx, "o2", "q1", "Falsch", isCorrect: false, sorting: 2);
        // YesNo, 1 point, correct = true
        AddQuestion(ctx, "q2", "t1", TestQuestionType.YesNo, sorting: 2, points: 1, correctYesNo: true);
        // FreeText, 3 points, keyword must appear
        AddQuestion(ctx, "q3", "t1", TestQuestionType.FreeText, sorting: 3, points: 3, keywords: "sicherheit", minKeywordHits: 1);
        // answers (all correct)
        AddAnswer(ctx, "an1", "as1", "q1", selectedOptionId: "o1");
        AddAnswer(ctx, "an2", "as1", "q2", freeText: "Ja");
        AddAnswer(ctx, "an3", "as1", "q3", freeText: "Ich achte auf sicherheit");
        var (svc, _, _) = Build(ctx);

        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.NotNull(eval);
        Assert.Equal("Prüfung", eval!.Title);
        Assert.Equal(3, eval.Items.Count);
        Assert.Equal(6, eval.MaxPoints);
        Assert.Equal(6, eval.TotalPoints);
        Assert.Equal(100, eval.Percent);
        Assert.True(eval.Passed);
    }

    [Fact]
    public async Task GetEvaluationAsync_ManualOverride_TrumpsAutoGrade()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Prüfung", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, points: 5);
        AddOption(ctx, "o1", "q1", "Richtig", isCorrect: true, sorting: 1);
        // chose the correct option (auto=true) but HRB manually marked it wrong
        AddAnswer(ctx, "an1", "as1", "q1", selectedOptionId: "o1", manualCorrect: false);
        var (svc, _, _) = Build(ctx);

        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.NotNull(eval);
        var item = Assert.Single(eval!.Items);
        Assert.True(item.AutoCorrect);
        Assert.False(item.ManualCorrect);
        Assert.False(item.EffectiveCorrect);
        Assert.Equal(0, item.AwardedPoints);
        Assert.Equal(0, eval.TotalPoints);
    }

    [Fact]
    public async Task GetEvaluationAsync_ReturnsNull_WhenNoAssignment()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetEvaluationAsync("b1", Hrb()));
    }

    [Fact]
    public async Task GetEvaluationAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetEvaluationAsync("b1", NonHrb()));
    }

    // ---------- SetAwardedPointsAsync / grading transitions ----------

    [Fact]
    public async Task SetAwardedPointsAsync_StoresPoints_AndBroadcasts()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 5);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, broadcaster, _) = Build(ctx);
        var fired = new List<string>();
        broadcaster.Modified += fired.Add;

        await svc.SetAwardedPointsAsync("as1", "q1", 3, Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(3, (await check.BewerbungTestAnswers.SingleAsync(a => a.Id == "an1")).ManualPoints);
        Assert.Contains("b1", fired);
    }

    [Fact]
    public async Task SetAwardedPointsAsync_ClampsToTheQuestionsMaximum()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 4);
        var (svc, _, _) = Build(ctx);

        await svc.SetAwardedPointsAsync("as1", "q1", 99, Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(4, (await check.BewerbungTestAnswers.SingleAsync(a => a.QuestionId == "q1")).ManualPoints);
    }

    [Fact]
    public async Task SetAwardedPointsAsync_CreatesARowForAQuestionAddedAfterTheSubmission()
    {
        // the old grading keyed on an existing answer row, so such a question counted against the maximum but
        // could never be graded
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "spaet", "t1", TestQuestionType.FreeText, sorting: 9, points: 2);
        var (svc, _, _) = Build(ctx);

        await svc.SetAwardedPointsAsync("as1", "spaet", 2, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.SingleAsync(a => a.QuestionId == "spaet");
        Assert.Equal(2, stored.ManualPoints);
    }

    [Fact]
    public async Task SetAwardedPointsAsync_Auto_ClearsTheOldVerdictToo()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 5);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas", manualCorrect: true);
        var (svc, _, _) = Build(ctx);

        await svc.SetAwardedPointsAsync("as1", "q1", null, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.SingleAsync(a => a.Id == "an1");
        Assert.Null(stored.ManualPoints);
        Assert.Null(stored.ManualCorrect);
    }

    [Fact]
    public async Task SetAwardedPointsAsync_Throws_ForAQuestionOfAnotherTest()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddTest(ctx, "t2", "Fremd", sorting: 2);
        AddQuestion(ctx, "fremd", "t2", TestQuestionType.FreeText, sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetAwardedPointsAsync("as1", "fremd", 1, Hrb()));
    }

    [Fact]
    public async Task SetAwardedPointsAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetAwardedPointsAsync("as1", "q1", 1, NonHrb()));
    }

    [Fact]
    public async Task GetEvaluationAsync_AwardedPoints_PreferTheHandGradeOverTheAutomaticVerdict()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, points: 6);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        AddAnswer(ctx, "an1", "as1", "q1", selectedOptionId: "o1");
        var (svc, _, _) = Build(ctx);

        await svc.SetAwardedPointsAsync("as1", "q1", 2, Hrb());
        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.NotNull(eval);
        Assert.Equal(2, eval!.TotalPoints);
        Assert.Equal(6, eval.MaxPoints);
    }

    [Fact]
    public async Task AMultipleChoiceQuestionWithoutAKeyStaysUndecided()
    {
        // "wrong" would let CompleteGradingAsync freeze a 0 for a question the machine could not judge and
        // nobody ever looked at
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, points: 5, allowMultiple: true);
        AddOption(ctx, "o1", "q1", "A", isCorrect: false, sorting: 1);
        AddOption(ctx, "o2", "q1", "B", isCorrect: false, sorting: 2);
        AddAnswer(ctx, "an1", "as1", "q1", selectedOptionId: "o1");
        var (svc, _, _) = Build(ctx);

        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.NotNull(eval);
        Assert.Equal(1, eval!.OpenCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteGradingAsync("b1", Hrb()));
    }

    [Fact]
    public async Task AStoredMultiAnswerIsStillGradedAsASetAfterTheSwitchIsTurnedOff()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        // not pre-completed: the applicant's own submission is what stores the multi-answer here
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, points: 5, allowMultiple: true);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        AddOption(ctx, "o2", "q1", "B", isCorrect: true, sorting: 2);
        var (svc, _, _) = Build(ctx);
        await svc.SubmitAnswersAsync("as1", 1,
            [new TestAnswerInput { QuestionId = "q1", SelectedOptionIds = ["o1", "o2"] }], Applicant("userA"));

        // the author changes their mind after the applicant has answered
        await svc.UpdateQuestionAsync("q1", "Frage", required: true, points: 5, correctYesNo: null,
            keywords: null, minKeywordHits: null, keepOptionOrder: false, allowMultiple: false, Hrb());

        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        // graded on the answer that was actually given, not on whichever single option came first
        Assert.Equal(5, eval!.TotalPoints);
    }

    [Fact]
    public async Task AFrozenVerdictSurvivesAChangeToThePassMark()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1, passPercent: 50);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 10);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, _, _) = Build(ctx);
        await svc.SetAwardedPointsAsync("as1", "q1", 6, Hrb());
        await svc.CompleteGradingAsync("b1", Hrb());
        Assert.True((await svc.GetEvaluationAsync("b1", Hrb()))!.Passed);

        await svc.UpdateTestAsync("t1", "Test", null, isActive: true, passPercent: 90, timeLimitMinutes: null, Hrb());

        // the applicant was already told the outcome; raising the bar afterwards must not turn it around
        Assert.True((await svc.GetEvaluationAsync("b1", Hrb()))!.Passed);
    }

    [Fact]
    public async Task CompleteGradingAsync_Throws_WhileAQuestionIsUndecided()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        // free text without keywords: the machine cannot decide it
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 3);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteGradingAsync("b1", Hrb()));
    }

    [Fact]
    public async Task CompleteGradingAsync_FreezesTheResult()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 4);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, _, _) = Build(ctx);
        await svc.SetAwardedPointsAsync("as1", "q1", 3, Hrb());

        await svc.CompleteGradingAsync("b1", Hrb());

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAssignments.SingleAsync(a => a.Id == "as1");
        Assert.NotNull(stored.GradedAt);
        Assert.Equal(3, stored.FinalPoints);
        Assert.Equal(4, stored.FinalMaxPoints);
    }

    [Fact]
    public async Task AFrozenResultSurvivesAChangeToTheTest()
    {
        // the reason to freeze at all: raising a question's points must not rewrite an old applicant's score
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 4);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, _, _) = Build(ctx);
        await svc.SetAwardedPointsAsync("as1", "q1", 4, Hrb());
        await svc.CompleteGradingAsync("b1", Hrb());

        await svc.UpdateQuestionAsync("q1", "Frage", required: true, points: 40,
            correctYesNo: null, keywords: null, minKeywordHits: null, keepOptionOrder: false,
            allowMultiple: false, Hrb());

        var eval = await svc.GetEvaluationAsync("b1", Hrb());

        Assert.NotNull(eval);
        Assert.Equal(4, eval!.TotalPoints);
        Assert.Equal(4, eval.MaxPoints);
    }

    [Fact]
    public async Task GradingIsLockedWhileClosed_AndOpensAgainOnReopen()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "userA");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, points: 4);
        AddAnswer(ctx, "an1", "as1", "q1", freeText: "etwas");
        var (svc, _, _) = Build(ctx);
        await svc.SetAwardedPointsAsync("as1", "q1", 1, Hrb());
        await svc.CompleteGradingAsync("b1", Hrb());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetAwardedPointsAsync("as1", "q1", 2, Hrb()));

        await svc.ReopenGradingAsync("b1", Hrb());
        await svc.SetAwardedPointsAsync("as1", "q1", 2, Hrb());

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAssignments.SingleAsync(a => a.Id == "as1");
        Assert.Null(stored.GradedAt);
        Assert.Null(stored.FinalPoints);
        Assert.Equal(2, (await check.BewerbungTestAnswers.SingleAsync(a => a.Id == "an1")).ManualPoints);
    }

    [Fact]
    public async Task CompleteGradingAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CompleteGradingAsync("b1", NonHrb()));
    }

    [Fact]
    public async Task ReopenGradingAsync_Throws_WhenNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ReopenGradingAsync("b1", NonHrb()));
    }

    [Fact]
    public async Task UpdateQuestionAsync_MultipleChoice_PersistsKeepOptionOrder()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateQuestionAsync("q1", "Frage", required: true, points: 1,
            correctYesNo: null, keywords: null, minKeywordHits: null, keepOptionOrder: true, allowMultiple: false, Hrb());

        using var check = ctx.NewContext();
        Assert.True((await check.BewerbungTestQuestions.SingleAsync(q => q.Id == "q1")).KeepOptionOrder);
    }

    [Fact]
    public async Task UpdateQuestionAsync_NonMultipleChoice_ClearsKeepOptionOrder()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, keepOptionOrder: true);
        var (svc, _, _) = Build(ctx);

        await svc.UpdateQuestionAsync("q1", "Frage", required: true, points: 1,
            correctYesNo: null, keywords: null, minKeywordHits: null, keepOptionOrder: true, allowMultiple: false, Hrb());

        using var check = ctx.NewContext();
        // not applicable without options
        Assert.False((await check.BewerbungTestQuestions.SingleAsync(q => q.Id == "q1")).KeepOptionOrder);
    }

    // ---------- GetAssignedForApplicantAsync ----------

    [Fact]
    public async Task GetAssignedForApplicantAsync_ReturnsTestView_ForLatestBewerbung()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Portal-Test", sorting: 1, passPercent: 60);
        // older + newer application for the same applicant; newest (by SubmittedAt) wins
        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(new Bewerbung { Id = "bOld", ApplicantUserId = "app1", Name = "Alt", CaseNumber = "FIB-B-2026-0001", SubmittedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedAt = Ts });
            db.Bewerbungen.Add(new Bewerbung { Id = "bNew", ApplicantUserId = "app1", Name = "Neu", CaseNumber = "FIB-B-2026-0002", SubmittedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), CreatedAt = Ts });
            db.SaveChanges();
        }
        AddAssignment(ctx, "as1", "bNew", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant("app1"));

        Assert.NotNull(view);
        Assert.Equal("as1", view!.AssignmentId);
        Assert.Equal("Portal-Test", view.Title);
        Assert.False(view.Completed);
        var q = Assert.Single(view.Questions);
        Assert.Equal("q1", q.QuestionId);
        Assert.Single(q.Options);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_ReturnsNull_WhenNoBewerbung()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetAssignedForApplicantAsync(Applicant("app1")));
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_ReturnsNull_WhenNoAssignment()
    {
        using var ctx = new SqliteTestContext();
        AddBewerbung(ctx, "b1", "app1");
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetAssignedForApplicantAsync(Applicant("app1")));
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_CarriesCaseNumber_ForTheWatermark()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "app1", caseNumber: "FIB-B-2026-0042");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant("app1"));

        Assert.Equal("FIB-B-2026-0042", view!.CaseNumber);
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_KeepOptionOrder_ServesAuthoringOrder()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, keepOptionOrder: true);
        for (var i = 1; i <= 6; i++)
        {
            AddOption(ctx, $"o{i}", "q1", $"Option {i}", isCorrect: i == 1, sorting: i);
        }
        var (svc, _, _) = Build(ctx);

        var view = await svc.GetAssignedForApplicantAsync(Applicant("app1"));

        Assert.Equal(new[] { "o1", "o2", "o3", "o4", "o5", "o6" },
            view!.Questions.Single().Options.Select(o => o.OptionId).ToArray());
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_Default_ShufflesOptions_ButStaysStableAcrossCalls()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        for (var i = 1; i <= 8; i++)
        {
            AddOption(ctx, $"o{i}", "q1", $"Option {i}", isCorrect: i == 1, sorting: i);
        }
        var (svc, _, _) = Build(ctx);
        var authored = new[] { "o1", "o2", "o3", "o4", "o5", "o6", "o7", "o8" };

        var first = (await svc.GetAssignedForApplicantAsync(Applicant("app1")))!
            .Questions.Single().Options.Select(o => o.OptionId).ToArray();
        var second = (await svc.GetAssignedForApplicantAsync(Applicant("app1")))!
            .Questions.Single().Options.Select(o => o.OptionId).ToArray();

        Assert.Equal(first, second);                          // the actual point: deterministic
        Assert.NotEqual(authored, first);                     // creation order is no longer the key
        Assert.Equal(authored.OrderBy(x => x), first.OrderBy(x => x));  // nothing lost
    }

    [Fact]
    public async Task GetAssignedForApplicantAsync_Throws_WhenNotApplicant()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        // an active HRB agent is not an applicant
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAssignedForApplicantAsync(Hrb()));
    }

    // ---------- SubmitAnswersAsync ----------

    [Fact]
    public async Task SubmitAnswersAsync_PersistsAnswers_CompletesAssignment_Broadcasts_AndNotifiesAgent()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "Erste", isCorrect: true, sorting: 1);
        AddQuestion(ctx, "q2", "t1", TestQuestionType.FreeText, sorting: 2);
        AddQuestion(ctx, "q3", "t1", TestQuestionType.MultipleChoice, sorting: 3, required: false);
        AddOption(ctx, "o3", "q3", "Einzige", isCorrect: true, sorting: 1);
        AddBewerbung(ctx, "b1", "app1", assignedAgentId: "agentHandler");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, broadcaster, notifications) = Build(ctx);
        var fired = new List<string>();
        broadcaster.Modified += fired.Add;

        var inputs = new List<TestAnswerInput>
        {
            new() { QuestionId = "q1", SelectedOptionIds = ["o1"] },
            new() { QuestionId = "q2", FreeText = "  meine Antwort  " },
            new() { QuestionId = "q3", SelectedOptionIds = ["   "] }, // not an option of q3 => stored null
        };

        await svc.SubmitAnswersAsync("as1", 1, inputs, Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.Where(a => a.AssignmentId == "as1").ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.Equal("meine Antwort", stored.Single(a => a.QuestionId == "q2").FreeTextAnswer);
        Assert.Null(stored.Single(a => a.QuestionId == "q3").SelectedOptionId);
        Assert.NotNull((await check.BewerbungTestAssignments.SingleAsync(a => a.Id == "as1")).CompletedAt);
        Assert.Contains("b1", fired);
        await notifications.Received(1).NotifyAsync("agentHandler", NotificationType.Recruiting,
            Arg.Any<string>(), "/bewerbungen/b1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAnswersAsync_MultiSelect_StoresEveryTickedOption()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, allowMultiple: true);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        AddOption(ctx, "o2", "q1", "B", isCorrect: true, sorting: 2);
        AddOption(ctx, "o3", "q1", "C", isCorrect: false, sorting: 3);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("as1", 1,
            [new TestAnswerInput { QuestionId = "q1", SelectedOptionIds = ["o1", "o2"] }], Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.SingleAsync(a => a.AssignmentId == "as1");
        Assert.Equal("o1,o2", stored.SelectedOptionIds);
        // the scalar column stays empty: it means "exactly one", and two were ticked
        Assert.Null(stored.SelectedOptionId);
    }

    [Fact]
    public async Task SubmitAnswersAsync_SingleChoice_KeepsOnlyOneTick()
    {
        // the form offers radio buttons, but the payload is not the form
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        AddOption(ctx, "o2", "q1", "B", isCorrect: false, sorting: 2);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("as1", 1,
            [new TestAnswerInput { QuestionId = "q1", SelectedOptionIds = ["o1", "o2"] }], Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.SingleAsync(a => a.AssignmentId == "as1");
        Assert.Equal("o1", stored.SelectedOptionId);
        Assert.Equal("o1", stored.SelectedOptionIds);
    }

    [Fact]
    public async Task SubmitAnswersAsync_DropsAnOptionThatBelongsToAnotherQuestion()
    {
        // the column carries no foreign key, so nothing downstream would catch an invented id
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.MultipleChoice, sorting: 1, required: false);
        AddOption(ctx, "o1", "q1", "A", isCorrect: true, sorting: 1);
        AddQuestion(ctx, "q2", "t1", TestQuestionType.MultipleChoice, sorting: 2, required: false);
        AddOption(ctx, "o2", "q2", "B", isCorrect: true, sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("as1", 1,
            [new TestAnswerInput { QuestionId = "q1", SelectedOptionIds = ["o2", "erfunden"] }], Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.SingleAsync(a => a.QuestionId == "q1");
        Assert.Null(stored.SelectedOptionIds);
        Assert.Null(stored.SelectedOptionId);
    }

    [Fact]
    public async Task SubmitAnswersAsync_IgnoresAQuestionFromAnotherTest()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1, required: false);
        AddTest(ctx, "t2", "Fremd", sorting: 2);
        AddQuestion(ctx, "fremd", "t2", TestQuestionType.FreeText, sorting: 1, required: false);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("as1", 1,
            [new TestAnswerInput { QuestionId = "fremd", FreeText = "hallo" }], Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.Where(a => a.AssignmentId == "as1").ToListAsync();
        Assert.Equal("q1", Assert.Single(stored).QuestionId);
    }

    [Fact]
    public async Task SubmitAnswersAsync_CollapsesTwoRowsForOneQuestion()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await svc.SubmitAnswersAsync("as1", 1,
            [
                new TestAnswerInput { QuestionId = "q1", FreeText = "erste" },
                new TestAnswerInput { QuestionId = "q1", FreeText = "zweite" },
            ], Applicant("app1"));

        using var check = ctx.NewContext();
        var stored = await check.BewerbungTestAnswers.Where(a => a.AssignmentId == "as1").ToListAsync();
        Assert.Equal("zweite", Assert.Single(stored).FreeTextAnswer);
    }

    [Fact]
    public async Task SubmitAnswersAsync_Throws_WhenAMandatoryQuestionIsLeftEmpty()
    {
        // the form blocks it too, but the submission travels over SignalR and never passes the form's check
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddQuestion(ctx, "q1", "t1", TestQuestionType.FreeText, sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("as1", 1, [], Applicant("app1")));

        using var check = ctx.NewContext();
        Assert.Null((await check.BewerbungTestAssignments.SingleAsync(a => a.Id == "as1")).CompletedAt);
    }

    [Fact]
    public async Task SubmitAnswersAsync_Throws_WhenAssignmentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("nope", 1, new List<TestAnswerInput>(), Applicant("app1")));
    }

    [Fact]
    public async Task SubmitAnswersAsync_Throws_WhenNotOwner()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "ownerUser");
        AddAssignment(ctx, "as1", "b1", "t1");
        var (svc, _, _) = Build(ctx);

        // applicant "intruder" does not own bewerbung b1
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SubmitAnswersAsync("as1", 1, new List<TestAnswerInput>(), Applicant("intruder")));

        using var check = ctx.NewContext();
        Assert.Null((await check.BewerbungTestAssignments.SingleAsync(a => a.Id == "as1")).CompletedAt);
    }

    [Fact]
    public async Task SubmitAnswersAsync_Throws_WhenAlreadyCompleted()
    {
        using var ctx = new SqliteTestContext();
        AddTest(ctx, "t1", "Test", sorting: 1);
        AddBewerbung(ctx, "b1", "app1");
        AddAssignment(ctx, "as1", "b1", "t1", completedAt: Ts);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAnswersAsync("as1", 1, new List<TestAnswerInput>(), Applicant("app1")));
    }

    [Fact]
    public async Task SubmitAnswersAsync_Throws_WhenNotApplicant()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SubmitAnswersAsync("as1", 1, new List<TestAnswerInput>(), Hrb()));
    }
}
