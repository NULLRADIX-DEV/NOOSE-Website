using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The inactivity radar: who is on it, how the silence is counted, the nudge and the page-visit stamp.</summary>
public sealed class InactivityRadarTests
{
    // relative to the clock, because the radar reads the real time; the day is read per call, not per class,
    // so a run across Berlin midnight compares like with like
    private static readonly DateTime Now = DateTime.UtcNow;
    private static DateOnly Today => MeetingTime.Day(DateTime.UtcNow);

    private static (InactivityRadarService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new InactivityRadarService(ctx.Factory, notifications), notifications);
    }

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Chef").Build();

    private static ClaimsPrincipal Junior(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    /// <summary>An active agent whose only trace is a page visit the given number of days ago.</summary>
    private static Agent Silent(string id, int days, Action<Agent>? configure = null)
        => Seed.Agent(id, Rank.SpecialAgent, configure: a =>
        {
            a.LastSeenAt = Now.AddDays(-days);
            configure?.Invoke(a);
        });

    private static void Add(SqliteTestContext ctx, params object[] rows)
    {
        using var db = ctx.NewContext();
        db.AddRange(rows);
        db.SaveChanges();
    }

    private static int DaysSince(DateTime utc) => Today.DayNumber - MeetingTime.Day(utc).DayNumber;

    [Fact]
    public async Task Only_selectable_agents_silent_past_the_first_threshold_are_listed()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx,
            Seed.Agent("lead", Rank.Director, configure: a => a.LastSeenAt = Now),
            Silent("silent", 20),
            Silent("lead-team", 40, a => a.IsTeamLead = true),
            Silent("partner", 40, a => a.PartnerAgency = PartnerAgency.LSPD),
            Silent("blocked", 40, a => a.Status = AgentStatus.Blocked),
            Silent("gone", 40, a => a.Status = AgentStatus.Terminated),
            Silent(DemoIdentity.AgentId, 40),
            Silent("recent", 5),
            // released three days ago and never seen since: new, not inactive
            Seed.Agent("new", Rank.JuniorAgent, configure: a => a.ReleasedAt = Now.AddDays(-3)));
        var (svc, _) = Build(ctx);

        var radar = await svc.GetAsync(Leader());

        Assert.Equal(["silent"], radar.Rows.Select(r => r.AgentId));
        Assert.Equal(InactivityThresholds.Default, radar.Thresholds);
    }

    [Fact]
    public async Task The_latest_of_write_read_and_visit_is_the_trace()
    {
        using var ctx = new SqliteTestContext();
        var write = Now.AddDays(-50);
        var read = Now.AddDays(-35);
        var seen = Now.AddDays(-40);
        Add(ctx,
            Seed.Agent("a", Rank.SpecialAgent, configure: a => a.LastSeenAt = seen),
            new AuditLog { AgentId = "a", Timestamp = write, EntityType = "Person", EntityId = "p1", Action = AuditAction.Created },
            new AccessLog { AgentId = "a", Timestamp = read, EntityType = "Person", EntityId = "p1" },
            // another agent's newer rows never count for "a"
            new AccessLog { AgentId = "b", Timestamp = Now, EntityType = "Person", EntityId = "p1" });
        var (svc, _) = Build(ctx);

        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);

        Assert.Equal(InactivityTraceKind.Read, row.LastTraceKind);
        Assert.Equal(read, row.LastTraceAt, TimeSpan.FromSeconds(1));
        Assert.Equal(write, row.LastWriteAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(DaysSince(read), row.InactiveDays);
        Assert.Equal(InactivityTier.Note, row.Tier);
    }

    [Fact]
    public async Task Absence_days_are_excused_and_a_running_absence_is_shown()
    {
        using var ctx = new SqliteTestContext();
        var seen = Now.AddDays(-50);
        Add(ctx,
            Seed.Agent("a", Rank.SpecialAgent, configure: a => a.LastSeenAt = seen),
            new Absence { AgentId = "a", FromDate = Today.AddDays(-10), ToDate = Today.AddDays(5), Days = 16 },
            // deleted absences excuse nothing
            new Absence { AgentId = "a", FromDate = Today.AddDays(-30), ToDate = Today.AddDays(-20), Days = 11, IsDeleted = true });
        var (svc, _) = Build(ctx);

        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);

        Assert.Equal(DaysSince(seen), row.SilentDays);
        Assert.Equal(11, row.AbsentDays);
        Assert.Equal(row.SilentDays - 11, row.InactiveDays);
        Assert.Equal(Today.AddDays(5), row.AbsentUntil);
    }

    [Fact]
    public async Task An_absence_covering_the_silence_keeps_an_agent_off_the_radar()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx,
            Silent("a", 20),
            new Absence { AgentId = "a", FromDate = Today.AddDays(-25), ToDate = Today.AddDays(3), Days = 29 });
        var (svc, _) = Build(ctx);

        Assert.Empty((await svc.GetAsync(Leader())).Rows);
    }

    [Fact]
    public async Task The_radar_is_for_leadership_and_real_names_only_for_who_may_see_them()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Silent("a", 20, a => a.RealName = "Max Muster"));
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAsync(Junior("j")));
        Assert.Equal("Max Muster", Assert.Single((await svc.GetAsync(Leader())).Rows).RealName);
        var oversight = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();
        Assert.Null(Assert.Single((await svc.GetAsync(oversight)).Rows).RealName);
    }

    [Fact]
    public async Task A_nudge_stamps_the_row_logs_against_the_file_and_rings_the_agent()
    {
        using var ctx = new SqliteTestContext();
        var seen = Now.AddDays(-20);
        Add(ctx, Seed.Agent("lead", Rank.Director), Silent("a", 20, a => a.LastSeenAt = seen));
        var (svc, notifications) = Build(ctx);

        await svc.NudgeAsync("a", Leader());

        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a" })),
            NotificationType.InactivityNudge,
            Arg.Is<string>(t => t.Contains(MeetingTime.Day(seen).ToString("dd.MM.yyyy"))),
            InactivityRadarService.NudgeHref, "lead", Arg.Any<CancellationToken>());
        using (var db = ctx.NewContext())
        {
            var agent = db.Users.Single(u => u.Id == "a");
            Assert.NotNull(agent.InactivityNudgedAt);
            Assert.Equal("Chef", agent.InactivityNudgedByName);
            var log = Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Agent) && l.EntityId == "a"));
            Assert.Equal("lead", log.AgentId);
        }
        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);
        Assert.NotNull(row.NudgedAt);
        Assert.Equal("Chef", row.NudgedByName);
    }

    [Fact]
    public async Task A_nudge_older_than_the_last_trace_is_forgotten()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Silent("a", 20, a =>
        {
            a.InactivityNudgedAt = Now.AddDays(-40);
            a.InactivityNudgedByName = "Chef";
        }));
        var (svc, _) = Build(ctx);

        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);

        Assert.Null(row.NudgedAt);
        Assert.Null(row.NudgedByName);
    }

    [Fact]
    public async Task A_nudge_checks_write_access_before_rank_and_refuses_who_is_not_on_the_radar()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx,
            Silent("a", 20),
            Silent("tl", 40, a => a.IsTeamLead = true),
            Seed.Agent("active", Rank.SpecialAgent, configure: a => a.LastSeenAt = Now));
        var (svc, notifications) = Build(ctx);
        // below leadership AND read-only: only the write guard running first names the read-only mode
        var oversight = ClaimsPrincipalBuilder.Agent("tl2").WithRank(Rank.JuniorAgent).AsTeamLead().Build();
        var demo = ClaimsPrincipalBuilder.Agent("d").WithRank(Rank.JuniorAgent).AsDemo().Build();

        Assert.Contains("Nur-Lese",
            (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.NudgeAsync("a", oversight))).Message);
        Assert.Contains("Nur-Lese",
            (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.NudgeAsync("a", demo))).Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.NudgeAsync("a", Junior("j")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NudgeAsync("lead", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NudgeAsync("active", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NudgeAsync("tl", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NudgeAsync("unknown", Leader()));

        await notifications.DidNotReceiveWithAnyArgs().NotifyManyAsync(default!, default, default!, default, default);
        using var db = ctx.NewContext();
        Assert.All(db.Users.ToList(), u => Assert.Null(u.InactivityNudgedAt));
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task A_page_visit_is_stamped_at_most_hourly_and_only_for_internal_writers()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx,
            Seed.Agent("a", Rank.SpecialAgent),
            Seed.Agent("p", Rank.SpecialAgent, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Seed.Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true));
        var (svc, _) = Build(ctx);
        var agent = Junior("a");

        await svc.StampSeenAsync(agent);
        var first = SeenAt(ctx, "a");
        Assert.NotNull(first);

        await SetSeenAsync(ctx, "a", Now.AddMinutes(-30));
        await svc.StampSeenAsync(agent);
        Assert.Equal(Now.AddMinutes(-30), SeenAt(ctx, "a")!.Value, TimeSpan.FromSeconds(1));

        await SetSeenAsync(ctx, "a", Now.AddHours(-2));
        await svc.StampSeenAsync(agent);
        Assert.True(SeenAt(ctx, "a") > Now.AddMinutes(-1));

        await svc.StampSeenAsync(ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build());
        await svc.StampSeenAsync(ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build());
        Assert.Null(SeenAt(ctx, "p"));
        Assert.Null(SeenAt(ctx, "tl"));
    }

    [Fact]
    public async Task Saving_an_account_never_writes_back_a_stale_stamp()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Seed.Agent("a", Rank.SpecialAgent));
        var (svc, _) = Build(ctx);

        // a long-lived context holds the account from before the visit, as UserManager's does
        using var stale = ctx.NewContext();
        var copy = stale.Users.Single(u => u.Id == "a");
        await svc.StampSeenAsync(Junior("a"));
        var stamped = SeenAt(ctx, "a");

        copy.Codename = "Neu";
        stale.Update(copy);
        await stale.SaveChangesAsync();

        Assert.NotNull(stamped);
        Assert.Equal(stamped, SeenAt(ctx, "a"));
        using var db = ctx.NewContext();
        Assert.Equal("Neu", db.Users.Single(u => u.Id == "a").Codename);
    }

    [Fact]
    public async Task Thresholds_round_trip_behind_both_guards_and_leave_a_log_row()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Silent("a", 10));
        var (svc, _) = Build(ctx);
        var oversight = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.JuniorAgent).AsTeamLead().Build();

        Assert.Contains("Nur-Lese", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.SaveThresholdsAsync(new InactivityThresholds(7, 14, 30), oversight))).Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.SaveThresholdsAsync(new InactivityThresholds(7, 14, 30), Junior("j")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SaveThresholdsAsync(new InactivityThresholds(30, 20, 60), Leader()));
        Assert.Empty((await svc.GetAsync(Leader())).Rows);

        await svc.SaveThresholdsAsync(new InactivityThresholds(7, 14, 30), Leader());

        Assert.Equal(new InactivityThresholds(7, 14, 30), await svc.GetThresholdsAsync());
        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);
        Assert.Equal(InactivityTier.FollowUp, row.Tier);
        using var db = ctx.NewContext();
        Assert.Single(db.AuditLogs.Where(l => l.EntityType == InactivityRadarService.AuditType));
    }

    [Theory]
    [InlineData("{\"FollowUpDays\":30,\"NoteDays\":20}")]
    [InlineData("{nicht json")]
    public async Task An_invalid_or_unreadable_stored_threshold_falls_back_to_the_defaults(string stored)
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, new SystemSetting { Key = "InaktivitaetSchwellen", Value = stored });
        var (svc, _) = Build(ctx);

        Assert.Equal(InactivityThresholds.Default, await svc.GetThresholdsAsync());
    }

    [Fact]
    public async Task A_second_nudge_within_a_day_is_refused_and_rings_nothing()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Silent("a", 20));
        var (svc, notifications) = Build(ctx);

        await svc.NudgeAsync("a", Leader());
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NudgeAsync("a", Leader("lead2")));

        Assert.Contains("24 Stunden", again.Message);
        await notifications.Received(1).NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.InactivityNudge, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        using var db = ctx.NewContext();
        Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Agent)));
    }

    [Fact]
    public async Task A_reactivated_agent_is_counted_from_the_reactivation()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx,
            Silent("back", 100, a => a.ActiveSinceAt = Now.AddDays(-3)),
            Silent("back-long", 100, a => a.ActiveSinceAt = Now.AddDays(-20)));
        var (svc, _) = Build(ctx);

        var row = Assert.Single((await svc.GetAsync(Leader())).Rows);

        Assert.Equal("back-long", row.AgentId);
        Assert.Equal(InactivityTraceKind.Reactivated, row.LastTraceKind);
        Assert.Equal(InactivityTier.FollowUp, row.Tier);
    }

    [Fact]
    public async Task A_single_row_is_fresh_and_null_once_the_agent_is_back()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, Silent("a", 40), Seed.Agent("active", Rank.SpecialAgent, configure: a => a.LastSeenAt = Now));
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetRowAsync("a", Junior("j")));
        Assert.Equal(InactivityTier.Note, (await svc.GetRowAsync("a", Leader()))!.Tier);
        Assert.Null(await svc.GetRowAsync("active", Leader()));

        await svc.StampSeenAsync(Junior("a"));
        Assert.Null(await svc.GetRowAsync("a", Leader()));
    }

    private static DateTime? SeenAt(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        return db.Users.AsNoTracking().Single(u => u.Id == id).LastSeenAt;
    }

    private static async Task SetSeenAsync(SqliteTestContext ctx, string id, DateTime at)
    {
        await using var db = ctx.NewContext();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, (DateTime?)at));
    }
}
