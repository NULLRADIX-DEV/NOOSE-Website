using System.Security.Claims;

using NetCord.Rest;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The bot's pure rules: who may use it, what it parses, what it registers.</summary>
public class DiscordBotTests
{
    private const string Guild = "100000000000000001";
    private static readonly DateOnly Today = new(2026, 11, 13);

    // ---------------------------------------------------------------- dates

    [Theory]
    [InlineData("heute", 2026, 11, 13)]
    [InlineData("Morgen", 2026, 11, 14)]
    [InlineData("übermorgen", 2026, 11, 15)]
    [InlineData("+3", 2026, 11, 16)]
    [InlineData("20.11.", 2026, 11, 20)]
    [InlineData("20.11", 2026, 11, 20)]
    [InlineData("1.2.27", 2027, 2, 1)]
    [InlineData("24.12.2026", 2026, 12, 24)]
    [InlineData("03.01.", 2027, 1, 3)]
    public void Days_are_read_the_German_way(string input, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), DiscordDateParser.ParseDay(input, Today));

    [Theory]
    [InlineData("")]
    [InlineData("nächste Woche")]
    [InlineData("31.02.")]
    [InlineData("13.13.")]
    [InlineData("+999")]
    public void Nonsense_is_no_day(string input) => Assert.Null(DiscordDateParser.ParseDay(input, Today));

    [Fact]
    public void A_moment_takes_its_time_in_Berlin()
    {
        var utc = DiscordDateParser.ParseMoment("20.11. 18:30", Today, new TimeOnly(9, 0));
        // November is winter time, UTC+1
        Assert.Equal(new DateTime(2026, 11, 20, 17, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc), DiscordDateParser.ParseMoment("morgen", Today, new TimeOnly(9, 0)));
        Assert.Null(DiscordDateParser.ParseMoment("morgen 25:00", Today, new TimeOnly(9, 0)));
    }

    // ---------------------------------------------------------------- links and ids

    [Fact]
    public void Only_own_site_record_links_are_previewed()
    {
        const string id = "0f8fad5b-d9cb-469f-a165-70867728950e";
        Assert.Equal((nameof(Person), id), DiscordLinkParser.Find($"schau mal https://fibhq.com/personen/{id} an", "https://fibhq.com"));
        Assert.Equal((nameof(Faction), id), DiscordLinkParser.Find($"<https://www.fibhq.com/fraktionen/{id.ToUpperInvariant()}>", "https://fibhq.com"));
        Assert.Null(DiscordLinkParser.Find($"https://evil.example/personen/{id}", "https://fibhq.com"));
        Assert.Null(DiscordLinkParser.Find($"https://fibhq.com.evil.example/personen/{id}", "https://fibhq.com"));
        Assert.Null(DiscordLinkParser.Find("https://fibhq.com/personen/neu", "https://fibhq.com"));
    }

    [Fact]
    public void Custom_ids_round_trip_and_reject_foreign_ones()
    {
        var id = BotIds.Make("jd", "abc", "2");
        var parsed = BotIds.Parse(id)!.Value;
        Assert.Equal("jd", parsed.Action);
        Assert.Equal(["abc", "2"], parsed.Args);
        Assert.Null(BotIds.Parse("other:jd:abc"));
        Assert.Null(BotIds.Parse(null));
        Assert.Throws<InvalidOperationException>(() => BotIds.Make("x", new string('a', 120)));
    }

    [Fact]
    public void Channel_lists_keep_only_snowflakes()
    {
        Assert.Equal([111111111111111111UL, 222222222222222222UL],
            DiscordBotSettingsService.ParseIds("111111111111111111, 222222222222222222;abc 111111111111111111"));
        Assert.Empty(DiscordBotSettingsService.ParseIds(null));
        Assert.Null(DiscordBotSettingsService.ParseId("12ab"));
        var open = new DiscordBotSettings(true, null, null, [], false);
        Assert.True(open.MayShareIn(5));
        Assert.False(open.MayShareIn(null));
        var limited = open with { ShareChannelIds = [7] };
        Assert.False(limited.MayShareIn(5));
        Assert.True(limited.MayShareIn(7));
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void User_text_cannot_format_or_ping()
    {
        var escaped = DiscordCards.Escape("**fett** @everyone <@123> [x](y)");
        Assert.DoesNotContain("**fett**", escaped);
        Assert.Contains("\\@everyone", escaped);
        Assert.Contains("\\<\\@123\\>", escaped);
        Assert.Equal("abc…", DiscordCards.Clip("abcdefg", 4));
    }

    [Fact]
    public void Html_turns_into_plain_lines()
        => Assert.Equal("Eins\nZwei & drei", DiscordCards.PlainFromHtml("<p>Eins</p><p>Zwei &amp; <b>drei</b></p>", 100));

    [Fact]
    public void Typos_still_find_the_one_meant()
    {
        string[] names = ["Falke", "Fuchs", "Adler", "Strafgesetzbuch"];
        Assert.Equal("Falke", BotFuzzy.Single(names, "Fakle", n => [n]));
        Assert.Equal("Strafgesetzbuch", BotFuzzy.Single(names, "Strafgestzbuch", n => [n]));
        Assert.Equal("Adler", BotFuzzy.Rank(names, "adl", n => [n]).First());
        Assert.Null(BotFuzzy.Single(names, "Zebra", n => [n]));
        Assert.Equal(names.Length, BotFuzzy.Rank(names, null, n => [n]).Count());
    }

    [Fact]
    public void A_whole_message_stays_under_Discords_text_total()
    {
        var big = DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Text(new string('a', 3000)), DiscordCards.Text(new string('b', 3000)), DiscordCards.Text("kurz"));
        NetCord.Rest.IMessageComponentProperties[] message = [big];
        DiscordCards.FitText(message);
        var total = big.Components.OfType<TextDisplayProperties>().Sum(t => t.Content.Length);
        Assert.InRange(total, 1, DiscordCards.MaxText);
        Assert.Contains(big.Components.OfType<TextDisplayProperties>(), t => t.Content == "kurz");
    }

    [Fact]
    public void A_time_in_the_skipped_spring_hour_moves_on()
        => Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc),
            DiscordDateParser.ParseMoment("28.03.27 2:30", Today, new TimeOnly(9, 0)));

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Every_command_fits_Discord_limits()
    {
        var commands = BotCommandCatalog.All();
        Assert.Equal(commands.Count, commands.Select(c => (c.Name, c.GetType())).Distinct().Count());
        foreach (var slash in commands.OfType<SlashCommandProperties>())
        {
            Assert.Matches("^[a-z0-9_-]{1,32}$", slash.Name);
            Assert.InRange(slash.Description.Length, 1, 100);
            var options = (slash.Options ?? []).ToList();
            Assert.True(options.Count <= 25);
            foreach (var option in options.SelectMany(o => new[] { o }.Concat(o.Options ?? [])))
            {
                Assert.Matches("^[a-z0-9_-]{1,32}$", option.Name);
                Assert.InRange(option.Description.Length, 1, 100);
            }
            // required options first, Discord refuses otherwise
            var flags = options.Where(o => o.Type != NetCord.ApplicationCommandOptionType.SubCommand).Select(o => o.Required == true).ToList();
            Assert.Equal(flags.OrderByDescending(f => f), flags);
            Assert.Contains(NetCord.InteractionContextType.BotDMChannel, slash.Contexts!);
        }
    }

    [Fact]
    public void Shareable_commands_all_have_the_share_option()
    {
        foreach (var slash in BotCommandCatalog.All().OfType<SlashCommandProperties>())
        {
            var hasShare = (slash.Options ?? []).Any(o => o.Name == "teilen");
            Assert.Equal(DiscordBotRouter.Shareable.Contains(slash.Name), hasShare);
        }
    }

    [Theory]
    [InlineData("note")]
    [InlineData("fu")]
    [InlineData("habs")]
    [InlineData("fibi")]
    public void Modal_buttons_open_a_modal_without_reading(string action)
    {
        Assert.Equal(BotAnswerMode.Modal, DiscordBotRouter.ModeForComponent(action, []));
        Assert.NotNull(DiscordBotRouter.ModalFor(action, ["Person", "x"]).Modal);
    }

    // ---------------------------------------------------------------- actor

    private sealed class ActorFixture : IDisposable
    {
        public SqliteTestContext Ctx { get; } = new();
        public IAgentPrincipalService Principals { get; } = Substitute.For<IAgentPrincipalService>();
        public IDiscordRoleSyncService Sync { get; } = Substitute.For<IDiscordRoleSyncService>();
        public DiscordActorResolver Resolver { get; }

        public ActorFixture(Action<NOOSE_Website.Data.Entities.Agent>? configure = null, ClaimsPrincipal? principal = null)
        {
            Sync.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new DiscordRoleSyncSettings(true, Guild, DiscordRoleMapping.Empty));
            using (var db = Ctx.NewContext())
            {
                db.Users.Add(Seed.Agent("a1", Rank.SpecialAgent, configure: a =>
                {
                    a.DiscordId = "300000000000000001";
                    configure?.Invoke(a);
                }));
                db.SaveChanges();
            }
            Principals.BuildAsync("a1", Arg.Any<CancellationToken>())
                .Returns(principal ?? ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.SpecialAgent).WithStatus(AgentStatus.Active).Build());
            Resolver = new DiscordActorResolver(Ctx.Factory, Principals, Sync);
        }

        public void Dispose() => Ctx.Dispose();
    }

    private static BotContext Ctx(ulong user = 300000000000000001, ulong? guild = 100000000000000001)
        => new(user, "tester", guild, 5);

    [Fact]
    public async Task A_linked_active_agent_is_resolved_with_fresh_rights()
    {
        using var f = new ActorFixture();
        var (actor, refusal) = await f.Resolver.ResolveAsync(Ctx());
        Assert.Null(refusal);
        Assert.Equal("a1", actor!.Agent.Id);
        // also in the bot's direct message
        Assert.NotNull((await f.Resolver.ResolveAsync(Ctx(guild: null))).Actor);
    }

    [Fact]
    public async Task An_unknown_Discord_user_is_told_to_link_the_account()
    {
        using var f = new ActorFixture();
        var (actor, refusal) = await f.Resolver.ResolveAsync(Ctx(user: 999999999999999999));
        Assert.Null(actor);
        Assert.Equal(DiscordActorResolver.NotLinked, refusal!.Text);
    }

    [Fact]
    public async Task Another_server_is_refused()
    {
        using var f = new ActorFixture();
        var (_, refusal) = await f.Resolver.ResolveAsync(Ctx(guild: 999999999999999999));
        Assert.Equal(DiscordActorResolver.WrongServer, refusal!.Text);
    }

    [Fact]
    public async Task Without_a_configured_server_only_the_DM_works()
    {
        using var f = new ActorFixture();
        f.Sync.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new DiscordRoleSyncSettings(true, null, DiscordRoleMapping.Empty));
        Assert.Equal(DiscordActorResolver.WrongServer, (await f.Resolver.ResolveAsync(Ctx())).Refusal!.Text);
        Assert.NotNull((await f.Resolver.ResolveAsync(Ctx(guild: null))).Actor);
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("partner")]
    [InlineData("blocked")]
    [InlineData("terminated")]
    public async Task Nobody_outside_the_pickers_may_use_the_bot(string kind)
    {
        using var f = new ActorFixture(a =>
        {
            switch (kind)
            {
                case "teamlead": a.IsTeamLead = true; break;
                case "partner": a.PartnerAgency = PartnerAgency.LSPD; break;
                case "blocked": a.Status = AgentStatus.Blocked; break;
                default: a.Status = AgentStatus.Terminated; break;
            }
        });
        var (actor, refusal) = await f.Resolver.ResolveAsync(Ctx());
        Assert.Null(actor);
        Assert.Equal(DiscordActorResolver.NotAllowed, refusal!.Text);
    }

    [Fact]
    public async Task A_read_only_principal_is_refused()
    {
        using var f = new ActorFixture(principal: ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.SpecialAgent).AsDemo().Build());
        Assert.Equal(DiscordActorResolver.NotAllowed, (await f.Resolver.ResolveAsync(Ctx())).Refusal!.Text);
    }

    [Fact]
    public void The_rate_limit_counts_per_user_and_minute()
    {
        var clock = new StepClock { Now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero) };
        var limiter = new DiscordBotRateLimiter(clock);
        for (var i = 0; i < DiscordBotRateLimiter.PerMinute; i++)
        {
            Assert.True(limiter.TryEnter(1));
        }
        Assert.False(limiter.TryEnter(1));
        Assert.True(limiter.TryEnter(2));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.True(limiter.TryEnter(1));
    }

    // ---------------------------------------------------------------- ambient user

    [Fact]
    public async Task Acting_as_an_agent_reaches_audit_and_barrier()
    {
        var service = new CurrentUserService(Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), new CircuitServicesAccessor());
        Assert.Null(service.Get().Id);
        using (CurrentUserService.ActAs(ClaimsPrincipalBuilder.Agent("bot-agent").WithCodename("Falke").Build()))
        {
            await Task.Yield();
            Assert.Equal("bot-agent", (await service.GetAsync()).Id);
            Assert.Equal("bot-agent", service.Get().Id);
        }
        Assert.Null(service.Get().Id);
    }

    [Fact]
    public async Task Creating_a_job_needs_write_access()
    {
        using var ctx = new SqliteTestContext();
        var svc = new JobService(ctx.Factory, Substitute.For<ICaseNumberService>(), Substitute.For<INotificationService>());
        var demo = ClaimsPrincipalBuilder.Agent("d").WithRank(Rank.Director).AsDemo().Build();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(new JobInput { Title = "x" }, [], demo));
    }
}

internal sealed class StepClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
