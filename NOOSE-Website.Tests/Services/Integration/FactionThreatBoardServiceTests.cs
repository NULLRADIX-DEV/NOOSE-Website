using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Threat;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The Discord threat board: who stands on it, and when it is posted, edited, removed or left alone.</summary>
public sealed class FactionThreatBoardServiceTests : IDisposable
{
    private const string Hook = "https://discord.com/api/webhooks/111/token";
    private const string OtherHook = "https://discord.com/api/webhooks/222/other";

    private readonly SqliteTestContext _ctx = new();
    private readonly IDiscordWebhookService _discord = Substitute.For<IDiscordWebhookService>();
    private readonly FactionThreatBoardService _service;
    private DiscordWebhookConfig _config = Config(Hook);

    public FactionThreatBoardServiceTests()
    {
        _discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(_ => _config);
        _discord.PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>())
            .Returns("555");
        _discord.EditThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>())
            .Returns(ThreatBoardEdit.Edited);
        _discord.DeleteThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThreatBoardRemoval.Removed);
        _service = new FactionThreatBoardService(_ctx.Factory, _discord, NullLogger<FactionThreatBoardService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    private static DiscordWebhookConfig Config(string? url, bool enabled = true, int threshold = 90)
        => new(enabled, "https://noose.info",
            new Dictionary<NotificationType, string?> { [NotificationType.FactionThreatBoard] = url },
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

    private string? Stored(string key)
    {
        using var db = _ctx.NewContext();
        return db.SystemSettings.Where(s => s.Key == key).Select(s => s.Value).SingleOrDefault();
    }

    private ThreatBoardMessage? StoredBoard() => FactionThreatBoard.ParseRef(Stored(SystemSettingKeys.DiscordThreatBoardMessage));

    private ThreatBoardEmbed LastSent()
        => _discord.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name is nameof(IDiscordWebhookService.PostThreatBoardAsync)
                or nameof(IDiscordWebhookService.EditThreatBoardAsync))
            .GetArguments().OfType<ThreatBoardEmbed>().Single();

    // the Discord traffic in order, without the config reads in between
    private List<string> DiscordCalls()
        => _discord.ReceivedCalls()
            .Select(c => (Name: c.GetMethodInfo().Name, Url: c.GetArguments().FirstOrDefault() as string))
            .Where(c => c.Name != nameof(IDiscordWebhookService.GetConfigAsync))
            .Select(c => $"{c.Name.Replace("ThreatBoardAsync", string.Empty)} {c.Url}")
            .ToList();

    private void EditAnswers(params ThreatBoardEdit[] answers)
        => _discord.EditThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>())
            .Returns(answers[0], answers[1..]);

    private void PostAnswers(params string?[] answers)
        => _discord.PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>())
            .Returns(answers[0], answers[1..]);

    private void DeleteAnswers(params ThreatBoardRemoval[] answers)
        => _discord.DeleteThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(answers[0], answers[1..]);

    [Fact]
    public async Task Nothing_happens_while_discord_is_off_or_the_board_has_no_webhook()
    {
        SeedFaction("vagos", "Vagos", 97);

        _config = Config(Hook, enabled: false);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());
        _config = Config(null);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());

        Assert.DoesNotContain(_discord.ReceivedCalls(), c => c.GetMethodInfo().Name != nameof(IDiscordWebhookService.GetConfigAsync));
    }

    [Fact]
    public async Task Only_active_unclassified_factions_above_the_threshold_stand_on_the_board()
    {
        SeedFaction("ballas", "Ballas", 91);
        SeedFaction("vagos", "Vagos", 97);
        SeedFaction("grenze", "Genau Neunzig", 90);
        SeedFaction("vs", "Verschlusssache", 99, f => f.IsClassified = true);
        SeedFaction("tru", "TRU-Sache", 99, f => { f.IsClassified = true; f.IsTRUClassified = true; });
        // a row that broke the setters' invariant still stays off
        SeedFaction("hrb", "HRB-Sache", 99, f => f.IsHRBClassified = true);
        SeedFaction("archiv", "Archiviert", 99, f => f.IsArchived = true);
        SeedFaction("papierkorb", "Gelöscht", 99, f => f.IsDeleted = true);
        SeedFaction("staat", "Staatsfraktion", 99, f => f.IsStateFaction = true);
        SeedFaction("ohne", "Ohne Score", null);

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        var lines = LastSent().Description.Split('\n').Where(l => l.StartsWith("🔴")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("[Vagos]", lines[0]);
        Assert.Contains("[Ballas]", lines[1]);
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
        Assert.NotNull(Stored(SystemSettingKeys.DiscordThreatBoardState));
        await _discord.Received(1).PostThreatBoardAsync(Hook, Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_threshold_comes_from_the_settings()
    {
        SeedFaction("ballas", "Ballas", 93);
        SeedFaction("vagos", "Vagos", 97);
        _config = Config(Hook, threshold: 95);

        await _service.RunAsync();

        var embed = LastSent();
        Assert.Contains("ÜBER 95", embed.Title);
        Assert.Contains("[Vagos]", embed.Description);
        Assert.DoesNotContain("[Ballas]", embed.Description);
    }

    [Fact]
    public async Task An_unchanged_board_is_left_alone()
    {
        SeedFaction("vagos", "Vagos", 97);

        await _service.RunAsync();
        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());

        await _discord.Received(1).PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
        await _discord.DidNotReceiveWithAnyArgs().EditThreatBoardAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_changed_score_edits_the_stored_message_with_a_new_stamp()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        var first = LastSent().AsOfUtc;

        await Task.Delay(20);
        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync());

        await _discord.Received(1).EditThreatBoardAsync(Hook, "555", Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
        Assert.Contains("`95`", LastSent().Description);
        Assert.True(LastSent().AsOfUtc > first);
        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());
    }

    [Fact]
    public async Task A_faction_dropping_below_empties_the_board_in_place()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        SetScore("vagos", 80);
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync());

        Assert.Contains("keine Fraktion über 90", LastSent().Description);
    }

    [Fact]
    public async Task A_forced_pass_renews_the_stamp_of_an_unchanged_board()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        var first = LastSent().AsOfUtc;

        await Task.Delay(20);
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync(force: true));

        Assert.True(LastSent().AsOfUtc > first);
    }

    [Fact]
    public async Task A_board_deleted_in_discord_is_found_by_the_forced_pass_and_posted_again()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        var state = Stored(SystemSettingKeys.DiscordThreatBoardState);
        EditAnswers(ThreatBoardEdit.Missing);
        PostAnswers("777");

        Assert.Equal(ThreatBoardOutcome.Unchanged, await _service.RunAsync());
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync(force: true));

        Assert.Equal(new ThreatBoardMessage("777", Hook), StoredBoard());
        Assert.Equal(state, Stored(SystemSettingKeys.DiscordThreatBoardState));
    }

    [Fact]
    public async Task A_lost_board_whose_repost_fails_is_retried_on_the_next_ordinary_pass()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        EditAnswers(ThreatBoardEdit.Missing);
        PostAnswers(null, "777");

        Assert.Equal(ThreatBoardOutcome.Failed, await _service.RunAsync(force: true));
        Assert.Null(StoredBoard());
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        Assert.Equal(new ThreatBoardMessage("777", Hook), StoredBoard());
    }

    [Fact]
    public async Task A_failed_edit_never_reposts_and_tries_again_next_pass()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        EditAnswers(ThreatBoardEdit.Failed, ThreatBoardEdit.Edited);

        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Failed, await _service.RunAsync());
        Assert.Equal(ThreatBoardOutcome.Edited, await _service.RunAsync());

        await _discord.Received(1).PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
        await _discord.Received(2).EditThreatBoardAsync(Hook, "555", Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_first_post_stores_nothing()
    {
        SeedFaction("vagos", "Vagos", 97);
        PostAnswers(null, "555");

        Assert.Equal(ThreatBoardOutcome.Failed, await _service.RunAsync());
        Assert.Null(Stored(SystemSettingKeys.DiscordThreatBoardMessage));
        Assert.Null(Stored(SystemSettingKeys.DiscordThreatBoardState));

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());
    }

    [Fact]
    public async Task A_new_webhook_removes_the_old_board_before_posting_the_new_one()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        _config = Config(OtherHook);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        Assert.Equal(["Post " + Hook, "Delete " + Hook, "Post " + OtherHook], DiscordCalls());
        Assert.Equal(new ThreatBoardMessage("555", OtherHook), StoredBoard());
    }

    [Fact]
    public async Task A_board_frozen_in_an_archived_thread_is_reposted_and_the_old_copy_removed()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        EditAnswers(ThreatBoardEdit.Archived);
        PostAnswers("777");

        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        // the post reopens the thread, only then can the frozen copy go
        Assert.Equal(["Post " + Hook, "Edit " + Hook, "Post " + Hook, "Delete " + Hook], DiscordCalls());
        await _discord.Received(1).DeleteThreatBoardAsync(Hook, "555", Arg.Any<CancellationToken>());
        Assert.Equal(new ThreatBoardMessage("777", Hook), StoredBoard());
    }

    [Fact]
    public async Task An_archived_board_stays_known_while_its_repost_fails()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        EditAnswers(ThreatBoardEdit.Archived);
        PostAnswers((string?)null);

        SetScore("vagos", 95);
        Assert.Equal(ThreatBoardOutcome.Failed, await _service.RunAsync());

        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
        await _discord.DidNotReceiveWithAnyArgs().DeleteThreatBoardAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_removal_that_keeps_failing_is_given_up_after_an_hour()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var service = new FactionThreatBoardService(_ctx.Factory, _discord, NullLogger<FactionThreatBoardService>.Instance, clock);
        SeedFaction("vagos", "Vagos", 97);
        await service.RunAsync();
        DeleteAnswers(ThreatBoardRemoval.Failed);

        _config = Config(OtherHook);
        Assert.Equal(ThreatBoardOutcome.Failed, await service.RunAsync());
        clock.Now += FactionThreatBoardService.RetireGiveUpAfter - TimeSpan.FromMinutes(1);
        Assert.Equal(ThreatBoardOutcome.Failed, await service.RunAsync());
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(ThreatBoardOutcome.Posted, await service.RunAsync());

        Assert.Equal(new ThreatBoardMessage("555", OtherHook), StoredBoard());
    }

    [Fact]
    public async Task A_rotated_token_keeps_the_old_thread_when_removing()
    {
        const string threaded = "https://discord.com/api/webhooks/111/token?thread_id=9";
        const string rotated = "https://discord.com/api/webhooks/111/new-token";
        _config = Config(threaded);
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        DeleteAnswers(ThreatBoardRemoval.Unreachable, ThreatBoardRemoval.Removed);

        _config = Config(rotated);
        await _service.RunAsync();

        await _discord.Received(1).DeleteThreatBoardAsync(rotated + "?thread_id=9", "555", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_container_builds_the_service_and_its_worker_without_a_registered_clock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(_ctx.Factory);
        services.AddScoped(_ => _discord);
        services.AddScoped<IFactionThreatBoardService, FactionThreatBoardService>();
        services.AddHostedService<FactionThreatBoardWorker>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<IFactionThreatBoardService>();

        Assert.Equal(ThreatBoardOutcome.Posted, await service.RunAsync());
        Assert.Single(provider.GetServices<IHostedService>().OfType<FactionThreatBoardWorker>());
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Switching_discord_off_removes_the_board_once()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        _config = Config(Hook, enabled: false);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());

        await _discord.Received(1).DeleteThreatBoardAsync(Hook, "555", Arg.Any<CancellationToken>());
        Assert.Null(StoredBoard());

        _config = Config(Hook);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());
    }

    [Fact]
    public async Task Clearing_the_webhook_removes_the_board()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();

        _config = Config(null);
        Assert.Equal(ThreatBoardOutcome.Off, await _service.RunAsync());

        await _discord.Received(1).DeleteThreatBoardAsync(Hook, "555", Arg.Any<CancellationToken>());
        Assert.Null(StoredBoard());
    }

    [Fact]
    public async Task A_failed_removal_holds_everything_back_until_it_succeeds()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        DeleteAnswers(ThreatBoardRemoval.Failed, ThreatBoardRemoval.Removed);

        _config = Config(OtherHook);
        Assert.Equal(ThreatBoardOutcome.Failed, await _service.RunAsync());
        Assert.Equal(new ThreatBoardMessage("555", Hook), StoredBoard());
        await _discord.Received(1).PostThreatBoardAsync(Arg.Any<string>(), Arg.Any<ThreatBoardEmbed>(), Arg.Any<CancellationToken>());

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());
        Assert.Equal(new ThreatBoardMessage("555", OtherHook), StoredBoard());
    }

    [Fact]
    public async Task A_rotated_token_removes_the_old_board_through_the_new_url()
    {
        const string rotated = "https://discord.com/api/webhooks/111/new-token";
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        DeleteAnswers(ThreatBoardRemoval.Unreachable, ThreatBoardRemoval.Removed);

        _config = Config(rotated);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        await _discord.Received(1).DeleteThreatBoardAsync(Hook, "555", Arg.Any<CancellationToken>());
        await _discord.Received(1).DeleteThreatBoardAsync(rotated, "555", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unreachable_old_board_on_another_webhook_is_given_up()
    {
        SeedFaction("vagos", "Vagos", 97);
        await _service.RunAsync();
        DeleteAnswers(ThreatBoardRemoval.Unreachable);

        _config = Config(OtherHook);
        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RunAsync());

        await _discord.Received(1).DeleteThreatBoardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(new ThreatBoardMessage("555", OtherHook), StoredBoard());
    }

    [Fact]
    public async Task Only_writing_leadership_may_refresh_by_hand()
    {
        SeedFaction("vagos", "Vagos", 97);
        var junior = ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RefreshAsync(junior));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RefreshAsync(supervision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RefreshAsync(demo));
        await _discord.DidNotReceiveWithAnyArgs().PostThreatBoardAsync(default!, default!, default);

        Assert.Equal(ThreatBoardOutcome.Posted, await _service.RefreshAsync(leader));
    }
}
