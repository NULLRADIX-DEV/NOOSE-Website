using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Models.Threat;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>An aggregate can report the existence of records no tool would name. Everything here is about the
/// one flag that decides whether it does.</summary>
public sealed class StatisticsToolTests
{
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static StatisticsReport EmptyReport() => new(
        new DashboardMetrics(3, 1, 0, 2, 0, 1, 0),
        [new DistributionSegment("Verdachtsfall", 2)], [], [], [], [], [], [], [], []);

    [Fact]
    public async Task Overview_DerivesLeadershipFromTheScope_NeverAssertsIt()
    {
        var statistics = Substitute.For<IStatisticsService>();
        statistics.GetReportAsync(Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyReport());
        var tool = new StatisticsTool(statistics, Substitute.For<IThreatStatisticsService>(), Substitute.For<IThreatTrendService>());

        await tool.InvokeAsync(Args("""{"bereich":"ueberblick"}"""), NooseiToolContext.From(Junior()));
        await tool.InvokeAsync(Args("""{"bereich":"ueberblick"}"""), NooseiToolContext.From(Leader()));

        await statistics.Received(1).GetReportAsync(false, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await statistics.Received(1).GetReportAsync(true, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overview_SaysSoWhenTheFiguresAreIncomplete()
    {
        var statistics = Substitute.For<IStatisticsService>();
        statistics.GetReportAsync(Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyReport());
        var tool = new StatisticsTool(statistics, Substitute.For<IThreatStatisticsService>(), Substitute.For<IThreatTrendService>());

        var junior = await tool.InvokeAsync(Args("""{"bereich":"ueberblick"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(Args("""{"bereich":"ueberblick"}"""), NooseiToolContext.From(Leader()));

        // without the note a junior reads a partial count as the whole stock
        Assert.Contains("nur nicht eingestufte Akten", junior.Text);
        Assert.DoesNotContain("nur nicht eingestufte Akten", leader.Text);
    }

    [Fact]
    public async Task Threat_PassesTheScopeIntoTheStatisticsScope()
    {
        var threat = Substitute.For<IThreatStatisticsService>();
        threat.GetHeadlineAsync(Arg.Any<StatisticsScope>(), Arg.Any<CancellationToken>())
            .Returns(new ThreatHeadline(10, 3, 1, 42.5, 0.8));
        var tool = new StatisticsTool(Substitute.For<IStatisticsService>(), threat, Substitute.For<IThreatTrendService>());

        await tool.InvokeAsync(Args("""{"bereich":"bedrohung"}"""), NooseiToolContext.From(Junior()));

        await threat.Received(1).GetHeadlineAsync(
            Arg.Is<StatisticsScope>(s => !s.IncludeClassified), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Movers_HandTheRealPrincipalOver_AndCiteWhatTheyName()
    {
        var trends = Substitute.For<IThreatTrendService>();
        trends.GetTopMoversAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new ThreatMover(nameof(NOOSE_Website.Data.Entities.Factions.Faction), "f1", "Ballas", 40, 70, 30, false, "/fraktionen/f1")]);
        var tool = new StatisticsTool(Substitute.For<IStatisticsService>(), Substitute.For<IThreatStatisticsService>(), trends);
        var junior = Junior();

        var result = await tool.InvokeAsync(Args("""{"bereich":"bewegung","tage":14}"""), NooseiToolContext.From(junior));

        // this service filters itself, so the principal must arrive unchanged rather than a derived flag
        await trends.Received(1).GetTopMoversAsync(junior, 14, Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Contains("Ballas", result.Text);
        Assert.Contains("Fraktion", result.Text);
        Assert.Equal("f1", Assert.Single(result.Refs!).Id);
    }

    [Fact]
    public async Task UnknownArea_IsRefused()
    {
        var tool = new StatisticsTool(Substitute.For<IStatisticsService>(),
            Substitute.For<IThreatStatisticsService>(), Substitute.For<IThreatTrendService>());

        var result = await tool.InvokeAsync(Args("""{"bereich":"alles"}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
    }
}
