using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetCord.Rest;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Threat;

/// <summary>Threat list and bot faction board as one board.</summary>
public sealed class FactionBoardMergeTests : IDisposable
{
    private const string Hook = "https://discord.com/api/webhooks/111/token";

    private readonly SqliteTestContext _ctx = new();
    private readonly IDiscordWebhookService _discord = Substitute.For<IDiscordWebhookService>();
    private readonly FakeBotRoutes _routes = new();
    private readonly FactionThreatBoardService _service;
    private DiscordWebhookConfig _config = Config(90);

    public FactionBoardMergeTests()
    {
        _discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(_ => _config);
        _discord.PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>()).Returns("555");
        _discord.EditThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>())
            .Returns(ThreatBoardEdit.Edited);
        _discord.DeleteThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThreatBoardRemoval.Removed);
        _service = new FactionThreatBoardService(_ctx.Factory, _discord, _routes, NullLogger<FactionThreatBoardService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    private static DiscordWebhookConfig Config(int threshold)
        => new(true, "https://fibhq.com", new Dictionary<NotificationType, string?> { [NotificationType.FactionThreatBoard] = Hook },
            new Dictionary<NotificationType, string?>(), IncludeHeadline: true, ThreatBoardThreshold: threshold);

    private void SeedFaction(string id, string name, int? score, Action<Faction>? configure = null)
    {
        using var db = _ctx.NewContext();
        db.Factions.Add(Seed.Faction(id, name, f =>
        {
            f.ThreatScore = score;
            configure?.Invoke(f);
        }));
        db.SaveChanges();
    }

    private void SetScore(string id, int score)
    {
        using var db = _ctx.NewContext();
        db.Factions.Where(f => f.Id == id).ExecuteUpdate(s => s.SetProperty(f => f.ThreatScore, score));
    }

    private ThreatBoardMessage? StoredBoard()
    {
        using var db = _ctx.NewContext();
        return FactionThreatBoard.ParseRef(db.SystemSettings
            .Where(s => s.Key == SystemSettingKeys.DiscordThreatBoardMessage).Select(s => s.Value).SingleOrDefault());
    }

    // stored bot board
    private void StoreBotBoard(ulong channel)
    {
        using var db = _ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordBotFactionBoardState, Value = $"{channel}|1|hash" });
        db.SaveChanges();
    }

    // Discord calls in order
    private List<string> DiscordCalls()
        => _discord.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Where(name => name != nameof(IDiscordWebhookService.GetConfigAsync))
            .Select(name => name.Replace("ThreatBoardAsync", string.Empty))
            .ToList();

    private DiscordLiveBoards Boards()
    {
        var webhook = Substitute.For<IDiscordWebhookService>();
        webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(_ => _config);
        return new DiscordLiveBoards(_ctx.Factory, Substitute.For<IPublicWantedService>(),
            Substitute.For<ISystemSettingService>(), webhook);
    }

    private static string Text(BoardCard card) => string.Concat(card.Components.OfType<ComponentContainerProperties>()
        .SelectMany(c => c.Components).OfType<TextDisplayProperties>().Select(t => t.Content + "\n"));

    [Fact]
    public async Task Webhook_board_retires_when_bot_takes_over()
    {
        SeedFaction("vagos", "Vagos", 97);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());

        _routes.FactionBoard = 9;
        StoreBotBoard(9);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync(force: true));

        // removed exactly once
        Assert.Equal(["Post", "Delete"], DiscordCalls());
        await _discord.Received(1).DeleteThreatBoardAsync(Hook, "555", Arg.Any<CancellationToken>());
        Assert.Null(StoredBoard());
    }

    [Fact]
    public async Task Webhook_board_stays_until_the_bot_board_is_posted()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        // no bot board yet
        _routes.FactionBoard = 9;
        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());
        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync());
        Assert.Equal(["Post", "Edit"], DiscordCalls());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());

        StoreBotBoard(9);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());
        Assert.Equal(["Post", "Edit", "Delete"], DiscordCalls());
        Assert.Null(StoredBoard());
    }

    [Fact]
    public async Task Webhook_board_posts_while_the_bot_has_no_board_yet()
    {
        SeedFaction("vagos", "Vagos", 97);
        _routes.FactionBoard = 9;

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
    }

    [Fact]
    public async Task Webhook_board_stays_while_the_bot_board_is_in_another_channel()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        _routes.FactionBoard = 9;
        StoreBotBoard(8);
        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());

        Assert.Equal(["Post"], DiscordCalls());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
    }

    [Fact]
    public async Task Webhook_board_stays_without_bot()
    {
        SeedFaction("vagos", "Vagos", 97);

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());
        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());
        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync());

        Assert.Equal(["Post", "Edit"], DiscordCalls());
        await _discord.Received(1).EditThreatBoardAsync(Hook, "555", Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
    }

    [Fact]
    public async Task Webhook_board_returns_when_the_bot_gives_the_board_up()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        _routes.FactionBoard = 9;
        StoreBotBoard(9);
        await _service.RunAsync();
        _discord.PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>()).Returns("777");

        _routes.FactionBoard = null;
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        Assert.Equal(["Post", "Delete", "Post"], DiscordCalls());
        Assert.Equal(new ThreatBoardMessage("777", Hook), StoredBoard());
    }

    [Fact]
    public async Task A_takeover_waits_for_the_removal_and_gives_up_after_an_hour()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var service = new FactionThreatBoardService(_ctx.Factory, _discord, _routes, NullLogger<FactionThreatBoardService>.Instance, clock);
        SeedFaction("vagos", "Vagos", 97);
        await service.RunAsync();
        _discord.DeleteThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThreatBoardRemoval.Failed);

        _routes.FactionBoard = 9;
        StoreBotBoard(9);
        Assert.Equal(ThreatBoardOutcome.Failed, await service.RunAsync());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
        clock.Now += FactionThreatBoardService.RetireGiveUpAfter + TimeSpan.FromMinutes(1);
        Assert.Equal(ThreatBoardOutcome.Off, await service.RunAsync());

        Assert.Null(StoredBoard());
    }

    [Fact]
    public async Task Faction_card_respects_threshold()
    {
        SeedFaction("low", "Marabunta", 40);
        SeedFaction("high", "Vagos", 60);
        _config = Config(50);

        var card = await Boards().RenderFactionsAsync(CancellationToken.None);

        var text = Text(card);
        Assert.Contains("Vagos", text);
        Assert.DoesNotContain("Marabunta", text);
    }

    [Fact]
    public async Task Faction_card_stays_empty_at_the_threshold_and_says_so()
    {
        SeedFaction("edge", "Marabunta", 50);
        _config = Config(50);

        var text = Text(await Boards().RenderFactionsAsync(CancellationToken.None));

        Assert.DoesNotContain("Marabunta", text);
        Assert.Contains("keine Fraktion über 50", text);
    }

    [Fact]
    public async Task A_changed_threshold_renders_the_card_again_even_for_the_same_list()
    {
        SeedFaction("high", "Vagos", 60);

        _config = Config(50);
        var first = await Boards().RenderFactionsAsync(CancellationToken.None);
        _config = Config(55);
        var second = await Boards().RenderFactionsAsync(CancellationToken.None);

        Assert.Equal(Text(first).Contains("Vagos"), Text(second).Contains("Vagos"));
        Assert.NotEqual(first.Hash, second.Hash);
    }

    // pure worker decision
    [Theory]
    [InlineData(null, Hook, 403, null, Hook)]
    [InlineData(null, Hook, 404, 10003, Hook)]
    [InlineData(null, Hook, 400, 50013, Hook)]
    [InlineData(null, Hook, 500, null, null)]
    [InlineData(null, Hook, 429, null, null)]
    [InlineData(666UL, Hook, 403, 50013, null)]
    [InlineData(null, null, 403, null, null)]
    [InlineData(null, "  ", 403, null, null)]
    public void A_channel_problem_hands_a_webhook_derived_board_back_to_the_webhook(
        ulong? explicitChannel, string? webhook, int status, int? code, string? expected)
        => Assert.Equal(expected, DiscordSendFailure.FactionBoardFallback(explicitChannel, webhook, status, code));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
