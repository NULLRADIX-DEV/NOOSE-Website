using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetCord.Rest;
using NOOSE_Website.Infrastructure.Discord.Bot;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The post queue, the failure rule and the worker that sends a post through the bot or, failing that, the webhook.</summary>
public class DiscordPostQueueTests
{
    private const string Hook = "https://discord.test/api/webhooks/1/secret-token?thread_id=7";
    private const ulong Channel = 700000000000000001;
    private const ulong Marker = 999;
    private const ulong FirstUser = 100000000000000001;

    private sealed record SentMessage(
        ulong Channel, IReadOnlyList<IMessageComponentProperties>? Card, string? Text, AllowedMentionsProperties Mentions, ulong? ReplyTo);

    private sealed class FakeSender : IDiscordBotSender
    {
        private readonly SemaphoreSlim _sent = new(0);

        public ConcurrentQueue<SentMessage> Messages { get; } = new();

        /// <summary>The exception a send to this channel throws, if any.</summary>
        public Func<ulong, Exception?> Failure { get; set; } = _ => null;

        /// <summary>Holds a send until it is released or cancelled.</summary>
        public Func<ulong, CancellationToken, Task>? Hold { get; set; }

        public async Task<ulong> SendAsync(ulong channelId, IReadOnlyList<IMessageComponentProperties> card,
            AllowedMentionsProperties mentions, ulong? replyTo = null, CancellationToken cancellationToken = default)
        {
            await RecordAsync(new SentMessage(channelId, card, null, mentions, replyTo), cancellationToken);
            return 1;
        }

        public Task SendTextAsync(ulong channelId, string content, AllowedMentionsProperties mentions,
            ulong? replyTo = null, CancellationToken cancellationToken = default)
            => RecordAsync(new SentMessage(channelId, null, content, mentions, replyTo), cancellationToken);

        public Task EditAsync(ulong channelId, ulong messageId, IReadOnlyList<IMessageComponentProperties> card,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task WaitForSendAsync(ulong channelId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!Messages.Any(m => m.Channel == channelId))
            {
                Assert.True(await _sent.WaitAsync(TimeSpan.FromSeconds(10)) && DateTime.UtcNow < deadline,
                    $"No send to channel {channelId}.");
            }
        }

        private async Task RecordAsync(SentMessage message, CancellationToken cancellationToken)
        {
            if (Hold is { } hold)
            {
                await hold(message.Channel, cancellationToken);
            }
            if (Failure(message.Channel) is { } failure)
            {
                throw failure;
            }
            Messages.Enqueue(message);
            _sent.Release();
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly SemaphoreSlim _arrived = new(0);

        public ConcurrentQueue<(string Url, string Body)> Posts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts.Enqueue((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            _arrived.Release();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        public async Task WaitForPostsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10)), $"Expected {count} webhook posts, saw {i}.");
            }
        }
    }

    private sealed class ListLogger : ILogger<DiscordPostWorker>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private sealed class Host : IAsyncDisposable
    {
        public Host()
        {
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient("discord").Returns(_ => new HttpClient(Handler, disposeHandler: false));
            Worker = new DiscordPostWorker(Queue, Sender, Routes, factory, Log);
        }

        public DiscordPostQueue Queue { get; } = new();

        public FakeSender Sender { get; } = new();

        public IDiscordBotRoutes Routes { get; } = Substitute.For<IDiscordBotRoutes>();

        public CapturingHandler Handler { get; } = new();

        public ListLogger Log { get; } = new();

        public DiscordPostWorker Worker { get; }

        public string Logged => string.Join('\n', Log.Lines);

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    private static DiscordPost Post(
        ulong channel = Channel, NotificationType category = NotificationType.Announcement,
        IReadOnlyList<ulong>? users = null, ulong? role = null, IReadOnlyList<string>? extraPings = null,
        WebhookFallback? fallback = null, string? alarmId = null)
        => new(category, channel, [DiscordCards.Card(DiscordCards.Bronze, DiscordCards.Text("Karte"))],
            users ?? [], role, extraPings ?? [], fallback, alarmId);

    private static WebhookFallback Fallback(params string[] contents)
        => new(Hook, contents.Select(c => (object)new { content = c, username = "FIB" }).ToList());

    private static RestException Rest(HttpStatusCode status, int? code = null)
        => code is { } c
            ? new RestException(status, status.ToString(), new RestError(c, "Discord says no", null!))
            : new RestException(status, status.ToString());

    private static IReadOnlyList<ulong> Users(int count)
        => Enumerable.Range(0, count).Select(i => FirstUser + (ulong)i).ToList();

    private static string Content(string body) => JsonDocument.Parse(body).RootElement.GetProperty("content").GetString()!;

    // ---------------------------------------------------------------- queue

    [Fact]
    public void Alarm_is_read_first()
    {
        var queue = new DiscordPostQueue();
        queue.Enqueue(Post(channel: 1));
        queue.Enqueue(Post(channel: 2));
        queue.Enqueue(Post(channel: 3, alarmId: "alarm-1"));

        Assert.True(queue.TryRead(out var first));
        Assert.True(queue.TryRead(out var second));
        Assert.True(queue.TryRead(out var third));
        Assert.False(queue.TryRead(out var none));

        Assert.Equal<ulong>([3, 1, 2], [first!.ChannelId, second!.ChannelId, third!.ChannelId]);
        Assert.Null(none);
    }

    [Fact]
    public async Task ReadAsync_hands_out_the_alarm_before_older_posts()
    {
        var queue = new DiscordPostQueue();
        queue.Enqueue(Post(channel: 1));
        queue.Enqueue(Post(channel: 2, alarmId: "alarm-1"));

        var first = await queue.ReadAsync(CancellationToken.None);
        var second = await queue.ReadAsync(CancellationToken.None);

        Assert.Equal<ulong>([2, 1], [first.ChannelId, second.ChannelId]);
    }

    [Fact]
    public async Task ReadAsync_waits_for_a_post_and_a_later_alarm_still_wins()
    {
        var queue = new DiscordPostQueue();
        var read = queue.ReadAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(read.IsCompleted);

        queue.Enqueue(Post(channel: 5));

        Assert.Equal<ulong>(5, (await read.WaitAsync(TimeSpan.FromSeconds(10))).ChannelId);
    }

    [Fact]
    public async Task ReadAsync_stops_when_cancelled()
    {
        var queue = new DiscordPostQueue();
        using var cts = new CancellationTokenSource();
        var read = queue.ReadAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Taking_a_post_with_TryRead_leaves_the_count_right_for_ReadAsync()
    {
        var queue = new DiscordPostQueue();
        queue.Enqueue(Post(channel: 1));
        queue.Enqueue(Post(channel: 2));

        Assert.True(queue.TryRead(out var taken));
        var next = await queue.ReadAsync(CancellationToken.None);
        var empty = queue.ReadAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);

        Assert.Equal<ulong>([1, 2], [taken!.ChannelId, next.ChannelId]);
        Assert.False(empty.IsCompleted);
    }

    // ---------------------------------------------------------------- failure rule

    [Theory]
    [InlineData(403, 50013, true)]
    [InlineData(404, 10003, true)]
    [InlineData(403, null, true)]
    [InlineData(404, null, true)]
    [InlineData(400, 50001, true)]
    [InlineData(400, 50083, true)]
    [InlineData(429, null, false)]
    [InlineData(500, null, false)]
    [InlineData(400, 50035, false)]
    public void A_missing_channel_or_right_is_a_channel_problem(int status, int? code, bool expected)
        => Assert.Equal(expected, DiscordSendFailure.IsChannelProblem(status, code));

    // ---------------------------------------------------------------- worker

    [Fact]
    public async Task A_sent_card_needs_no_fallback()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(fallback: Fallback("webhook")));
        host.Queue.Enqueue(Post(channel: Marker));
        await host.Sender.WaitForSendAsync(Marker);

        Assert.Equal(Channel, host.Sender.Messages.First().Channel);
        Assert.Empty(host.Handler.Posts);
        host.Routes.DidNotReceiveWithAnyArgs().MarkFailing(default!);
    }

    [Fact]
    public async Task A_channel_problem_marks_the_webhook_failing_and_posts_every_fallback_payload()
    {
        await using var host = new Host();
        host.Sender.Failure = channel => channel == Channel ? Rest(HttpStatusCode.Forbidden, 50013) : null;
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(fallback: Fallback("eins", "zwei")));
        await host.Handler.WaitForPostsAsync(2);

        host.Routes.Received(1).MarkFailing(Hook);
        Assert.Equal([Hook, Hook], host.Handler.Posts.Select(p => p.Url));
        Assert.Equal(["eins", "zwei"], host.Handler.Posts.Select(p => Content(p.Body)));
        Assert.Equal("FIB", JsonDocument.Parse(host.Handler.Posts.First().Body).RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_server_error_posts_the_fallback_without_marking_the_webhook_failing()
    {
        await using var host = new Host();
        host.Sender.Failure = channel => channel == Channel ? Rest(HttpStatusCode.InternalServerError) : null;
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(fallback: Fallback("webhook")));
        await host.Handler.WaitForPostsAsync(1);

        Assert.Equal(Hook, host.Handler.Posts.Single().Url);
        host.Routes.DidNotReceiveWithAnyArgs().MarkFailing(default!);
    }

    [Fact]
    public async Task A_failure_that_is_no_rest_error_also_falls_back_without_marking()
    {
        await using var host = new Host();
        host.Sender.Failure = channel => channel == Channel ? new HttpRequestException("boom") : null;
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(fallback: Fallback("webhook")));
        await host.Handler.WaitForPostsAsync(1);

        host.Routes.DidNotReceiveWithAnyArgs().MarkFailing(default!);
    }

    [Fact]
    public async Task Without_a_webhook_a_failed_send_is_only_logged_and_the_next_post_still_goes_out()
    {
        await using var host = new Host();
        host.Sender.Failure = channel => channel == Channel ? Rest(HttpStatusCode.Forbidden, 50013) : null;
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(category: NotificationType.PersonnelEntry, fallback: null));
        host.Queue.Enqueue(Post(channel: Marker));
        await host.Sender.WaitForSendAsync(Marker);

        Assert.Empty(host.Handler.Posts);
        host.Routes.DidNotReceiveWithAnyArgs().MarkFailing(default!);
        Assert.Contains("PersonnelEntry", host.Logged);
        Assert.Contains("403", host.Logged);
    }

    [Fact]
    public async Task The_log_names_the_category_and_status_but_never_the_webhook()
    {
        await using var host = new Host();
        host.Sender.Failure = channel => channel == Channel ? Rest(HttpStatusCode.Forbidden, 50013) : null;
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(category: NotificationType.DecorationAwarded, fallback: Fallback("webhook")));
        await host.Handler.WaitForPostsAsync(1);
        host.Queue.Enqueue(Post(channel: Marker));
        await host.Sender.WaitForSendAsync(Marker);

        Assert.Contains("403", host.Logged);
        Assert.DoesNotContain("secret-token", host.Logged);
        Assert.DoesNotContain("discord.test", host.Logged);
        Assert.DoesNotContain("Discord says no", host.Logged);
    }

    // ---------------------------------------------------------------- mentions

    [Fact]
    public async Task The_card_may_ping_exactly_the_listed_users()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(users: [FirstUser, FirstUser + 1]));
        await host.Sender.WaitForSendAsync(Channel);

        var mentions = host.Sender.Messages.Single().Mentions;
        Assert.Equal<ulong>([FirstUser, FirstUser + 1], mentions.AllowedUsers!);
        Assert.NotNull(mentions.AllowedRoles);
        Assert.Empty(mentions.AllowedRoles);
        Assert.False(mentions.Everyone);
        Assert.False(mentions.ReplyMention);
    }

    [Fact]
    public async Task The_card_may_ping_exactly_the_one_role()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(role: 800000000000000001));
        await host.Sender.WaitForSendAsync(Channel);

        var mentions = host.Sender.Messages.Single().Mentions;
        Assert.Equal<ulong>([800000000000000001], mentions.AllowedRoles!);
        Assert.NotNull(mentions.AllowedUsers);
        Assert.Empty(mentions.AllowedUsers);
        Assert.False(mentions.Everyone);
    }

    [Fact]
    public async Task A_card_without_ping_targets_may_ping_nobody()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post());
        await host.Sender.WaitForSendAsync(Channel);

        var mentions = host.Sender.Messages.Single().Mentions;
        Assert.NotNull(mentions.AllowedUsers);
        Assert.NotNull(mentions.AllowedRoles);
        Assert.Empty(mentions.AllowedUsers);
        Assert.Empty(mentions.AllowedRoles);
        Assert.False(mentions.Everyone);
    }

    [Fact]
    public async Task More_than_a_hundred_users_are_pinged_by_the_card_and_the_extra_lines_with_nobody_left_out()
    {
        await using var host = new Host();
        var users = Users(250);
        var lines = NotificationCards.PingLines(users.Select(u => u.ToString()).ToList());
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Post(users: users, extraPings: lines.Rest));
        await host.Sender.WaitForSendAsync(Channel);
        host.Queue.Enqueue(Post(channel: Marker));
        await host.Sender.WaitForSendAsync(Marker);

        var sent = host.Sender.Messages.Where(m => m.Channel == Channel).ToList();
        Assert.Equal(3, sent.Count);
        Assert.NotNull(sent[0].Card);
        Assert.Equal(lines.Rest, sent.Skip(1).Select(m => m.Text));
        Assert.All(sent, m => Assert.True(m.Mentions.AllowedUsers!.Count() <= 100));
        Assert.Equal(users.Take(100), sent[0].Mentions.AllowedUsers!);
        foreach (var line in sent.Skip(1))
        {
            Assert.Equal(line.Text!.Split(' ').Select(chip => ulong.Parse(chip.Trim('<', '@', '>'))), line.Mentions.AllowedUsers!);
            Assert.Empty(line.Mentions.AllowedRoles!);
        }
        Assert.Equal(users, sent.SelectMany(m => m.Mentions.AllowedUsers!).ToList());
    }

    // ---------------------------------------------------------------- stopping

    [Fact]
    public async Task Stopping_ends_the_worker_while_it_waits_for_posts()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);
        // loop is waiting
        host.Queue.Enqueue(Post());
        await host.Sender.WaitForSendAsync(Channel);

        await host.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(host.Worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stopping_sends_what_is_still_queued_with_the_alarm_first()
    {
        await using var host = new Host();
        host.Queue.Enqueue(Post(channel: 1));
        host.Queue.Enqueue(Post(channel: 2));
        host.Queue.Enqueue(Post(channel: 3, alarmId: "alarm-1"));

        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal<ulong>([3, 1, 2], host.Sender.Messages.Select(m => m.Channel));
        Assert.False(host.Queue.TryRead(out _));
    }

    [Fact]
    public async Task Stopping_gives_queued_posts_the_webhook_fallback_too()
    {
        await using var host = new Host();
        host.Sender.Failure = _ => Rest(HttpStatusCode.Forbidden, 50013);
        host.Queue.Enqueue(Post(fallback: Fallback("webhook")));

        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(Hook, host.Handler.Posts.Single().Url);
    }

    [Fact]
    public async Task The_drain_ends_with_the_stop_token_and_leaves_the_rest_in_the_queue()
    {
        await using var host = new Host();
        host.Sender.Hold = (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        host.Queue.Enqueue(Post(channel: 1));
        host.Queue.Enqueue(Post(channel: 2));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await host.Worker.StopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(host.Sender.Messages);
        Assert.Empty(host.Handler.Posts);
        host.Routes.DidNotReceiveWithAnyArgs().MarkFailing(default!);
        Assert.True(host.Queue.TryRead(out var left));
        Assert.Equal<ulong>(2, left!.ChannelId);
    }
}
