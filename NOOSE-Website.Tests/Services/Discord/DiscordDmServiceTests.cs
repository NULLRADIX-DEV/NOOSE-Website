using System.Net.Http;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>Direct message delivery against a fake bot: the stamp follows the outcome, only agents fall back to the channel, switches need an admin.</summary>
public class DiscordDmServiceTests
{
    private const string UserA = "200000000000000001";
    private const string Secret = "ein-geheimer-text";

    private static ClaimsPrincipal Admin()
        => ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).Build();

    private sealed class FakeBot : IDiscordBotClient
    {
        public bool IsConfigured { get; set; } = true;

        public List<(string UserId, string Content)> Sent { get; } = [];

        /// <summary>Called per send; a returned exception is thrown.</summary>
        public Func<Exception?>? Fault { get; set; }

        public Task SendDirectMessageAsync(string userId, string content, CancellationToken ct)
        {
            Sent.Add((userId, content));
            return Fault?.Invoke() is { } fault ? throw fault : Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
            => throw new NotSupportedException();
    }

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

        public FakeBot Bot { get; } = new();

        public IDiscordWebhookService Webhook { get; } = Substitute.For<IDiscordWebhookService>();

        public ListLogger<DiscordDmService> Log { get; } = new();

        public DiscordDmService Service { get; }

        public Fixture() => Service = new DiscordDmService(Ctx.Factory, Bot, Webhook, Log);

        public static async Task<Fixture> WithAgentAsync(DateTime? blockedAt = null)
        {
            var fixture = new Fixture();
            await using var db = fixture.Ctx.NewContext();
            db.Users.Add(Seed.Agent("a", configure: a => a.DiscordId = UserA));
            await db.SaveChangesAsync();
            if (blockedAt is not null)
            {
                await fixture.SetStampAsync("a", blockedAt);
            }
            return fixture;
        }

        public async Task SetStampAsync(string id, DateTime? value)
        {
            await using var db = Ctx.NewContext();
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordDmBlockedAt, value));
        }

        public async Task<DateTime?> StampAsync(string id = "a")
        {
            await using var db = Ctx.NewContext();
            return await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DiscordDmBlockedAt).SingleAsync();
        }

        public void Dispose() => Ctx.Dispose();
    }

    private static DiscordDmJob AgentJob(string content = "Dir wurde eine Aufgabe zugewiesen.")
        => new("a", UserA, content, NotificationType.JobAssigned, "/aufgaben");

    private static DiscordDmJob ApplicantJob(string content = "Deine Bewerbung beim FIB ist eingegangen.")
        => new("a", UserA, content, null, null);

    private static readonly string DigestPost
        = $"🗞️ **Tagesüberblick** – ungelesen seit dem letzten Überblick:\n<@{UserA}> 2× Aufgabe";

    private static DiscordDmJob DigestJob()
        => new("a", UserA, "Tagesüberblick: 2× Aufgabe", NotificationType.PingDigest, "/benachrichtigungen",
            DigestPost);

    private static readonly DateTime Blocked = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static async Task AssertNoFallbackAsync(Fixture f)
    {
        await f.Webhook.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
        await f.Webhook.DidNotReceiveWithAnyArgs().PushDigestAsync(default!, default!, default, default);
    }

    private static async Task AssertChannelFallbackAsync(Fixture f)
    {
        await f.Webhook.Received(1).PushAsync(NotificationType.JobAssigned, "/aufgaben",
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a" })), null, Arg.Any<CancellationToken>());
        await f.Webhook.DidNotReceiveWithAnyArgs().PushDigestAsync(default!, default!, default, default);
    }

    // ---------- settings ----------

    [Fact]
    public async Task Both_switches_are_off_by_default()
    {
        using var f = new Fixture();

        var settings = await f.Service.GetSettingsAsync();

        Assert.False(settings.Personal);
        Assert.False(settings.Applicants);
        Assert.True(settings.BotConfigured);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Saved_switches_read_back(bool personal, bool applicants)
    {
        using var f = new Fixture();

        await f.Service.SaveSettingsAsync(personal, applicants, null, Admin());

        var settings = await f.Service.GetSettingsAsync();
        Assert.Equal(personal, settings.Personal);
        Assert.Equal(applicants, settings.Applicants);
    }

    [Fact]
    public async Task A_second_save_overwrites_the_first()
    {
        using var f = new Fixture();
        await f.Service.SaveSettingsAsync(true, true, null, Admin());

        await f.Service.SaveSettingsAsync(false, true, null, Admin());

        var settings = await f.Service.GetSettingsAsync();
        Assert.False(settings.Personal);
        Assert.True(settings.Applicants);
    }

    [Fact]
    public async Task The_switches_live_under_their_german_keys()
    {
        using var f = new Fixture();

        await f.Service.SaveSettingsAsync(true, false, null, Admin());

        await using var db = f.Ctx.NewContext();
        var rows = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Equal("true", rows["DiscordDmPersoenlich"]);
        Assert.Equal("false", rows["DiscordDmBewerber"]);
    }

    [Fact]
    public async Task The_status_reports_whether_the_bot_has_a_token()
    {
        using var f = new Fixture();
        f.Bot.IsConfigured = false;

        Assert.False((await f.Service.GetSettingsAsync()).BotConfigured);
    }

    [Fact]
    public async Task Saving_needs_an_admin_who_may_write()
    {
        using var f = new Fixture();

        // leadership without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, null, Leader()));
        // read-only supervision: team lead without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, null,
            ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build()));
        // an admin who may not write (demo session)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, null,
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsAdmin().AsDemo().Build()));

        var settings = await f.Service.GetSettingsAsync();
        Assert.False(settings.Personal);
        Assert.False(settings.Applicants);
    }

    [Fact]
    public async Task The_write_guard_is_checked_before_the_admin_guard()
    {
        using var f = new Fixture();

        // a demo visitor is neither: the read-only message wins
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.SpecialAgent).AsDemo().Build();
        var readOnly = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, null, demo));
        var notAdmin = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, null, Leader()));

        Assert.Contains("Nur-Lese-Modus", readOnly.Message);
        Assert.Contains("Admins vorbehalten", notAdmin.Message);
    }

    // ---------- invite link ----------

    private const string DefaultLink = "https://discord.gg/9TeYjJyYxf";

    private static async Task<string?> StoredLinkAsync(Fixture f)
    {
        await using var db = f.Ctx.NewContext();
        return await db.SystemSettings.AsNoTracking().Where(s => s.Key == "DiscordEinladungslink")
            .Select(s => s.Value).SingleOrDefaultAsync();
    }

    [Fact]
    public async Task The_invite_link_defaults_to_the_servers_own()
    {
        using var f = new Fixture();

        Assert.Equal(DefaultLink, (await f.Service.GetSettingsAsync()).InviteLink);
        Assert.Equal(DefaultLink, DiscordDmSettings.DefaultInviteLink);
    }

    [Theory]
    [InlineData("https://discord.gg/abc123")]
    [InlineData("https://discord.com/invite/abc123")]
    [InlineData("https://discord.gg/fib-hq_01")]
    [InlineData("https://discord.gg/abc123?event=1234567890")]
    public async Task A_saved_invite_link_reads_back_under_its_german_key(string link)
    {
        using var f = new Fixture();

        await f.Service.SaveSettingsAsync(true, true, link, Admin());

        Assert.Equal(link, (await f.Service.GetSettingsAsync()).InviteLink);
        Assert.Equal(link, await StoredLinkAsync(f));
    }

    [Fact]
    public async Task The_invite_link_is_trimmed()
    {
        using var f = new Fixture();

        await f.Service.SaveSettingsAsync(true, true, "  https://discord.gg/abc123 \n", Admin());

        Assert.Equal("https://discord.gg/abc123", (await f.Service.GetSettingsAsync()).InviteLink);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(DefaultLink)]
    public async Task An_empty_link_or_the_default_itself_puts_the_default_back_and_stores_nothing(string? link)
    {
        using var f = new Fixture();
        await f.Service.SaveSettingsAsync(true, true, "https://discord.gg/abc123", Admin());

        await f.Service.SaveSettingsAsync(true, true, link, Admin());

        Assert.Equal(DefaultLink, (await f.Service.GetSettingsAsync()).InviteLink);
        Assert.Null(await StoredLinkAsync(f));
    }

    [Theory]
    [InlineData("http://discord.gg/abc123")]
    [InlineData("https://discord.gg/")]
    [InlineData("https://discord.com/invite/")]
    [InlineData("https://discord.gg.evil.example/abc123")]
    [InlineData("https://evil.example/https://discord.gg/abc123")]
    [InlineData("https://discord.com/channels/1/2")]
    [InlineData("https://discord.gg@evil.example/abc123")]
    [InlineData("https://discord.gg/abc 123")]
    [InlineData("https://discord.gg/abc\"onclick=\"x")]
    [InlineData("https://discord.gg/<script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("discord.gg/abc123")]
    public async Task A_link_that_is_not_a_discord_invite_is_refused_and_nothing_is_saved(string link)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveSettingsAsync(true, true, link, Admin()));

        Assert.Contains("https://discord.gg/", ex.Message);
        Assert.Contains("https://discord.com/invite/", ex.Message);
        Assert.Null(await StoredLinkAsync(f));
        // one save: a bad link keeps the switches where they were
        var settings = await f.Service.GetSettingsAsync();
        Assert.False(settings.Personal);
        Assert.False(settings.Applicants);
    }

    [Fact]
    public async Task An_overlong_link_is_refused()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.SaveSettingsAsync(true, true, "https://discord.gg/" + new string('a', 200), Admin()));
    }

    [Fact]
    public async Task A_stored_value_that_is_no_invite_reads_as_the_default()
    {
        using var f = new Fixture();
        await using (var db = f.Ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = "DiscordEinladungslink", Value = "javascript:alert(1)" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(DefaultLink, (await f.Service.GetSettingsAsync()).InviteLink);
    }

    [Fact]
    public async Task Saving_the_invite_link_needs_an_admin_who_may_write()
    {
        using var f = new Fixture();
        const string link = "https://discord.gg/abc123";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, link, Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, link,
            ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, link,
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsAdmin().AsDemo().Build()));

        Assert.Null(await StoredLinkAsync(f));
        Assert.Equal(DefaultLink, (await f.Service.GetSettingsAsync()).InviteLink);
    }

    [Fact]
    public async Task The_invite_link_guard_runs_before_the_link_is_looked_at()
    {
        using var f = new Fixture();

        // an invalid link from a non-admin is still a guard failure, not a hint about the format
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(true, true, "kaputt", Leader()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task Personal_is_active_only_with_the_switch_and_a_token(bool personal, bool applicants, bool expected)
    {
        using var f = new Fixture();
        await f.Service.SaveSettingsAsync(personal, applicants, null, Admin());

        Assert.Equal(expected, await f.Service.PersonalActiveAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Applicants_is_active_only_with_the_switch_and_a_token(bool personal, bool applicants, bool expected)
    {
        using var f = new Fixture();
        await f.Service.SaveSettingsAsync(personal, applicants, null, Admin());

        Assert.Equal(expected, await f.Service.ApplicantsActiveAsync());
    }

    [Fact]
    public async Task Without_a_token_nothing_is_active_however_the_switches_stand()
    {
        using var f = new Fixture();
        await f.Service.SaveSettingsAsync(true, true, null, Admin());
        f.Bot.IsConfigured = false;

        Assert.False(await f.Service.PersonalActiveAsync());
        Assert.False(await f.Service.ApplicantsActiveAsync());
    }

    // ---------- delivery ----------

    [Fact]
    public async Task A_delivered_message_goes_to_the_discord_id_and_posts_nothing_to_the_channel()
    {
        using var f = await Fixture.WithAgentAsync();

        await f.Service.DeliverAsync(AgentJob("Hallo"));

        Assert.Equal([(UserA, "Hallo")], f.Bot.Sent);
        await AssertNoFallbackAsync(f);
        Assert.Null(await f.StampAsync());
    }

    [Fact]
    public async Task Success_clears_the_stamp()
    {
        using var f = await Fixture.WithAgentAsync(Blocked);

        await f.Service.DeliverAsync(AgentJob());

        Assert.Null(await f.StampAsync());
    }

    [Fact]
    public async Task Success_clears_the_stamp_of_an_applicant_too()
    {
        using var f = await Fixture.WithAgentAsync(Blocked);

        await f.Service.DeliverAsync(ApplicantJob());

        Assert.Null(await f.StampAsync());
    }

    [Fact]
    public async Task A_closed_dm_stamps_the_agent_and_falls_back_to_the_channel()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: Cannot send messages to this user",
            DiscordBotException.CannotSendToUser);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await f.Service.DeliverAsync(AgentJob());

        var stamp = await f.StampAsync();
        Assert.NotNull(stamp);
        Assert.InRange(stamp!.Value, before, DateTime.UtcNow.AddSeconds(1));
        await AssertChannelFallbackAsync(f);
    }

    [Fact]
    public async Task Code_50007_counts_as_closed_whatever_the_status()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(400, "Discord 400: x", 50007);

        await f.Service.DeliverAsync(AgentJob());

        Assert.NotNull(await f.StampAsync());
        await AssertChannelFallbackAsync(f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(50001)]
    [InlineData(50013)]
    public async Task Any_403_counts_as_closed(int? code)
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: no", code);

        await f.Service.DeliverAsync(AgentJob());

        Assert.NotNull(await f.StampAsync());
        await AssertChannelFallbackAsync(f);
    }

    [Fact]
    public async Task A_closed_dm_stamps_an_applicant_without_any_fallback()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);

        await f.Service.DeliverAsync(ApplicantJob());

        Assert.NotNull(await f.StampAsync());
        await AssertNoFallbackAsync(f);
    }

    [Fact]
    public async Task A_second_refusal_moves_the_stamp_forward()
    {
        using var f = await Fixture.WithAgentAsync(Blocked);
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);

        await f.Service.DeliverAsync(AgentJob());

        Assert.True(await f.StampAsync() > Blocked);
    }

    public static TheoryData<Exception> OtherFailures => new()
    {
        new DiscordBotException(500, "Discord 500: x"),
        new DiscordBotException(502, "Discord 502: x"),
        new DiscordBotException(429, "Discord 429: x"),
        new DiscordBotException(401, "Discord 401: x"),
        new DiscordBotException(404, "Discord 404: x", 10003),
        new DiscordBotException(200, "Discord 200: no usable direct channel"),
        new TaskCanceledException("timed out"),
        new TimeoutException(),
        new HttpRequestException("down"),
        new ArgumentException("Not a Discord id."),
        new InvalidOperationException("Discord bot token is not configured."),
    };

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task Another_failure_leaves_the_stamp_and_falls_back_for_an_agent(Exception failure)
    {
        using var f = await Fixture.WithAgentAsync(Blocked);
        f.Bot.Fault = () => failure;

        await f.Service.DeliverAsync(AgentJob());

        // neither set nor cleared: the user's setting is unknown
        Assert.Equal(Blocked, await f.StampAsync());
        await AssertChannelFallbackAsync(f);
    }

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task Another_failure_does_not_set_a_stamp(Exception failure)
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => failure;

        await f.Service.DeliverAsync(AgentJob());

        Assert.Null(await f.StampAsync());
    }

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task Another_failure_gives_an_applicant_nothing(Exception failure)
    {
        using var f = await Fixture.WithAgentAsync(Blocked);
        f.Bot.Fault = () => failure;

        await f.Service.DeliverAsync(ApplicantJob());

        Assert.Equal(Blocked, await f.StampAsync());
        await AssertNoFallbackAsync(f);
    }

    [Fact]
    public async Task The_digest_falls_back_to_the_digest_channel_with_heading_and_mention_line()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);

        await f.Service.DeliverAsync(DigestJob());

        await f.Webhook.Received(1).PushDigestAsync(DigestPost,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { UserA })), "/benachrichtigungen",
            Arg.Any<CancellationToken>());
        await f.Webhook.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
        Assert.NotNull(await f.StampAsync());
    }

    [Fact]
    public async Task The_digest_falls_back_on_any_failure_too()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new HttpRequestException("down");

        await f.Service.DeliverAsync(DigestJob());

        await f.Webhook.Received(1).PushDigestAsync(DigestPost, Arg.Any<IReadOnlyList<string>>(),
            "/benachrichtigungen", Arg.Any<CancellationToken>());
        Assert.Null(await f.StampAsync());
    }

    [Fact]
    public async Task A_delivered_digest_posts_nothing_to_the_channel()
    {
        using var f = await Fixture.WithAgentAsync();

        await f.Service.DeliverAsync(DigestJob());

        Assert.Equal([(UserA, "Tagesüberblick: 2× Aufgabe")], f.Bot.Sent);
        await AssertNoFallbackAsync(f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_digest_without_a_fallback_line_posts_nothing(string? line)
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new HttpRequestException("down");

        await f.Service.DeliverAsync(DigestJob() with { FallbackContent = line });

        await AssertNoFallbackAsync(f);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t ")]
    public async Task Empty_content_is_never_sent_and_does_nothing_else(string content)
    {
        using var f = await Fixture.WithAgentAsync(Blocked);

        await f.Service.DeliverAsync(AgentJob(content));
        await f.Service.DeliverAsync(ApplicantJob(content));

        Assert.Empty(f.Bot.Sent);
        Assert.Equal(Blocked, await f.StampAsync());
        await AssertNoFallbackAsync(f);
        Assert.NotEmpty(f.Log.Lines);
    }

    [Fact]
    public async Task The_demo_account_is_skipped_without_a_message_a_fallback_or_a_stamp()
    {
        using var f = await Fixture.WithAgentAsync(Blocked);

        await f.Service.DeliverAsync(AgentJob() with { AgentId = DemoIdentity.AgentId });

        Assert.Empty(f.Bot.Sent);
        Assert.Equal(Blocked, await f.StampAsync());
        await AssertNoFallbackAsync(f);
        Assert.Contains(f.Log.Lines, line => line.Contains(DemoIdentity.AgentId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("demo")]
    [InlineData("discord-a")]
    [InlineData("2000000000000000x1")]
    public async Task A_discord_id_that_is_no_snowflake_is_skipped_without_a_message_or_a_fallback(string discordId)
    {
        using var f = await Fixture.WithAgentAsync(Blocked);
        f.Bot.Fault = () => new HttpRequestException("down");

        await f.Service.DeliverAsync(AgentJob() with { DiscordId = discordId });
        await f.Service.DeliverAsync(DigestJob() with { DiscordId = discordId });

        Assert.Empty(f.Bot.Sent);
        Assert.Equal(Blocked, await f.StampAsync());
        await AssertNoFallbackAsync(f);
    }

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.EmergencyAlarm)]
    [InlineData(NotificationType.PersonnelEntry)]
    [InlineData(NotificationType.InactivityNudge)]
    public async Task Only_a_personal_category_or_the_digest_falls_back_to_the_channel(NotificationType type)
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);

        await f.Service.DeliverAsync(AgentJob() with { FallbackType = type });

        // the refusal is still recorded, there is just no channel post for a role or notice category
        Assert.NotNull(await f.StampAsync());
        await AssertNoFallbackAsync(f);
    }

    [Theory]
    [MemberData(nameof(PersonalTypes))]
    public async Task Every_personal_category_falls_back_to_the_channel(NotificationType type)
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new HttpRequestException("down");

        await f.Service.DeliverAsync(AgentJob() with { FallbackType = type });

        await f.Webhook.Received(1).PushAsync(type, "/aufgaben",
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a" })), null, Arg.Any<CancellationToken>());
    }

    public static TheoryData<NotificationType> PersonalTypes => new(DiscordPings.Personal);

    [Fact]
    public async Task Cancelling_the_run_escapes_without_stamp_or_fallback()
    {
        using var f = await Fixture.WithAgentAsync();
        using var cts = new CancellationTokenSource();
        f.Bot.Fault = () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.DeliverAsync(AgentJob(), cts.Token));

        Assert.Null(await f.StampAsync());
        await AssertNoFallbackAsync(f);
    }

    [Fact]
    public async Task A_timeout_that_surfaces_as_a_cancellation_is_a_failure_not_an_abort()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new OperationCanceledException();

        await f.Service.DeliverAsync(AgentJob(), CancellationToken.None);

        await AssertChannelFallbackAsync(f);
    }

    [Fact]
    public async Task A_failing_fallback_never_escapes()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);
        f.Webhook.PushAsync(default, default, default, default, default).ReturnsForAnyArgs(
            Task.FromException(new InvalidOperationException("webhook down")));

        await f.Service.DeliverAsync(AgentJob());

        // the stamp was written before the fallback
        Assert.NotNull(await f.StampAsync());
    }

    [Fact]
    public async Task A_missing_agent_row_is_not_an_error()
    {
        using var f = new Fixture();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);

        await f.Service.DeliverAsync(AgentJob());
        f.Bot.Fault = null;
        await f.Service.DeliverAsync(AgentJob());

        Assert.Equal(2, f.Bot.Sent.Count);
    }

    [Fact]
    public async Task The_log_holds_neither_the_text_nor_the_discord_id()
    {
        using var f = await Fixture.WithAgentAsync();
        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: Cannot send messages to this user",
            DiscordBotException.CannotSendToUser);
        await f.Service.DeliverAsync(AgentJob(Secret));
        f.Bot.Fault = () => new HttpRequestException("down");
        await f.Service.DeliverAsync(AgentJob(Secret));
        await f.Service.DeliverAsync(DigestJob() with { Content = Secret, FallbackContent = Secret });
        await f.Service.DeliverAsync(AgentJob(" "));

        Assert.NotEmpty(f.Log.Lines);
        Assert.All(f.Log.Lines, line =>
        {
            Assert.DoesNotContain(Secret, line);
            Assert.DoesNotContain(UserA, line);
        });
    }

    // ---------- the stamp column ----------

    [Fact]
    public async Task A_stale_user_manager_copy_keeps_the_dm_stamp()
    {
        using var f = await Fixture.WithAgentAsync();

        // a long-lived context holds the account from before the failure, as UserManager's does
        await using var stale = f.Ctx.NewContext();
        var copy = stale.Users.Single(u => u.Id == "a");

        f.Bot.Fault = () => new DiscordBotException(403, "Discord 403: x", DiscordBotException.CannotSendToUser);
        await f.Service.DeliverAsync(AgentJob());
        var stamp = await f.StampAsync();

        copy.Codename = "Neu";
        stale.Update(copy);
        await stale.SaveChangesAsync();

        Assert.Equal(stamp, await f.StampAsync());
        await using var fresh = f.Ctx.NewContext();
        Assert.Equal("Neu", fresh.Users.Single(u => u.Id == "a").Codename);
    }

    [Fact]
    public async Task A_stale_copy_cannot_bring_back_a_cleared_stamp()
    {
        using var f = await Fixture.WithAgentAsync(Blocked);

        await using var stale = f.Ctx.NewContext();
        var copy = stale.Users.Single(u => u.Id == "a");
        Assert.Equal(Blocked, copy.DiscordDmBlockedAt);

        await f.Service.DeliverAsync(AgentJob());
        copy.Codename = "Neu";
        stale.Update(copy);
        await stale.SaveChangesAsync();

        Assert.Null(await f.StampAsync());
    }
}
