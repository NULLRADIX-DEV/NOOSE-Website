using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The webhook service decides per post: a card in the bot queue, or the webhook post of today.</summary>
public class DiscordWebhookRoutingTests
{
    private const string Hook = "https://discord.test/api/webhooks/1/token?thread_id=7";
    private const string FirstId = "111111111111111111";
    private const string SecondId = "222222222222222222";
    private const ulong Channel = 7;
    private static readonly DateTime At = new(2026, 11, 2, 14, 3, 0, DateTimeKind.Utc);
    private static readonly BotRoute ToBot = new(BotRouteState.Bot, Channel);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Posts.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class Host
    {
        public required DiscordWebhookService Service { get; init; }

        public required CapturingHandler Handler { get; init; }

        public required FakeBotRoutes Routes { get; init; }

        public required DiscordPostQueue Queue { get; init; }
    }

    private static async Task<Host> HostAsync(SqliteTestContext ctx, NotificationType type, BotRoute? route = null,
        bool includeHeadline = true, bool enabled = true, IServiceProvider? services = null,
        Func<AppDbContext, Task>? seed = null)
    {
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordEnabled, Value = enabled ? "true" : "false" });
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.DiscordIncludeHeadline, Value = includeHeadline ? "true" : "false",
            });
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordWebhookPrefix + type, Value = Hook });
            db.Users.Add(Seed.Agent("a1", configure: a => a.DiscordId = FirstId));
            db.Users.Add(Seed.Agent("a2", configure: a => a.DiscordId = SecondId));
            await db.SaveChangesAsync();
            if (seed is not null)
            {
                await seed(db);
                await db.SaveChangesAsync();
            }
        }

        var handler = new CapturingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("discord").Returns(_ => new HttpClient(handler, disposeHandler: false));
        var routes = new FakeBotRoutes { Route = route ?? ToBot };
        var queue = new DiscordPostQueue();
        var service = new DiscordWebhookService(factory, ctx.Factory, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordWebhookService>.Instance, routes, queue,
            services ?? new ServiceCollection().BuildServiceProvider());
        return new Host { Service = service, Handler = handler, Routes = routes, Queue = queue };
    }

    private static DiscordPost Queued(Host host)
    {
        Assert.True(host.Queue.TryRead(out var post), "Nothing was queued.");
        Assert.False(host.Queue.TryRead(out _), "More than one post was queued.");
        return post;
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

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

    private static List<string> Texts(DiscordPost post)
        => Walk(post.Card).OfType<TextDisplayProperties>().Select(t => t.Content).ToList();

    private static string Everything(DiscordPost post) => string.Join("\n", Texts(post));

    private static List<string> CustomIds(DiscordPost post)
        => Walk(post.Card).OfType<ButtonProperties>().Select(b => b.CustomId).ToList();

    // ---------------------------------------------------------------- brief

    [Fact]
    public async Task Routed_category_is_queued_not_posted()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned);

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1"]);

        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Equal(NotificationType.JobAssigned, post.Category);
        Assert.Equal([ulong.Parse(FirstId)], post.PingUsers);
        Assert.Null(post.PingRole);
        Assert.Empty(post.ExtraPings);
        Assert.Equal($"<@{FirstId}>", Texts(post)[0]);
        Assert.Equal([Hook], host.Routes.Asked);
        var expected = new
        {
            content = $"<@{FirstId}> {DiscordWebhookService.PersonalText(NotificationType.JobAssigned, DiscordWebhookConfig.DefaultBaseUrl, "/aufgaben/a")}",
            username = "FIB",
            allowed_mentions = new { parse = Array.Empty<string>(), users = new[] { FirstId } },
        };
        Assert.Equal(Hook, post.Fallback!.Url);
        Assert.Equal(Json(expected), Json(Assert.Single(post.Fallback.Payloads)));
    }

    [Theory]
    [InlineData(BotRouteState.BotOff)]
    [InlineData(BotRouteState.OtherServer)]
    [InlineData(BotRouteState.Failing)]
    [InlineData(BotRouteState.Unknown)]
    [InlineData(BotRouteState.NoWebhook)]
    public async Task Unrouted_category_posts_by_webhook(BotRouteState state)
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, new BotRoute(state, null));

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1"]);

        Assert.Single(host.Handler.Posts);
        Assert.False(host.Queue.TryRead(out _));
    }

    [Fact]
    public async Task A_failing_route_lookup_posts_by_webhook()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned);
        host.Routes.Failure = new InvalidOperationException("kaputt");

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1"]);

        Assert.Single(host.Handler.Posts);
        Assert.False(host.Queue.TryRead(out _));
    }

    [Fact]
    public async Task The_fallback_is_the_body_the_webhook_path_posts()
    {
        using var ctx = new SqliteTestContext();
        var bot = await HostAsync(ctx, NotificationType.JobAssigned);
        await bot.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1", "a2"]);
        using var plainCtx = new SqliteTestContext();
        var plain = await HostAsync(plainCtx, NotificationType.JobAssigned, new BotRoute(BotRouteState.BotOff, null));
        await plain.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1", "a2"]);

        var fallback = Assert.Single(Queued(bot).Fallback!.Payloads);

        Assert.Equal(Assert.Single(plain.Handler.Posts), Json(fallback));
    }

    [Fact]
    public async Task Role_category_pings_role()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.Announcement);
        var role = DiscordRouting.DefaultRole(NotificationType.Announcement);

        await host.Service.PushAsync(NotificationType.Announcement, "/brett/x", ["a1", "a2"]);

        var post = Queued(host);
        Assert.Equal(ulong.Parse(role), post.PingRole);
        Assert.Empty(post.PingUsers);
        Assert.Empty(post.ExtraPings);
        Assert.Equal($"<@&{role}>", Texts(post)[0]);
    }

    [Fact]
    public async Task Termination_pings_nobody()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.AgentTerminated);

        await host.Service.PushTerminationAsync("a1", "Max Mustermann", "a2", "Exec", At, "Grund", "/personal/a1");

        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Empty(post.PingUsers);
        Assert.Null(post.PingRole);
        Assert.Empty(post.ExtraPings);
        var text = Everything(post);
        Assert.Contains("Max Mustermann", text);
        Assert.Contains($"<@{FirstId}>", text);
        Assert.Contains($"<@{SecondId}>", text);
        // no ping line, the header opens the card
        Assert.StartsWith("### ", Texts(post)[0]);
        var mentions = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement.GetProperty("allowed_mentions");
        Assert.Empty(mentions.GetProperty("parse").EnumerateArray());
        Assert.False(mentions.TryGetProperty("users", out _));
        Assert.False(mentions.TryGetProperty("roles", out _));
    }

    [Fact]
    public async Task Bool_methods_report_queued_as_sent()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.Announcement);

        var sent = await host.Service.PushCustomAsync(NotificationType.Announcement, "**Bester Agent** ist a1", "/bestenliste");

        Assert.True(sent);
        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Contains("**Bester Agent** ist a1", Everything(post));
        Assert.Equal(ulong.Parse(DiscordRouting.DefaultRole(NotificationType.Announcement)), post.PingRole);
        var body = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement.GetProperty("content").GetString()!;
        Assert.EndsWith("**Bester Agent** ist a1\nhttps://fibhq.com/bestenliste", body);
    }

    [Fact]
    public async Task Over_100_recipients_split()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, seed: db =>
        {
            for (var i = 0; i < 150; i++)
            {
                var id = (300000000000000000UL + (ulong)i).ToString(CultureInfo.InvariantCulture);
                db.Users.Add(Seed.Agent($"bulk-{i}", configure: a => a.DiscordId = id));
            }
            return Task.CompletedTask;
        });
        var recipients = Enumerable.Range(0, 150).Select(i => $"bulk-{i}").ToList();

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", recipients);

        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal(150, post.PingUsers.Count);
        Assert.Equal(150, post.PingUsers.Distinct().Count());
        var extra = Assert.Single(post.ExtraPings);
        Assert.Equal(100, Texts(post)[0].Split(' ').Length);
        Assert.Equal(50, extra.Split(' ').Length);
        // the card line and the extra line together name everybody, once
        Assert.Equal(150, Texts(post)[0].Split(' ').Concat(extra.Split(' ')).Distinct().Count());
        // the webhook fallback keeps today's batches
        Assert.Equal(2, post.Fallback!.Payloads.Count);
    }

    // ---------------------------------------------------------------- contracts

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Announcement_card_offers_acknowledge_only_when_it_is_required(bool required)
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.Announcement, seed: db =>
        {
            db.Announcements.Add(new Announcement
            {
                Id = "ann-1", CaseNumber = "FIB-N-2026-0001", Title = "Dienstanweisung", AcknowledgmentRequired = required,
            });
            return Task.CompletedTask;
        });

        await host.Service.PushAsync(NotificationType.Announcement, "/brett/ann-1", null);

        Assert.Equal(required, CustomIds(Queued(host)).Contains("fib:cack:ann-1"));
    }

    [Fact]
    public async Task An_announcement_that_is_gone_offers_no_acknowledge()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.Announcement);

        await host.Service.PushAsync(NotificationType.Announcement, "/brett/missing", null);

        Assert.DoesNotContain(CustomIds(Queued(host)), id => id.StartsWith("fib:cack:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NotificationType.Announcement, true, true)]
    [InlineData(NotificationType.Announcement, false, false)]
    [InlineData(NotificationType.Recruiting, true, true)]
    [InlineData(NotificationType.Mention, true, false)]
    [InlineData(NotificationType.EmergencyAlarm, false, true)]
    public async Task The_headline_reaches_the_card_only_where_the_webhook_shows_it(
        NotificationType type, bool includeHeadline, bool shown)
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, type, includeHeadline: includeHeadline);

        await host.Service.PushAsync(type, "/x/1", ["a1"], "Titel der Meldung");

        var post = Queued(host);
        Assert.Equal(shown, Everything(post).Contains("Titel der Meldung"));
        Assert.Equal(shown, Json(Assert.Single(post.Fallback!.Payloads)).Contains("Titel der Meldung"));
    }

    [Fact]
    public async Task A_recipient_without_a_discord_id_is_posted_without_a_ping()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, seed: db =>
        {
            db.Users.Add(Seed.Agent("demo", configure: a => a.DiscordId = "demo"));
            return Task.CompletedTask;
        });

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["demo"]);

        var post = Queued(host);
        Assert.Empty(post.PingUsers);
        Assert.StartsWith("### ", Texts(post)[0]);
    }

    [Fact]
    public async Task A_switched_off_config_neither_asks_the_routes_nor_queues()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, enabled: false);

        await host.Service.PushAsync(NotificationType.JobAssigned, "/aufgaben/a", ["a1"]);

        Assert.Empty(host.Routes.Asked);
        Assert.Empty(host.Handler.Posts);
        Assert.False(host.Queue.TryRead(out _));
    }

    // ---------------------------------------------------------------- the other methods

    [Fact]
    public async Task A_personnel_entry_pings_the_subject_and_keeps_its_embed_as_fallback()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.PersonnelEntry);

        await host.Service.PushPersonnelEntryAsync("a1", "Max Mustermann", "Verwarnung", At, "Verspätet", ["Exec Eins"],
            "/personal/a1");

        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal([ulong.Parse(FirstId)], post.PingUsers);
        Assert.Equal($"<@{FirstId}>", Texts(post)[0]);
        Assert.Contains("Max Mustermann", Everything(post));
        var embed = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement.GetProperty("embeds")[0];
        Assert.Equal("📝 EINTRAG", embed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_decoration_pings_the_honoured_agent_and_keeps_its_embed_as_fallback()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.DecorationAwarded);

        await host.Service.PushDecorationAsync("a2", "Falke", "Verdienstkreuz", At, "Für Mut", "Chief", "/personal/a2");

        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal([ulong.Parse(SecondId)], post.PingUsers);
        Assert.Contains("Falke", Everything(post));
        var embed = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement.GetProperty("embeds")[0];
        Assert.Equal("🎖️ EHRUNG", embed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_wanted_card_pings_the_role_and_reports_queued_as_sent()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.PublicWantedPublished);
        var wanted = new WantedDiscordEmbed("Öffentliche Fahndung", "/gesucht/FIB-FA-1", 0xEF6C00,
            [new WantedDiscordField("Name", "Max Mustermann", true)], null, null, "Federal Investigation Bureau", At);

        var sent = await host.Service.PushWantedCardAsync(NotificationType.PublicWantedPublished, wanted);

        Assert.True(sent);
        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal(ulong.Parse(DiscordRouting.DefaultRole(NotificationType.PublicWantedPublished)), post.PingRole);
        Assert.Contains("Max Mustermann", Everything(post));
        var embed = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement.GetProperty("embeds")[0];
        Assert.Equal("https://fibhq.com/gesucht/FIB-FA-1", embed.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_digest_allows_exactly_its_users_and_reports_queued_as_sent()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.PingDigest);

        var sent = await host.Service.PushDigestAsync($"<@{FirstId}> hat 2 offene Punkte", [FirstId, "demo"], "/benachrichtigungen");

        Assert.True(sent);
        Assert.Empty(host.Handler.Posts);
        var post = Queued(host);
        Assert.Equal([ulong.Parse(FirstId)], post.PingUsers);
        Assert.Empty(post.ExtraPings);
        Assert.Contains("hat 2 offene Punkte", Everything(post));
        var body = JsonDocument.Parse(Json(Assert.Single(post.Fallback!.Payloads))).RootElement;
        Assert.EndsWith("\nhttps://fibhq.com/benachrichtigungen", body.GetProperty("content").GetString());
        Assert.Equal(FirstId, Assert.Single(body.GetProperty("allowed_mentions").GetProperty("users").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task A_share_post_goes_by_its_own_url_and_pings_nobody()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned);
        const string shareHook = "https://discord.test/api/webhooks/2/share-token";

        var sent = await host.Service.PushToShareChannelAsync(shareHook, "**Person** FIB-P-1 <@123>");

        Assert.True(sent);
        Assert.Empty(host.Handler.Posts);
        Assert.Equal([shareHook], host.Routes.Asked);
        var post = Queued(host);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Empty(post.PingUsers);
        Assert.Null(post.PingRole);
        Assert.Empty(post.ExtraPings);
        Assert.Contains("**Person** FIB-P-1 <@123>", Everything(post));
        Assert.Equal(shareHook, post.Fallback!.Url);
        var body = JsonDocument.Parse(Json(Assert.Single(post.Fallback.Payloads))).RootElement;
        Assert.Equal("**Person** FIB-P-1 <@123>", body.GetProperty("content").GetString());
        Assert.Empty(body.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task A_share_post_to_another_server_posts_by_webhook()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, new BotRoute(BotRouteState.OtherServer, null));

        var sent = await host.Service.PushToShareChannelAsync(Hook, "Text");

        Assert.True(sent);
        Assert.Single(host.Handler.Posts);
        Assert.False(host.Queue.TryRead(out _));
    }

    // ---------------------------------------------------------------- test message

    private static ClaimsPrincipalBuilder Leadership => ClaimsPrincipalBuilder.Agent().AsAdmin();

    [Fact]
    public async Task The_test_message_goes_through_the_bot_when_the_category_is_routed()
    {
        using var ctx = new SqliteTestContext();
        var sender = Substitute.For<IDiscordBotSender>();
        var services = new ServiceCollection().AddSingleton(sender).BuildServiceProvider();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, services: services);

        var ok = await host.Service.TestAsync(NotificationType.JobAssigned, Leadership);

        Assert.True(ok);
        Assert.Empty(host.Handler.Posts);
        Assert.False(host.Queue.TryRead(out _));
        var call = Assert.Single(sender.ReceivedCalls());
        var args = call.GetArguments();
        Assert.Equal(Channel, args[0]);
        var mentions = Assert.IsType<AllowedMentionsProperties>(args[2]);
        Assert.False(mentions.Everyone);
        Assert.Empty(mentions.AllowedUsers ?? []);
        Assert.Empty(mentions.AllowedRoles ?? []);
    }

    [Fact]
    public async Task A_refused_bot_test_message_reports_failure()
    {
        using var ctx = new SqliteTestContext();
        var sender = Substitute.For<IDiscordBotSender>();
        sender.SendAsync(Arg.Any<ulong>(), Arg.Any<IReadOnlyList<IMessageComponentProperties>>(), Arg.Any<AllowedMentionsProperties>(),
                Arg.Any<ulong?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ulong>>(_ => throw new HttpRequestException("403"));
        var services = new ServiceCollection().AddSingleton(sender).BuildServiceProvider();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, services: services);

        var ok = await host.Service.TestAsync(NotificationType.JobAssigned, Leadership);

        Assert.False(ok);
        Assert.Empty(host.Handler.Posts);
    }

    [Fact]
    public async Task The_test_message_posts_by_webhook_when_no_bot_sender_exists()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned);

        var ok = await host.Service.TestAsync(NotificationType.JobAssigned, Leadership);

        Assert.True(ok);
        var body = JsonDocument.Parse(Assert.Single(host.Handler.Posts)).RootElement;
        Assert.StartsWith("🔔 Test-Benachrichtigung", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_test_message_posts_by_webhook_when_the_category_is_not_routed()
    {
        using var ctx = new SqliteTestContext();
        var sender = Substitute.For<IDiscordBotSender>();
        var services = new ServiceCollection().AddSingleton(sender).BuildServiceProvider();
        var host = await HostAsync(ctx, NotificationType.JobAssigned, new BotRoute(BotRouteState.OtherServer, null), services: services);

        var ok = await host.Service.TestAsync(NotificationType.JobAssigned, Leadership);

        Assert.True(ok);
        Assert.Single(host.Handler.Posts);
        Assert.Empty(sender.ReceivedCalls());
    }

    [Fact]
    public async Task The_test_message_stays_leadership_only_before_anything_is_looked_up()
    {
        using var ctx = new SqliteTestContext();
        var host = await HostAsync(ctx, NotificationType.JobAssigned);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.TestAsync(NotificationType.JobAssigned, ClaimsPrincipalBuilder.Agent().Build()));

        Assert.Empty(host.Routes.Asked);
        Assert.Empty(host.Handler.Posts);
    }
}
