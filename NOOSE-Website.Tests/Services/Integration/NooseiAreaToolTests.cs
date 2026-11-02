using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The area tool reports a standing state. What matters is that an area the viewer has no right to reads
/// exactly like an empty one — otherwise the tool answers questions about rights instead of about the agency.</summary>
public sealed class NooseiAreaToolTests
{
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Treasury_ReportsTheBalancePerAccount()
    {
        using var ctx = new SqliteTestContext();
        var treasury = Substitute.For<IKassenService>();
        treasury.GetSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KassenKontoSummary>>(
            [
                new(KassenKonto.Schwarzgeld, 125_000m, 12, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
            ]));
        treasury.GetLedgerAsync(Arg.Any<KassenKonto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KassenBuchungDisplay>>([]));

        var result = await NooseiToolHost.Area(ctx, treasury: treasury).InvokeAsync(
            Args("""{"bereich":"kasse"}"""), NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("125.000 $", result.Text);
        Assert.Contains("Buchungen: 12", result.Text);
    }

    [Fact]
    public async Task Roster_StaysEmptyForAnOrdinaryAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: "a1", configure: a => a.Codename = "Falke"));
            db.SaveChanges();
        }
        var tool = NooseiToolHost.Area(ctx);

        var plain = await tool.InvokeAsync(
            Args("""{"bereich":"personal"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(
            Args("""{"bereich":"personal"}"""), NooseiToolContext.From(Leader()));

        Assert.DoesNotContain("Falke", plain.Text);
        Assert.Contains("Falke", leader.Text);
    }

    [Fact]
    public async Task AnAreaTheViewerMayNotRead_AnswersLikeAnEmptyOne()
    {
        using var ctx = new SqliteTestContext();
        var counterIntel = Substitute.For<ICounterIntelService>();
        counterIntel.GetOverviewAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<CounterIntelOverview>>(_ => throw new UnauthorizedAccessException());
        var tool = NooseiToolHost.Area(ctx, counterIntel: counterIntel);

        var refused = await tool.InvokeAsync(
            Args("""{"bereich":"gegenaufklaerung"}"""), NooseiToolContext.From(Junior()));
        var empty = await tool.InvokeAsync(
            Args("""{"bereich":"wiedervorlagen"}"""), NooseiToolContext.From(Junior()));

        // same shape of sentence, so the model cannot tell a missing right from a quiet area
        Assert.False(refused.IsError);
        Assert.Contains("liegt dir nichts vor", refused.Text);
        Assert.Contains("liegt dir nichts vor", empty.Text);
    }

    [Fact]
    public async Task CounterIntelligence_ReportsTheWindowAndTheFlags()
    {
        using var ctx = new SqliteTestContext();
        var counterIntel = Substitute.For<ICounterIntelService>();
        counterIntel.GetOverviewAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CounterIntelOverview(400, 9, 120, 17, 30)));
        counterIntel.GetFlagsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<InsiderFlag>>(
            [
                new("a1", "Falke", "Nachtzugriffe", "14 Zugriffe zwischen 2 und 5 Uhr", 3, null),
            ]));

        var result = await NooseiToolHost.Area(ctx, counterIntel: counterIntel).InvokeAsync(
            Args("""{"bereich":"gegenaufklaerung"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("letzte 30 Tage", result.Text);
        Assert.Contains("Falke", result.Text);
        Assert.Contains("Nachtzugriffe", result.Text);
    }

    [Fact]
    public async Task AnAreaThatDoesNotExist_IsRefusedRatherThanGuessed()
    {
        using var ctx = new SqliteTestContext();

        var result = await NooseiToolHost.Area(ctx).InvokeAsync(
            Args("""{"bereich":"kaffeekasse"}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
        Assert.Contains("kasse", result.Text);
        Assert.Contains("asservatenkammer", result.Text);
    }

    [Fact]
    public async Task EveryArea_IsAnsweredByABranch()
    {
        using var ctx = new SqliteTestContext();
        var tool = NooseiToolHost.Area(ctx);
        var areas = JsonSerializer.Deserialize<string[]>(
            tool.ParameterSchema.GetProperty("properties").GetProperty("bereich").GetProperty("enum"))!;

        Assert.NotEmpty(areas);
        foreach (var area in areas)
        {
            var result = await tool.InvokeAsync(
                Args($$"""{"bereich":"{{area}}"}"""), NooseiToolContext.From(Leader()));
            // an area in the schema without a branch would be refused as unknown, which is the drift this catches
            Assert.False(result.IsError, $"Bereich {area} wird vom Schema angeboten, aber nicht beantwortet.");
        }
    }
}
