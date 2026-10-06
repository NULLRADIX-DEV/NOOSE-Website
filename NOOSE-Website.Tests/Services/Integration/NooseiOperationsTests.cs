using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The turn ran, and what it cost is only half of that. These cover the other half: how it ran, and what
/// the operations view makes of it.</summary>
public sealed class NooseiOperationsTests
{
    private const string AgentId = "agent-ops";

    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Director).WithCodename("Falke").Build();

    private static LlmOptions Tuning(Action<LlmOptions>? configure = null)
    {
        var o = new LlmOptions
        {
            Enabled = true,
            ApiKey = "k",
            Model = "vendor/model",
            TurnTimeoutSeconds = 30,
            MaxToolRounds = 3,
            MaxToolCallsPerRound = 4,
            ToolTimeoutSeconds = 5,
        };
        configure?.Invoke(o);
        return o;
    }

    private static LlmResult Result(
        string? text = "Antwort", decimal cost = 0.01m, string finish = "stop", params LlmToolCall[] calls)
        => new(text, calls, new LlmUsage(100, 20, 120, 0, 0, cost), "Baidu", "vendor/model", finish, "gen-1", 1, 42);

    private static async Task<(NooseiGateway Gateway, ILlmService Llm)> BuildAsync(
        SqliteTestContext ctx, Action<LlmOptions>? configure = null)
    {
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(AgentId, Rank.Director, configure: a =>
            {
                a.Rank = Rank.Director;
                a.Codename = "Falke";
            }));
            await db.SaveChangesAsync();
        }

        var configService = Substitute.For<ILlmQuotaConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(LlmQuotaConfig.Default());
        var tuning = Options.Create(Tuning(configure));
        var providers = NooseiProviderStub.Returning();
        var quota = new LlmQuotaService(ctx.Factory, configService, providers, tuning, NullLogger<LlmQuotaService>.Instance);
        var llm = Substitute.For<ILlmService>();
        llm.IsConfigured.Returns(true);
        return (new NooseiGateway(llm, quota, providers, tuning, NullLogger<NooseiGateway>.Instance), llm);
    }

    private static NooseiCall Call(
        IReadOnlyList<LlmToolDefinition>? tools = null, NooseiToolExecutor? executor = null)
        => new(LlmFeature.Chat, [LlmMessage.System("sys"), LlmMessage.User("Wer führt die Ballas?")],
            LoggedPrompt: "Wer führt die Ballas?", Tools: tools, ToolExecutor: executor);

    private static LlmToolDefinition Tool(string name = "suche_akten")
        => new(name, "Sucht Akten.", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    // ---- 4.4: the calls of one round run together ----

    [Fact]
    public async Task ToolsOfOneRound_RunTogether_NotOneAfterTheOther()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.001m, "tool_calls",
                    new LlmToolCall("c1", "suche_akten", """{"q":"a"}"""),
                    new LlmToolCall("c2", "suche_akten", """{"q":"b"}"""),
                    new LlmToolCall("c3", "suche_akten", """{"q":"c"}"""))
                : Result("Fertig"));

        var running = 0;
        var peak = 0;
        var answer = await gateway.AskAsync(Call([Tool()], async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            // no lock needed: only the maximum matters, and it is only ever raised
            peak = Math.Max(peak, now);
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref running);
            return NooseiToolOutcome.Plain("Treffer");
        }), Agent());

        Assert.Equal("Fertig", answer.Text);
        // serialised, three 40 ms reads would have had a peak of one
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task ParallelTools_KeepTheModelsOrder_InTheTranscriptAndTheRefs()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.001m, "tool_calls",
                    new LlmToolCall("c1", "lies_akte", """{"id":"1"}"""),
                    new LlmToolCall("c2", "lies_akte", """{"id":"2"}"""),
                    new LlmToolCall("c3", "lies_akte", """{"id":"3"}"""))
                : Result("Fertig"));

        // the first call is the slowest, so a result-order transcript would put it last
        var answer = await gateway.AskAsync(Call([Tool("lies_akte")], async (call, ct) =>
        {
            await Task.Delay(call.Id == "c1" ? 60 : 5, ct);
            return new NooseiToolOutcome($"Akte {call.Id}", [new LlmContextRef("Person", call.Id, "P" + call.Id)]);
        }), Agent());

        Assert.Equal("Fertig", answer.Text);
        var toolTexts = answer.Transcript.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content).ToList();
        Assert.Equal(["Akte c1", "Akte c2", "Akte c3"], toolTexts);
        // the source list dedups by position, so a shuffled ref order would silently reorder the chips
        var people = answer.Refs!.Where(r => r.Kind == "Person").Select(r => r.Id).ToList();
        Assert.Equal(["c1", "c2", "c3"], people);
    }

    [Fact]
    public async Task ARepeatedCall_IsStillAnsweredFromTheTranscript_AndCountsNoToolEntry()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.001m, "tool_calls",
                    new LlmToolCall("c1", "suche_akten", """{"q":"a"}"""),
                    new LlmToolCall("c2", "suche_akten", """{"q":"a"}"""))
                : Result("Fertig"));

        var executed = 0;
        var answer = await gateway.AskAsync(Call([Tool()], (_, _) =>
        {
            Interlocked.Increment(ref executed);
            return Task.FromResult(NooseiToolOutcome.Plain("Treffer"));
        }), Agent());

        Assert.Equal(1, executed);
        Assert.Equal(2, answer.Transcript.Count(m => m.Role == LlmRole.Tool));
        // one tool entry, not two: a repeat never ran, and a phantom call would skew the ranking
        Assert.Single(answer.Refs!, r => r.Kind == "tool");
        Assert.Contains("c2", answer.BarrenTools!);
    }

    // ---- 4.5: a turn that runs out of time still hands over what it has ----

    [Fact]
    public async Task TurnTimeout_HandsOverThePartialAnswer_InsteadOfThrowing()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx, o => o.TurnTimeoutSeconds = 5);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result("Erste Erkenntnisse zur Lage", 0.001m, "tool_calls",
                    new LlmToolCall("c1", "suche_akten", "{}"))
                : Result("Nie erreicht"));

        // the tool outlives the turn budget, which is exactly the case that used to end in a bare exception
        var answer = await gateway.AskAsync(Call([Tool()], async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return NooseiToolOutcome.Plain("zu spät");
        }), Agent());

        Assert.StartsWith("Erste Erkenntnisse zur Lage", answer.Text);
        Assert.True(answer.Truncated);
        Assert.Contains("abgelaufen", answer.Text);

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        // a success that names a failure kind: the agent got text, and the row still says it was cut short
        Assert.True(row.Success);
        Assert.Equal(LlmFailureKind.Timeout, row.FailureKind);
        Assert.Equal(LlmToolWithdrawal.TimeSpent, row.Withdrawal);
    }

    [Fact]
    public async Task TurnTimeout_WithoutAnyTextYet_StillThrows()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx, o => o.TurnTimeoutSeconds = 5);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result(null, 0.001m, "tool_calls", new LlmToolCall("c1", "suche_akten", "{}")));

        // nothing to rescue: inventing an answer out of an empty round would be worse than the error
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.AskAsync(
            Call([Tool()], async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                return NooseiToolOutcome.Plain("zu spät");
            }), Agent()));
    }

    [Fact]
    public async Task TheAgentsOwnCancel_FallsThrough_EvenWithTextInHand()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        using var cts = new CancellationTokenSource();
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result("Zwischenstand", 0.001m, "tool_calls", new LlmToolCall("c1", "suche_akten", "{}"))
                : Result("Nie erreicht"));

        // pressing Abbrechen must stop, not deliver a half answer the agent did not wait for
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.AskAsync(
            Call([Tool()], async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                return NooseiToolOutcome.Plain("zu spät");
            }), Agent(), cts.Token));
    }

    // ---- 4.7: the row says how the turn ran ----

    [Fact]
    public async Task TheLogRow_RecordsHowTheTurnRan()
    {
        using var ctx = new SqliteTestContext();
        // the final round stops on length to show the column; continuing it is another test's business
        var (gateway, llm) = await BuildAsync(ctx, o => o.MaxAnswerContinuations = 0);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.001m, "tool_calls",
                    new LlmToolCall("c1", "suche_akten", """{"q":"a"}"""),
                    new LlmToolCall("c2", "lies_akte", """{"id":"1"}"""))
                : Result("Fertig", 0.001m, "length"));

        await gateway.AskAsync(Call([Tool(), Tool("lies_akte")], (call, _) => Task.FromResult(
            call.Name == "lies_akte" ? NooseiToolOutcome.Failed("kaputt") : NooseiToolOutcome.Plain("Treffer"))),
            Agent());

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal("length", row.FinishReason);
        Assert.Equal(2, row.ToolCalls);
        Assert.Equal(1, row.ToolFailures);
        Assert.Equal(2, row.Attempts);
        Assert.Equal(LlmToolWithdrawal.Answered, row.Withdrawal);
        Assert.False(row.Degraded);
        Assert.Null(row.FailureKind);
        // the two model rounds took 42 ms each; the rest of the duration is what the tools cost
        Assert.Equal(84, row.ModelLatencyMs);
    }

    [Fact]
    public async Task AFeatureWithoutTools_ReportsNoWithdrawalAtAll()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result());

        await gateway.AskAsync(Call(), Agent());

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        // null, not "Answered": proofreading never had tools, so it never withdrew any
        Assert.Null(row.Withdrawal);
        Assert.Equal(0, row.ToolCalls);
    }

    [Fact]
    public async Task RunningOutOfRounds_IsToldApartFromALoop()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx, o => o.MaxToolRounds = 2);
        var seen = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                ? Result(null, 0.001m, "tool_calls", new LlmToolCall("c" + ++seen, "suche_akten", $$"""{"q":"{{seen}}"}"""))
                : Result("Endlich"));

        await gateway.AskAsync(Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("Treffer"))), Agent());

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal(LlmToolWithdrawal.RoundsSpent, row.Withdrawal);
    }

    [Fact]
    public async Task AFailedRequest_IsFiledUnderItsCause()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<LlmResult>(_ => throw new HttpRequestException("Endpunkt weg"));

        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.AskAsync(Call(), Agent()));

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.False(row.Success);
        Assert.Equal(LlmFailureKind.Upstream, row.FailureKind);
    }

    // ---- 4.8: the operations report ----

    private static ILlmRequestLogService LogService(SqliteTestContext ctx)
        => new LlmRequestLogService(ctx.Factory, Substitute.For<ILlmQuotaService>());

    [Fact]
    public async Task Operations_MeasuresCacheShareSuccessAndSpeed()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(AgentId, Rank.Director));
            for (var i = 0; i < 4; i++)
            {
                db.LlmRequests.Add(Row(durationMs: 100 * (i + 1), modelMs: 40, prompt: 1_000, cached: 250,
                    success: i < 3, finish: "stop"));
            }
            await db.SaveChangesAsync();
        }

        var report = await LogService(ctx).GetOperationsAsync(30, Agent());

        Assert.Equal(4, report.TotalRequests);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0.75, report.SuccessShare, 3);
        Assert.Equal(0.25, report.CacheHitShare, 3);
        var chat = Assert.Single(report.ByFeature);
        // nearest rank over 100/200/300/400
        Assert.Equal(200, chat.MedianMs);
        Assert.Equal(400, chat.P95Ms);
        // duration minus the model's own time: what the record database took
        Assert.Equal(160, chat.MedianToolMs);
    }

    [Fact]
    public async Task Operations_RanksToolsByHowOftenTheyRan()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(AgentId, Rank.Director));
            db.LlmRequests.Add(Row(refs: """[{"Kind":"tool","Id":null,"Name":"suche_akten"},{"Kind":"Person","Id":"p1","Name":"Max"}]"""));
            db.LlmRequests.Add(Row(refs: """[{"Kind":"tool","Id":null,"Name":"suche_akten"},{"Kind":"tool","Id":null,"Name":"lies_akte"}]"""));
            await db.SaveChangesAsync();
        }

        var report = await LogService(ctx).GetOperationsAsync(30, Agent());

        Assert.Equal(2, report.Tools.Count);
        Assert.Equal("Aktensuche", report.Tools[0].Label);
        Assert.Equal(2, report.Tools[0].Count);
        // a record reference carries a real name and has no place in a chart of tools
        Assert.DoesNotContain(report.Tools, t => t.Label.Contains("Max", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Operations_LeavesOlderRowsOutOfTheDistributions_ButNotOutOfTheCount()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(AgentId, Rank.Director));
            // a row from before the operations columns existed: every new field is null on it
            db.LlmRequests.Add(Row(finish: null, withdrawal: null, quotaTokens: 500));
            db.LlmRequests.Add(Row(finish: "stop", withdrawal: LlmToolWithdrawal.Answered, quotaTokens: 700));
            await db.SaveChangesAsync();
        }

        var report = await LogService(ctx).GetOperationsAsync(30, Agent());

        Assert.Equal(2, report.TotalRequests);
        Assert.Equal(1_200L, report.QuotaTokens);
        Assert.Single(report.FinishReasons);
        Assert.Single(report.Withdrawals);
    }

    [Fact]
    public async Task Operations_IsNotForReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        var supervisor = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => LogService(ctx).GetOperationsAsync(30, supervisor));
    }

    private static NOOSE_Website.Data.Entities.Llm.LlmRequestLog Row(
        int durationMs = 100, int? modelMs = 40, int prompt = 100, int cached = 0, bool success = true,
        string? finish = "stop", LlmToolWithdrawal? withdrawal = LlmToolWithdrawal.Answered,
        long quotaTokens = 100, string? refs = null)
    {
        var (year, week) = IsoWeekPeriod.Current();
        return new NOOSE_Website.Data.Entities.Llm.LlmRequestLog
        {
            AgentId = AgentId,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            BudgetYear = year,
            BudgetWeek = week,
            Feature = LlmFeature.Chat,
            PromptTokens = prompt,
            CachedTokens = cached,
            QuotaTokens = quotaTokens,
            DurationMs = durationMs,
            ModelLatencyMs = modelMs,
            Success = success,
            FinishReason = finish,
            Withdrawal = withdrawal,
            FailureKind = success ? null : LlmFailureKind.Upstream,
            ContextRefsJson = refs,
        };
    }
}
