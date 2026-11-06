using System.Net.Http;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The membership check: only a clean "not on the server" answer says no, everything else reads as unknown.</summary>
public class DiscordMembershipCheckTests
{
    private const string Guild = "100000000000000001";
    private const string UserA = "200000000000000001";
    private const string Token = "geheimes-bot-token";
    private const string AgentId = "agent-42";

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteTestContext Ctx { get; } = new();

        public IDiscordBotClient Bot { get; } = Substitute.For<IDiscordBotClient>();

        public IDiscordRoleSyncService Sync { get; } = Substitute.For<IDiscordRoleSyncService>();

        public ListLogger<DiscordMembershipCheck> Log { get; } = new();

        public DiscordMembershipCheck Check { get; }

        public Fixture(string? guildId = Guild, bool configured = true, string? discordId = UserA)
        {
            Bot.IsConfigured.Returns(configured);
            Sync.GetSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(new DiscordRoleSyncSettings(true, guildId, DiscordRoleMapping.Empty));
            Bot.GetMemberRolesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<string>?)["300000000000000001"]);
            if (discordId is not null)
            {
                using var db = Ctx.NewContext();
                db.Users.Add(Seed.Agent(AgentId, configure: a => a.DiscordId = discordId));
                db.SaveChanges();
            }
            Check = new DiscordMembershipCheck(Ctx.Factory, Bot, Sync, Log);
        }

        public void Dispose() => Ctx.Dispose();
    }

    [Fact]
    public async Task A_member_of_the_server_is_a_member()
    {
        using var f = new Fixture();

        Assert.Equal(DiscordMembership.Member, await f.Check.CheckAsync(AgentId));
        await f.Bot.Received(1).GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_without_any_role_is_still_a_member()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)Array.Empty<string>());

        Assert.Equal(DiscordMembership.Member, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task Someone_who_is_not_on_the_server_is_not_a_member()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<string>?)null);

        Assert.Equal(DiscordMembership.NotMember, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task Without_a_bot_token_it_is_unknown_and_Discord_is_not_asked()
    {
        using var f = new Fixture(configured: false);

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
        await f.Bot.DidNotReceiveWithAnyArgs().GetMemberRolesAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nicht-numerisch")]
    public async Task Without_a_usable_server_id_it_is_unknown(string? guildId)
    {
        using var f = new Fixture(guildId: guildId);

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
        await f.Bot.DidNotReceiveWithAnyArgs().GetMemberRolesAsync(default!, default!, default);
    }

    [Fact]
    public async Task An_unknown_agent_is_unknown()
    {
        using var f = new Fixture();

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync("gibt-es-nicht"));
        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(""));
        await f.Bot.DidNotReceiveWithAnyArgs().GetMemberRolesAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("demo")]
    [InlineData("12ab34")]
    [InlineData("<@200000000000000001>")]
    public async Task An_agent_without_a_Discord_id_is_unknown(string discordId)
    {
        using var f = new Fixture(discordId: discordId);

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
        await f.Bot.DidNotReceiveWithAnyArgs().GetMemberRolesAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_Discord_refusal_is_unknown()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new DiscordBotException(403, "Missing Access", 50001));

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task A_network_error_is_unknown()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new HttpRequestException("offline"));

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task A_timeout_is_unknown()
    {
        using var f = new Fixture();
        // HttpClient reports its own timeout as a cancellation the caller never asked for
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new TaskCanceledException("timeout"));

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task A_failing_settings_read_is_unknown()
    {
        using var f = new Fixture();
        f.Sync.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns<DiscordRoleSyncSettings>(_ => throw new InvalidOperationException("db weg"));

        Assert.Equal(DiscordMembership.Unknown, await f.Check.CheckAsync(AgentId));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Check.CheckAsync(AgentId, cts.Token));
    }

    [Fact]
    public async Task A_failure_is_logged_without_the_token_the_Discord_id_or_the_server_id()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new HttpRequestException(
                $"Authorization: Bot {Token} for {UserA} on {Guild}"));

        await f.Check.CheckAsync(AgentId);

        var log = string.Join("\n", f.Log.Lines);
        Assert.NotEmpty(f.Log.Lines);
        Assert.Contains(AgentId, log);
        Assert.DoesNotContain(Token, log);
        Assert.DoesNotContain(UserA, log);
        Assert.DoesNotContain(Guild, log);
    }

    [Fact]
    public async Task A_Discord_refusal_is_logged_by_status_and_code_only()
    {
        using var f = new Fixture();
        f.Bot.GetMemberRolesAsync(Guild, UserA, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>?>(_ => throw new DiscordBotException(403, $"Bot {Token}", 50001));

        await f.Check.CheckAsync(AgentId);

        var log = string.Join("\n", f.Log.Lines);
        Assert.Contains("403/50001", log);
        Assert.DoesNotContain(Token, log);
    }
}
