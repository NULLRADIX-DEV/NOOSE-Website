using System.Security.Claims;
using NOOSE_Website.Data;
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
        return (new EmergencyAlarmService(ctx.Factory, broadcaster, notifications, Operations(ctx)), notifications, reports);
    }

    // real service, so allocation and its rights run along
    private static OperationService Operations(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        // unique across service instances
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-OP-{Guid.NewGuid():N}");
        return new OperationService(ctx.Factory, caseNo, Substitute.For<IProfileSuggestionService>(),
            Substitute.For<INotificationService>());
    }

    private static Operation StoredOperation(SqliteTestContext ctx, string operationId)
    {
        using var db = ctx.NewContext();
        return db.Operations.AsNoTracking().Include(o => o.Agents).Single(o => o.Id == operationId);
    }

    private static int OperationCount(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        return db.Operations.Count();
    }

    // trigger, answer, end: the state convert and attach need
    private static async Task<string> EndedAlarmAsync(EmergencyAlarmService svc, ClaimsPrincipal triggerer,
        EmergencyAlarmInput input, params (string Id, EmergencyAlarmAnswer Answer)[] answers)
    {
        var id = await svc.TriggerAsync(input, triggerer);
        foreach (var (agentId, answer) in answers)
        {
            await svc.RespondAsync(id, answer, Junior(agentId));
        }
        await svc.EndAsync(id, triggerer);
        return id;
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

    [Fact]
    public async Task GetDetail_RowsComingThenNotComingThenOpen()
    {
        using var ctx = new SqliteTestContext();
        // names sort against the wanted order
        Agents(ctx, Agent("zz", Rank.SectionChief), Agent("aa"), Agent("bb"), Agent("cc"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader("zz"));
        await svc.RespondAsync(id, EmergencyAlarmAnswer.NotComing, Junior("aa"));
        await svc.RespondAsync(id, EmergencyAlarmAnswer.Coming, Junior("cc"));

        var rows = (await svc.GetDetailAsync(id, Leader("zz")))!.Rows;

        Assert.Equal(new[] { "cc", "zz", "aa", "bb" }, rows.Select(r => r.AgentId).ToArray());
        Assert.Equal(new EmergencyAlarmAnswer?[]
        {
            EmergencyAlarmAnswer.Coming, EmergencyAlarmAnswer.Coming, EmergencyAlarmAnswer.NotComing, null,
        }, rows.Select(r => r.Answer).ToArray());
    }

    // ---------- best effort and once ----------

    [Fact]
    public async Task Trigger_RecipientNotificationFails_LeadershipStillAlerted()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"), Agent("a1"), Agent("sc", Rank.SectionChief));
        var (svc, notifications, _) = Build(ctx);
        notifications.NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(), NotificationType.EmergencyAlarm,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("down")));

        var id = await svc.TriggerAsync(Input(reason: "Eile"), Junior());

        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => Same(ids, "sc")), NotificationType.EmergencyAlarmByAgent,
            Arg.Any<string>(), $"/einsatzalarme/{id}", "junior", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reping_WritesAuditRow()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());

        await svc.RepingAsync(id, Leader());

        using var db = ctx.NewContext();
        var row = Assert.Single(db.AuditLogs.AsNoTracking().Where(l => l.EntityType == nameof(EmergencyAlarm)));
        Assert.Equal(id, row.EntityId);
        Assert.Equal(AuditAction.Modified, row.Action);
        Assert.Equal("lead", row.AgentId);
        Assert.Contains("Nachgepingt", row.ChangesJson);
    }

    // ---------- convert / attach ----------

    [Fact]
    public async Task Convert_CreatesRunningOperationWithComingAgents()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("a2"), Agent("a3"));
        var (svc, _, reports) = Build(ctx);
        var id = await EndedAlarmAsync(svc, Leader(), Input(),
            ("a1", EmergencyAlarmAnswer.Coming), ("a2", EmergencyAlarmAnswer.NotComing));
        reports.Clear();

        var operationId = await svc.ConvertToOperationAsync(id, Leader());

        var operation = StoredOperation(ctx, operationId);
        var alarm = Stored(ctx, id);
        Assert.Equal(OperationStatus.Running, operation.Status);
        Assert.Equal("Geiselnahme", operation.Title);
        Assert.Equal("Maze Bank", operation.Location);
        Assert.Equal(alarm.TriggeredAt, operation.Start);
        Assert.True(Same(operation.Agents.Select(a => a.AgentId).ToList(), "lead", "a1"));
        Assert.True(operation.Agents.Single(a => a.AgentId == "lead").IsInvestigationLead);
        Assert.False(operation.Agents.Single(a => a.AgentId == "a1").IsInvestigationLead);
        Assert.Equal(operationId, alarm.OperationId);
        Assert.Equal(new[] { id }, reports);
        var detail = (await svc.GetDetailAsync(id, Leader()))!;
        Assert.False(detail.MayConvert);
        Assert.Equal(operationId, detail.Summary.OperationId);
    }

    [Fact]
    public async Task Convert_WhileOpen_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await svc.TriggerAsync(Input(), Leader());
        var existing = (await Operations(ctx).CreateAsync(new OperationInput { Title = "Bestand" }, Leader())).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConvertToOperationAsync(id, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AttachToOperationAsync(id, existing, Leader()));

        Assert.Equal(1, OperationCount(ctx));
        Assert.Null(Stored(ctx, id).OperationId);
    }

    [Fact]
    public async Task Convert_Twice_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await EndedAlarmAsync(svc, Leader(), Input(), ("a1", EmergencyAlarmAnswer.Coming));
        var first = await svc.ConvertToOperationAsync(id, Leader());
        var existing = (await Operations(ctx).CreateAsync(new OperationInput { Title = "Bestand" }, Leader())).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConvertToOperationAsync(id, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AttachToOperationAsync(id, existing, Leader()));

        Assert.Equal(2, OperationCount(ctx));
        Assert.Equal(first, Stored(ctx, id).OperationId);
        Assert.Single(StoredOperation(ctx, existing).Agents);
    }

    [Fact]
    public async Task Convert_SkipsAgentsNoLongerSelectable()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("gone"), Agent("partner"));
        var (svc, _, _) = Build(ctx);
        var id = await EndedAlarmAsync(svc, Leader(), Input(), ("a1", EmergencyAlarmAnswer.Coming),
            ("gone", EmergencyAlarmAnswer.Coming), ("partner", EmergencyAlarmAnswer.Coming));
        using (var db = ctx.NewContext())
        {
            db.Users.Single(u => u.Id == "gone").Status = AgentStatus.Terminated;
            db.Users.Single(u => u.Id == "partner").PartnerAgency = PartnerAgency.LSPD;
            db.SaveChanges();
        }

        var operationId = await svc.ConvertToOperationAsync(id, Leader());

        Assert.True(Same(StoredOperation(ctx, operationId).Agents.Select(a => a.AgentId).ToList(), "lead", "a1"));
        Assert.Equal(operationId, Stored(ctx, id).OperationId);
    }

    [Fact]
    public async Task Convert_NoComing_StillCreates()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var situation = new string('x', EmergencyAlarmRules.MaxSituation);
        var id = await EndedAlarmAsync(svc, Leader(), Input(situation: situation, location: null),
            ("a1", EmergencyAlarmAnswer.NotComing));

        var operationId = await svc.ConvertToOperationAsync(id, Leader());

        var operation = StoredOperation(ctx, operationId);
        Assert.Equal("lead", Assert.Single(operation.Agents).AgentId);
        Assert.Equal(EmergencyAlarmService.MaxOperationTitle, operation.Title.Length);
        Assert.Null(operation.Location);
    }

    [Fact]
    public async Task Convert_NotTriggererOrLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"), Agent("a1"));
        var (svc, _, _) = Build(ctx);
        var id = await EndedAlarmAsync(svc, Junior(), Input(reason: "Eile"), ("a1", EmergencyAlarmAnswer.Coming));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ConvertToOperationAsync(id, Junior("a1")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ConvertToOperationAsync(id, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ConvertToOperationAsync(id, Partner()));

        Assert.Equal(0, OperationCount(ctx));
        // the triggerer may, even below leadership
        var operationId = await svc.ConvertToOperationAsync(id, Junior());
        Assert.True(Same(StoredOperation(ctx, operationId).Agents.Select(a => a.AgentId).ToList(), "junior", "a1"));
    }

    [Fact]
    public async Task Attach_SkipsAlreadyAllocated()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("lead", Rank.SectionChief), Agent("a1"), Agent("a2"), Agent("a3"));
        var (svc, _, reports) = Build(ctx);
        var operations = Operations(ctx);
        var existing = (await operations.CreateAsync(new OperationInput { Title = "Bestand" }, Leader())).Id;
        await operations.AgentAllocateAsync(existing, "a1", false, Leader());
        var id = await EndedAlarmAsync(svc, Leader(), Input(), ("a1", EmergencyAlarmAnswer.Coming),
            ("a2", EmergencyAlarmAnswer.Coming), ("a3", EmergencyAlarmAnswer.NotComing));
        reports.Clear();

        await svc.AttachToOperationAsync(id, existing, Leader());

        var operation = StoredOperation(ctx, existing);
        Assert.True(Same(operation.Agents.Select(a => a.AgentId).ToList(), "lead", "a1", "a2"));
        Assert.True(operation.Agents.Single(a => a.AgentId == "lead").IsInvestigationLead);
        Assert.Equal(existing, Stored(ctx, id).OperationId);
        Assert.Equal(new[] { id }, reports);
    }

    [Fact]
    public async Task Attach_NotLeadOrLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        Agents(ctx, Agent("junior"), Agent("a1"), Agent("sc", Rank.SectionChief));
        var (svc, _, _) = Build(ctx);
        var operations = Operations(ctx);
        var foreign = (await operations.CreateAsync(new OperationInput { Title = "Fremd" }, Leader("sc"))).Id;
        var own = (await operations.CreateAsync(new OperationInput { Title = "Eigen" }, Junior())).Id;
        var id = await EndedAlarmAsync(svc, Junior(), Input(reason: "Eile"), ("a1", EmergencyAlarmAnswer.Coming));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AttachToOperationAsync(id, foreign, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AttachToOperationAsync(id, own, Junior("a1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AttachToOperationAsync(id, "missing", Junior()));

        Assert.Equal("sc", Assert.Single(StoredOperation(ctx, foreign).Agents).AgentId);
        Assert.Null(Stored(ctx, id).OperationId);
        // as investigation lead of the target it works
        await svc.AttachToOperationAsync(id, own, Junior());
        Assert.True(Same(StoredOperation(ctx, own).Agents.Select(a => a.AgentId).ToList(), "junior", "a1"));
        Assert.Equal(own, Stored(ctx, id).OperationId);
    }
}
