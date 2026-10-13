using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Per-agent bell control: a silenced category arrives already read and never rings.</summary>
public sealed class NotificationMuteTests
{
    private static NotificationService NewService(SqliteTestContext ctx)
        => new(ctx.Factory, new NotificationBroadcaster(), Substitute.For<IDiscordWebhookService>());

    private static void SeedAgent(SqliteTestContext ctx, string id, Action<NavPreferences>? prefs = null)
    {
        var p = new NavPreferences();
        prefs?.Invoke(p);
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id, configure: a => a.NavPreferencesJson = JsonSerializer.Serialize(p)));
        db.SaveChanges();
    }

    private static void SeedAbsenceCoveringToday(SqliteTestContext ctx, string agentId)
    {
        var today = MeetingTime.Day(DateTime.UtcNow);
        using var db = ctx.NewContext();
        db.Absences.Add(new Absence
        {
            AgentId = agentId,
            FromDate = today.AddDays(-1),
            ToDate = today.AddDays(1),
            Days = 3,
            Category = AbsenceCategory.Vacation,
        });
        db.SaveChanges();
    }

    private static DateTime? ReadAtOf(SqliteTestContext ctx, string recipientId)
    {
        using var db = ctx.NewContext();
        return db.Notifications.Single(n => n.RecipientId == recipientId).ReadAt;
    }

    [Fact]
    public async Task A_muted_category_arrives_read_while_others_still_ring()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "stumm", p => p.MutedNotifications.Add(nameof(NotificationType.Feedback)));
        SeedAgent(ctx, "laut");

        await NewService(ctx).NotifyManyAsync(["stumm", "laut"], NotificationType.Feedback, "Neues Feedback",
            "/feedback/1", triggerId: null);

        Assert.NotNull(ReadAtOf(ctx, "stumm"));
        Assert.Null(ReadAtOf(ctx, "laut"));
    }

    [Fact]
    public async Task Muting_one_category_leaves_the_others_ringing()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", p => p.MutedNotifications.Add(nameof(NotificationType.Feedback)));

        await NewService(ctx).NotifyAsync("a1", NotificationType.JobAssigned, "Neue Aufgabe", "/aufgaben/1");

        Assert.Null(ReadAtOf(ctx, "a1"));
    }

    [Fact]
    public async Task A_locked_category_rings_even_when_stored_as_muted()
    {
        using var ctx = new SqliteTestContext();
        // a hand-edited or older blob must not switch off what is meant to always arrive
        SeedAgent(ctx, "a1", p => p.MutedNotifications.Add(nameof(NotificationType.Account)));

        await NewService(ctx).NotifyAsync("a1", NotificationType.Account, "Konto geändert", "/profil");

        Assert.Null(ReadAtOf(ctx, "a1"));
    }

    [Fact]
    public async Task Quiet_during_absence_silences_only_while_an_absence_covers_today()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "weg", p => p.QuietDuringAbsence = true);
        SeedAgent(ctx, "da", p => p.QuietDuringAbsence = true);
        SeedAbsenceCoveringToday(ctx, "weg");

        await NewService(ctx).NotifyManyAsync(["weg", "da"], NotificationType.Announcement, "Ankündigung",
            "/ankuendigungen/1", triggerId: null);

        Assert.NotNull(ReadAtOf(ctx, "weg"));
        Assert.Null(ReadAtOf(ctx, "da"));
    }

    [Fact]
    public async Task An_absence_alone_does_not_silence_without_the_switch()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "weg");
        SeedAbsenceCoveringToday(ctx, "weg");

        await NewService(ctx).NotifyAsync("weg", NotificationType.Announcement, "Ankündigung", "/ankuendigungen/1");

        Assert.Null(ReadAtOf(ctx, "weg"));
    }

    [Fact]
    public async Task Quiet_during_absence_still_lets_a_mention_through()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "weg", p => p.QuietDuringAbsence = true);
        SeedAbsenceCoveringToday(ctx, "weg");

        await NewService(ctx).NotifyAsync("weg", NotificationType.Account, "Konto geändert", "/profil");

        Assert.Null(ReadAtOf(ctx, "weg"));
    }

    [Fact]
    public async Task Silenced_changes_to_one_watched_record_fold_onto_one_read_row()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", p => p.MutedNotifications.Add(nameof(NotificationType.RecordModified)));
        var svc = NewService(ctx);

        // nothing unread holds the target open, so the watchlist fires on every change
        await svc.NotifyOnceAsync("a1", NotificationType.RecordModified, "Akte geändert (1)", "/personen/p1");
        await svc.NotifyOnceAsync("a1", NotificationType.RecordModified, "Akte geändert (2)", "/personen/p1");
        await svc.NotifyOnceAsync("a1", NotificationType.RecordModified, "Andere Akte", "/personen/p2");

        using var db = ctx.NewContext();
        var rows = db.Notifications.Where(n => n.RecipientId == "a1").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, n => Assert.NotNull(n.ReadAt));
        Assert.Equal("Akte geändert (2)", rows.Single(n => n.Href == "/personen/p1").Title);
    }

    [Fact]
    public async Task Silenced_events_that_share_a_target_each_keep_their_own_row()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "fuehrung", p => p.MutedNotifications.Add(nameof(NotificationType.AbsenceFiled)));
        var svc = NewService(ctx);

        // every absence points at the same overview; folding would leave only the latest in the inbox
        await svc.NotifyManyAsync(["fuehrung"], NotificationType.AbsenceFiled, "Abmeldung von Alpha",
            "/abmeldungen/uebersicht", triggerId: null);
        await svc.NotifyManyAsync(["fuehrung"], NotificationType.AbsenceFiled, "Abmeldung von Bravo",
            "/abmeldungen/uebersicht", triggerId: null);
        await svc.NotifyAsync("fuehrung", NotificationType.AbsenceFiled, "Abmeldung von Charlie",
            "/abmeldungen/uebersicht");

        using var db = ctx.NewContext();
        var rows = db.Notifications.Where(n => n.RecipientId == "fuehrung").ToList();
        Assert.Equal(["Abmeldung von Alpha", "Abmeldung von Bravo", "Abmeldung von Charlie"],
            rows.Select(n => n.Title).Order());
        Assert.All(rows, n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task A_silenced_thread_reply_folds_without_ringing()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", p => p.MutedNotifications.Add(nameof(NotificationType.PublicTicketOpened)));
        var svc = NewService(ctx);

        await svc.NotifyManyOnceAsync(["a1"], NotificationType.PublicTicketOpened, "Ticket 1", "/tickets/1", null);
        await svc.NotifyManyOnceAsync(["a1"], NotificationType.PublicTicketOpened, "Ticket 1 (neu)", "/tickets/1", null);

        using var db = ctx.NewContext();
        var row = db.Notifications.Single(n => n.RecipientId == "a1");
        Assert.NotNull(row.ReadAt);
        Assert.Equal("Ticket 1 (neu)", row.Title);
    }

    [Fact]
    public async Task The_shared_demo_account_ignores_whatever_is_stored_on_its_row()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, DemoIdentity.AgentId, p => p.MutedNotifications.Add(nameof(NotificationType.Feedback)));

        await NewService(ctx).NotifyAsync(DemoIdentity.AgentId, NotificationType.Feedback, "Feedback", "/feedback/1");

        Assert.Null(ReadAtOf(ctx, DemoIdentity.AgentId));
    }

    [Fact]
    public async Task A_broken_preferences_blob_never_blocks_delivery()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("kaputt", configure: a => a.NavPreferencesJson = "{kein json"));
            db.SaveChanges();
        }

        await NewService(ctx).NotifyAsync("kaputt", NotificationType.Feedback, "Feedback", "/feedback/1");

        Assert.Null(ReadAtOf(ctx, "kaputt"));
    }

    [Fact]
    public void Every_category_is_switchable_locked_or_channel_only_exactly_once()
    {
        var listed = NotificationMutes.Groups.SelectMany(g => g.Types)
            .Concat(NotificationMutes.Locked)
            .Concat(NotificationMutes.ChannelOnly)
            .ToList();

        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(Enum.GetValues<NotificationType>().OrderBy(t => t), listed.OrderBy(t => t));
    }

    [Fact]
    public async Task The_profile_switch_stores_a_mute_and_refuses_a_locked_category()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1");
        var prefs = new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

        await prefs.SetNotificationMutedAsync("a1", NotificationType.Feedback, muted: true);
        await prefs.SetNotificationMutedAsync("a1", NotificationType.Mention, muted: true);
        await prefs.SetQuietDuringAbsenceAsync("a1", quiet: true);

        using var db = ctx.NewContext();
        var stored = NavPreferences.FromJson(db.Users.Single(u => u.Id == "a1").NavPreferencesJson);
        Assert.Equal([nameof(NotificationType.Feedback)], stored.MutedNotifications);
        Assert.True(stored.QuietDuringAbsence);

        await prefs.SetNotificationMutedAsync("a1", NotificationType.Feedback, muted: false);
        Assert.False(NotificationMutes.IsMuted(await prefs.GetAsync("a1"), NotificationType.Feedback));
    }
}
