using System.Security.Claims;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="EmergencyAlarmService"/> against in-memory SQLite.</summary>
public sealed class EmergencyAlarmServiceTests
{
    private static (EmergencyAlarmService Service, INotificationService Notifications, List<string> Reports) Build(
        SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new EmergencyAlarmBroadcaster();
        var reports = new List<string>();
        broadcaster.Modified += reports.Add;
        return (new EmergencyAlarmService(ctx.Factory, broadcaster, notifications), notifications, reports);
    }

    private static ClaimsPrincipal As(string id, Rank rank)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).WithCodename($"Codename-{id}").Build();

    private static ClaimsPrincipal Leader(string id = "lead") => As(id, Rank.SectionChief);

    private static ClaimsPrincipal Junior(string id = "junior") => As(id, Rank.SpecialAgent);

    private static ClaimsPrincipal Partner(string id = "partner")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ClaimsPrincipal Supervision(string id = "tl")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private static EmergencyAlarmInput Input(Division? target = null, string? reason = null,
        string situation = "Geiselnahme", string? location = "Maze Bank")
        => new(situation, location, target, reason);

    private static void Agents(SqliteTestContext ctx, params Data.Entities.Agent[] agents)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        db.SaveChanges();
    }

    private static Data.Entities.Agent Agent(string id, Rank rank = Rank.SpecialAgent, Division division = Division.Cid,
        Action<Data.Entities.Agent>? configure = null)
        => Seed.Agent(id, rank, configure: a =>
        {
            a.Division = division;
            configure?.Invoke(a);
        });

    private static void AbsentToday(SqliteTestContext ctx, string agentId)
    {
        var today = MeetingTime.Day(DateTime.UtcNow);
        using var db = ctx.NewContext();
        db.Absences.Add(new Absence
        {
            AgentId = agentId, FromDate = today.AddDays(-1), ToDate = today.AddDays(1), Days = 3,
            Category = AbsenceCategory.Vacation,
        });
        db.SaveChanges();
    }

    private static List<EmergencyAlarmRecipient> Rows(SqliteTestContext ctx, string alarmId)
    {
        using var db = ctx.NewContext();
        return db.EmergencyAlarmRecipients.AsNoTracking().Where(r => r.AlarmId == alarmId).ToList();
    }

    private static EmergencyAlarm Stored(SqliteTestContext ctx, string alarmId)
    {
        using var db = ctx.NewContext();
        return db.EmergencyAlarms.AsNoTracking().Single(a => a.Id == alarmId);
    }

    private static bool Same(IReadOnlyCollection<string> ids, params string[] expected)
        => ids.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal));

    // ---------- trigger ----------

    [Fact]
    public async Task Trigger_Leadership_AllTarget_SnapshotsSelectableExceptAbsent()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("a2"), Agent("away"),
            Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true));
        AbsentToday(ctx, "away");
        var (svc, notifications, reports) = Build(ctx);

        var id = await svc.TriggerAsync(Input(), Leader());

        var rows = Rows(ctx, id);
        Assert.True(Same(rows.Select(r => r.AgentId).ToList(), "lead", "a1", "a2"));
        // the triggerer counts once, already coming
        var own = Assert.Single(rows, r => r.AgentId == "lead");
        Assert.Equal(EmergencyAlarmAnswer.Coming, own.Answer);
        Assert.NotNull(own.AnsweredAt);
        Assert.All(rows.Where(r => r.AgentId != "lead"), r => Assert.Null(r.Answer));
        var alarm = Stored(ctx, id);
        Assert.False(alarm.IsEmergency);
        Assert.Null(alarm.Reason);
        Assert.Equal("Codename-lead", alarm.TriggeredByName);
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "a1", "a2")), NotificationType.EmergencyAlarm,
            "Einsatzalarm: Geiselnahme · Maze Bank", $"/einsatzalarme/{id}", "lead", Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.EmergencyAlarmByAgent, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(new[] { id }, reports);
    }

    [Fact]
    public async Task Trigger_DivisionTarget_OnlyMembers_NoBoard()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("cid1"), Agent("hrt1", division: Division.Hrt),
            Agent("ad", Rank.AssistantDirector));
        var (svc, notifications, _) = Build(ctx);

        var id = await svc.TriggerAsync(Input(Division.Cid), Leader());

        Assert.True(Same(Rows(ctx, id).Select(r => r.AgentId).ToList(), "lead", "cid1"));
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "cid1")), NotificationType.EmergencyAlarm,
            Arg.Any<string>(), $"/einsatzalarme/{id}", "lead", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trigger_BelowLeadership_WithoutReason_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"));
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TriggerAsync(Input(reason: "  "), Junior()));

        using var db = ctx.NewContext();
        Assert.Empty(db.EmergencyAlarms);
    }

    [Fact]
    public async Task Trigger_BelowLeadership_WithReason_MarksEmergencyAndBellsLeadership()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"), Agent("a1"), Agent("sc", Rank.SectionChief),
            Agent("admin", configure: a => a.IsAdmin = true), Agent("dir", Rank.Director),
            Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true),
            Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD));
        var (svc, notifications, _) = Build(ctx);

        var id = await svc.TriggerAsync(Input(reason: "  Schüsse gefallen  "), Junior());

        var alarm = Stored(ctx, id);
        Assert.True(alarm.IsEmergency);
        Assert.Equal("Schüsse gefallen", alarm.Reason);
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "sc", "admin", "dir")),
            NotificationType.EmergencyAlarmByAgent,
            Arg.Is<string>(t => t.Contains("Codename-junior") && t.Contains("Geiselnahme") && !t.Contains("Schüsse")),
            $"/einsatzalarme/{id}", "junior", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trigger_Partner_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.TriggerAsync(Input(reason: "x"), Partner()));
    }

    [Fact]
    public async Task Trigger_Supervision_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.TriggerAsync(Input(), Supervision()));
    }

    [Fact]
    public async Task Trigger_EmptySituation_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief));
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TriggerAsync(Input(situation: "   "), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.TriggerAsync(Input(situation: new string('x', EmergencyAlarmRules.MaxSituation + 1)), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.TriggerAsync(Input(location: new string('x', EmergencyAlarmRules.MaxLocation + 1)), Leader()));
    }

    [Fact]
    public async Task Trigger_TriggererOutsideTarget_IsListedAsComing()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("hrt1", division: Division.Hrt), Agent("cid1"));
        var (svc, notifications, _) = Build(ctx);

        var id = await svc.TriggerAsync(Input(Division.Hrt), Leader());

        var rows = Rows(ctx, id);
        Assert.True(Same(rows.Select(r => r.AgentId).ToList(), "lead", "hrt1"));
        Assert.Equal(EmergencyAlarmAnswer.Coming, rows.Single(r => r.AgentId == "lead").Answer);
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "hrt1")), NotificationType.EmergencyAlarm,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trigger_SetsExpiryThirtyMinutes()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief));
        var (svc, _, _) = Build(ctx);

        var id = await svc.TriggerAsync(Input(), Leader());

        var alarm = Stored(ctx, id);
        Assert.Equal(TimeSpan.FromMinutes(30), alarm.Expiry - alarm.TriggeredAt);
        Assert.Null(alarm.EndedAt);
    }

    [Fact]
    public async Task Trigger_NotificationFailure_KeepsAlarm()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, notifications, reports) = Build(ctx);
        notifications.NotifyManyAsync(default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("down")));

        var id = await svc.TriggerAsync(Input(), Leader());

        Assert.Equal(2, Rows(ctx, id).Count);
        Assert.Equal(new[] { id }, reports);
    }

    [Fact]
    public async Task Recipients_SameAgentTwice_RejectedByDatabase()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        using var db = ctx.NewContext();
        db.EmergencyAlarmRecipients.Add(new EmergencyAlarmRecipient { AlarmId = id, AgentId = "a1" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---------- respond ----------

    [Fact]
    public async Task Respond_Recipient_CanChangeAnswer()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, reports) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        await svc.RespondAsync(id, EmergencyAlarmAnswer.NotComing, Junior("a1"));
        Assert.Equal(EmergencyAlarmAnswer.NotComing, Rows(ctx, id).Single(r => r.AgentId == "a1").Answer);

        await svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Junior("a1"));
        var row = Rows(ctx, id).Single(r => r.AgentId == "a1");
        Assert.Equal(EmergencyAlarmAnswer.Coming, row.Answer);
        Assert.NotNull(row.AnsweredAt);
        Assert.Equal(new[] { id, id, id }, reports);

        var detail = await svc.GetDetailAsync(id, Junior("a1"));
        Assert.NotNull(detail);
        Assert.True(detail.MayAnswer);
        Assert.Equal(EmergencyAlarmAnswer.Coming, detail.MyAnswer);
        Assert.Equal(2, detail.Summary.Coming);
        Assert.Equal(0, detail.Summary.Open);
    }

    [Fact]
    public async Task Respond_NonRecipient_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("cid1"), Agent("hrt1", division: Division.Hrt));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(Division.Cid), Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Junior("hrt1")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Partner()));
    }

    [Fact]
    public async Task Respond_AfterExpiryBeforeSweep_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());
        using (var db = ctx.NewContext())
        {
            var alarm = db.EmergencyAlarms.Single(a => a.Id == id);
            alarm.Expiry = DateTime.UtcNow.AddSeconds(-1);
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Junior("a1")));
        Assert.Null(Rows(ctx, id).Single(r => r.AgentId == "a1").Answer);
    }

    [Fact]
    public async Task Respond_TwoOpenAlarms_OnlyTheAnsweredChanges()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var first = await svc.TriggerAsync(Input(situation: "Erste Lage"), Leader());
        var second = await svc.TriggerAsync(Input(situation: "Zweite Lage"), Leader());

        Assert.Equal(2, (await svc.GetOpenForMeAsync(Junior("a1"))).Count);

        await svc.RespondAsync(first, EmergencyAlarmAnswer.Coming, Junior("a1"));

        Assert.Equal(EmergencyAlarmAnswer.Coming, Rows(ctx, first).Single(r => r.AgentId == "a1").Answer);
        Assert.Null(Rows(ctx, second).Single(r => r.AgentId == "a1").Answer);
        var open = Assert.Single(await svc.GetOpenForMeAsync(Junior("a1")));
        Assert.Equal(second, open.Id);
        // the triggerer already answered both
        Assert.Empty(await svc.GetOpenForMeAsync(Leader()));
    }

    // ---------- re-ping ----------

    [Fact]
    public async Task Reping_OnlyOpenRecipients_Once()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("a2"), Agent("a3"));
        var (svc, notifications, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());
        await svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Junior("a1"));
        await svc.RespondAsync(id, EmergencyAlarmAnswer.NotComing, Junior("a2"));
        Assert.True((await svc.GetDetailAsync(id, Leader()))!.MayReping);
        notifications.ClearReceivedCalls();

        await svc.RepingAsync(id, Leader());

        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "a3")), NotificationType.EmergencyAlarm,
            "Einsatzalarm (erneut): Geiselnahme · Maze Bank", $"/einsatzalarme/{id}", "lead", Arg.Any<CancellationToken>());
        Assert.NotNull(Stored(ctx, id).RepingedAt);
        Assert.False((await svc.GetDetailAsync(id, Leader()))!.MayReping);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepingAsync(id, Leader()));
    }

    [Fact]
    public async Task Reping_NotTriggerer_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("sc2", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepingAsync(id, Junior("a1")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepingAsync(id, Leader("sc2")));
        Assert.Null(Stored(ctx, id).RepingedAt);
    }

    // ---------- end ----------

    [Fact]
    public async Task End_TriggererOrLeadership_SecondEndIsNoop()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"), Agent("a1"), Agent("sc", Rank.SectionChief));
        var (svc, _, reports) = Build(ctx);
        var byLeadership = await svc.TriggerAsync(Input(reason: "Eile"), Junior());
        var byTriggerer = await svc.TriggerAsync(Input(reason: "Eile"), Junior());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.EndAsync(byLeadership, Junior("a1")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.EndAsync(byLeadership, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.EndAsync(byLeadership, Partner()));

        await svc.EndAsync(byLeadership, Leader("sc"));
        var ended = Stored(ctx, byLeadership);
        Assert.NotNull(ended.EndedAt);
        Assert.Equal("sc", ended.EndedById);

        reports.Clear();
        await svc.EndAsync(byLeadership, Junior());
        Assert.Equal("sc", Stored(ctx, byLeadership).EndedById);
        Assert.Empty(reports);

        await svc.EndAsync(byTriggerer, Junior());
        Assert.Equal("junior", Stored(ctx, byTriggerer).EndedById);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RespondAsync(byLeadership, EmergencyAlarmAnswer.Coming, Junior("a1")));
        var detail = (await svc.GetDetailAsync(byLeadership, Junior()))!;
        Assert.False(detail.Summary.IsOpen);
        Assert.False(detail.MayAnswer);
        Assert.False(detail.MayEnd);
        Assert.True(detail.MayConvert);
        Assert.False((await svc.GetDetailAsync(byLeadership, Junior("a1")))!.MayConvert);
    }

    // ---------- read ----------

    [Fact]
    public async Task GetList_Partner_Empty()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("a2"));
        var (svc, _, _) = Build(ctx);
        var older = await svc.TriggerAsync(Input(situation: "Alt"), Leader());
        var newer = await svc.TriggerAsync(Input(Division.Cid, situation: "Neu"), Leader());
        await svc.RespondAsync(newer, EmergencyAlarmAnswer.NotComing, Junior("a1"));

        Assert.Empty(await svc.GetListAsync(Partner()));
        Assert.Null(await svc.GetDetailAsync(newer, Partner()));
        Assert.Empty(await svc.GetOpenForMeAsync(Partner()));

        var list = await svc.GetListAsync(Junior("a2"));
        Assert.Equal(new[] { newer, older }, list.Select(s => s.Id).ToArray());
        var top = list[0];
        Assert.Equal("Neu", top.Situation);
        Assert.Equal(Division.Cid, top.Target);
        Assert.Equal("Codename-lead", top.TriggeredByName);
        Assert.True(top.IsOpen);
        Assert.Equal((1, 1, 1), (top.Coming, top.NotComing, top.Open));
    }

    [Fact]
    public async Task GetDetail_Supervision_ReadsButMayNotAnswer()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        var detail = await svc.GetDetailAsync(id, Supervision());

        Assert.NotNull(detail);
        Assert.Equal("Maze Bank", detail.Location);
        Assert.False(detail.MayAnswer);
        Assert.Null(detail.MyAnswer);
        Assert.False(detail.MayReping);
        Assert.False(detail.MayEnd);
        Assert.False(detail.MayConvert);
        Assert.Equal(2, detail.Rows.Count);
        Assert.Equal("Codename-lead", detail.Rows[0].Codename);
        Assert.Equal(EmergencyAlarmAnswer.Coming, detail.Rows[0].Answer);
        Assert.Single(await svc.GetListAsync(Supervision()));
        Assert.Empty(await svc.GetOpenForMeAsync(Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Supervision()));
    }

    [Fact]
    public async Task GetDetail_Triggerer_MayRepingAndEnd()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        var detail = (await svc.GetDetailAsync(id, Leader()))!;

        Assert.True(detail.MayReping);
        Assert.True(detail.MayEnd);
        Assert.False(detail.MayConvert);
        Assert.True(detail.MayAnswer);
        Assert.Equal(EmergencyAlarmAnswer.Coming, detail.MyAnswer);
        Assert.Equal(detail.Summary.TriggeredAt + EmergencyAlarmRules.Duration, detail.Expiry);
        Assert.Null(await svc.GetDetailAsync("missing", Leader()));
    }
}
