using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The bot's REST calls: auth and reason headers, single role endpoints, rate limits, and no token in errors.</summary>
public class DiscordBotClientTests
{
    private const string Token = "test-token";
    private const string Guild = "100";
    private const string User = "200";
    private const string Role = "300";
    private const string Bot = "900";

    private sealed record Sent(HttpMethod Method, string Path, string? Authorization, string? Reason);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Audit-Log-Reason", out var reason);
            Calls.Add(new Sent(request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(), reason?.Single()));
            return Task.FromResult(respond(request));
        }
    }

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
        Assert.DoesNotContain("oops", error.Message);
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
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Configured_with_a_token()
    {
        var (client, _) = Build(_ => Reply(HttpStatusCode.NoContent));

        Assert.True(client.IsConfigured);
    }
}
