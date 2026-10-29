using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Handover of a leaving agent's work: what is listed, what moves where, what closes, and who may.</summary>
public sealed class AgentHandoverServiceTests
{
    private const string Gone = "gone";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Codename-lead").Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static (AgentHandoverService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new AgentHandoverService(ctx.Factory, notifications), notifications);
    }

    /// <summary>One open and one finished record of every kind, all hanging on the leaving agent.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("senior", Rank.SeniorSpecialAgent));
        db.Users.Add(Seed.Agent(Gone, Rank.SpecialAgent, AgentStatus.Terminated));
        db.Users.Add(Seed.Agent("succ", Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("other", Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("former", Rank.SpecialAgent, AgentStatus.Terminated));
        db.Users.Add(Seed.Agent("bewerber", Rank.SpecialAgent, AgentStatus.Applicant));

        db.Followups.Add(new Followup
        {
            Id = "f-open", EntityType = nameof(Case), EntityId = "c-open", Note = "Zeugen nachfassen",
            DueAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), ResponsibleAgentId = Gone,
        });
        db.Followups.Add(new Followup
        {
            Id = "f-done", EntityType = nameof(Case), EntityId = "c-open", Done = true,
            DueAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ResponsibleAgentId = Gone,
        });

        db.Jobs.Add(new Job { Id = "j-open", CaseNumber = "NOOSE-A-2026-0001", Title = "Hafen überwachen" });
        db.Jobs.Add(new Job { Id = "j-done", CaseNumber = "NOOSE-A-2026-0002", Title = "Erledigt", Status = JobStatus.Done });
        db.JobAssignments.Add(new JobAssignment { JobId = "j-open", AgentId = Gone });
        db.JobAssignments.Add(new JobAssignment { JobId = "j-done", AgentId = Gone });

        db.Cases.Add(Seed.Case("c-open", "Hafenschmuggel"));
        db.Cases.Add(Seed.Case("c-done", "Alt", c => c.Status = CaseStatus.Completed));
        db.CaseAgents.Add(new CaseAgent { CaseId = "c-open", AgentId = Gone, IsCaseLead = true });
        db.CaseAgents.Add(new CaseAgent { CaseId = "c-done", AgentId = Gone });

        db.Operations.Add(new Operation
        {
            Id = "o-run", CaseNumber = "NOOSE-O-2026-0001", Title = "Nachtschicht", Status = OperationStatus.Running,
        });
        db.Operations.Add(new Operation
        {
            Id = "o-done", CaseNumber = "NOOSE-O-2026-0002", Title = "Vorbei", Status = OperationStatus.Completed,
        });
        db.OperationAgents.Add(new OperationAgent { OperationId = "o-run", AgentId = Gone, IsInvestigationLead = true });
        db.OperationAgents.Add(new OperationAgent { OperationId = "o-done", AgentId = Gone });

        db.Taskforces.Add(new Taskforce
        {
            Id = "t-ok", CaseNumber = "NOOSE-T-2026-0001", Name = "TF Hafen", Status = TaskforceStatus.Approved,
        });
        db.Taskforces.Add(new Taskforce
        {
            Id = "t-res", CaseNumber = "NOOSE-T-2026-0002", Name = "TF Alt", Status = TaskforceStatus.Resolved,
        });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t-ok", AgentId = Gone, Role = TaskforceRole.CidLead });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t-res", AgentId = Gone });

        db.Informants.Add(new Informant
        {
            Id = "i-act", CaseNumber = "NOOSE-I-2026-0001", RealName = "Tony Spitzel", HandlerId = Gone,
        });
        db.Informants.Add(new Informant
        {
            Id = "i-burn", CaseNumber = "NOOSE-I-2026-0002", RealName = "Verbrannt", HandlerId = Gone,
            Status = InformantStatus.Burned,
        });

        db.Factions.Add(Seed.Faction("fa", "Ballas"));
        db.Factions.Add(Seed.Faction("fa-arch", "Vagos", f => f.IsArchived = true));
        db.FactionAgents.Add(new FactionAgent { FactionId = "fa", AgentId = Gone });
        db.FactionAgents.Add(new FactionAgent { FactionId = "fa-arch", AgentId = Gone });
        db.PersonGroups.Add(new PersonGroup { Id = "g", CaseNumber = "NOOSE-G-2026-0001", Name = "Hafenbande" });
        db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g", AgentId = Gone, IsInvestigationLead = true });
        db.Parties.Add(new Party { Id = "p", CaseNumber = "NOOSE-PA-2026-0001", Name = "Hafenpartei" });
        db.PartyAgents.Add(new PartyAgent { PartyId = "p", AgentId = Gone });

        db.FinancingRequests.Add(new FinancingRequest
        {
            Id = "fr-req", CaseNumber = "NOOSE-FI-2026-0001", AgentId = Gone, Justification = "Funkgerät",
        });
        db.FinancingRequests.Add(new FinancingRequest
        {
            Id = "fr-app", CaseNumber = "NOOSE-FI-2026-0002", AgentId = Gone, Justification = "Weste",
            Status = FinancingStatus.Approved, ApprovedSubsidy = 500m, BudgetYear = 2026, BudgetMonth = 10,
            Lines = [new FinancingRequestLine { ItemName = "Weste", UnitPrice = 1000m, SubsidyPercent = 50, Quantity = 1, ApprovedQuantity = 1 }],
        });
        db.FinancingRequests.Add(new FinancingRequest
        {
            Id = "fr-paid", CaseNumber = "NOOSE-FI-2026-0003", AgentId = Gone, Justification = "Alt",
            Status = FinancingStatus.Paid,
        });

        db.Appointments.Add(new Appointment
        {
            Id = "ap-next", CaseNumber = "NOOSE-T-2026-0101", Title = "Lagebesprechung", Start = DateTime.UtcNow.AddDays(3),
        });
        db.Appointments.Add(new Appointment
        {
            Id = "ap-past", CaseNumber = "NOOSE-T-2026-0102", Title = "Gestern", Start = DateTime.UtcNow.AddDays(-3),
        });
        db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "ap-next", AgentId = Gone });
        db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "ap-past", AgentId = Gone });

        await db.SaveChangesAsync();
        return ctx;
    }

    private static readonly (HandoverKind Kind, string Id)[] OpenRows =
    [
        (HandoverKind.Followup, "f-open"),
        (HandoverKind.Job, "j-open"),
        (HandoverKind.Case, "c-open"),
        (HandoverKind.Operation, "o-run"),
        (HandoverKind.Taskforce, "t-ok"),
        (HandoverKind.Informant, "i-act"),
        (HandoverKind.Faction, "fa"),
        (HandoverKind.PersonGroup, "g"),
        (HandoverKind.Party, "p"),
        (HandoverKind.FinancingRequest, "fr-app"),
        (HandoverKind.FinancingRequest, "fr-req"),
        (HandoverKind.Appointment, "ap-next"),
    ];

    private static List<HandoverChoice> AllTo(string successor)
        => OpenRows.Select(r => new HandoverChoice(r.Kind, r.Id, successor)).ToList();

    [Fact]
    public async Task ListsOnlyWhatIsStillOpen()
    {
        using var ctx = await SeededAsync();

        var plan = await Build(ctx).Svc.GetAsync(Gone, Leader());

        Assert.Equal("Codename-gone", plan.Codename);
        Assert.Equal(OpenRows.OrderBy(r => r.Kind).ThenBy(r => r.Id), plan.Rows.Select(r => (r.Kind, r.Id)).OrderBy(r => r.Kind).ThenBy(r => r.Id));
        Assert.Equal(0, plan.Hidden);
        // rows come grouped in the order of the kinds
        Assert.Equal(plan.Rows.Select(r => r.Kind).OrderBy(k => k), plan.Rows.Select(r => r.Kind));
    }

    [Fact]
    public async Task ARowNamesItsRecordAndTheRoleThatGoesAlong()
    {
        using var ctx = await SeededAsync();

        var rows = (await Build(ctx).Svc.GetAsync(Gone, Leader())).Rows;

        var @case = rows.Single(r => r.Kind == HandoverKind.Case);
        Assert.Equal("Hafenschmuggel", @case.Title);
        Assert.Equal("/vorgaenge/c-open", @case.Href);
        Assert.Equal("Fallführer", @case.Role);
        Assert.Equal("CID-Lead", rows.Single(r => r.Kind == HandoverKind.Taskforce).Role);
        Assert.Equal("Ermittlungsleiter", rows.Single(r => r.Kind == HandoverKind.PersonGroup).Role);
        Assert.Null(rows.Single(r => r.Kind == HandoverKind.Party).Role);
        var followup = rows.Single(r => r.Kind == HandoverKind.Followup);
        Assert.Equal("Zeugen nachfassen", followup.Title);
        Assert.Equal("/vorgaenge/c-open", followup.Href);
    }

    [Fact]
    public async Task HandingOverMovesEveryKindToTheSuccessor()
    {
        using var ctx = await SeededAsync();

        var result = await Build(ctx).Svc.ApplyAsync(Gone, AllTo("succ"), Leader());

        Assert.Equal(new HandoverResult(Passed: 9, Closed: 3), result);
        await using var db = ctx.NewContext();
        Assert.Equal("succ", (await db.Followups.SingleAsync(f => f.Id == "f-open")).ResponsibleAgentId);
        Assert.Equal(["succ"], await db.JobAssignments.Where(a => a.JobId == "j-open").Select(a => a.AgentId).ToListAsync());
        var caseAgent = await db.CaseAgents.SingleAsync(a => a.CaseId == "c-open");
        Assert.Equal(("succ", true), (caseAgent.AgentId, caseAgent.IsCaseLead));
        var opAgent = await db.OperationAgents.SingleAsync(a => a.OperationId == "o-run");
        Assert.Equal(("succ", true), (opAgent.AgentId, opAgent.IsInvestigationLead));
        var tfAgent = await db.TaskforceAgents.SingleAsync(a => a.TaskforceId == "t-ok");
        Assert.Equal(("succ", TaskforceRole.CidLead), (tfAgent.AgentId, tfAgent.Role));
        Assert.Equal("succ", (await db.Informants.SingleAsync(i => i.Id == "i-act")).HandlerId);
        Assert.Equal("succ", (await db.FactionAgents.SingleAsync(a => a.FactionId == "fa")).AgentId);
        var groupAgent = await db.PersonGroupAgents.SingleAsync(a => a.PersonGroupId == "g");
        Assert.Equal(("succ", true), (groupAgent.AgentId, groupAgent.IsInvestigationLead));
        Assert.Equal("succ", (await db.PartyAgents.SingleAsync(a => a.PartyId == "p")).AgentId);
    }

    [Fact]
    public async Task FinishedRecordsKeepTheLeavingAgent()
    {
        using var ctx = await SeededAsync();

        await Build(ctx).Svc.ApplyAsync(Gone, AllTo("succ"), Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(Gone, (await db.Followups.SingleAsync(f => f.Id == "f-done")).ResponsibleAgentId);
        Assert.True(await db.JobAssignments.AnyAsync(a => a.JobId == "j-done" && a.AgentId == Gone));
        Assert.True(await db.CaseAgents.AnyAsync(a => a.CaseId == "c-done" && a.AgentId == Gone));
        Assert.Equal(Gone, (await db.Informants.SingleAsync(i => i.Id == "i-burn")).HandlerId);
        Assert.True(await db.FactionAgents.AnyAsync(a => a.FactionId == "fa-arch" && a.AgentId == Gone));
        Assert.True(await db.AppointmentAssignments.AnyAsync(a => a.AppointmentId == "ap-past" && a.AgentId == Gone));
        Assert.Equal(FinancingStatus.Paid, (await db.FinancingRequests.SingleAsync(r => r.Id == "fr-paid")).Status);
    }

    [Fact]
    public async Task ASuccessorAlreadyThereKeepsTheirEntryAndGainsTheLead()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.CaseAgents.Add(new CaseAgent { Id = "own", CaseId = "c-open", AgentId = "succ" });
            db.TaskforceAgents.Add(new TaskforceAgent { Id = "own-tf", TaskforceId = "t-ok", AgentId = "succ" });
            db.JobAssignments.Add(new JobAssignment { Id = "own-job", JobId = "j-open", AgentId = "succ" });
            await db.SaveChangesAsync();
        }

        await Build(ctx).Svc.ApplyAsync(Gone, AllTo("succ"), Leader());

        await using var check = ctx.NewContext();
        var caseAgent = await check.CaseAgents.SingleAsync(a => a.CaseId == "c-open");
        Assert.Equal(("own", true), (caseAgent.Id, caseAgent.IsCaseLead));
        var tfAgent = await check.TaskforceAgents.SingleAsync(a => a.TaskforceId == "t-ok");
        Assert.Equal(("own-tf", TaskforceRole.CidLead), (tfAgent.Id, tfAgent.Role));
        Assert.Equal("own-job", (await check.JobAssignments.SingleAsync(a => a.JobId == "j-open")).Id);
    }

    [Fact]
    public async Task ASuccessorsOwnTaskforceLeadIsNotOverwritten()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t-ok", AgentId = "succ", Role = TaskforceRole.TruLead });
            await db.SaveChangesAsync();
        }

        await Build(ctx).Svc.ApplyAsync(Gone, [new HandoverChoice(HandoverKind.Taskforce, "t-ok", "succ")], Leader());

        await using var check = ctx.NewContext();
        Assert.Equal(TaskforceRole.TruLead, (await check.TaskforceAgents.SingleAsync(a => a.TaskforceId == "t-ok")).Role);
    }

    [Fact]
    public async Task TheDepartureClosesRequestsAndFutureAppointments()
    {
        using var ctx = await SeededAsync();

        await Build(ctx).Svc.ApplyAsync(Gone, AllTo("succ"), Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(FinancingStatus.Withdrawn, (await db.FinancingRequests.SingleAsync(r => r.Id == "fr-req")).Status);
        var approved = await db.FinancingRequests.Include(r => r.Lines).SingleAsync(r => r.Id == "fr-app");
        Assert.Equal(FinancingStatus.Rejected, approved.Status);
        // the reservation goes back to the budget
        Assert.Null(approved.ApprovedSubsidy);
        Assert.Null(approved.BudgetMonth);
        Assert.Equal(0, approved.Lines.Single().ApprovedQuantity);
        Assert.Equal("Antragsteller ausgeschieden", approved.DecisionNote);
        Assert.Equal("Codename-lead", approved.DeciderName);
        // the payout recipient never changes hands
        Assert.Equal(Gone, approved.AgentId);
        Assert.False(await db.AppointmentAssignments.AnyAsync(a => a.AppointmentId == "ap-next"));
    }

    [Fact]
    public async Task RowsLeftOutStayAndShowUpAgain()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx).Svc;

        await svc.ApplyAsync(Gone, [new HandoverChoice(HandoverKind.Job, "j-open", "succ")], Leader());

        var plan = await svc.GetAsync(Gone, Leader());
        Assert.Equal(OpenRows.Length - 1, plan.Rows.Count);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == HandoverKind.Job);
        await using var db = ctx.NewContext();
        Assert.Equal(Gone, (await db.CaseAgents.SingleAsync(a => a.CaseId == "c-open")).AgentId);
    }

    [Fact]
    public async Task EachSuccessorGetsOneSummary()
    {
        using var ctx = await SeededAsync();
        var (svc, notifications) = Build(ctx);
        List<HandoverChoice> choices =
        [
            new(HandoverKind.Job, "j-open", "succ"),
            new(HandoverKind.Case, "c-open", "succ"),
            new(HandoverKind.Followup, "f-open", "succ"),
            new(HandoverKind.Informant, "i-act", "other"),
            // the operator takes this one over personally and needs no notice about it
            new(HandoverKind.Party, "p", "lead"),
            new(HandoverKind.Appointment, "ap-next", null),
        ];

        await svc.ApplyAsync(Gone, choices, Leader());

        await notifications.Received(1).NotifyAsync("succ", NotificationType.HandoverReceived,
            "Du hast 3 Einträge von Codename-gone übernommen: 1 Wiedervorlage, 1 Aufgabe, 1 Vorgang.", "/meine-schicht",
            Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyAsync("other", NotificationType.HandoverReceived,
            "Du hast 1 Eintrag von Codename-gone übernommen: 1 V-Person.", "/meine-schicht", Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyAsync("lead", Arg.Any<NotificationType>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnlyLeadershipWithWriteAccessHandsOver()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx).Svc;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAsync(Gone, Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ApplyAsync(Gone, AllTo("succ"), Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ApplyAsync(Gone, AllTo("succ"), Supervision()));

        await using var db = ctx.NewContext();
        Assert.Equal(Gone, (await db.Informants.SingleAsync(i => i.Id == "i-act")).HandlerId);
    }

    [Theory]
    [InlineData("former")]
    [InlineData(Gone)]
    [InlineData("nobody")]
    public async Task TheSuccessorMustBeSelectable(string successor)
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx).Svc;
        var choices = AllTo("succ");
        choices[3] = choices[3] with { SuccessorId = successor };

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(Gone, choices, Leader()));

        // nothing moved, not even the rows with a valid successor
        await using var db = ctx.NewContext();
        Assert.Equal(Gone, (await db.Followups.SingleAsync(f => f.Id == "f-open")).ResponsibleAgentId);
    }

    [Fact]
    public async Task ARowThatPassesNeedsASuccessor()
    {
        using var ctx = await SeededAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).Svc.ApplyAsync(Gone,
            [new HandoverChoice(HandoverKind.Job, "j-open", null)], Leader()));
    }

    [Fact]
    public async Task NotForYourselfAndNotForApplicants()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx).Svc;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAsync("lead", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("lead", [], Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAsync("bewerber", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAsync("nobody", Leader()));
    }

    [Fact]
    public async Task ARowOnARecordTheViewerCannotOpenIsCountedButNeverMoved()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            // the case it hangs on is gone, so nobody can open it
            db.Followups.Add(new Followup
            {
                Id = "f-hidden", EntityType = nameof(Case), EntityId = "c-missing", ResponsibleAgentId = Gone,
                DueAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }
        var svc = Build(ctx).Svc;

        var plan = await svc.GetAsync(Gone, Leader());
        Assert.Equal(1, plan.Hidden);
        Assert.DoesNotContain(plan.Rows, r => r.Id == "f-hidden");

        var result = await svc.ApplyAsync(Gone, [new HandoverChoice(HandoverKind.Followup, "f-hidden", "succ")], Leader());
        Assert.Equal(new HandoverResult(0, 0), result);
        await using var check = ctx.NewContext();
        Assert.Equal(Gone, (await check.Followups.SingleAsync(f => f.Id == "f-hidden")).ResponsibleAgentId);
    }

    [Fact]
    public async Task ARowThatNoLongerHangsOnTheAgentIsSkipped()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            // someone else took the informant over while the dialog was open
            (await db.Informants.SingleAsync(i => i.Id == "i-act")).HandlerId = "other";
            await db.SaveChangesAsync();
        }

        var result = await Build(ctx).Svc.ApplyAsync(Gone, [new HandoverChoice(HandoverKind.Informant, "i-act", "succ")], Leader());

        Assert.Equal(new HandoverResult(0, 0), result);
        await using var check = ctx.NewContext();
        Assert.Equal("other", (await check.Informants.SingleAsync(i => i.Id == "i-act")).HandlerId);
    }
}
