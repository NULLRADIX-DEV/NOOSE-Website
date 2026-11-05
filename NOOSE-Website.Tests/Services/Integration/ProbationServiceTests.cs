using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Onboarding plan: mentor, probation end, light, reminders and the closing note.</summary>
public sealed class ProbationServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SectionChief).WithCodename("Lead").Build();

    private static ClaimsPrincipal JuniorHrb(string id = "hrb")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithCodename("Hrb").AsHrb().Build();

    private static ClaimsPrincipal Plain(string id = "plain")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal OnlyReader(string id = "aufsicht")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private sealed record Fixture(SqliteTestContext Ctx, ProbationService Svc, INotificationService Notifications) : IDisposable
    {
        public void Dispose() => Ctx.Dispose();
    }

    private static Fixture Make(params Agent[] agents)
    {
        var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(agents);
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        return new Fixture(ctx, new ProbationService(ctx.Factory, notifications), notifications);
    }

    private static Agent Agent(string id, Rank rank = Rank.Trainee, Division division = Division.Cid,
        AgentStatus status = AgentStatus.Active)
        => Seed.Agent(id, rank, status, a => a.Division = division);

    private static void Add(Fixture f, params object[] rows)
    {
        using var db = f.Ctx.NewContext();
        db.AddRange(rows);
        db.SaveChanges();
    }

    private static TrainingModule Module(string id, bool mandatory, bool active = true)
        => new() { Id = id, Name = id, IsMandatory = mandatory, IsActive = active };

    private static AgentProbation Plan(string agentId, DateTime start, DateTime end, string? mentor = null)
        => new() { AgentId = agentId, StartedAt = start, EndsAt = end, MentorAgentId = mentor };

    private static AgentProbation Reload(Fixture f, string agentId)
    {
        using var db = f.Ctx.NewContext();
        return db.AgentProbations.AsNoTracking().OrderByDescending(p => p.StartedAt).First(p => p.AgentId == agentId);
    }

    // ---- light ----

    [Fact]
    public void Light_KeepsGreenWhileProgressKeepsPace()
    {
        var plan = Plan("t", Now.AddDays(-5), Now.AddDays(5));
        Assert.Equal(ProbationLight.Green, ProbationRules.Light(plan, 1, 2, Now));
        Assert.Equal(ProbationLight.Yellow, ProbationRules.Light(plan, 0, 2, Now));
    }

    [Fact]
    public void Light_TurnsRedOnlyWhenTheEndPassedWithModulesOpen()
    {
        var plan = Plan("t", Now.AddDays(-15), Now.AddDays(-1));
        Assert.Equal(ProbationLight.Red, ProbationRules.Light(plan, 1, 2, Now));
        Assert.Equal(ProbationLight.Green, ProbationRules.Light(plan, 2, 2, Now));
    }

    [Fact]
    public void Light_ShowsTheOutcomeOnceClosed()
    {
        var plan = Plan("t", Now.AddDays(-15), Now.AddDays(-1));
        plan.ClosedAt = Now;
        plan.Outcome = ProbationOutcome.Failed;
        Assert.Equal(ProbationLight.Failed, ProbationRules.Light(plan, 2, 2, Now));
    }

    // ---- view ----

    [Fact]
    public async Task GetAsync_CountsOnlyActiveMandatoryModules()
    {
        using var f = Make(Agent("t"), Agent("m", Rank.SeniorSpecialAgent));
        Add(f, Module("a", mandatory: true), Module("b", mandatory: true), Module("c", mandatory: false),
            Module("d", mandatory: true, active: false));
        Add(f, new AgentModuleCompletion { AgentId = "t", ModuleId = "a", CompletedAt = Now },
            new AgentModuleCompletion { AgentId = "t", ModuleId = "c", CompletedAt = Now },
            Plan("t", Now.AddDays(-1), Now.AddDays(13), mentor: "m"));

        var view = await f.Svc.GetAsync("t", Plain());

        Assert.NotNull(view);
        Assert.Equal(1, view.MandatoryDone);
        Assert.Equal(2, view.MandatoryTotal);
        Assert.Equal(new[] { "b" }, view.OpenModules);
        Assert.Equal("Codename-m", view.MentorCodename);
    }

    [Fact]
    public async Task GetAsync_IsNullWithoutPlan()
    {
        using var f = Make(Agent("t"));
        Assert.Null(await f.Svc.GetAsync("t", Plain()));
    }

    // ---- start / mentor / extend ----

    [Fact]
    public async Task StartAsync_UsesConfiguredDaysAndNotifiesTheMentor()
    {
        using var f = Make(Agent("t"), Agent("m", Rank.SeniorSpecialAgent));
        await f.Svc.SetDaysAsync(21, Leader());

        await f.Svc.StartAsync("t", "m", JuniorHrb());

        var plan = Reload(f, "t");
        Assert.Equal("m", plan.MentorAgentId);
        Assert.Equal(21, (plan.EndsAt - plan.StartedAt).TotalDays, precision: 3);
        await f.Notifications.Received().NotifyAsync("m", NotificationType.Account, Arg.Any<string>(),
            ProbationRules.Route("t"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_RefusesASecondOpenPlan()
    {
        using var f = Make(Agent("t"));
        await f.Svc.StartAsync("t", null, Leader());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.StartAsync("t", null, Leader()));
    }

    [Fact]
    public async Task StartAsync_RefusesAPartnerOrDepartedAccount()
    {
        using var f = Make(Agent("gone", status: AgentStatus.Terminated),
            Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.StartAsync("gone", null, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.StartAsync("partner", null, Leader()));
    }

    [Fact]
    public async Task Writes_AreRefusedForPlainAgentsAndTheReadOnlySupervision()
    {
        using var f = Make(Agent("t"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.StartAsync("t", null, Plain()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.StartAsync("t", null, OnlyReader()));
        await f.Svc.StartAsync("t", null, Leader());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.CloseAsync("t", ProbationOutcome.Passed, null, JuniorHrb()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.SetDaysAsync(30, JuniorHrb()));
    }

    [Fact]
    public async Task SetMentorAsync_RefusesTheAgentItselfAndInactiveAccounts()
    {
        using var f = Make(Agent("t"), Agent("gone", status: AgentStatus.Terminated));
        await f.Svc.StartAsync("t", null, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.SetMentorAsync("t", "t", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.SetMentorAsync("t", "gone", Leader()));
    }

    [Fact]
    public async Task ExtendAsync_RearmsRemindersThatAlreadyWentOut()
    {
        using var f = Make(Agent("t"));
        var plan = Plan("t", Now.AddDays(-14), Now.AddHours(-1));
        plan.ReminderStage = ProbationReminderStage.Due;
        Add(f, plan);

        await f.Svc.ExtendAsync("t", Now.AddDays(7), Leader());

        var after = Reload(f, "t");
        Assert.Equal(ProbationReminderStage.None, after.ReminderStage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.ExtendAsync("t", Now.AddDays(-1), Leader()));
    }

    // ---- close ----

    [Fact]
    public async Task CloseAsync_FilesATrainingNoteAndEndsThePlan()
    {
        using var f = Make(Agent("t"));
        await f.Svc.StartAsync("t", null, Leader());

        await f.Svc.CloseAsync("t", ProbationOutcome.Passed, "Sauber <b>gearbeitet</b>", Leader());

        var plan = Reload(f, "t");
        Assert.Equal(ProbationOutcome.Passed, plan.Outcome);
        Assert.NotNull(plan.ClosedAt);
        using var db = f.Ctx.NewContext();
        var note = db.AgentNotes.Single(n => n.AgentId == "t");
        Assert.Equal(AgentNoteKind.Training, note.Kind);
        Assert.Contains("Probezeit bestanden.", note.Text);
        Assert.Contains("&lt;b&gt;", note.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.CloseAsync("t", ProbationOutcome.Passed, null, Leader()));
    }

    // ---- reminders ----

    [Fact]
    public async Task RemindAsync_PingsMentorAndTheOwnLeadershipOncePerStage()
    {
        using var f = Make(Agent("t"), Agent("m", Rank.SeniorSpecialAgent),
            Agent("sc-cid", Rank.SectionChief), Agent("sc-ctd", Rank.SectionChief, Division.Ctd),
            Agent("board", Rank.AssistantDirector, Division.Ctd));
        Add(f, Plan("t", Now.AddDays(-12), Now.AddDays(2), mentor: "m"));

        Assert.Equal(1, await f.Svc.RemindAsync(Now));
        Assert.Equal(0, await f.Svc.RemindAsync(Now));

        await f.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 3 && r.Contains("m") && r.Contains("sc-cid") && r.Contains("board")),
            NotificationType.Account, Arg.Any<string>(), Arg.Is(ProbationRules.Route("t")), Arg.Is<string?>(x => x == null), Arg.Any<CancellationToken>());
        Assert.Equal(ProbationReminderStage.Soon, Reload(f, "t").ReminderStage);

        Assert.Equal(1, await f.Svc.RemindAsync(Now.AddDays(3)));
        Assert.Equal(ProbationReminderStage.Due, Reload(f, "t").ReminderStage);
    }

    [Fact]
    public async Task RemindAsync_StaysQuietForDepartedAgentsAndFarDeadlines()
    {
        using var f = Make(Agent("gone", status: AgentStatus.Terminated), Agent("t"), Agent("sc", Rank.SectionChief));
        Add(f, Plan("gone", Now.AddDays(-14), Now.AddDays(-1)), Plan("t", Now, Now.AddDays(10)));

        await f.Svc.RemindAsync(Now);

        await f.Notifications.DidNotReceiveWithAnyArgs().NotifyManyInAppAsync(default!, default, default!, default, default);
        Assert.Equal(ProbationReminderStage.None, Reload(f, "t").ReminderStage);
    }
}
