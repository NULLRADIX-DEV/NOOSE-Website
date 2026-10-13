using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Acknowledgment deadline, the one-time reminder and the overdue list of the board.</summary>
public sealed class AcknowledgmentReminderTests
{
    // relative to the clock, because the overdue list reads the real time
    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed record Reminder(List<string> Recipients, string Title, string? Href);

    private static (AnnouncementService Svc, List<Reminder> Sent, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var next = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"NOOSE-N-2026-{++next:0000}");
        var notifications = Substitute.For<INotificationService>();
        var sent = new List<Reminder>();
        notifications
            .When(n => n.NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(), NotificationType.AcknowledgmentReminder,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(ci => sent.Add(new Reminder(ci.ArgAt<IReadOnlyCollection<string>>(0).Order().ToList(),
                ci.ArgAt<string>(2), ci.ArgAt<string?>(3))));
        var svc = new AnnouncementService(ctx.Factory, caseNo, notifications, new AcknowledgmentBroadcaster(),
            NullLogger<AnnouncementService>.Instance);
        return (svc, sent, notifications);
    }

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static Announcement Board(string id, DateTime? due, string createdBy = "lead",
        AnnouncementAudience audience = AnnouncementAudience.AllActive, Action<Announcement>? configure = null)
    {
        var a = new Announcement
        {
            Id = id,
            CaseNumber = "NOOSE-N-2026-" + id,
            Title = "Dienstanweisung " + id,
            Content = string.Empty,
            Audience = audience,
            AcknowledgmentRequired = true,
            AcknowledgmentDueAt = due,
            CreatedAt = Now.AddDays(-5),
            CreatedById = createdBy,
        };
        configure?.Invoke(a);
        return a;
    }

    private static AnnouncementAcknowledgment Ack(string announcementId, string agentId, DateTime? at = null)
        => new() { AnnouncementId = announcementId, AgentId = agentId, AcknowledgedAt = at, CreatedAt = Now.AddDays(-5) };

    private static DateTime? RemindedAt(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        return db.Announcements.Single(a => a.Id == id).AcknowledgmentRemindedAt;
    }

    // ---------- the deadline ----------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(14)]
    public async Task Creating_with_an_acknowledgment_sets_the_chosen_deadline(int days)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead", Rank.Director));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var before = DateTime.UtcNow;
        var created = await svc.CreateAsync(new AnnouncementInput
        {
            Title = "Bitte quittieren",
            AcknowledgmentRequired = true,
            AcknowledgmentDays = days,
        }, Leader());
        var after = DateTime.UtcNow;

        Assert.InRange(created.AcknowledgmentDueAt!.Value, before.AddDays(days), after.AddDays(days));
        Assert.Null(created.AcknowledgmentRemindedAt);
    }

    [Fact]
    public async Task Without_a_reminder_or_an_acknowledgment_there_is_no_deadline()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead", Rank.Director));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var never = await svc.CreateAsync(new AnnouncementInput
        {
            Title = "Ohne Erinnerung", AcknowledgmentRequired = true, AcknowledgmentDays = 0,
        }, Leader());
        // a leftover choice from a switched-off acknowledgment means nothing
        var plain = await svc.CreateAsync(new AnnouncementInput
        {
            Title = "Nur Info", AcknowledgmentRequired = false, AcknowledgmentDays = 7,
        }, Leader());

        Assert.Null(never.AcknowledgmentDueAt);
        Assert.Null(plain.AcknowledgmentDueAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(365)]
    public async Task A_deadline_outside_the_offered_choices_is_refused(int days)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(new AnnouncementInput
        {
            Title = "Bitte quittieren", AcknowledgmentRequired = true, AcknowledgmentDays = days,
        }, Leader()));
    }

    // ---------- the reminder ----------

    [Fact]
    public async Task Nobody_is_reminded_before_the_deadline()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent));
            db.Announcements.Add(Board("soon", Now.AddMinutes(1)));
            db.AnnouncementAcknowledgments.Add(Ack("soon", "a1"));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);

        Assert.Equal(0, await svc.RemindOverdueAsync(Now));

        Assert.Empty(sent);
        Assert.Null(RemindedAt(ctx, "soon"));
    }

    [Fact]
    public async Task After_the_deadline_only_who_still_owes_it_is_reminded_and_only_once()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("a1", Rank.JuniorAgent),
                Seed.Agent("done", Rank.JuniorAgent),
                Seed.Agent("gone", Rank.JuniorAgent, AgentStatus.Terminated),
                Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true),
                Seed.Agent("lspd", Rank.JuniorAgent, configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Announcements.Add(Board("due", Now));
            db.AnnouncementAcknowledgments.AddRange(
                Ack("due", "a1"), Ack("due", "done", Now.AddDays(-1)), Ack("due", "gone"), Ack("due", "sup"),
                Ack("due", "lspd"));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);

        Assert.Equal(1, await svc.RemindOverdueAsync(Now));
        Assert.Equal(0, await svc.RemindOverdueAsync(Now.AddMinutes(5)));

        var reminder = Assert.Single(sent);
        Assert.Equal(["a1"], reminder.Recipients);
        Assert.Equal("/brett/due", reminder.Href);
        Assert.Contains("Dienstanweisung due", reminder.Title);
        Assert.Equal(Now, RemindedAt(ctx, "due"));
    }

    [Fact]
    public async Task When_everyone_answered_the_reminder_is_spent_without_a_notice()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent));
            db.Announcements.Add(Board("done", Now.AddHours(-1)));
            db.AnnouncementAcknowledgments.Add(Ack("done", "a1", Now.AddHours(-2)));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);

        Assert.Equal(0, await svc.RemindOverdueAsync(Now));

        Assert.Empty(sent);
        Assert.Equal(Now, RemindedAt(ctx, "done"));
    }

    [Fact]
    public async Task Without_a_deadline_or_in_the_trash_nothing_is_reminded()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent));
            db.Announcements.AddRange(
                Board("open-ended", due: null),
                Board("trashed", Now.AddDays(-1), configure: a => a.IsDeleted = true));
            db.AnnouncementAcknowledgments.AddRange(Ack("open-ended", "a1"), Ack("trashed", "a1"));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);

        Assert.Equal(0, await svc.RemindOverdueAsync(Now));

        Assert.Empty(sent);
        Assert.Empty(await svc.GetOverdueAcknowledgmentsAsync(Leader()));
        // the claim stays free, so restoring the entry still reminds
        using var check = ctx.NewContext();
        Assert.Null(check.Announcements.IgnoreQueryFilters().Single(a => a.Id == "trashed").AcknowledgmentRemindedAt);
    }

    [Fact]
    public async Task A_failed_notice_spends_only_its_own_claim()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent));
            db.Announcements.AddRange(Board("first", Now.AddHours(-2)), Board("second", Now.AddHours(-1)));
            db.AnnouncementAcknowledgments.AddRange(Ack("first", "a1"), Ack("second", "a1"));
            db.SaveChanges();
        }
        var (svc, sent, notifications) = Build(ctx);
        notifications.NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(), NotificationType.AcknowledgmentReminder,
                Arg.Any<string>(), "/brett/first", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Datenbank weg"));

        Assert.Equal(1, await svc.RemindOverdueAsync(Now));

        Assert.Equal(["/brett/first", "/brett/second"], sent.Select(r => r.Href!).ToArray());
        Assert.Equal(Now, RemindedAt(ctx, "first"));
        Assert.Equal(Now, RemindedAt(ctx, "second"));
    }

    // ---------- who still owes it ----------

    public static TheoryData<AnnouncementAudience> Narrowed =>
        [AnnouncementAudience.Taskforce, AnnouncementAudience.TruUnit, AnnouncementAudience.HrbUnit, AnnouncementAudience.FromRank];

    [Theory]
    [MemberData(nameof(Narrowed))]
    public async Task Leaving_the_audience_ends_the_debt_and_badge_board_and_reminder_agree(AnnouncementAudience audience)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // "stays" is still in the audience, "left" was in it when the entry went up
            var inside = (Action<Agent>)(a =>
            {
                a.IsTRU = audience == AnnouncementAudience.TruUnit;
                a.IsHRB = audience == AnnouncementAudience.HrbUnit;
            });
            db.Users.AddRange(
                Seed.Agent("lead", Rank.Director),
                Seed.Agent("stays", Rank.SpecialAgent, configure: inside),
                Seed.Agent("left", Rank.JuniorAgent));
            db.Taskforces.Add(new Taskforce { Id = "tf", Name = "Taskforce", CaseNumber = "NOOSE-TF-2026-0001", CreatedAt = Now.AddDays(-30) });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf", AgentId = "stays", CreatedAt = Now.AddDays(-30) });
            db.Announcements.Add(Board("due", Now.AddHours(-1), audience: audience, configure: a =>
            {
                a.TargetId = audience == AnnouncementAudience.Taskforce ? "tf" : null;
                a.MinRank = audience == AnnouncementAudience.FromRank ? Rank.SpecialAgent : null;
            }));
            db.AnnouncementAcknowledgments.AddRange(Ack("due", "stays"), Ack("due", "left"));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);
        var stays = ClaimsPrincipalBuilder.Agent("stays").WithRank(Rank.SpecialAgent)
            .AsTru(audience == AnnouncementAudience.TruUnit).AsHrb(audience == AnnouncementAudience.HrbUnit).Build();
        var left = Junior("left");

        await svc.RemindOverdueAsync(Now);

        Assert.Equal(["stays"], Assert.Single(sent).Recipients);
        Assert.Equal(["Codename-stays"], Assert.Single(await svc.GetOverdueAcknowledgmentsAsync(Leader())).Codenames);
        foreach (var (who, owes) in new[] { (stays, 1), (left, 0) })
        {
            var board = await svc.GetBoardAsync(who);
            Assert.Equal(owes, await svc.GetOpenAcknowledgmentsCountAsync(who));
            Assert.Equal(owes, board.Count(r => r.MustAcknowledge));
            Assert.Equal(owes, (await svc.GetOpenAcknowledgmentsAsync(who)).Count);
        }
    }

    [Fact]
    public async Task Leadership_still_owes_what_it_can_read_outside_the_audience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(Seed.Agent("lead", Rank.Director), Seed.Agent("boss", Rank.DeputyDirector));
            db.Taskforces.Add(new Taskforce { Id = "tf", Name = "Taskforce", CaseNumber = "NOOSE-TF-2026-0001", CreatedAt = Now.AddDays(-30) });
            db.Announcements.Add(Board("due", Now.AddHours(-1), audience: AnnouncementAudience.Taskforce,
                configure: a => a.TargetId = "tf"));
            db.AnnouncementAcknowledgments.Add(Ack("due", "boss"));
            db.SaveChanges();
        }
        var (svc, sent, _) = Build(ctx);
        var boss = ClaimsPrincipalBuilder.Agent("boss").WithRank(Rank.DeputyDirector).Build();

        await svc.RemindOverdueAsync(Now);

        // leadership reads every entry, so the board still asks and the reminder still comes
        Assert.Equal(["boss"], Assert.Single(sent).Recipients);
        Assert.Equal(1, await svc.GetOpenAcknowledgmentsCountAsync(boss));
        Assert.True(Assert.Single(await svc.GetBoardAsync(boss)).MustAcknowledge);
    }

    // ---------- the overdue list ----------

    [Fact]
    public async Task The_overdue_list_shows_managers_who_still_owes_latest_deadline_first()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("lead", Rank.Director),
                Seed.Agent("demoted", Rank.JuniorAgent),
                Seed.Agent("zulu", Rank.JuniorAgent, configure: a => a.Codename = "Zulu"),
                Seed.Agent("alpha", Rank.JuniorAgent, configure: a => a.Codename = "Alpha"),
                Seed.Agent("done", Rank.JuniorAgent));
            db.Announcements.AddRange(
                Board("older", Now.AddDays(-2)),
                Board("newer", Now.AddHours(-2)),
                Board("pending", Now.AddDays(1)),
                Board("answered", Now.AddDays(-1)),
                // an author who has since lost the rank still manages their own entry
                Board("own", Now.AddDays(-3), createdBy: "demoted"));
            db.AnnouncementAcknowledgments.AddRange(
                Ack("older", "zulu"), Ack("older", "alpha"), Ack("older", "done", Now.AddDays(-3)),
                Ack("newer", "alpha"),
                Ack("pending", "alpha"),
                Ack("answered", "alpha", Now.AddDays(-2)),
                Ack("own", "zulu"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var leader = await svc.GetOverdueAcknowledgmentsAsync(Leader("lead"));
        var author = await svc.GetOverdueAcknowledgmentsAsync(Junior("demoted"));
        var other = await svc.GetOverdueAcknowledgmentsAsync(Junior("alpha"));

        Assert.Equal(["newer", "older", "own"], leader.Select(o => o.AnnouncementId).ToArray());
        Assert.Equal(["Alpha", "Zulu"], leader[1].Codenames);
        Assert.Equal(["own"], author.Select(o => o.AnnouncementId).ToArray());
        Assert.Empty(other);
    }

    // ---------- rules ----------

    [Fact]
    public void Overdue_starts_at_the_deadline_itself()
    {
        var due = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(AcknowledgmentRules.IsOverdue(due, due.AddTicks(-1)));
        Assert.True(AcknowledgmentRules.IsOverdue(due, due));
        Assert.False(AcknowledgmentRules.IsOverdue(null, due));
        Assert.True(AcknowledgmentRules.IsValid(0));
        Assert.All(AcknowledgmentRules.DayChoices, d => Assert.True(AcknowledgmentRules.IsValid(d)));
        Assert.Contains(AcknowledgmentRules.DefaultDays, AcknowledgmentRules.DayChoices);
    }
}
