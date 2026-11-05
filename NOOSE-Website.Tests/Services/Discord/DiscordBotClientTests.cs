using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The bot's REST calls: auth and reason headers, single role endpoints, direct messages, rate limits, and no token in errors.</summary>
public class DiscordBotClientTests
{
    private const string Token = "test-token";
    private const string Guild = "100";
    private const string User = "200";
    private const string Role = "300";
    private const string Bot = "900";
    private const string DmChannel = "555";
    private static int lastUser = 1000;

    private sealed record Sent(HttpMethod Method, string Path, string? Authorization, string? Reason, string? Body = null);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Audit-Log-Reason", out var reason);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Sent(request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(), reason?.Single(), body));
            return respond(request);
        }
    }

    // every test gets its own user: the dm channel cache is static
    private static string NewUser() => Interlocked.Increment(ref lastUser).ToString();

    private static HttpResponseMessage DmReply(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/v10/users/@me/channels" => Reply(HttpStatusCode.OK, $$"""{"id":"{{DmChannel}}","type":1}"""),
        $"/api/v10/channels/{DmChannel}/messages" => Reply(HttpStatusCode.OK, """{"id":"1"}"""),
        _ => Reply(HttpStatusCode.NotFound),
    };

    private static HttpResponseMessage Reply(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }

    private static (DiscordBotClient Client, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = Token)
    {
        var handler = new StubHandler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(DiscordBotClient.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:BotToken"] = token })
            .Build();
        return (new DiscordBotClient(factory, config), handler);
    }

    [Fact]
    public async Task Sends_bot_token_and_reason_header()
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await client.AddRoleAsync(Guild, User, Role, "FIB-Website: Beförderung", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("Bot test-token", call.Authorization);
        Assert.Equal("FIB-Website%3A%20Bef%C3%B6rderung", call.Reason);
    }

    [Fact]
    public async Task Add_and_remove_use_single_role_endpoints()
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await client.AddRoleAsync(Guild, User, Role, "x", CancellationToken.None);
        await client.RemoveRoleAsync(Guild, User, Role, "x", CancellationToken.None);

        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], handler.Calls.Select(c => c.Method));
        Assert.All(handler.Calls, c => Assert.Equal($"/api/v10/guilds/{Guild}/members/{User}/roles/{Role}", c.Path));
    }

    // "." and ".." would be collapsed by Uri: DELETE .../roles/.. kicks the member, .../members/../roles/x hits the server's role
    public static TheoryData<string> NotSnowflakes => new()
    {
        "", ".", "..", "1/2", "u/../x", "../1", "1%2F2", "12 3", "-1", "1.5", "abc", "١٢",
        new string('1', 21),
    };

    [Theory]
    [MemberData(nameof(NotSnowflakes))]
    public async Task A_bad_guild_id_is_rejected_before_any_request(string bad)
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMemberRolesAsync(bad, User, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetGuildRolesAsync(bad, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddRoleAsync(bad, User, Role, "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveRoleAsync(bad, User, Role, "x", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(NotSnowflakes))]
    public async Task A_bad_user_id_is_rejected_before_any_request(string bad)
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMemberRolesAsync(Guild, bad, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddRoleAsync(Guild, bad, Role, "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveRoleAsync(Guild, bad, Role, "x", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(NotSnowflakes))]
    public async Task A_bad_role_id_is_rejected_before_any_request(string bad)
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await Assert.ThrowsAsync<ArgumentException>(() => client.AddRoleAsync(Guild, User, bad, "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveRoleAsync(Guild, User, bad, "x", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Ids_of_up_to_twenty_digits_are_accepted()
    {
        var id = new string('7', 20);
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent));

        await client.AddRoleAsync(id, id, id, "x", CancellationToken.None);

        Assert.Equal($"/api/v10/guilds/{id}/members/{id}/roles/{id}", Assert.Single(handler.Calls).Path);
    }

    [Fact]
    public async Task Member_roles_are_read_from_the_member()
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.OK, """{"user":{"id":"200"},"roles":["r1","r2"]}"""));

        var roles = await client.GetMemberRolesAsync(Guild, User, CancellationToken.None);

        Assert.Equal(["r1", "r2"], roles);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal($"/api/v10/guilds/{Guild}/members/{User}", call.Path);
    }

    [Fact]
    public async Task Missing_member_returns_null()
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.NotFound, """{"message":"Unknown Member","code":10007}"""));

        Assert.Null(await client.GetMemberRolesAsync(Guild, User, CancellationToken.None));
    }

    [Fact]
    public async Task Guild_roles_carry_the_bots_top_position()
    {
        var (client, handler) = Build(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/v10/guilds/{Guild}/roles" => Reply(HttpStatusCode.OK, """
                [{"id":"g","name":"@everyone","position":0,"managed":false},
                 {"id":"r1","name":"Mod","position":3,"managed":false},
                 {"id":"r2","name":"Bot","position":7,"managed":true},
                 {"id":"r3","name":"Chief","position":9,"managed":false}]
                """),
            "/api/v10/users/@me" => Reply(HttpStatusCode.OK, $$"""{"id":"{{Bot}}","username":"fib"}"""),
            $"/api/v10/guilds/{Guild}/members/{Bot}" => Reply(HttpStatusCode.OK, """{"roles":["r1","r2"]}"""),
            _ => Reply(HttpStatusCode.NotFound),
        });

        var result = await client.GetGuildRolesAsync(Guild, CancellationToken.None);

        Assert.Equal(7, result.BotTopPosition);
        Assert.Equal(4, result.Roles.Count);
        Assert.Equal(new DiscordGuildRole("r2", "Bot", 7, true), result.Roles[2]);
        Assert.Equal(new DiscordGuildRole("r3", "Chief", 9, false), result.Roles[3]);
        Assert.All(handler.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task A_bot_without_roles_sits_at_position_zero()
    {
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/v10/guilds/{Guild}/roles" => Reply(HttpStatusCode.OK,
                """[{"id":"r1","name":"Mod","position":3,"managed":false}]"""),
            "/api/v10/users/@me" => Reply(HttpStatusCode.OK, $$"""{"id":"{{Bot}}"}"""),
            _ => Reply(HttpStatusCode.OK, """{"roles":[]}"""),
        });

        var result = await client.GetGuildRolesAsync(Guild, CancellationToken.None);

        Assert.Equal(0, result.BotTopPosition);
    }

    [Fact]
    public async Task Waits_on_429_and_retries()
    {
        var calls = 0;
        var (client, handler) = Build(_ => ++calls == 1
            ? Reply((HttpStatusCode)429, """{"message":"You are being rate limited.","retry_after":0.01,"global":false}""")
            : Reply(HttpStatusCode.NoContent));

        await client.AddRoleAsync(Guild, User, Role, "x", CancellationToken.None);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Gives_up_after_three_429s()
    {
        var (client, handler) = Build(_ =>
            Reply((HttpStatusCode)429, """{"message":"You are being rate limited.","retry_after":0.01}"""));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.AddRoleAsync(Guild, User, Role, "x", CancellationToken.None));

        Assert.Equal(429, error.Status);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Theory]
    [InlineData(0.01, 0.01)]
    [InlineData(2.5, 2.5)]
    [InlineData(60, 10)]
    [InlineData(-3, 0)]
    public void Retry_wait_is_capped_at_ten_seconds(double asked, double waited)
        => Assert.Equal(TimeSpan.FromSeconds(waited), DiscordBotClient.RetryWait(asked));

    [Fact]
    public async Task Forbidden_throws_without_token_in_message()
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.Forbidden, """{"message":"Missing Permissions","code":50013}"""));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.RemoveRoleAsync(Guild, User, Role, "x", CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Contains("Missing Permissions", error.Message);
        Assert.DoesNotContain(Token, error.Message);
    }

    [Fact]
    public async Task A_failed_read_throws_instead_of_returning_null()
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.InternalServerError, "<html>oops</html>"));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.GetMemberRolesAsync(Guild, User, CancellationToken.None));

        Assert.Equal(500, error.Status);
        Assert.Null(error.Code);
        Assert.DoesNotContain("oops", error.Message);
    }

    [Theory]
    [InlineData("""{"message":"x","code":"50007"}""")]
    [InlineData("""{"message":"x","code":1.5}""")]
    [InlineData("""[{"code":50007}]""")]
    public async Task A_code_that_is_not_an_integer_is_ignored(string answer)
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.BadRequest, answer));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.AddRoleAsync(Guild, User, Role, "x", CancellationToken.None));

        Assert.Equal(400, error.Status);
        Assert.Null(error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Not_configured_without_token(string? token)
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.NoContent), token);

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetMemberRolesAsync(Guild, User, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetGuildRolesAsync(Guild, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AddRoleAsync(Guild, User, Role, "x", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RemoveRoleAsync(Guild, User, Role, "x", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendDirectMessageAsync(User, "x", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Direct_message_opens_the_channel_then_posts()
    {
        var user = NewUser();
        var (client, handler) = Build(DmReply);

        await client.SendDirectMessageAsync(user, "Hallo @everyone", CancellationToken.None);

        Assert.Equal(2, handler.Calls.Count);
        var open = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, open.Method);
        Assert.Equal("/api/v10/users/@me/channels", open.Path);
        Assert.Equal("Bot test-token", open.Authorization);
        using var openBody = JsonDocument.Parse(open.Body!);
        Assert.Equal(user, openBody.RootElement.GetProperty("recipient_id").GetString());

        var post = handler.Calls[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal($"/api/v10/channels/{DmChannel}/messages", post.Path);
        Assert.Equal("Bot test-token", post.Authorization);
        using var postBody = JsonDocument.Parse(post.Body!);
        Assert.Equal("Hallo @everyone", postBody.RootElement.GetProperty("content").GetString());
        var parse = postBody.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());
    }

    [Fact]
    public async Task The_dm_channel_is_opened_once_per_user()
    {
        var (first, second) = (NewUser(), NewUser());
        var opened = 0;
        var (client, handler) = Build(request => request.RequestUri!.AbsolutePath == "/api/v10/users/@me/channels"
            ? Reply(HttpStatusCode.OK, $$"""{"id":"{{500 + ++opened}}"}""")
            : Reply(HttpStatusCode.OK, """{"id":"1"}"""));

        await client.SendDirectMessageAsync(first, "a", CancellationToken.None);
        await client.SendDirectMessageAsync(first, "b", CancellationToken.None);
        await client.SendDirectMessageAsync(second, "c", CancellationToken.None);

        Assert.Equal(2, handler.Calls.Count(c => c.Path == "/api/v10/users/@me/channels"));
        Assert.Equal(
            ["/api/v10/channels/501/messages", "/api/v10/channels/501/messages", "/api/v10/channels/502/messages"],
            handler.Calls.Where(c => c.Path.EndsWith("/messages")).Select(c => c.Path));
    }

    [Fact]
    public async Task The_dm_channel_is_remembered_across_clients()
    {
        var user = NewUser();
        var (first, firstHandler) = Build(DmReply);
        var (second, secondHandler) = Build(DmReply);

        await first.SendDirectMessageAsync(user, "a", CancellationToken.None);
        await second.SendDirectMessageAsync(user, "b", CancellationToken.None);

        Assert.Equal(2, firstHandler.Calls.Count);
        Assert.Equal($"/api/v10/channels/{DmChannel}/messages", Assert.Single(secondHandler.Calls).Path);
    }

    [Fact]
    public async Task A_closed_dm_throws_with_code_50007()
    {
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath.EndsWith("/messages")
            ? Reply(HttpStatusCode.Forbidden, """{"message":"Cannot send messages to this user","code":50007}""")
            : DmReply(request));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.SendDirectMessageAsync(NewUser(), "x", CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Equal(50007, error.Code);
        Assert.Equal(DiscordBotException.CannotSendToUser, error.Code);
        Assert.Contains("Cannot send messages to this user", error.Message);
        Assert.DoesNotContain(Token, error.Message);
    }

    // the cached channel can go stale (deleted, or the bot lost access): evict it and reopen once
    [Theory]
    [InlineData(404, """{"message":"Unknown Channel","code":10003}""")]
    [InlineData(404, null)]
    [InlineData(400, """{"message":"Unknown Channel","code":10003}""")]
    [InlineData(403, """{"message":"Missing Access","code":50001}""")]
    [InlineData(403, null)]
    public async Task A_stale_cached_dm_channel_is_evicted_and_reopened_once(int status, string? answer)
    {
        var user = NewUser();
        var opened = 0;
        var stale = true;
        var (client, handler) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v10/users/@me/channels")
            {
                return Reply(HttpStatusCode.OK, $$"""{"id":"{{600 + ++opened}}"}""");
            }

            // only the channel opened first is dead
            return path == "/api/v10/channels/601/messages" && stale
                ? Reply((HttpStatusCode)status, answer)
                : Reply(HttpStatusCode.OK, """{"id":"1"}""");
        });

        stale = false;
        await client.SendDirectMessageAsync(user, "a", CancellationToken.None);
        stale = true;
        await client.SendDirectMessageAsync(user, "b", CancellationToken.None);

        Assert.Equal(2, opened);
        Assert.Equal(
            ["/api/v10/users/@me/channels", "/api/v10/channels/601/messages", "/api/v10/channels/601/messages",
                "/api/v10/users/@me/channels", "/api/v10/channels/602/messages"],
            handler.Calls.Select(c => c.Path));

        // the new channel is the cached one now
        await client.SendDirectMessageAsync(user, "c", CancellationToken.None);
        Assert.Equal("/api/v10/channels/602/messages", handler.Calls[^1].Path);
        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task A_fresh_dm_channel_that_fails_is_neither_retried_nor_cached()
    {
        var user = NewUser();
        var opened = 0;
        var (client, handler) = Build(request => request.RequestUri!.AbsolutePath == "/api/v10/users/@me/channels"
            ? Reply(HttpStatusCode.OK, $$"""{"id":"{{700 + ++opened}}"}""")
            : Reply(HttpStatusCode.NotFound, """{"message":"Unknown Channel","code":10003}"""));

        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.SendDirectMessageAsync(user, "x", CancellationToken.None));

        // reopening would hit the same channel, so one open and one post
        Assert.Equal(404, error.Status);
        Assert.Equal(10003, error.Code);
        Assert.Equal(2, handler.Calls.Count);

        // not cached after the failure: the next send opens again
        await Assert.ThrowsAsync<DiscordBotException>(
            () => client.SendDirectMessageAsync(user, "y", CancellationToken.None));
        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task A_stale_channel_that_fails_again_after_reopening_throws_after_two_posts()
    {
        var user = NewUser();
        var opened = 0;
        var failing = false;
        var (client, handler) = Build(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v10/users/@me/channels")
            {
                return Reply(HttpStatusCode.OK, $$"""{"id":"{{800 + ++opened}}"}""");
            }

            return failing ? Reply(HttpStatusCode.NotFound) : Reply(HttpStatusCode.OK, """{"id":"1"}""");
        });
        await client.SendDirectMessageAsync(user, "a", CancellationToken.None);
        handler.Calls.Clear();

        failing = true;
        var error = await Assert.ThrowsAsync<DiscordBotException>(
            () => client.SendDirectMessageAsync(user, "b", CancellationToken.None));

        Assert.Equal(404, error.Status);
        Assert.Equal(
            ["/api/v10/channels/801/messages", "/api/v10/users/@me/channels", "/api/v10/channels/802/messages"],
            handler.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task A_closed_dm_keeps_the_cached_channel()
    {
        var user = NewUser();
        var closed = false;
        var opened = 0;
        var (client, handler) = Build(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v10/users/@me/channels")
            {
                return Reply(HttpStatusCode.OK, $$"""{"id":"{{900 + ++opened}}"}""");
            }

            return closed
                ? Reply(HttpStatusCode.Forbidden, """{"message":"Cannot send messages to this user","code":50007}""")
                : Reply(HttpStatusCode.OK, """{"id":"1"}""");
        });
        await client.SendDirectMessageAsync(user, "a", CancellationToken.None);

        closed = true;
        await Assert.ThrowsAsync<DiscordBotException>(() => client.SendDirectMessageAsync(user, "b", CancellationToken.None));
        closed = false;
        await client.SendDirectMessageAsync(user, "c", CancellationToken.None);

        // the channel is fine, only the user's setting says no
        Assert.Equal(1, opened);
        Assert.Equal(3, handler.Calls.Count(c => c.Path.EndsWith("/messages")));
        Assert.All(handler.Calls.Where(c => c.Path.EndsWith("/messages")), c => Assert.Equal("/api/v10/channels/901/messages", c.Path));
    }

    [Theory]
    [MemberData(nameof(NotSnowflakes))]
    public async Task Non_snowflake_user_is_rejected_before_any_request(string bad)
    {
        var (client, handler) = Build(DmReply);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendDirectMessageAsync(bad, "x", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("""{"id":"../1"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":123}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("<html>oops</html>")]
    public async Task A_dm_channel_without_a_snowflake_id_is_not_used(string answer)
    {
        var (client, handler) = Build(_ => Reply(HttpStatusCode.OK, answer));

        await Assert.ThrowsAsync<DiscordBotException>(
            () => client.SendDirectMessageAsync(NewUser(), "x", CancellationToken.None));
        Assert.Equal("/api/v10/users/@me/channels", Assert.Single(handler.Calls).Path);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(2000, 2000)]
    [InlineData(2001, 2000)]
    [InlineData(2500, 2000)]
    public async Task Long_content_is_cut_to_2000(int length, int posted)
    {
        var (client, handler) = Build(DmReply);

        await client.SendDirectMessageAsync(NewUser(), new string('a', length), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Calls[1].Body!);
        Assert.Equal(posted, body.RootElement.GetProperty("content").GetString()!.Length);
    }

    [Fact]
    public async Task Cutting_never_splits_a_surrogate_pair()
    {
        var (client, handler) = Build(DmReply);

        await client.SendDirectMessageAsync(NewUser(), new string('a', 1999) + "\U0001F600\U0001F600", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Calls[1].Body!);
        Assert.Equal(new string('a', 1999), body.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Configured_with_a_token()
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.NoContent));

        Assert.True(client.IsConfigured);
    }
}
