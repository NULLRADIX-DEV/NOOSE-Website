using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Models.Notifications;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Personal Discord pings: now, collected into the daily digest, or never.</summary>
public sealed class DiscordPingTests
{
    // GUID-shaped ids required by MentionParser's token regex
    private const string RecipientGuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TriggerGuid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string PersonGuid = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    // half past six in the evening, Berlin time, whatever zone the test machine runs in
    private static readonly DateTime Evening = MeetingTime.ToUtc(new DateTime(2026, 10, 13, 18, 30, 0));

    private const string FirstSnowflake = "111111111111111111";
    private const string SecondSnowflake = "222222222222222222";

    private sealed class RecordingQueue : IDiscordDmQueue
    {
        public List<DiscordDmJob> Jobs { get; } = [];

        public ChannelReader<DiscordDmJob> Reader => throw new NotSupportedException();

        public void Enqueue(DiscordDmJob job) => Jobs.Add(job);
    }

    private static IDiscordDmService Dm(bool personal)
    {
        var dm = Substitute.For<IDiscordDmService>();
        dm.PersonalActiveAsync(Arg.Any<CancellationToken>()).Returns(personal);
        return dm;
    }

    private static NotificationService NewService(SqliteTestContext ctx, IDiscordWebhookService discord,
        bool direct = false, RecordingQueue? queue = null)
        => new(ctx.Factory, new NotificationBroadcaster(), discord, Dm(direct), queue ?? new RecordingQueue());

    private static PingDigestService NewDigest(SqliteTestContext ctx, IDiscordWebhookService discord,
        bool direct = false, RecordingQueue? queue = null)
        => new(ctx.Factory, discord, Dm(direct), queue ?? new RecordingQueue(), NullLogger<PingDigestService>.Instance);

    private static void SeedAgent(SqliteTestContext ctx, string id, string? discordId = null,
        Action<NavPreferences>? prefs = null)
    {
        var p = new NavPreferences();
        prefs?.Invoke(p);
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id, configure: a =>
        {
            a.Codename = $"Codename-{id}";
            a.DiscordId = discordId ?? a.DiscordId;
            a.NavPreferencesJson = JsonSerializer.Serialize(p);
        }));
        db.SaveChanges();
    }

    private static void SeedNotice(SqliteTestContext ctx, string recipientId, NotificationType type, DateTime createdAt,
        DateTime? readAt = null)
    {
        using var db = ctx.NewContext();
        db.Notifications.Add(new Notification
        {
            RecipientId = recipientId,
            Type = type,
            Title = "Geheimer Aktentitel",
            Href = "/aufgaben/1",
            CreatedAt = createdAt,
            ReadAt = readAt,
        });
        db.SaveChanges();
    }

    private static IDiscordWebhookService Discord(bool enabled = true, bool digestChannel = true)
    {
        var discord = Substitute.For<IDiscordWebhookService>();
        var webhooks = new Dictionary<NotificationType, string?>
        {
            [NotificationType.PingDigest] = digestChannel ? "https://discord.com/api/webhooks/1/abc" : null,
        };
        discord.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new DiscordWebhookConfig(enabled, DiscordWebhookConfig.DefaultBaseUrl, webhooks,
                new Dictionary<NotificationType, string?>(), IncludeHeadline: true));
        discord.PushDigestAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(true);
        return discord;
    }

    // ---- the event post ----------------------------------------------------

    [Fact]
    public async Task A_personal_ping_leaves_out_who_collects_it_or_switched_it_off()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "sofort");
        SeedAgent(ctx, "gesammelt", prefs: p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedAgent(ctx, "nie", prefs: p => p.MutedPings.Add(nameof(NotificationType.JobAssigned)));
        var discord = Substitute.For<IDiscordWebhookService>();

        await NewService(ctx, discord).NotifyManyAsync(["sofort", "gesammelt", "nie"], NotificationType.JobAssigned,
            "Neue Aufgabe", "/aufgaben/1", triggerId: null);

        await discord.Received(1).PushAsync(NotificationType.JobAssigned, "/aufgaben/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "sofort" })),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // the bell is not Discord: every one of them still has the notice, unread
        using var db = ctx.NewContext();
        Assert.Equal(3, db.Notifications.Count(n => n.ReadAt == null));
    }

    [Fact]
    public async Task With_nobody_left_to_ping_there_is_no_post_at_all()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "gesammelt", prefs: p => p.DigestPings.Add(nameof(NotificationType.Followup)));
        var discord = Substitute.For<IDiscordWebhookService>();

        await NewService(ctx, discord).NotifyManyAsync(["gesammelt"], NotificationType.Followup,
            "Wiedervorlage fällig", "/personen/p1", triggerId: null);

        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default);
    }

    [Fact]
    public async Task A_bell_only_notice_rings_but_never_reaches_the_channel()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1");
        var discord = Substitute.For<IDiscordWebhookService>();

        await NewService(ctx, discord).NotifyManyInAppAsync(["a1"], NotificationType.AcknowledgmentReminder,
            "Bitte quittieren", "/brett/1", triggerId: null);

        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
        using var db = ctx.NewContext();
        var row = Assert.Single(db.Notifications, n => n.RecipientId == "a1");
        Assert.Null(row.ReadAt);
    }

    [Fact]
    public async Task A_role_category_ignores_whatever_is_stored_for_it()
    {
        using var ctx = new SqliteTestContext();
        // a hand-edited blob: role pings cannot leave one member out, so the choice must not cancel the post
        SeedAgent(ctx, "a1", prefs: p => p.MutedPings.Add(nameof(NotificationType.Announcement)));
        var discord = Substitute.For<IDiscordWebhookService>();

        await NewService(ctx, discord).NotifyManyAsync(["a1"], NotificationType.Announcement, "Ankündigung",
            "/brett/1", triggerId: null);

        await discord.Received(1).PushAsync(NotificationType.Announcement, "/brett/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("a1")), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mention_pings_only_who_wants_it_now_but_always_rings_the_bell()
    {
        using var ctx = new SqliteTestContext();
        using (var seed = ctx.NewContext())
        {
            var prefs = new NavPreferences();
            prefs.MutedPings.Add(nameof(NotificationType.Mention));
            seed.Users.Add(Seed.Agent(RecipientGuid, rank: Rank.Trainee,
                configure: a => a.NavPreferencesJson = JsonSerializer.Serialize(prefs)));
            seed.People.Add(Seed.Person(PersonGuid, "Ziel", p => p.IsClassified = false));
            seed.SaveChanges();
        }
        var discord = Substitute.For<IDiscordWebhookService>();
        var trigger = ClaimsPrincipalBuilder.Agent(TriggerGuid).Build();

        await NewService(ctx, discord).NotifyMentionedAsync($"Hallo @{{Agent:{RecipientGuid}}}!",
            "Du wurdest erwähnt", "/personen/x", "Person", PersonGuid, trigger);

        using var db = ctx.NewContext();
        Assert.Null(db.Notifications.Single().ReadAt);
        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default);
    }

    [Fact]
    public async Task The_demo_account_and_a_broken_blob_are_pinged_as_before()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, DemoIdentity.AgentId, prefs: p => p.MutedPings.Add(nameof(NotificationType.JobAssigned)));
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("kaputt", configure: a => a.NavPreferencesJson = "{kein json"));
            db.SaveChanges();
        }
        var discord = Substitute.For<IDiscordWebhookService>();

        await NewService(ctx, discord).NotifyManyAsync([DemoIdentity.AgentId, "kaputt"], NotificationType.JobAssigned,
            "Neue Aufgabe", "/aufgaben/1", triggerId: null);

        await discord.Received(1).PushAsync(NotificationType.JobAssigned, "/aufgaben/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.Count == 2), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- the event post as a direct message ---------------------------------

    [Fact]
    public async Task With_private_pings_on_each_instant_recipient_gets_one_message_and_the_channel_none()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "eins", FirstSnowflake);
        SeedAgent(ctx, "zwei", SecondSnowflake);
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: true, queue).NotifyManyAsync(["eins", "zwei"],
            NotificationType.JobAssigned, "Geheimer Aufgabentitel", "/aufgaben/1", triggerId: null);

        // the channel text without the mention, no title, and the channel post as the agent's fallback
        const string text = "📌 Dir wurde eine Aufgabe zugewiesen.\nhttps://fibhq.com/aufgaben/1";
        Assert.Equal(
            [
                new DiscordDmJob("eins", FirstSnowflake, text, NotificationType.JobAssigned, "/aufgaben/1"),
                new DiscordDmJob("zwei", SecondSnowflake, text, NotificationType.JobAssigned, "/aufgaben/1"),
            ],
            queue.Jobs);
        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
        // the bell is unaffected
        using var db = ctx.NewContext();
        Assert.Equal(2, db.Notifications.Count(n => n.ReadAt == null));
    }

    [Fact]
    public async Task With_private_pings_on_who_collects_or_muted_gets_no_message_either()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "sofort", FirstSnowflake);
        SeedAgent(ctx, "gesammelt", SecondSnowflake, p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedAgent(ctx, "nie", "333333333333333333", p => p.MutedPings.Add(nameof(NotificationType.JobAssigned)));
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: true, queue).NotifyManyAsync(["sofort", "gesammelt", "nie"],
            NotificationType.JobAssigned, "Neue Aufgabe", "/aufgaben/1", triggerId: null);

        Assert.Equal(["sofort"], queue.Jobs.Select(j => j.AgentId));
        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task With_nobody_left_for_a_direct_message_nothing_is_queued_and_nothing_posted()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "gesammelt", FirstSnowflake, p => p.DigestPings.Add(nameof(NotificationType.Followup)));
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: true, queue).NotifyManyAsync(["gesammelt"], NotificationType.Followup,
            "Wiedervorlage fällig", "/personen/p1", triggerId: null);

        Assert.Empty(queue.Jobs);
        await discord.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Whoever_has_no_discord_id_is_still_pinged_in_the_channel()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "verknuepft", FirstSnowflake);
        SeedAgent(ctx, "ohne", "");
        SeedAgent(ctx, DemoIdentity.AgentId, "demo");
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: true, queue).NotifyManyAsync(
            ["verknuepft", "ohne", DemoIdentity.AgentId], NotificationType.JobAssigned, "Neue Aufgabe", "/aufgaben/1",
            triggerId: null);

        Assert.Equal(["verknuepft"], queue.Jobs.Select(j => j.AgentId));
        await discord.Received(1).PushAsync(NotificationType.JobAssigned, "/aufgaben/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "ohne", DemoIdentity.AgentId })),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_private_pings_off_the_channel_post_is_exactly_as_before()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "eins", FirstSnowflake);
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: false, queue).NotifyManyAsync(["eins"], NotificationType.JobAssigned,
            "Neue Aufgabe", "/aufgaben/1", triggerId: null);

        Assert.Empty(queue.Jobs);
        await discord.Received(1).PushAsync(NotificationType.JobAssigned, "/aufgaben/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "eins" })), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_role_category_stays_in_the_channel_whatever_the_switch_says()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "eins", FirstSnowflake);
        var discord = Discord();
        var queue = new RecordingQueue();

        await NewService(ctx, discord, direct: true, queue).NotifyManyAsync(["eins"], NotificationType.Announcement,
            "Ankündigung", "/brett/1", triggerId: null);

        Assert.Empty(queue.Jobs);
        await discord.Received(1).PushAsync(NotificationType.Announcement, "/brett/1",
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "eins" })), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_folded_notice_is_no_new_direct_message()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "eins", FirstSnowflake);
        var discord = Discord();
        var queue = new RecordingQueue();
        var service = NewService(ctx, discord, direct: true, queue);

        await service.NotifyManyOnceAsync(["eins"], NotificationType.Followup, "Wiedervorlage", "/personen/p1", null);
        await service.NotifyManyOnceAsync(["eins"], NotificationType.Followup, "Wiedervorlage", "/personen/p1", null);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal("⏰ Eine Wiedervorlage ist fällig.\nhttps://fibhq.com/personen/p1", job.Content);
        Assert.Equal(NotificationType.Followup, job.FallbackType);
    }

    [Fact]
    public void The_text_is_the_notice_and_an_absolute_link_and_never_a_mention()
    {
        Assert.Equal("📌 Dir wurde eine Aufgabe zugewiesen.\nhttps://fibhq.com/aufgaben/1",
            DiscordWebhookService.PersonalText(NotificationType.JobAssigned, "https://fibhq.com/", "/aufgaben/1"));
        // no record link: the category's own page
        Assert.Equal("💬 Es gibt eine neue Erwähnung in einer Akte.\nhttps://fibhq.com/dashboard",
            DiscordWebhookService.PersonalText(NotificationType.Mention, "https://fibhq.com", null));
        Assert.Equal("⏰ Ein Test wird bald fällig.\nhttps://fibhq.com/tests",
            DiscordWebhookService.PersonalText(NotificationType.TestDueReminder, "https://fibhq.com", " "));
        Assert.All(DiscordPings.Personal, type =>
            Assert.DoesNotContain("<@", DiscordWebhookService.PersonalText(type, "https://fibhq.com", "/x")));
    }

    [Fact]
    public async Task The_profile_keeps_one_choice_per_category_and_refuses_the_others()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1");
        var prefs = new NavPreferencesService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

        await prefs.SetDiscordPingAsync("a1", NotificationType.JobAssigned, DiscordPingMode.Off);
        await prefs.SetDiscordPingAsync("a1", NotificationType.JobAssigned, DiscordPingMode.Digest);
        await prefs.SetDiscordPingAsync("a1", NotificationType.Announcement, DiscordPingMode.Off);

        var stored = await prefs.GetAsync("a1");
        Assert.Equal(DiscordPingMode.Digest, DiscordPings.ModeFor(stored, NotificationType.JobAssigned));
        Assert.Empty(stored.MutedPings);
        Assert.Equal(DiscordPingMode.Instant, DiscordPings.ModeFor(stored, NotificationType.Announcement));

        await prefs.SetDiscordPingAsync("a1", NotificationType.JobAssigned, DiscordPingMode.Instant);
        Assert.Empty((await prefs.GetAsync("a1")).DigestPings);
    }

    [Fact]
    public void Every_category_that_pings_its_recipients_is_a_personal_choice_except_the_notices_about_you()
    {
        // a nudge from the inactivity radar is leadership addressing you, like a personnel entry; an alarm is no
        // choice either: it must reach everybody it names, right away
        var recipientPings = DiscordRouting.RoutableTypes.Where(DiscordRouting.PingsRecipients)
            .Except([NotificationType.PersonnelEntry, NotificationType.DecorationAwarded, NotificationType.PingDigest, NotificationType.InactivityNudge,
                NotificationType.EmergencyAlarm])
            .OrderBy(t => t);

        Assert.Equal(recipientPings, DiscordPings.Personal.OrderBy(t => t));
    }

    // ---- the daily digest --------------------------------------------------

    [Fact]
    public void The_digest_is_due_once_a_day_after_the_digest_hour()
    {
        var afternoon = MeetingTime.ToUtc(new DateTime(2026, 10, 13, 17, 59, 0));
        var earlierToday = MeetingTime.ToUtc(new DateTime(2026, 10, 13, 18, 5, 0));
        var yesterday = MeetingTime.ToUtc(new DateTime(2026, 10, 12, 18, 5, 0));

        Assert.False(PingDigestService.IsDue(afternoon, lastRunUtc: null));
        Assert.True(PingDigestService.IsDue(Evening, lastRunUtc: null));
        Assert.True(PingDigestService.IsDue(Evening, yesterday));
        Assert.False(PingDigestService.IsDue(Evening, earlierToday));
    }

    [Fact]
    public async Task The_digest_lists_each_collecting_agent_with_the_unread_counts_of_what_they_collect()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p =>
        {
            p.DigestPings.Add(nameof(NotificationType.JobAssigned));
            p.DigestPings.Add(nameof(NotificationType.Mention));
        });
        SeedAgent(ctx, "b", "222", p => p.DigestPings.Add(nameof(NotificationType.Followup)));
        SeedAgent(ctx, "sofort", "333");
        SeedAgent(ctx, "ohne-discord", "", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        var hourAgo = Evening.AddHours(-1);
        SeedNotice(ctx, "a", NotificationType.JobAssigned, hourAgo);
        SeedNotice(ctx, "a", NotificationType.JobAssigned, hourAgo);
        SeedNotice(ctx, "a", NotificationType.JobAssigned, hourAgo, readAt: Evening.AddMinutes(-5));
        SeedNotice(ctx, "a", NotificationType.Mention, hourAgo);
        SeedNotice(ctx, "a", NotificationType.Followup, hourAgo);
        SeedNotice(ctx, "b", NotificationType.Followup, hourAgo);
        SeedNotice(ctx, "b", NotificationType.Followup, Evening.AddDays(-2));
        SeedNotice(ctx, "sofort", NotificationType.JobAssigned, hourAgo);
        SeedNotice(ctx, "ohne-discord", NotificationType.JobAssigned, hourAgo);
        var discord = Discord();

        var listed = await NewDigest(ctx, discord).RunDueAsync(Evening);

        Assert.Equal(2, listed);
        await discord.Received(1).PushDigestAsync(
            Arg.Is<string>(c => c.Contains("<@111> 1× Erwähnung, 2× Aufgabe\n<@222> 1× Wiedervorlage fällig")
                && !c.Contains("333") && !c.Contains("Geheimer")),
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "111", "222" })),
            "/benachrichtigungen", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Before_the_digest_hour_nothing_goes_out_and_the_day_stays_open()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(-3));
        var discord = Discord();
        var digest = NewDigest(ctx, discord);

        Assert.Equal(0, await digest.RunDueAsync(Evening.AddHours(-1)));
        Assert.Equal(1, await digest.RunDueAsync(Evening));
    }

    [Fact]
    public async Task The_next_digest_counts_only_what_arrived_after_the_last_one()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(-1));
        var discord = Discord();
        var digest = NewDigest(ctx, discord);
        await digest.RunDueAsync(Evening);

        // the same evening again: already sent
        Assert.Equal(0, await digest.RunDueAsync(Evening.AddHours(2)));

        // the old notice is still unread, but yesterday's digest already counted it
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(20));
        discord.ClearReceivedCalls();
        Assert.Equal(1, await digest.RunDueAsync(Evening.AddDays(1)));
        await discord.Received(1).PushDigestAsync(Arg.Is<string>(c => c.Contains("<@111> 1× Aufgabe")),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Without_a_live_digest_channel_nothing_is_posted_and_the_day_is_done(bool enabled, bool channel)
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(-1));
        var discord = Discord(enabled, channel);

        Assert.Equal(0, await NewDigest(ctx, discord).RunDueAsync(Evening));

        await discord.DidNotReceiveWithAnyArgs().PushDigestAsync(default!, default!);
        using var db = ctx.NewContext();
        Assert.Single(db.SystemSettings, s => s.Key == SystemSettingKeys.PingDigestLastRun);
    }

    [Fact]
    public async Task With_private_pings_on_the_digest_goes_as_one_message_per_agent_without_the_channel_post()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p =>
        {
            p.DigestPings.Add(nameof(NotificationType.JobAssigned));
            p.DigestPings.Add(nameof(NotificationType.Mention));
        });
        SeedAgent(ctx, "b", "222", p => p.DigestPings.Add(nameof(NotificationType.Followup)));
        SeedAgent(ctx, "sofort", "333");
        var hourAgo = Evening.AddHours(-1);
        SeedNotice(ctx, "a", NotificationType.JobAssigned, hourAgo);
        SeedNotice(ctx, "a", NotificationType.JobAssigned, hourAgo);
        SeedNotice(ctx, "a", NotificationType.Mention, hourAgo);
        SeedNotice(ctx, "b", NotificationType.Followup, hourAgo);
        SeedNotice(ctx, "sofort", NotificationType.JobAssigned, hourAgo);
        var discord = Discord();
        var queue = new RecordingQueue();

        var listed = await NewDigest(ctx, discord, direct: true, queue).RunDueAsync(Evening);

        Assert.Equal(2, listed);
        // the counts and the inbox link, no mention and no title; the channel line only as the fallback
        Assert.Equal(
            [
                new DiscordDmJob("a", "111", "🗞️ Tagesüberblick: 1× Erwähnung, 2× Aufgabe\nhttps://fibhq.com/benachrichtigungen",
                    NotificationType.PingDigest, "/benachrichtigungen", "<@111> 1× Erwähnung, 2× Aufgabe"),
                new DiscordDmJob("b", "222", "🗞️ Tagesüberblick: 1× Wiedervorlage fällig\nhttps://fibhq.com/benachrichtigungen",
                    NotificationType.PingDigest, "/benachrichtigungen", "<@222> 1× Wiedervorlage fällig"),
            ],
            queue.Jobs);
        Assert.All(queue.Jobs, j =>
        {
            Assert.DoesNotContain("<@", j.Content);
            Assert.DoesNotContain("Geheimer", j.Content);
        });
        await discord.DidNotReceiveWithAnyArgs().PushDigestAsync(default!, default!);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task With_private_pings_on_the_digest_needs_no_channel_and_the_day_is_still_done(bool enabled, bool channel)
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(-1));
        var discord = Discord(enabled, channel);
        var queue = new RecordingQueue();
        var digest = NewDigest(ctx, discord, direct: true, queue);

        Assert.Equal(1, await digest.RunDueAsync(Evening));
        Assert.Equal(0, await digest.RunDueAsync(Evening.AddHours(1)));

        Assert.Single(queue.Jobs);
        await discord.DidNotReceiveWithAnyArgs().PushDigestAsync(default!, default!);
        using var db = ctx.NewContext();
        Assert.Single(db.SystemSettings, s => s.Key == SystemSettingKeys.PingDigestLastRun);
    }

    [Fact]
    public async Task With_private_pings_on_nobody_collecting_queues_nothing_and_stamps_the_day()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "sofort", "333");
        SeedNotice(ctx, "sofort", NotificationType.JobAssigned, Evening.AddHours(-1));
        var discord = Discord();
        var queue = new RecordingQueue();

        Assert.Equal(0, await NewDigest(ctx, discord, direct: true, queue).RunDueAsync(Evening));

        Assert.Empty(queue.Jobs);
        using var db = ctx.NewContext();
        Assert.Single(db.SystemSettings, s => s.Key == SystemSettingKeys.PingDigestLastRun);
    }

    [Fact]
    public async Task With_private_pings_off_the_digest_is_exactly_the_channel_post_as_before()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a", "111", p => p.DigestPings.Add(nameof(NotificationType.JobAssigned)));
        SeedNotice(ctx, "a", NotificationType.JobAssigned, Evening.AddHours(-1));
        var discord = Discord();
        var queue = new RecordingQueue();

        Assert.Equal(1, await NewDigest(ctx, discord, direct: false, queue).RunDueAsync(Evening));

        Assert.Empty(queue.Jobs);
        await discord.Received(1).PushDigestAsync(Arg.Is<string>(c => c.Contains("<@111> 1× Aufgabe")),
            Arg.Any<IReadOnlyList<string>>(), "/benachrichtigungen", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_long_digest_is_split_into_messages_discord_accepts()
    {
        var entries = Enumerable.Range(0, 95)
            .Select(i => new PingDigestEntry($"agent-{i}", $"{100000000000000000 + i}",
                [new PingDigestCount(NotificationType.JobAssigned, 3), new PingDigestCount(NotificationType.Followup, 1)]))
            .ToList();

        var messages = PingDigestService.Compose(entries);

        Assert.True(messages.Count >= 2);
        Assert.Equal(95, messages.Sum(m => m.DiscordIds.Count));
        Assert.All(messages, m =>
        {
            Assert.StartsWith("🗞️ **Tagesüberblick**", m.Content);
            Assert.True(m.Content.Length <= 1900);
            Assert.True(m.DiscordIds.Count <= 90);
            Assert.All(m.DiscordIds, id => Assert.Contains($"<@{id}>", m.Content));
        });
    }
}
