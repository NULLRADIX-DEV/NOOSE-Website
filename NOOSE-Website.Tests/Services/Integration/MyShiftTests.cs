using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Models.Shift;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>"Meine Schicht": the visit window and the per-agent queries the page aggregates.</summary>
public sealed class MyShiftTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Junior(string id = "me")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).WithCodename("Rookie").Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falcon").Build();

    // ---------- visit window ----------

    [Fact]
    public void A_first_look_shows_the_last_day()
    {
        var prefs = new NavPreferences();

        var window = ShiftRules.Stamp(prefs, T0);

        Assert.Equal(new ShiftWindow(T0 - ShiftRules.FirstLookBack, ShiftWindowKind.FirstVisit), window);
        Assert.Equal(T0, prefs.ShiftSeenUtc);
        Assert.Null(prefs.ShiftBoundaryUtc);
    }

    [Fact]
    public void A_reload_inside_the_visit_keeps_the_boundary()
    {
        var prefs = new NavPreferences();
        ShiftRules.Stamp(prefs, T0);
        ShiftRules.Stamp(prefs, T0.AddHours(3));

        var window = ShiftRules.Stamp(prefs, T0.AddHours(3).AddMinutes(10));

        Assert.Equal(new ShiftWindow(T0, ShiftWindowKind.SinceLastVisit), window);
        Assert.Equal(T0.AddHours(3).AddMinutes(10), prefs.ShiftSeenUtc);
    }

    [Fact]
    public void A_new_visit_starts_at_the_last_look_of_the_previous_one()
    {
        var prefs = new NavPreferences();
        ShiftRules.Stamp(prefs, T0);
        // the previous visit lasted a while: its last look is the boundary, not its first
        ShiftRules.Stamp(prefs, T0.AddMinutes(20));
        ShiftRules.Stamp(prefs, T0.AddMinutes(40));

        var window = ShiftRules.Stamp(prefs, T0.AddHours(5));

        Assert.Equal(new ShiftWindow(T0.AddMinutes(40), ShiftWindowKind.SinceLastVisit), window);
    }

    [Fact]
    public void A_long_absence_is_capped_at_a_week()
    {
        var prefs = new NavPreferences();
        ShiftRules.Stamp(prefs, T0);
        var now = T0.AddDays(30);

        var window = ShiftRules.Stamp(prefs, now);

        Assert.Equal(new ShiftWindow(now - ShiftRules.MaxLookBack, ShiftWindowKind.Capped), window);
    }

    [Fact]
    public void An_older_look_from_a_second_tab_moves_nothing_back()
    {
        var prefs = new NavPreferences();
        ShiftRules.Stamp(prefs, T0);
        ShiftRules.Stamp(prefs, T0.AddHours(2));

        var window = ShiftRules.Stamp(prefs, T0.AddHours(2).AddMinutes(-5));

        Assert.Equal(T0.AddHours(2), prefs.ShiftSeenUtc);
        Assert.Equal(new ShiftWindow(T0, ShiftWindowKind.SinceLastVisit), window);
    }

    [Fact]
    public async Task The_stamp_survives_a_new_circuit()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
            db.SaveChanges();
        }

        var first = await new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()))
            .StampShiftVisitAsync("me", T0);
        // a fresh cache reads the row, so the stamp is in the database and not only in memory
        var second = await new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()))
            .StampShiftVisitAsync("me", T0.AddDays(1));

        Assert.Equal(ShiftWindowKind.FirstVisit, first.Kind);
        Assert.Equal(new ShiftWindow(T0, ShiftWindowKind.SinceLastVisit), second);
    }

    // ---------- jobs ----------

    private static JobService Jobs(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<ICaseNumberService>(), Substitute.For<INotificationService>());

    private static Job Job(string id, JobStatus status = JobStatus.Open, DateTime? due = null,
        JobPriority priority = JobPriority.Normal, string? creator = "boss", bool restricted = false)
        => new()
        {
            Id = id,
            Title = "Aufgabe " + id,
            CaseNumber = "NOOSE-A-2026-" + id,
            CreatedById = creator,
            CreatedAt = T0,
            Status = status,
            DueDate = due,
            Priority = priority,
            IsRestricted = restricted,
        };

    private static JobAssignment Assign(string jobId, string agentId)
        => new() { JobId = jobId, AgentId = agentId, CreatedAt = T0 };

    [Fact]
    public async Task My_open_jobs_are_the_open_ones_assigned_to_me()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
            db.Users.Add(Seed.Agent("other", Rank.JuniorAgent));
            db.Users.Add(Seed.Agent("boss", Rank.Director));
            db.Jobs.AddRange(
                Job("open"), Job("working", JobStatus.InProcessing), Job("done", JobStatus.Done),
                Job("aborted", JobStatus.Aborted), Job("theirs"), Job("created", creator: "me"),
                Job("restricted", restricted: true));
            db.JobAssignments.AddRange(
                Assign("open", "me"), Assign("working", "me"), Assign("done", "me"), Assign("aborted", "me"),
                Assign("theirs", "other"), Assign("restricted", "me"));
            db.SaveChanges();
        }

        var rows = await Jobs(ctx).GetMyOpenAsync(Junior());

        // created but not assigned stays on the board, not on the shift
        Assert.Equal(["open", "restricted", "working"], rows.Select(r => r.Id).Order().ToArray());
    }

    [Fact]
    public async Task My_open_jobs_put_the_soonest_due_first_and_undated_last()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
            db.Users.Add(Seed.Agent("boss", Rank.Director));
            db.Jobs.AddRange(
                Job("undated-high", priority: JobPriority.High),
                Job("later", due: new DateTime(2026, 10, 20)),
                Job("sooner-low", due: new DateTime(2026, 10, 14), priority: JobPriority.Low),
                Job("sooner-high", due: new DateTime(2026, 10, 14), priority: JobPriority.High));
            db.JobAssignments.AddRange(Assign("undated-high", "me"), Assign("later", "me"),
                Assign("sooner-low", "me"), Assign("sooner-high", "me"));
            db.SaveChanges();
        }

        var rows = await Jobs(ctx).GetMyOpenAsync(Junior());

        Assert.Equal(["sooner-high", "sooner-low", "later", "undated-high"], rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Without_an_agent_id_there_are_no_jobs()
    {
        using var ctx = new SqliteTestContext();

        Assert.Empty(await Jobs(ctx).GetMyOpenAsync(ClaimsPrincipalBuilder.Anonymous()));
    }

    // ---------- follow-ups ----------

    [Fact]
    public async Task Followups_until_an_instant_include_the_upcoming_ones()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.AddRange(
                new Followup { EntityType = "Person", EntityId = "p1", ResponsibleAgentId = "me", DueAt = now.AddDays(-1), Note = "due" },
                new Followup { EntityType = "Person", EntityId = "p1", ResponsibleAgentId = "me", DueAt = now.AddDays(3), Note = "soon" },
                new Followup { EntityType = "Person", EntityId = "p1", ResponsibleAgentId = "me", DueAt = now.AddDays(10), Note = "later" });
            db.SaveChanges();
        }
        var svc = new FollowupService(ctx.Factory, Substitute.For<INotificationService>());

        var week = await svc.GetMineUntilAsync(Junior(), now + ShiftRules.UpcomingFollowups);
        var due = await svc.GetMyDueAsync(Junior());

        Assert.Equal(["due", "soon"], week.Select(f => f.Note!).ToArray());
        Assert.Equal(["due"], due.Select(f => f.Note!).ToArray());
    }

    // ---------- acknowledgments ----------

    private static AnnouncementService Announcements(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<ICaseNumberService>(), Substitute.For<INotificationService>(),
            new AcknowledgmentBroadcaster(), NullLogger<AnnouncementService>.Instance);

    private static Announcement Announcement(string id, bool required = true, bool important = false, int day = 1)
        => new()
        {
            Id = id,
            CaseNumber = "NOOSE-N-2026-" + id,
            Title = "Eintrag " + id,
            Content = string.Empty,
            Audience = AnnouncementAudience.AllActive,
            AcknowledgmentRequired = required,
            Important = important,
            CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        };

    private static AnnouncementAcknowledgment Ack(string announcementId, DateTime? at = null)
        => new() { AnnouncementId = announcementId, AgentId = "me", AcknowledgedAt = at, CreatedAt = T0 };

    [Fact]
    public async Task Open_acknowledgments_list_what_the_badge_counts_important_first()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
            db.Announcements.AddRange(
                Announcement("old", day: 1), Announcement("new", day: 5), Announcement("urgent", important: true, day: 9),
                Announcement("acked"), Announcement("optional", required: false));
            db.AnnouncementAcknowledgments.AddRange(Ack("old"), Ack("new"), Ack("urgent"), Ack("acked", T0), Ack("optional"));
            db.SaveChanges();
        }
        var svc = Announcements(ctx);

        var open = await svc.GetOpenAcknowledgmentsAsync(Junior());

        Assert.Equal(["urgent", "old", "new"], open.Select(a => a.AnnouncementId).ToArray());
        Assert.Equal(open.Count, await svc.GetOpenAcknowledgmentsCountAsync(Junior()));
    }

    // ---------- taskforce chat ----------

    private static TaskforceChatService Chat(SqliteTestContext ctx)
        => new(ctx.Factory, new TaskforceChatBroadcaster(), Substitute.For<INotificationService>());

    private static void SeedAgents(AppDbContext db)
    {
        db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("other", Rank.JuniorAgent));
    }

    private static void SeedTaskforce(AppDbContext db, string id, bool member = true, bool archived = false, DateTime? readAt = null)
    {
        db.Taskforces.Add(new Taskforce
        {
            Id = id,
            Name = "Taskforce " + id,
            CaseNumber = "NOOSE-TF-2026-" + id,
            IsArchived = archived,
            CreatedAt = T0.AddDays(-30),
        });
        if (member)
        {
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = id, AgentId = "me", CreatedAt = T0.AddDays(-30), LastReadAt = readAt });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = id, AgentId = "lead", CreatedAt = T0.AddDays(-30), LastReadAt = readAt });
        }
    }

    private static TaskforceMessage Message(string taskforceId, DateTime at, string? by = "other", string? author = "Wolf")
        => new() { TaskforceId = taskforceId, Text = "…", AuthorName = author, CreatedAt = at, CreatedById = by };

    [Fact]
    public async Task Taskforce_unread_counts_messages_by_others_after_the_read_mark()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedAgents(db);
            SeedTaskforce(db, "busy", readAt: T0.AddDays(-1));
            SeedTaskforce(db, "quiet", readAt: T0.AddDays(-1));
            db.TaskforceMessages.AddRange(
                Message("busy", T0.AddHours(-2), "other", "Wolf"),
                Message("busy", T0.AddHours(-1), "lead", "Falcon"),
                // own message and one already read do not count
                Message("busy", T0.AddMinutes(-30), "me", "Rookie"),
                Message("busy", T0.AddDays(-2), "other", "Wolf"),
                Message("quiet", T0.AddDays(-3), "other", "Wolf"));
            db.SaveChanges();
        }

        var activity = await Chat(ctx).GetUnreadAsync(Junior());

        var busy = Assert.Single(activity);
        Assert.Equal("busy", busy.TaskforceId);
        Assert.Equal(2, busy.Count);
        Assert.Equal(T0.AddHours(-1), busy.LastAtUtc);
        Assert.Equal("Falcon", busy.LastAuthor);
    }

    [Fact]
    public async Task Taskforce_unread_covers_own_live_taskforces_only()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedAgents(db);
            SeedTaskforce(db, "mine");
            SeedTaskforce(db, "foreign", member: false);
            SeedTaskforce(db, "archived", archived: true);
            db.TaskforceMessages.AddRange(
                Message("mine", T0.AddHours(-3)), Message("mine", T0.AddHours(-1)),
                Message("foreign", T0.AddHours(-2)), Message("archived", T0.AddHours(-2)));
            db.SaveChanges();
        }

        // leadership may read every taskforce, but the shift lists only the ones it belongs to
        var activity = await Chat(ctx).GetUnreadAsync(Leader());

        Assert.Equal(["mine"], activity.Select(a => a.TaskforceId).ToArray());
    }

    [Fact]
    public async Task Taskforce_unread_is_ordered_by_the_latest_message()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            SeedAgents(db);
            SeedTaskforce(db, "a");
            SeedTaskforce(db, "b");
            db.TaskforceMessages.AddRange(Message("a", T0.AddHours(-5)), Message("b", T0.AddHours(-1)), Message("a", T0.AddHours(-4)));
            db.SaveChanges();
        }

        var activity = await Chat(ctx).GetUnreadAsync(Junior());

        Assert.Equal(["b", "a"], activity.Select(a => a.TaskforceId).ToArray());
    }

    // ---------- house block ----------

    [Fact]
    public async Task The_house_block_leaves_out_own_changes_but_keeps_actorless_ones()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.AuditLogs.AddRange(
                new AuditLog { EntityType = nameof(Person), EntityId = "p1", Action = AuditAction.Modified, Timestamp = T0.AddHours(-3), AgentId = "lead", AgentName = "Falcon" },
                new AuditLog { EntityType = nameof(Person), EntityId = "p1", Action = AuditAction.Modified, Timestamp = T0.AddHours(-2), AgentId = "other", AgentName = "Wolf" },
                new AuditLog { EntityType = nameof(Person), EntityId = "p1", Action = AuditAction.Modified, Timestamp = T0.AddHours(-1), AgentId = null, AgentName = null });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase,
                Timestamp = T0.AddMinutes(-30), AgentId = "lead", AgentName = "Falcon",
            });
            db.SaveChanges();
        }
        var chronik = new GlobalChronikService(ctx.Factory);
        var query = new ChronikQuery(T0.AddDays(-1), T0.AddMinutes(1), MinEvents: 8, ExcludeAgentId: "lead");

        var events = (await chronik.GetEventsAsync(query, Leader())).Events;
        var density = await chronik.GetDensityAsync(query, Leader());

        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.ActorName == "Falcon");
        Assert.Equal(events.Count, density.Total);
    }
}
