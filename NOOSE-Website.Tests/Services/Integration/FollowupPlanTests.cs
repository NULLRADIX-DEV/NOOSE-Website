using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The personal plan, the leadership backlog, reassigning and the escalation of long-overdue follow-ups.</summary>
public sealed class FollowupPlanTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static FollowupService Build(SqliteTestContext ctx, INotificationService? notifications = null)
        => new(ctx.Factory, notifications ?? Substitute.For<INotificationService>());

    private static ClaimsPrincipal Junior(string id = "me")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // read-only supervision: reads everything, writes nothing
    private static ClaimsPrincipal Supervision(string id = "aufsicht")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Partner(string id = "lspd")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static Followup Due(string id, string personId, string? responsible, DateTime dueAt, Action<Followup>? configure = null)
    {
        var f = new Followup
        {
            Id = id,
            EntityType = nameof(Person),
            EntityId = personId,
            ResponsibleAgentId = responsible,
            DueAt = dueAt,
            Note = "Notiz " + id,
        };
        configure?.Invoke(f);
        return f;
    }

    // midnight in Berlin, so a day count never depends on the hour the test runs
    private static DateTime DayStart(int offsetDays)
        => MeetingTime.ToUtc(MeetingTime.Day(Now).AddDays(offsetDays).ToDateTime(TimeOnly.MinValue));

    private static Followup Read(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        return db.Followups.AsNoTracking().Single(f => f.Id == id);
    }

    // ---------- personal plan ----------

    [Fact]
    public async Task The_plan_holds_only_my_open_followups_in_their_buckets()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anna Akte"));
            db.Followups.AddRange(
                Due("late", "p1", "me", Now.AddDays(-3)),
                Due("today", "p1", "me", Now),
                Due("later", "p1", "me", Now.AddDays(30)),
                Due("done", "p1", "me", Now.AddDays(-1), f => f.Done = true),
                Due("theirs", "p1", "other", Now.AddDays(-1)),
                // created by me, but someone else's to work
                Due("handed", "p1", "other", Now.AddDays(-1), f => f.CreatedById = "me"));
            db.SaveChanges();
        }

        var plan = await Build(ctx).GetMyPlanAsync(Junior());

        Assert.Equal(["late", "today", "later"], plan.Items.Select(i => i.Id).ToArray());
        Assert.Equal([FollowupBucket.Overdue, FollowupBucket.Today, FollowupBucket.Later], plan.Items.Select(i => i.Bucket).ToArray());
        Assert.All(plan.Items, i => Assert.Contains("Anna Akte", i.Display));
        Assert.All(plan.Items, i => Assert.Equal("/personen/p1", i.Href));
        Assert.Equal(0, plan.Hidden);
        Assert.False(plan.Capped);
    }

    [Fact]
    public async Task A_followup_on_a_record_I_may_not_see_is_counted_but_never_named()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open", "Offene Akte"));
            db.People.Add(Seed.Person("vs", "Geheime Akte", p => p.IsClassified = true));
            db.Taskforces.Add(new Taskforce { Id = "tf", Name = "Sonderkommando", CaseNumber = "NOOSE-TF-2026-0001" });
            db.Followups.AddRange(
                Due("visible", "open", "me", Now),
                Due("secret", "vs", "me", Now, f => f.Note = "Geheime Notiz"),
                Due("foreign", "tf", "me", Now, f => f.EntityType = nameof(Taskforce)));
            db.SaveChanges();
        }

        var plan = await Build(ctx).GetMyPlanAsync(Junior());

        var item = Assert.Single(plan.Items);
        Assert.Equal("visible", item.Id);
        Assert.Equal(2, plan.Hidden);
        Assert.DoesNotContain(plan.Items, i => i.Display.Contains("Geheime") || i.Display.Contains("Sonderkommando")
            || (i.Note ?? string.Empty).Contains("Geheime"));
    }

    [Fact]
    public async Task Leadership_sees_its_followup_on_a_classified_record_by_name()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("vs", "Geheime Akte", p => p.IsClassified = true));
            db.Followups.Add(Due("secret", "vs", "lead", Now));
            db.SaveChanges();
        }

        var plan = await Build(ctx).GetMyPlanAsync(Leader());

        Assert.Contains("Geheime Akte", Assert.Single(plan.Items).Display);
        Assert.Equal(0, plan.Hidden);
    }

    [Fact]
    public async Task A_followup_on_a_trashed_record_drops_out_of_the_plan()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("gone", configure: p => p.IsDeleted = true));
            db.Followups.Add(Due("orphan", "gone", "me", Now));
            db.SaveChanges();
        }

        var plan = await Build(ctx).GetMyPlanAsync(Junior());

        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.Hidden);
    }

    [Fact]
    public async Task A_partner_has_no_plan()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).GetMyPlanAsync(Partner()));
    }

    // ---------- postpone ----------

    [Fact]
    public async Task Postponing_an_overdue_followup_moves_it_to_tomorrow_and_rearms_both_stamps()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "me", Now.AddDays(-10), f =>
            {
                f.NotifiedAt = Now.AddDays(-10);
                f.EscalatedAt = Now.AddDays(-3);
            }));
            db.SaveChanges();
        }

        await Build(ctx).PostponeAsync("f", 1, Junior());

        var f = Read(ctx, "f");
        Assert.Equal(MeetingTime.Day(Now).AddDays(1), MeetingTime.Day(f.DueAt));
        Assert.Null(f.NotifiedAt);
        Assert.Null(f.EscalatedAt);
    }

    [Fact]
    public async Task Postponing_a_future_followup_by_a_week_counts_from_its_own_day()
    {
        using var ctx = new SqliteTestContext();
        var due = Now.AddDays(3);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "me", due));
            db.SaveChanges();
        }

        await Build(ctx).PostponeAsync("f", 7, Junior());

        Assert.Equal(MeetingTime.Day(due).AddDays(7), MeetingTime.Day(Read(ctx, "f").DueAt));
    }

    [Fact]
    public async Task The_read_only_supervision_is_refused_before_anything_is_looked_up()
    {
        using var ctx = new SqliteTestContext();

        // the id does not exist: a write guard behind the lookup would answer "nicht gefunden" instead
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).PostponeAsync("missing", 1, Supervision()));
        Assert.Contains("Nur-Lese", ex.Message);
    }

    [Fact]
    public async Task Postponing_someone_elses_followup_is_refused()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "other", Now, f => f.CreatedById = "other"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).PostponeAsync("f", 1, Junior()));
    }

    [Fact]
    public async Task Postponing_a_followup_on_a_record_I_may_not_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("vs", configure: p => p.IsClassified = true));
            db.Followups.Add(Due("f", "vs", "me", Now));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).PostponeAsync("f", 1, Junior()));
        Assert.Equal(MeetingTime.Day(Now), MeetingTime.Day(Read(ctx, "f").DueAt));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task Only_a_day_or_a_week_is_accepted(int days)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "me", Now));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).PostponeAsync("f", days, Junior()));
    }

    [Fact]
    public async Task A_done_followup_is_not_postponed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "me", Now, f => f.Done = true));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).PostponeAsync("f", 1, Junior()));
    }

    [Fact]
    public async Task Editing_the_due_date_rearms_the_escalation_and_editing_the_note_does_not()
    {
        using var ctx = new SqliteTestContext();
        var due = Now.AddDays(-10);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "me", due, f => f.EscalatedAt = Now));
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var stored = Read(ctx, "f").DueAt;

        await svc.RefreshAsync("f", new FollowupInput(DateTime.SpecifyKind(stored, DateTimeKind.Utc), "anders", "me"), Junior());
        Assert.NotNull(Read(ctx, "f").EscalatedAt);

        await svc.RefreshAsync("f", new FollowupInput(DateTime.SpecifyKind(stored, DateTimeKind.Utc).AddDays(20), "anders", "me"), Junior());
        Assert.Null(Read(ctx, "f").EscalatedAt);
    }

    // ---------- leadership backlog ----------

    [Fact]
    public async Task The_backlog_is_for_leadership_and_supervision_only()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetBacklogAsync(Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetBacklogAsync(Partner()));
        Assert.Empty((await svc.GetBacklogAsync(Supervision())).Items);
        Assert.Empty((await svc.GetBacklogAsync(Leader())).Items);
    }

    [Fact]
    public async Task The_backlog_lists_every_open_followup_oldest_first_with_its_agent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("a1", Rank.JuniorAgent),
                Seed.Agent("gone", Rank.JuniorAgent, AgentStatus.Terminated));
            db.People.Add(Seed.Person("p1", "Anna Akte"));
            db.Followups.AddRange(
                Due("fresh", "p1", "a1", Now.AddDays(2)),
                Due("old", "p1", "a1", DayStart(-9), f => f.EscalatedAt = Now.AddDays(-2)),
                Due("left", "p1", "gone", Now.AddDays(-4)),
                Due("nobody", "p1", null, Now.AddDays(-1)),
                Due("done", "p1", "a1", Now.AddDays(-20), f => f.Done = true));
            db.SaveChanges();
        }

        var backlog = await Build(ctx).GetBacklogAsync(Leader());

        Assert.Equal(["old", "left", "nobody", "fresh"], backlog.Items.Select(i => i.Id).ToArray());
        var old = backlog.Items[0];
        Assert.Equal("Codename-a1", old.ResponsibleCodename);
        Assert.True(old.ResponsibleSelectable);
        Assert.NotNull(old.EscalatedAt);
        Assert.Equal(9, old.DaysOverdue);
        Assert.Contains("Anna Akte", old.Display);
        Assert.Equal("Notiz old", old.Note);
        Assert.False(backlog.Items[1].ResponsibleSelectable);
        Assert.Null(backlog.Items[2].ResponsibleCodename);
        Assert.False(backlog.Items[2].ResponsibleSelectable);
        Assert.True(backlog.Items[3].DaysOverdue < 0);
    }

    [Fact]
    public async Task A_backlog_row_on_a_record_the_viewer_may_not_see_carries_no_name_link_or_note()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent));
            db.Documents.Add(new Document { Id = "d1", Title = "Gesperrtes Dokument" });
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { Id = "x1", DocumentId = "d1", AgentId = "lead" });
            db.Followups.Add(Due("f", "d1", "a1", Now.AddDays(-2), f =>
            {
                f.EntityType = nameof(Document);
                f.Note = "Geheime Notiz";
            }));
            db.SaveChanges();
        }

        var row = Assert.Single((await Build(ctx).GetBacklogAsync(Leader())).Items);

        Assert.False(row.RecordVisible);
        Assert.Null(row.Display);
        Assert.Null(row.Href);
        Assert.Null(row.RecordLabel);
        Assert.Null(row.Note);
        Assert.Equal("Codename-a1", row.ResponsibleCodename);
    }

    // ---------- reassign ----------

    private static void SeedReassign(SqliteTestContext ctx, Action<Data.AppDbContext>? more = null)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead", Rank.Director),
            Seed.Agent("from", Rank.JuniorAgent),
            Seed.Agent("to", Rank.JuniorAgent));
        db.People.Add(Seed.Person("p1", "Anna Akte"));
        db.Followups.Add(Due("f", "p1", "from", Now.AddDays(-8), f => f.EscalatedAt = Now));
        more?.Invoke(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Leadership_hands_a_followup_on_and_the_new_agent_is_told()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx);
        var notifications = Substitute.For<INotificationService>();

        await Build(ctx, notifications).ReassignAsync("f", "to", Leader());

        var f = Read(ctx, "f");
        Assert.Equal("to", f.ResponsibleAgentId);
        // a new hand does not reset the escalation; only a new due date does
        Assert.NotNull(f.EscalatedAt);
        await notifications.Received(1).NotifyAsync("to", NotificationType.Followup,
            Arg.Is<string>(t => t.Contains("Anna Akte")), "/personen/p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Taking_a_followup_oneself_notifies_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx);
        var notifications = Substitute.For<INotificationService>();

        await Build(ctx, notifications).ReassignAsync("f", "lead", Leader());

        Assert.Equal("lead", Read(ctx, "f").ResponsibleAgentId);
        await notifications.DidNotReceiveWithAnyArgs().NotifyAsync(default, default, default!, default);
    }

    [Fact]
    public async Task The_read_only_supervision_cannot_reassign_and_is_refused_as_such()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).ReassignAsync("f", "to", Supervision()));

        Assert.Contains("Nur-Lese", ex.Message);
        Assert.Equal("from", Read(ctx, "f").ResponsibleAgentId);
    }

    [Fact]
    public async Task A_regular_agent_cannot_reassign()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).ReassignAsync("f", "to", Junior("from")));
        Assert.Equal("from", Read(ctx, "f").ResponsibleAgentId);
    }

    [Theory]
    [InlineData("terminated")]
    [InlineData("teamlead")]
    [InlineData("partner")]
    [InlineData("unknown")]
    public async Task Only_a_selectable_agent_takes_a_followup(string target)
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx, db => db.Users.AddRange(
            Seed.Agent("terminated", Rank.JuniorAgent, AgentStatus.Terminated),
            Seed.Agent("teamlead", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).ReassignAsync("f", target, Leader()));
        Assert.Equal("from", Read(ctx, "f").ResponsibleAgentId);
    }

    [Fact]
    public async Task Nobody_takes_a_followup_on_a_record_they_may_not_see()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx, db =>
        {
            db.People.Add(Seed.Person("vs", "Geheime Akte", p => p.IsClassified = true));
            db.Followups.Add(Due("secret", "vs", "lead", Now.AddDays(-8)));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).ReassignAsync("secret", "to", Leader()));
        Assert.Equal("lead", Read(ctx, "secret").ResponsibleAgentId);
    }

    [Fact]
    public async Task A_leader_cannot_reassign_a_followup_on_a_record_hidden_from_them()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx, db =>
        {
            db.Documents.Add(new Document { Id = "d1", Title = "Gesperrtes Dokument" });
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { Id = "x1", DocumentId = "d1", AgentId = "lead" });
            db.Followups.Add(Due("doc", "d1", "from", Now.AddDays(-8), f => f.EntityType = nameof(Document)));
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).ReassignAsync("doc", "to", Leader()));
        Assert.Equal("from", Read(ctx, "doc").ResponsibleAgentId);
    }

    [Fact]
    public async Task A_done_followup_is_not_reassigned()
    {
        using var ctx = new SqliteTestContext();
        SeedReassign(ctx, db => db.Followups.Add(Due("closed", "p1", "from", Now.AddDays(-8), f => f.Done = true)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).ReassignAsync("closed", "to", Leader()));
    }

    // ---------- escalation ----------

    private static void SeedLeadership(Data.AppDbContext db)
    {
        db.Users.AddRange(
            Seed.Agent("lead", Rank.Director),
            Seed.Agent("admin", Rank.JuniorAgent, configure: a => a.IsAdmin = true),
            Seed.Agent("junior", Rank.JuniorAgent),
            Seed.Agent("teamlead", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Seed.Agent("terminated", Rank.Director, AgentStatus.Terminated),
            Seed.Agent(DemoIdentity.AgentId, Rank.Director));
    }

    [Fact]
    public async Task A_long_overdue_followup_is_escalated_once_to_leadership_only()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedLeadership(db);
            db.People.Add(Seed.Person("p1", "Anna Akte"));
            db.Followups.AddRange(
                Due("rotten", "p1", "junior", Now.AddDays(-(FollowupPlanRules.EscalateAfterDays + 1))),
                Due("recent", "p1", "junior", Now.AddDays(-(FollowupPlanRules.EscalateAfterDays - 2))),
                Due("done", "p1", "junior", Now.AddDays(-30), f => f.Done = true));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = Build(ctx, notifications);

        Assert.Equal(1, await svc.EscalateOverdueAsync(Now));
        Assert.Equal(0, await svc.EscalateOverdueAsync(Now));

        Assert.NotNull(Read(ctx, "rotten").EscalatedAt);
        Assert.Null(Read(ctx, "recent").EscalatedAt);
        Assert.Null(Read(ctx, "done").EscalatedAt);
        await notifications.Received(1).NotifyManyOnceAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.OrderBy(x => x).SequenceEqual(new[] { "admin", "lead" })),
            NotificationType.FollowupEscalated, FollowupPlanRules.EscalationTitle, FollowupPlanRules.BacklogRoute,
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_escalation_names_neither_the_record_nor_the_agent()
    {
        Assert.DoesNotContain("Codename", FollowupPlanRules.EscalationTitle);
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedLeadership(db);
            db.People.Add(Seed.Person("vs", "Geheime Akte", p => p.IsClassified = true));
            db.Followups.Add(Due("rotten", "vs", "junior", Now.AddDays(-20)));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();

        await Build(ctx, notifications).EscalateOverdueAsync(Now);

        await notifications.Received(1).NotifyManyOnceAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.FollowupEscalated, Arg.Is<string>(t => !t.Contains("Geheime") && !t.Contains("junior")),
            Arg.Is<string?>(h => h == FollowupPlanRules.BacklogRoute), Arg.Is<string?>(t => t == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_followup_on_a_trashed_record_is_stamped_without_a_notice()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedLeadership(db);
            db.People.Add(Seed.Person("gone", configure: p => p.IsDeleted = true));
            db.Followups.Add(Due("orphan", "gone", "junior", Now.AddDays(-20)));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();

        Assert.Equal(0, await Build(ctx, notifications).EscalateOverdueAsync(Now));

        Assert.NotNull(Read(ctx, "orphan").EscalatedAt);
        await notifications.DidNotReceiveWithAnyArgs().NotifyManyOnceAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task A_postponed_followup_can_escalate_again_once_it_is_long_overdue_again()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedLeadership(db);
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(Due("f", "p1", "junior", Now.AddDays(-20)));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        Assert.Equal(1, await svc.EscalateOverdueAsync(Now));
        await svc.PostponeAsync("f", 1, Junior("junior"));
        Assert.Null(Read(ctx, "f").EscalatedAt);

        // not yet: the new due date is tomorrow
        Assert.Equal(0, await svc.EscalateOverdueAsync(Now));
        Assert.Equal(1, await svc.EscalateOverdueAsync(Now.AddDays(FollowupPlanRules.EscalateAfterDays + 2)));
    }

    // ---------- shared recipient rule ----------

    [Fact]
    public void The_reminder_scope_reads_leadership_from_rank_or_admin()
    {
        Assert.True(FollowupService.ReminderScope("a", false, Rank.SupervisorySpecialAgent, null).MayClassifiedRead);
        Assert.True(FollowupService.ReminderScope("a", true, Rank.JuniorAgent, null).MayClassifiedRead);
        Assert.False(FollowupService.ReminderScope("a", false, Rank.SeniorSpecialAgent, null).MayClassifiedRead);
        Assert.False(FollowupService.ReminderScope("a", false, null, PartnerAgency.LSPD).IsInternalAgent);
    }
}
