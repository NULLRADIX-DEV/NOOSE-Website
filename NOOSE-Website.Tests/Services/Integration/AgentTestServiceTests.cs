using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Models.Tests;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AgentTestService"/> and <see cref="AgentTestExpiryService"/>.</summary>
public sealed class AgentTestServiceTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (AgentTestService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var svc = new AgentTestService(ctx.Factory, new AgentTestBroadcaster(), notifications,
            new TrainingModuleService(ctx.Factory), NullLogger<AgentTestService>.Instance);
        return (svc, notifications);
    }

    private static AgentTestExpiryService BuildExpiry(SqliteTestContext ctx, INotificationService notifications)
        => new(ctx.Factory, new AgentTestBroadcaster(), notifications, NullLogger<AgentTestExpiryService>.Instance);

    // HRB without leadership rank: may assign and grade, but not write personnel entries through the file service
    private static ClaimsPrincipal Hrb(string id = "hrb1")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).AsHrb().WithCodename("HRB-Alpha").Build();

    private static ClaimsPrincipal Taker(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent)
        .WithCodename($"Codename-{id}").Build();

    private static void SeedAgents(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("hrb1", Rank.SpecialAgent, configure: a => a.IsHRB = true),
            Seed.Agent("a1", Rank.SpecialAgent, configure: a => a.IsTRU = true),
            Seed.Agent("a2", Rank.SpecialAgent, configure: a => a.IsTRU = true),
            Seed.Agent("a3", Rank.JuniorAgent),
            // never selectable: read-only supervision and a released-but-blocked account
            Seed.Agent("lead", Rank.Director, configure: a => { a.IsTeamLead = true; a.IsTRU = true; }),
            Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Blocked, a => a.IsTRU = true));
        db.SaveChanges();
    }

    /// <summary>A test with one auto-gradable yes/no question and one free-text question without keywords.</summary>
    private static void SeedTest(SqliteTestContext ctx, string id = "t1", TestUsage usage = TestUsage.Intern,
        int? passPercent = 50, string? moduleId = null, bool freeText = false)
    {
        using var db = ctx.NewContext();
        db.BewerbungTests.Add(new BewerbungTest
        {
            Id = id, Title = "Funkverfahren", Usage = usage, PassPercent = passPercent, TrainingModuleId = moduleId,
            CreatedAt = Ts,
        });
        db.BewerbungTestQuestions.Add(new BewerbungTestQuestion
        {
            Id = $"{id}-q1", TestId = id, Type = TestQuestionType.YesNo, Prompt = "Ist Kanal 1 offen?", Sorting = 1,
            Points = 2, CorrectYesNo = true, CreatedAt = Ts,
        });
        if (freeText)
        {
            db.BewerbungTestQuestions.Add(new BewerbungTestQuestion
            {
                Id = $"{id}-q2", TestId = id, Type = TestQuestionType.FreeText, Prompt = "Beschreibe", Sorting = 2,
                Points = 2, CreatedAt = Ts,
            });
        }
        db.SaveChanges();
    }

    private static List<TestAnswerInput> YesNo(string testId, string answer)
        => [new TestAnswerInput { QuestionId = $"{testId}-q1", FreeText = answer }];

    private static async Task<string> AssignOneAsync(AgentTestService svc, SqliteTestContext ctx, string agentId,
        string testId = "t1")
    {
        await svc.AssignAsync(testId, new AgentTestTargets { AgentIds = [agentId] }, null, Hrb());
        using var db = ctx.NewContext();
        return db.AgentTestAssignments.Single(a => a.AgentId == agentId && a.TestId == testId && a.GradedAt == null).Id;
    }

    // ---------- assigning ----------

    [Fact]
    public async Task Assign_resolves_singles_and_group_to_selectable_agents_once()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, notifications) = Build(ctx);

        var result = await svc.AssignAsync("t1", new AgentTestTargets
        {
            AgentIds = ["a3", "a1", "lead", "unknown"],
            Audience = AnnouncementAudience.TruUnit,
        }, null, Hrb());

        // a1 named twice (single + TRU) counts once; lead and the blocked account are never selectable
        Assert.Equal(new AgentTestAssignResult(3, 0), result);
        using var db = ctx.NewContext();
        Assert.Equal(["a1", "a2", "a3"], db.AgentTestAssignments.Select(a => a.AgentId).OrderBy(x => x).ToList());
        Assert.All(db.AgentTestAssignments, a => Assert.Equal("HRB-Alpha", a.AssignedByName));
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 3), NotificationType.TestAssigned,
            Arg.Any<string>(), "/tests", "hrb1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_skips_agents_who_still_hold_an_ungraded_assignment()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, _) = Build(ctx);

        await svc.AssignAsync("t1", new AgentTestTargets { AgentIds = ["a1"] }, null, Hrb());
        var second = await svc.AssignAsync("t1", new AgentTestTargets { AgentIds = ["a1", "a2"] }, null, Hrb());

        Assert.Equal(new AgentTestAssignResult(1, 1), second);
    }

    [Fact]
    public async Task Assign_refuses_a_recruiting_only_test_and_a_past_due_date()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx, usage: TestUsage.Bewerbung);
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AssignAsync("t1", new AgentTestTargets { AgentIds = ["a1"] }, null, Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AssignAsync("t1", new AgentTestTargets { AgentIds = ["a1"] }, DateTime.UtcNow.AddDays(-1), Hrb()));
    }

    [Fact]
    public async Task Assign_needs_write_access_and_hrb_or_leadership()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, _) = Build(ctx);
        var targets = new AgentTestTargets { AgentIds = ["a1"] };

        ClaimsPrincipal[] refused =
        [
            ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.JuniorAgent).Build(),
            // the demo principal carries HRB and Director, the read-only supervision reads everything
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build(),
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsHrb().AsTeamLead().Build(),
        ];
        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AssignAsync("t1", targets, null, actor));
        }
    }

    // ---------- taking ----------

    [Fact]
    public async Task Another_agent_can_neither_open_nor_answer_an_assignment()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");

        Assert.Null(await svc.OpenAsync(id, Taker("a2")));
        Assert.Null(await svc.GetMyStatusAsync(id, Taker("a2")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SubmitAsync(id, 1, YesNo("t1", "Ja"), Taker("a2")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveDraftAsync(id, 1, YesNo("t1", "Ja"), Taker("a2")));
    }

    [Fact]
    public async Task Supervision_partner_and_demo_take_no_tests()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        ClaimsPrincipal[] refused =
        [
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent("demo").AsDemo().Build(),
            ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
        ];
        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetMineAsync(actor));
        }
    }

    [Fact]
    public async Task Open_starts_once_submit_closes_and_the_taker_sees_no_result_before_grading()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, notifications) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");

        var first = await svc.OpenAsync(id, Taker("a1"));
        var second = await svc.OpenAsync(id, Taker("a1"));
        Assert.NotNull(first);
        Assert.Single(first!.Questions);
        Assert.Equal(first.AttemptNumber, second!.AttemptNumber);

        Assert.Equal(TestSubmitOutcome.Submitted, await svc.SubmitAsync(id, first.AttemptNumber, YesNo("t1", "Ja"), Taker("a1")));
        Assert.Null(await svc.GetMyResultAsync(id, Taker("a1")));
        Assert.True((await svc.GetMyStatusAsync(id, Taker("a1")))!.Completed);
        await notifications.Received(1).NotifyAsync("hrb1", NotificationType.TestSubmitted, Arg.Any<string>(),
            $"/tests/korrektur/{id}", Arg.Any<CancellationToken>());
    }

    // ---------- grading ----------

    [Fact]
    public async Task Passing_grade_ticks_the_module_writes_the_personnel_entry_and_shows_the_taker_points()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(new TrainingModule { Id = "m1", Name = "Funk", IsActive = true });
            db.SaveChanges();
        }
        SeedTest(ctx, moduleId: "m1");
        var (svc, notifications) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));
        await svc.SubmitAsync(id, view!.AttemptNumber, YesNo("t1", "Ja"), Taker("a1"));

        await svc.CompleteGradingAsync(id, Hrb());

        using var check = ctx.NewContext();
        var assignment = check.AgentTestAssignments.Single(a => a.Id == id);
        Assert.True(assignment.Passed);
        Assert.Equal(2, assignment.FinalPoints);
        Assert.True(check.AgentModuleCompletions.Any(c => c.AgentId == "a1" && c.ModuleId == "m1"));
        var note = check.AgentNotes.Single(n => n.AgentId == "a1");
        Assert.Equal(AgentNoteKind.Training, note.Kind);
        Assert.Equal(note.Id, assignment.PersonnelNoteId);
        Assert.Contains("bestanden", note.Text);

        var result = await svc.GetMyResultAsync(id, Taker("a1"));
        Assert.Equal((2, 2, 100, true), (result!.Points, result.MaxPoints, result.Percent, result.Passed));
        await notifications.Received(1).NotifyAsync("a1", NotificationType.TestGraded, Arg.Any<string>(),
            $"/tests/{id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_module_already_ticked_by_hand_does_not_break_the_grading()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(new TrainingModule { Id = "m1", Name = "Funk", IsActive = true });
            db.AgentModuleCompletions.Add(new AgentModuleCompletion { AgentId = "a1", ModuleId = "m1", CompletedAt = Ts });
            db.SaveChanges();
        }
        SeedTest(ctx, moduleId: "m1");
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));
        await svc.SubmitAsync(id, view!.AttemptNumber, YesNo("t1", "Ja"), Taker("a1"));

        await svc.CompleteGradingAsync(id, Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(1, check.AgentModuleCompletions.Count(c => c.AgentId == "a1"));
        Assert.Single(check.AgentNotes.Where(n => n.AgentId == "a1"));
    }

    [Fact]
    public async Task Failing_writes_the_entry_without_a_tick_and_a_regrade_rewrites_the_same_entry()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(new TrainingModule { Id = "m1", Name = "Funk", IsActive = true });
            db.SaveChanges();
        }
        SeedTest(ctx, moduleId: "m1");
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));
        await svc.SubmitAsync(id, view!.AttemptNumber, YesNo("t1", "Nein"), Taker("a1"));

        await svc.CompleteGradingAsync(id, Hrb());
        using (var check = ctx.NewContext())
        {
            Assert.False(check.AgentTestAssignments.Single(a => a.Id == id).Passed);
            Assert.False(check.AgentModuleCompletions.Any());
            Assert.Contains("nicht bestanden", check.AgentNotes.Single().Text);
        }

        // the grader overrules the machine on reopening; the personnel file keeps one entry
        await svc.ReopenGradingAsync(id, Hrb());
        await svc.SetAwardedPointsAsync(id, "t1-q1", 2, Hrb());
        await svc.CompleteGradingAsync(id, Hrb());

        using var after = ctx.NewContext();
        var note = Assert.Single(after.AgentNotes);
        Assert.DoesNotContain("nicht bestanden", note.Text);
        Assert.True(after.AgentModuleCompletions.Any(c => c.AgentId == "a1"));
    }

    [Fact]
    public async Task Grading_waits_for_open_questions_and_the_evaluation_waits_for_the_hand_in()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx, freeText: true);
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));

        // an open attempt's drafts are not for the grader's eyes
        Assert.Null(await svc.GetEvaluationAsync(id, Hrb()));

        await svc.SubmitAsync(id, view!.AttemptNumber,
            [new TestAnswerInput { QuestionId = "t1-q1", FreeText = "Ja" }, new TestAnswerInput { QuestionId = "t1-q2", FreeText = "Text" }],
            Taker("a1"));
        var evaluation = await svc.GetEvaluationAsync(id, Hrb());
        Assert.Equal(1, evaluation!.OpenCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteGradingAsync(id, Hrb()));
    }

    [Fact]
    public async Task A_graded_assignment_is_neither_reset_nor_withdrawn_but_can_be_assigned_again()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));
        await svc.SubmitAsync(id, view!.AttemptNumber, YesNo("t1", "Nein"), Taker("a1"));
        await svc.CompleteGradingAsync(id, Hrb());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ResetAsync(id, "Grund", Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.WithdrawAsync(id, Hrb()));

        var retake = await svc.AssignAsync("t1", new AgentTestTargets { AgentIds = ["a1"] }, null, Hrb());
        Assert.Equal(1, retake.Created);
        var history = await svc.GetForAgentAsync("a1", Hrb());
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task Reset_discards_the_answers_and_bumps_the_attempt()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var (svc, _) = Build(ctx);
        var id = await AssignOneAsync(svc, ctx, "a1");
        var view = await svc.OpenAsync(id, Taker("a1"));
        await svc.SubmitAsync(id, view!.AttemptNumber, YesNo("t1", "Ja"), Taker("a1"));

        await svc.ResetAsync(id, "Technischer Fehler", Hrb());

        using var db = ctx.NewContext();
        var assignment = db.AgentTestAssignments.Single(a => a.Id == id);
        Assert.Equal((2, (DateTime?)null, (DateTime?)null), (assignment.AttemptCount, assignment.StartedAt, assignment.CompletedAt));
        Assert.Empty(db.AgentTestAnswers.Where(a => a.AssignmentId == id));
        // answers typed for the old attempt are refused
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAsync(id, 1, YesNo("t1", "Ja"), Taker("a1")));
    }

    // ---------- sweep ----------

    [Fact]
    public async Task The_sweep_hands_in_an_expired_attempt_and_reminds_each_due_date_once()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        var now = DateTime.UtcNow;
        using (var db = ctx.NewContext())
        {
            db.AgentTestAssignments.AddRange(
                new AgentTestAssignment
                {
                    Id = "expired", AgentId = "a1", TestId = "t1", AssignedById = "hrb1", CreatedAt = now.AddHours(-2),
                    StartedAt = now.AddHours(-1), TimeLimitMinutes = 10, DeadlineAt = now.AddMinutes(-50),
                },
                new AgentTestAssignment
                {
                    Id = "soon", AgentId = "a2", TestId = "t1", CreatedAt = now.AddDays(-3), DueAt = now.AddHours(3),
                },
                // assigned an hour ago with a short due date: the assignment ping was enough
                new AgentTestAssignment
                {
                    Id = "fresh", AgentId = "a3", TestId = "t1", CreatedAt = now.AddHours(-1), DueAt = now.AddHours(3),
                },
                new AgentTestAssignment
                {
                    Id = "late", AgentId = "a3", TestId = "t1", CreatedAt = now.AddDays(-5), DueAt = now.AddHours(-1),
                });
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var sweep = BuildExpiry(ctx, notifications);

        Assert.Equal(1, await sweep.ExpireDueAsync());
        Assert.Equal(2, await sweep.RemindDueAsync());
        Assert.Equal(0, await sweep.RemindDueAsync());

        using var check = ctx.NewContext();
        var expired = check.AgentTestAssignments.Single(a => a.Id == "expired");
        Assert.True(expired.TimedOut);
        Assert.NotNull(expired.CompletedAt);
        // the untouched question gets the empty row the evaluation keys on
        Assert.Single(check.AgentTestAnswers.Where(a => a.AssignmentId == "expired"));
        await notifications.Received(1).NotifyManyAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "a2"),
            NotificationType.TestDueReminder, Arg.Any<string>(), Arg.Is<string?>("/tests/soon"), Arg.Is<string?>(t => t == null),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyManyAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "a3"),
            NotificationType.TestDueReminder, Arg.Is<string>(t => t.Contains("überfällig")), Arg.Is<string?>("/tests/late"),
            Arg.Is<string?>(t => t == null), Arg.Any<CancellationToken>());
    }

    // ---------- shared pieces ----------

    [Fact]
    public async Task The_recruiting_side_refuses_an_internal_only_test()
    {
        using var ctx = new SqliteTestContext();
        SeedTest(ctx, usage: TestUsage.Intern);
        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(new Bewerbung
            {
                Id = "b1", ApplicantUserId = "app", Name = "B", CaseNumber = "NOOSE-B-2026-0099", SubmittedAt = Ts, CreatedAt = Ts,
            });
            db.SaveChanges();
        }
        var recruiting = new BewerbungTestService(ctx.Factory, new BewerbungBroadcaster(), Substitute.For<INotificationService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => recruiting.AssignAsync("b1", "t1", Hrb()));
    }

    [Fact]
    public async Task A_running_agent_attempt_locks_the_questions_like_an_applicant_attempt()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedTest(ctx);
        using (var db = ctx.NewContext())
        {
            db.AgentTestAssignments.Add(new AgentTestAssignment
            {
                Id = "live", AgentId = "a1", TestId = "t1", StartedAt = DateTime.UtcNow,
                DeadlineAt = DateTime.UtcNow.AddMinutes(20), TimeLimitMinutes = 20,
            });
            db.SaveChanges();
        }
        var recruiting = new BewerbungTestService(ctx.Factory, new BewerbungBroadcaster(), Substitute.For<INotificationService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => recruiting.DeleteQuestionAsync("t1-q1", Hrb()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recruiting.DeleteTestAsync("t1", Hrb()));
    }

    [Fact]
    public async Task Audience_rules_resolve_taskforce_and_rank_like_announcements()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        using (var db = ctx.NewContext())
        {
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf1", AgentId = "a3" });
            db.SaveChanges();
        }
        await using var read = ctx.NewContext();

        Assert.Equal(["a3"], await AudienceRules.RecipientIdsAsync(read, AnnouncementAudience.Taskforce, "tf1", null));
        Assert.Equal(["a1", "a2", "hrb1"],
            (await AudienceRules.RecipientIdsAsync(read, AnnouncementAudience.FromRank, null, Rank.SpecialAgent)).Order().ToList());
        // a missing threshold reaches nobody rather than everybody
        Assert.Empty(await AudienceRules.RecipientIdsAsync(read, AnnouncementAudience.FromRank, null, null));
        Assert.True(await AudienceRules.IsMemberAsync(read, AnnouncementAudience.Taskforce, "tf1", null, "a3", false, false, null));
        Assert.False(await AudienceRules.IsMemberAsync(read, AnnouncementAudience.Taskforce, "tf1", null, "a1", false, false, null));
    }
}
