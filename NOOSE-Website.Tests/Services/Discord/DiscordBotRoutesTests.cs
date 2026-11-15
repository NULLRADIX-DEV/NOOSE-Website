using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>Bot routing: the decision rule, thread ids and the webhook lookup.</summary>
public class DiscordBotRoutesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Hook = "https://discord.com/api/webhooks/1/secret-token";
    private const string OtherHook = "https://discord.com/api/webhooks/2/other-token";
    private const string LookupBody = """{"id":"1","guild_id":"42","channel_id":"7","token":"secret-token"}""";

    [Fact]
    public void A_webhook_on_our_server_routes_to_the_bot_with_its_channel()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(42, 7), null, Now);

        Assert.Equal(new BotRoute(BotRouteState.Bot, 7), route);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Without_a_token_or_with_the_bot_switched_off_the_webhook_keeps_posting(bool configured, bool enabled)
    {
        var route = DiscordBotRouting.Decide(configured, enabled, 42, new WebhookTarget(42, 7), null, Now);

        Assert.Equal(new BotRoute(BotRouteState.BotOff, null), route);
    }

    [Fact]
    public void Bot_off_wins_over_every_other_reason()
    {
        var route = DiscordBotRouting.Decide(false, true, null, null, Now.AddMinutes(5), Now);

        Assert.Equal(BotRouteState.BotOff, route.State);
    }

    [Fact]
    public void Without_a_server_id_no_webhook_belongs_to_our_server()
    {
        var route = DiscordBotRouting.Decide(true, true, null, new WebhookTarget(42, 7), null, Now);

        Assert.Equal(new BotRoute(BotRouteState.OtherServer, null), route);
    }

    [Fact]
    public void A_webhook_of_another_server_stays_a_webhook()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(43, 7), null, Now);

        Assert.Equal(new BotRoute(BotRouteState.OtherServer, null), route);
    }

    [Fact]
    public void A_webhook_that_could_not_be_resolved_is_unknown()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, null, null, Now);

        Assert.Equal(new BotRoute(BotRouteState.Unknown, null), route);
    }

    [Fact]
    public void A_recent_failure_sends_the_post_straight_to_the_webhook()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(42, 7), Now.AddMinutes(10), Now);

        Assert.Equal(new BotRoute(BotRouteState.Failing, null), route);
    }

    [Fact]
    public void A_failure_that_has_run_out_lets_the_bot_try_again()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(42, 7), Now, Now);

        Assert.Equal(new BotRoute(BotRouteState.Bot, 7), route);
    }

    [Fact]
    public void A_failure_long_past_does_not_hold_the_bot_back()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(42, 7), Now.AddHours(-1), Now);

        Assert.Equal(BotRouteState.Bot, route.State);
    }

    [Fact]
    public void A_failure_on_a_foreign_webhook_still_reads_as_the_other_server()
    {
        var route = DiscordBotRouting.Decide(true, true, 42, new WebhookTarget(43, 7), Now.AddMinutes(10), Now);

        Assert.Equal(BotRouteState.OtherServer, route.State);
    }

    [Fact]
    public void The_thread_id_is_read_from_the_query()
        => Assert.Equal(99UL, DiscordBotRouting.ThreadId("https://discord.com/api/webhooks/1/abc?thread_id=99"));

    [Fact]
    public void The_thread_id_is_found_among_other_parameters()
        => Assert.Equal(99UL, DiscordBotRouting.ThreadId("https://discord.com/api/webhooks/1/abc?wait=true&thread_id=99&x=1"));

    [Fact]
    public void A_url_without_a_query_has_no_thread()
        => Assert.Null(DiscordBotRouting.ThreadId("https://discord.com/api/webhooks/1/abc"));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=x")]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=")]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=0")]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=-5")]
    [InlineData("https://discord.com/api/webhooks/1/abc?thread_id=99999999999999999999999")]
    [InlineData("https://discord.com/api/webhooks/1/abc?wait=true")]
    [InlineData("")]
    public void Anything_but_a_snowflake_is_no_thread(string url)
        => Assert.Null(DiscordBotRouting.ThreadId(url));

    private static readonly DiscordBotSettings On = new(true, null, null, [], false);

    private readonly SqliteTestContext _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string? json = null)
        => new(status) { Content = new StringContent(json ?? "", Encoding.UTF8, "application/json") };

    private static DiscordWebhookConfig Webhooks(bool enabled, string? alarm, string? board)
        => new(enabled, "https://fibhq.com",
            new Dictionary<NotificationType, string?>
            {
                [NotificationType.EmergencyAlarm] = alarm,
                [NotificationType.FactionThreatBoard] = board,
            },
            new Dictionary<NotificationType, string?>(), true);

    private (DiscordBotRoutes Routes, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, bool token = true,
        DiscordBotSettings? settings = null, string? guild = "42", DiscordWebhookConfig? webhooks = null)
    {
        // replace server id
        using (var db = _db.NewContext())
        {
            db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DiscordGuildId).ExecuteDelete();
            if (guild is not null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordGuildId, Value = guild });
                db.SaveChanges();
            }
        }
        var client = Substitute.For<IDiscordBotClient>();
        client.IsConfigured.Returns(token);
        var settingsService = Substitute.For<IDiscordBotSettingsService>();
        settingsService.GetAsync(Arg.Any<CancellationToken>()).Returns(settings ?? On);
        var webhookService = Substitute.For<IDiscordWebhookService>();
        webhookService.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(webhooks ?? Webhooks(true, null, null));
        var scopes = new ServiceCollection()
            .AddSingleton(client).AddSingleton(settingsService).AddSingleton(webhookService)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var handler = new StubHandler(respond ?? (_ => Reply(HttpStatusCode.OK, LookupBody)));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("discord").Returns(_ => new HttpClient(handler, disposeHandler: false));
        var routes = new DiscordBotRoutes(factory, _db.Factory, new MemoryCache(new MemoryCacheOptions()), scopes, TimeProvider.System);
        return (routes, handler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_category_without_a_webhook_has_no_route(string? url)
    {
        var (routes, handler) = Build();

        Assert.Equal(new BotRoute(BotRouteState.NoWebhook, null), await routes.ForWebhookAsync(url));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task The_channel_comes_from_looking_up_the_webhook_without_its_query()
    {
        var (routes, handler) = Build();

        var route = await routes.ForWebhookAsync(Hook + "?wait=true");

        Assert.Equal(new BotRoute(BotRouteState.Bot, 7), route);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal(Hook, call.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_thread_webhook_posts_into_the_thread_not_the_parent_channel()
    {
        var (routes, _) = Build();

        var route = await routes.ForWebhookAsync(Hook + "?thread_id=99");

        Assert.Equal(new BotRoute(BotRouteState.Bot, 99), route);
    }

    [Fact]
    public async Task A_webhook_of_another_server_is_left_to_the_webhook()
    {
        var (routes, _) = Build(_ => Reply(HttpStatusCode.OK, """{"guild_id":"43","channel_id":"7"}"""));

        Assert.Equal(BotRouteState.OtherServer, (await routes.ForWebhookAsync(Hook)).State);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_looked_up()
    {
        var (routes, handler) = Build(token: false);

        Assert.Equal(BotRouteState.BotOff, (await routes.ForWebhookAsync(Hook)).State);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task With_the_bot_switched_off_nothing_is_looked_up()
    {
        var (routes, handler) = Build(settings: On with { Enabled = false });

        Assert.Equal(BotRouteState.BotOff, (await routes.ForWebhookAsync(Hook)).State);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task Without_a_server_id_nothing_is_looked_up(string? guild)
    {
        var (routes, handler) = Build(guild: guild);

        Assert.Equal(BotRouteState.OtherServer, (await routes.ForWebhookAsync(Hook)).State);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_lookup_is_asked_only_once_per_webhook()
    {
        var (routes, handler) = Build();

        await routes.ForWebhookAsync(Hook);
        await routes.ForWebhookAsync(Hook);
        await routes.ForWebhookAsync(OtherHook);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"channel_id":"7"}""")]
    [InlineData(HttpStatusCode.OK, """{"guild_id":42,"channel_id":"7"}""")]
    public async Task A_lookup_that_fails_is_unknown_and_not_asked_again(HttpStatusCode status, string body)
    {
        var (routes, handler) = Build(_ => Reply(status, body));

        Assert.Equal(new BotRoute(BotRouteState.Unknown, null), await routes.ForWebhookAsync(Hook));
        Assert.Equal(BotRouteState.Unknown, (await routes.ForWebhookAsync(Hook)).State);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_lookup_that_throws_is_unknown()
    {
        var (routes, _) = Build(_ => throw new HttpRequestException("no route to host"));

        Assert.Equal(BotRouteState.Unknown, (await routes.ForWebhookAsync(Hook)).State);
    }

    [Fact]
    public async Task A_cancelled_lookup_is_not_swallowed()
    {
        var (routes, _) = Build(_ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => routes.ForWebhookAsync(Hook, cts.Token));
    }

    [Fact]
    public async Task A_marked_webhook_goes_straight_to_the_webhook_while_others_keep_the_bot()
    {
        var (routes, _) = Build();
        await routes.ForWebhookAsync(Hook);

        routes.MarkFailing(Hook);

        Assert.Equal(new BotRoute(BotRouteState.Failing, null), await routes.ForWebhookAsync(Hook));
        Assert.Equal(BotRouteState.Bot, (await routes.ForWebhookAsync(OtherHook)).State);
    }

    [Fact]
    public async Task Marking_an_empty_url_marks_nothing()
    {
        var (routes, _) = Build();

        routes.MarkFailing("");
        routes.MarkFailing("  ");

        Assert.Equal(BotRouteState.Bot, (await routes.ForWebhookAsync(Hook)).State);
    }

    [Fact]
    public async Task The_alarm_goes_to_the_bots_own_alarm_channel_before_the_webhook()
    {
        var (routes, handler) = Build(settings: On with { AlarmChannelId = 555 }, webhooks: Webhooks(true, Hook, null));

        Assert.Equal(555UL, await routes.AlarmChannelAsync());
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Without_an_alarm_channel_the_alarm_follows_its_webhook()
    {
        var (routes, _) = Build(webhooks: Webhooks(true, Hook, null));

        Assert.Equal(7UL, await routes.AlarmChannelAsync());
    }

    [Fact]
    public async Task An_alarm_webhook_the_bot_cannot_use_leaves_the_alarm_to_the_webhook()
    {
        var (routes, _) = Build(webhooks: Webhooks(true, Hook, null));
        routes.MarkFailing(Hook);

        Assert.Null(await routes.AlarmChannelAsync());
    }

    [Fact]
    public async Task With_webhooks_off_or_unset_the_alarm_has_no_channel()
    {
        var (off, _) = Build(webhooks: Webhooks(false, Hook, null));
        var (unset, _) = Build(webhooks: Webhooks(true, null, null));

        Assert.Null(await off.AlarmChannelAsync());
        Assert.Null(await unset.AlarmChannelAsync());
    }

    [Fact]
    public async Task With_the_bot_off_neither_the_alarm_nor_the_board_has_a_channel()
    {
        var off = On with { Enabled = false, AlarmChannelId = 555, FactionChannelId = 666 };
        var (routes, handler) = Build(settings: off, webhooks: Webhooks(true, Hook, Hook));
        var (tokenless, _) = Build(token: false, settings: On with { AlarmChannelId = 555, FactionChannelId = 666 });

        Assert.Null(await routes.AlarmChannelAsync());
        Assert.Null(await routes.FactionBoardChannelAsync());
        Assert.Null(await tokenless.AlarmChannelAsync());
        Assert.Null(await tokenless.FactionBoardChannelAsync());
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task The_board_goes_to_the_bots_faction_channel_before_the_webhook()
    {
        var (routes, handler) = Build(settings: On with { FactionChannelId = 666 }, webhooks: Webhooks(true, null, Hook));

        Assert.Equal(666UL, await routes.FactionBoardChannelAsync());
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Without_a_faction_channel_the_board_follows_its_webhook_and_not_the_alarm_one()
    {
        var (routes, _) = Build(
            respond: r => r.RequestUri!.ToString() == OtherHook
                ? Reply(HttpStatusCode.OK, """{"guild_id":"42","channel_id":"8"}""")
                : Reply(HttpStatusCode.OK, LookupBody),
            webhooks: Webhooks(true, Hook, OtherHook));

        Assert.Equal(8UL, await routes.FactionBoardChannelAsync());
        Assert.Equal(7UL, await routes.AlarmChannelAsync());
    }
}
