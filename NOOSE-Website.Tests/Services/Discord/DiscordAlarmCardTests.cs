using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetCord.Rest;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Discord.Bot;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The alarm as one live bot card.</summary>
public partial class DiscordAlarmCardTests
{
    private const string Hook = "https://discord.test/api/webhooks/1/secret-token";
    private const string AlarmId = "x";
    private const ulong AlarmChannel = 5;
    private const ulong StoredMessage = 77;
    private const string FirstId = "111111111111111111";

    private sealed record Sent(string Kind, ulong Channel, ulong? Message,
        IReadOnlyList<IMessageComponentProperties>? Card, string? Text, AllowedMentionsProperties? Mentions);

    private sealed class FakeSender : IDiscordBotSender
    {
        private ulong _next = 500;

        public ConcurrentQueue<Sent> Calls { get; } = new();

        /// <summary>The exception a call throws, if any.</summary>
        public Func<Sent, Exception?> Failure { get; set; } = _ => null;

        public Task<ulong> SendAsync(ulong channelId, IReadOnlyList<IMessageComponentProperties> card,
            AllowedMentionsProperties mentions, ulong? replyTo = null, CancellationToken cancellationToken = default)
        {
            Record(new Sent("card", channelId, replyTo, card, null, mentions));
            return Task.FromResult(Interlocked.Increment(ref _next));
        }

        public Task SendTextAsync(ulong channelId, string content, AllowedMentionsProperties mentions,
            ulong? replyTo = null, CancellationToken cancellationToken = default)
        {
            Record(new Sent("text", channelId, replyTo, null, content, mentions));
            return Task.CompletedTask;
        }

        public Task EditAsync(ulong channelId, ulong messageId, IReadOnlyList<IMessageComponentProperties> card,
            CancellationToken cancellationToken = default)
        {
            Record(new Sent("edit", channelId, messageId, card, null, null));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private void Record(Sent call)
        {
            if (Failure(call) is { } failure)
            {
                throw failure;
            }
            Calls.Enqueue(call);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private static ServiceProvider Services(SqliteTestContext ctx)
    {
        var webhook = Substitute.For<IDiscordWebhookService>();
        webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, "https://fibhq.com",
            new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(), true));
        return new ServiceCollection()
            .AddSingleton<IDbContextFactory<AppDbContext>>(ctx.Factory)
            .AddSingleton(Substitute.For<IPublicWantedService>())
            .AddSingleton(Substitute.For<ISystemSettingService>())
            .AddSingleton(webhook)
            .AddScoped<DiscordLiveBoards>()
            .BuildServiceProvider();
    }

    private sealed class Cards : IAsyncDisposable
    {
        public Cards(SqliteTestContext ctx)
        {
            Provider = Services(ctx);
            Alarm = new DiscordAlarmCards(Provider.GetRequiredService<IServiceScopeFactory>(), Sender, Log);
        }

        public ServiceProvider Provider { get; }

        public FakeSender Sender { get; } = new();

        public ListLogger<DiscordAlarmCards> Log { get; } = new();

        public DiscordAlarmCards Alarm { get; }

        public string Logged => string.Join('\n', Log.Lines);

        public async Task<(ulong Channel, ulong Message, string Hash)?> StoredAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DiscordLiveBoards>().GetAlarmBoardAsync(AlarmId, CancellationToken.None);
        }

        public async Task<BoardCard> RenderAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var rendered = await scope.ServiceProvider.GetRequiredService<DiscordLiveBoards>().RenderAlarmAsync(AlarmId, CancellationToken.None);
            return rendered!.Value.Card;
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    /// <summary>Seeds an open alarm; r0 triggered it and comes.</summary>
    private static async Task SeedAlarmAsync(SqliteTestContext ctx, int recipients, Func<int, string> discordId)
    {
        await using var db = ctx.NewContext();
        var now = DateTime.UtcNow;
        var alarm = new EmergencyAlarm
        {
            Id = AlarmId, Situation = "Schüsse am Pier", TriggeredById = "r0", TriggeredByName = "Falke",
            TriggeredAt = now, Expiry = now.AddMinutes(30),
        };
        for (var i = 0; i < recipients; i++)
        {
            var index = i;
            db.Users.Add(Seed.Agent($"r{i}", configure: a => a.DiscordId = discordId(index)));
            alarm.Recipients.Add(new EmergencyAlarmRecipient
            {
                AgentId = $"r{i}", Answer = i == 0 ? EmergencyAlarmAnswer.Coming : null,
            });
        }
        db.EmergencyAlarms.Add(alarm);
        await db.SaveChangesAsync();
    }

    private static string Snowflake(int i) => (300000000000000000UL + (ulong)i).ToString(CultureInfo.InvariantCulture);

    private static async Task StoreBoardAsync(SqliteTestContext ctx, string hash = "old")
    {
        await using var db = ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.DiscordBotAlarmBoards,
            Value = JsonSerializer.Serialize(new Dictionary<string, string> { [AlarmId] = $"{AlarmChannel}|{StoredMessage}|{hash}" }),
        });
        await db.SaveChangesAsync();
    }

    private static DiscordPost AlarmPost(IEnumerable<ulong> users, bool reping = false, WebhookFallback? fallback = null)
        => new(NotificationType.EmergencyAlarm, AlarmChannel, [DiscordCards.Card(DiscordCards.Danger, DiscordCards.Text("Ersatz"))],
            users.ToList(), null, [], fallback, AlarmId, reping);

    private static RestException Rest(HttpStatusCode status, int? code = null)
        => code is { } c
            ? new RestException(status, status.ToString(), new RestError(c, "Discord says no", null!))
            : new RestException(status, status.ToString());

    private static IEnumerable<object> Walk(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            yield return item;
            IEnumerable<object> children = item switch
            {
                ComponentContainerProperties container => container.Components,
                ComponentSectionProperties section => section.Components.Cast<object>().Append(section.Accessory),
                ActionRowProperties row => row.Components,
                _ => [],
            };
            foreach (var child in Walk(children))
            {
                yield return child;
            }
        }
    }

    private static string Text(IReadOnlyList<IMessageComponentProperties> card)
        => string.Join("\n", Walk(card).OfType<TextDisplayProperties>().Select(t => t.Content));

    private static List<ulong> Chips(string text)
        => Chip().Matches(text).Select(m => ulong.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    [GeneratedRegex(@"<@(\d+)>")]
    private static partial Regex Chip();

    [Fact]
    public async Task Alarm_card_names_the_alarmed()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await using var cards = new Cards(ctx);

        var text = Text((await cards.RenderAsync()).Components);

        Assert.Contains("-# Alarmiert: <@1> <@2> <@3>", text);
    }

    [Fact]
    public async Task Alarm_card_names_at_most_a_hundred_and_counts_the_rest()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 120, Snowflake);
        await using var cards = new Cards(ctx);

        var text = Text((await cards.RenderAsync()).Components);

        var line = text.Split('\n').Single(l => l.StartsWith("-# Alarmiert:", StringComparison.Ordinal));
        Assert.Equal(100, Chips(line).Count);
        Assert.EndsWith(" +20 weitere", line);
    }

    [Fact]
    public async Task Alarm_card_leaves_out_ids_that_are_no_snowflakes()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => i switch { 0 => "demo", 1 => "", _ => "3" });
        await using var cards = new Cards(ctx);

        var text = Text((await cards.RenderAsync()).Components);

        Assert.Contains("-# Alarmiert: <@3>", text);
        Assert.DoesNotContain("demo", text);
    }

    [Fact]
    public async Task Alarm_card_hash_changes_with_answers()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await using var cards = new Cards(ctx);
        var before = (await cards.RenderAsync()).Hash;
        Assert.Equal(before, (await cards.RenderAsync()).Hash);

        await using (var db = ctx.NewContext())
        {
            var row = await db.EmergencyAlarmRecipients.SingleAsync(r => r.AgentId == "r1");
            row.Answer = EmergencyAlarmAnswer.NotComing;
            await db.SaveChangesAsync();
        }

        Assert.NotEqual(before, (await cards.RenderAsync()).Hash);
    }

    private sealed record Pushed(DiscordWebhookService Service, DiscordPostQueue Queue, FakeBotRoutes Routes, List<string> Posts);

    private sealed class CapturingHandler(List<string> posts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            posts.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static async Task<Pushed> WebhookAsync(SqliteTestContext ctx, bool webhooksOn, string? hook, ulong? alarmChannel)
    {
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordEnabled, Value = webhooksOn ? "true" : "false" });
            if (hook is not null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordWebhookPrefix + NotificationType.EmergencyAlarm, Value = hook });
            }
            db.Users.Add(Seed.Agent("a1", configure: a => a.DiscordId = FirstId));
            await db.SaveChangesAsync();
        }
        var posts = new List<string>();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("discord").Returns(_ => new HttpClient(new CapturingHandler(posts), disposeHandler: true));
        var routes = new FakeBotRoutes { Route = new BotRoute(BotRouteState.Bot, 9), AlarmChannel = alarmChannel };
        var queue = new DiscordPostQueue();
        var service = new DiscordWebhookService(factory, ctx.Factory, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordWebhookService>.Instance, routes, queue, new ServiceCollection().BuildServiceProvider());
        return new Pushed(service, queue, routes, posts);
    }

    private static DiscordPost Queued(DiscordPostQueue queue)
    {
        Assert.True(queue.TryRead(out var post), "Nothing was queued.");
        Assert.False(queue.TryRead(out _), "More than one post was queued.");
        return post;
    }

    [Fact]
    public async Task Alarm_routes_without_webhook_when_bot_channel_set()
    {
        using var ctx = new SqliteTestContext();
        var host = await WebhookAsync(ctx, webhooksOn: false, hook: null, alarmChannel: AlarmChannel);

        await host.Service.PushAsync(NotificationType.EmergencyAlarm, $"/einsatzalarme/{AlarmId}", ["a1"], "Schüsse am Pier");

        var post = Queued(host.Queue);
        Assert.Equal(AlarmChannel, post.ChannelId);
        Assert.Equal(AlarmId, post.AlarmId);
        Assert.False(post.IsReping);
        Assert.Null(post.Fallback);
        Assert.Equal([ulong.Parse(FirstId, CultureInfo.InvariantCulture)], post.PingUsers);
        Assert.Empty(host.Posts);
    }

    [Fact]
    public async Task Reping_is_marked()
    {
        using var ctx = new SqliteTestContext();
        await StoreBoardAsync(ctx);
        var host = await WebhookAsync(ctx, webhooksOn: false, hook: null, alarmChannel: AlarmChannel);

        await host.Service.PushAsync(NotificationType.EmergencyAlarm, $"/einsatzalarme/{AlarmId}", ["a1"], "Erneut: Schüsse am Pier");

        Assert.True(Queued(host.Queue).IsReping);
    }

    [Fact]
    public async Task An_enabled_alarm_webhook_is_the_fallback_of_the_card()
    {
        using var ctx = new SqliteTestContext();
        var host = await WebhookAsync(ctx, webhooksOn: true, hook: Hook, alarmChannel: AlarmChannel);

        await host.Service.PushAsync(NotificationType.EmergencyAlarm, $"/einsatzalarme/{AlarmId}", ["a1"], "Schüsse am Pier");

        var post = Queued(host.Queue);
        Assert.Equal(Hook, post.Fallback!.Url);
        var content = JsonDocument.Parse(JsonSerializer.Serialize(Assert.Single(post.Fallback.Payloads), JsonSerializerOptions.Web))
            .RootElement.GetProperty("content").GetString()!;
        Assert.Equal($"<@{FirstId}> Schüsse am Pier\nhttps://fibhq.com/einsatzalarme/{AlarmId}", content);
        Assert.Empty(host.Posts);
        // alarm channel decides
        Assert.Empty(host.Routes.Asked);
    }

    [Fact]
    public async Task Without_an_alarm_channel_the_alarm_stays_a_webhook_post()
    {
        using var ctx = new SqliteTestContext();
        var host = await WebhookAsync(ctx, webhooksOn: true, hook: Hook, alarmChannel: null);

        await host.Service.PushAsync(NotificationType.EmergencyAlarm, $"/einsatzalarme/{AlarmId}", ["a1"], "Schüsse am Pier");

        Assert.Single(host.Posts);
        Assert.False(host.Queue.TryRead(out _));
        Assert.Empty(host.Routes.Asked);
    }

    [Fact]
    public async Task Without_an_alarm_channel_and_webhooks_off_nothing_goes_out()
    {
        using var ctx = new SqliteTestContext();
        var host = await WebhookAsync(ctx, webhooksOn: false, hook: Hook, alarmChannel: null);

        await host.Service.PushAsync(NotificationType.EmergencyAlarm, $"/einsatzalarme/{AlarmId}", ["a1"], "Schüsse am Pier");

        Assert.Empty(host.Posts);
        Assert.False(host.Queue.TryRead(out _));
    }

    [Fact]
    public async Task The_first_post_is_one_card_that_pings_the_alarmed_and_is_stored()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await using var cards = new Cards(ctx);

        await cards.Alarm.HandleAsync(AlarmPost([2, 3]));

        var sent = Assert.Single(cards.Sender.Calls);
        Assert.Equal("card", sent.Kind);
        Assert.Equal(AlarmChannel, sent.Channel);
        Assert.Null(sent.Message);
        Assert.Contains("EINSATZALARM", Text(sent.Card!));
        Assert.Equal<ulong>([2, 3], sent.Mentions!.AllowedUsers!);
        Assert.Empty(sent.Mentions.AllowedRoles!);
        Assert.False(sent.Mentions.Everyone);
        var stored = await cards.StoredAsync();
        Assert.Equal(AlarmChannel, stored!.Value.Channel);
        Assert.Equal(501UL, stored.Value.Message);
        Assert.Equal((await cards.RenderAsync()).Hash, stored.Value.Hash);
    }

    [Fact]
    public async Task Over_a_hundred_alarmed_are_all_pinged_once_by_the_card_and_plain_lines()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 250, Snowflake);
        await using var cards = new Cards(ctx);
        var users = Enumerable.Range(1, 249).Select(i => ulong.Parse(Snowflake(i), CultureInfo.InvariantCulture)).ToList();

        await cards.Alarm.HandleAsync(AlarmPost(users));

        var calls = cards.Sender.Calls.ToList();
        Assert.Equal("card", calls[0].Kind);
        Assert.All(calls.Skip(1), c => Assert.Equal("text", c.Kind));
        Assert.All(calls.Skip(1), c => Assert.True(c.Text!.Length <= 2000));
        Assert.All(calls, c => Assert.True(c.Mentions!.AllowedUsers!.Count() <= 100));
        // chips match mentions
        Assert.All(calls, c => Assert.Empty(c.Mentions!.AllowedUsers!.Except(Chips(c.Card is null ? c.Text! : Text(c.Card)))));
        var pinged = calls.SelectMany(c => c.Mentions!.AllowedUsers!).ToList();
        Assert.Equal(users.Count, pinged.Count);
        Assert.Equal(users.Order(), pinged.Order());
    }

    [Fact]
    public async Task A_failing_ping_line_after_the_card_is_logged_and_the_next_line_still_goes_out()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 260, Snowflake);
        await using var cards = new Cards(ctx);
        var users = Enumerable.Range(1, 259).Select(i => ulong.Parse(Snowflake(i), CultureInfo.InvariantCulture)).ToList();
        var texts = 0;
        cards.Sender.Failure = call => call.Kind == "text" && Interlocked.Increment(ref texts) == 1
            ? Rest(HttpStatusCode.Forbidden, 50013) : null;

        await cards.Alarm.HandleAsync(AlarmPost(users));

        Assert.Single(cards.Sender.Calls, c => c.Kind == "card");
        Assert.Single(cards.Sender.Calls, c => c.Kind == "text");
        Assert.NotNull(await cards.StoredAsync());
        Assert.Contains("403", cards.Logged);
    }

    [Fact]
    public async Task A_failing_card_throws_so_the_worker_falls_back_and_nothing_is_stored()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await using var cards = new Cards(ctx);
        cards.Sender.Failure = call => call.Kind == "card" ? Rest(HttpStatusCode.Forbidden, 50013) : null;

        await Assert.ThrowsAsync<RestException>(() => cards.Alarm.HandleAsync(AlarmPost([2, 3])));

        Assert.Null(await cards.StoredAsync());
    }

    [Fact]
    public async Task An_alarm_that_is_gone_still_goes_out_with_the_queued_card()
    {
        using var ctx = new SqliteTestContext();
        await using var cards = new Cards(ctx);

        await cards.Alarm.HandleAsync(AlarmPost([2]));

        var calls = cards.Sender.Calls.ToList();
        Assert.Equal("Ersatz", Text(calls[0].Card!));
        // no chip
        Assert.Equal("<@2>", calls[1].Text);
        Assert.Equal<ulong>([2], calls[1].Mentions!.AllowedUsers!);
        Assert.Equal(2, calls.Count);
        Assert.Null(await cards.StoredAsync());
    }

    [Fact]
    public async Task A_reping_edits_the_card_and_replies_under_it_pinging_only_the_open()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);

        await cards.Alarm.HandleAsync(AlarmPost([3], reping: true));

        var calls = cards.Sender.Calls.ToList();
        Assert.Equal(["edit", "text"], calls.Select(c => c.Kind));
        Assert.Equal((AlarmChannel, (ulong?)StoredMessage), (calls[0].Channel, calls[0].Message));
        var reply = calls[1];
        Assert.Equal("🔔 Erneut alarmiert: <@3>", reply.Text);
        Assert.Equal(StoredMessage, reply.Message);
        Assert.Equal<ulong>([3], reply.Mentions!.AllowedUsers!);
        Assert.Empty(reply.Mentions.AllowedRoles!);
        Assert.False(reply.Mentions.Everyone);
        Assert.Equal((await cards.RenderAsync()).Hash, (await cards.StoredAsync())!.Value.Hash);
    }

    [Fact]
    public async Task A_reping_without_the_flag_still_finds_the_stored_card()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);

        await cards.Alarm.HandleAsync(AlarmPost([3]));

        Assert.DoesNotContain(cards.Sender.Calls, c => c.Kind == "card");
    }

    [Theory]
    [InlineData(404, null)]
    [InlineData(404, 10008)]
    [InlineData(400, 10008)]
    public async Task A_reping_of_a_deleted_card_posts_a_fresh_card_and_stores_it(int status, int? code)
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);
        cards.Sender.Failure = call => call.Kind == "edit" ? Rest((HttpStatusCode)status, code) : null;

        await cards.Alarm.HandleAsync(AlarmPost([3], reping: true));

        var sent = Assert.Single(cards.Sender.Calls);
        Assert.Equal("card", sent.Kind);
        Assert.Equal<ulong>([3], sent.Mentions!.AllowedUsers!);
        Assert.Equal(501UL, (await cards.StoredAsync())!.Value.Message);
    }

    [Fact]
    public async Task A_reping_whose_edit_fails_otherwise_still_replies()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);
        cards.Sender.Failure = call => call.Kind == "edit" ? Rest(HttpStatusCode.InternalServerError) : null;

        await cards.Alarm.HandleAsync(AlarmPost([3], reping: true));

        var reply = Assert.Single(cards.Sender.Calls);
        Assert.Equal("text", reply.Kind);
        Assert.Equal(StoredMessage, reply.Message);
        Assert.Equal("old", (await cards.StoredAsync())!.Value.Hash);
    }

    [Fact]
    public async Task A_failing_reply_throws_so_the_worker_falls_back()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);
        cards.Sender.Failure = call => call.Kind == "text" ? Rest(HttpStatusCode.Forbidden, 50013) : null;

        await Assert.ThrowsAsync<RestException>(() => cards.Alarm.HandleAsync(AlarmPost([3], reping: true)));
    }

    [Fact]
    public async Task A_long_reping_fits_each_message_and_pings_everybody_open()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 150, Snowflake);
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);
        var users = Enumerable.Range(1, 149).Select(i => ulong.Parse(Snowflake(i), CultureInfo.InvariantCulture)).ToList();

        await cards.Alarm.HandleAsync(AlarmPost(users, reping: true));

        var texts = cards.Sender.Calls.Where(c => c.Kind == "text").ToList();
        Assert.StartsWith("🔔 Erneut alarmiert: ", texts[0].Text);
        Assert.All(texts, t => Assert.True(t.Text!.Length <= 2000));
        Assert.All(texts, t => Assert.Equal(Chips(t.Text!), t.Mentions!.AllowedUsers!));
        Assert.Equal(users, texts.SelectMany(t => t.Mentions!.AllowedUsers!).ToList());
    }

    [Fact]
    public async Task An_update_never_creates_a_card()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await using var cards = new Cards(ctx);

        await cards.Alarm.UpdateAsync(AlarmId);

        Assert.Empty(cards.Sender.Calls);
        Assert.Null(await cards.StoredAsync());
    }

    [Fact]
    public async Task An_update_edits_the_stored_card_only_when_it_changed()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);

        await cards.Alarm.UpdateAsync(AlarmId);
        await cards.Alarm.UpdateAsync(AlarmId);

        var edit = Assert.Single(cards.Sender.Calls);
        Assert.Equal(("edit", AlarmChannel, (ulong?)StoredMessage), (edit.Kind, edit.Channel, edit.Message));
        Assert.Equal((await cards.RenderAsync()).Hash, (await cards.StoredAsync())!.Value.Hash);
    }

    [Fact]
    public async Task An_update_of_a_deleted_card_leaves_it_to_the_next_reping()
    {
        using var ctx = new SqliteTestContext();
        await SeedAlarmAsync(ctx, 3, i => (i + 1).ToString(CultureInfo.InvariantCulture));
        await StoreBoardAsync(ctx);
        await using var cards = new Cards(ctx);
        cards.Sender.Failure = call => call.Kind == "edit" ? Rest(HttpStatusCode.NotFound, 10008) : null;

        await cards.Alarm.UpdateAsync(AlarmId);

        Assert.Empty(cards.Sender.Calls);
        Assert.Equal("old", (await cards.StoredAsync())!.Value.Hash);
    }
}
