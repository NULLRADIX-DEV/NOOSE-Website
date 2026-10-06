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

/// <summary>The gateway is the only path to the model: it pre-checks the quota, bills exactly once and always logs.</summary>
public sealed class NooseiGatewayTests
{
    private const string AgentId = "agent-gw";

    private static ClaimsPrincipal Agent(Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(AgentId).WithRank(rank).WithCodename("Falke").Build();

    private static LlmOptions Tuning(Action<LlmOptions>? configure = null)
    {
        var o = new LlmOptions
        {
            Enabled = true,
            ApiKey = "k",
            Model = "vendor/model",
            TurnTimeoutSeconds = 30,
            MaxToolRounds = 3,
            MaxToolCallsPerRound = 2,
            ToolTimeoutSeconds = 5,
        };
        configure?.Invoke(o);
        return o;
    }

    private static LlmResult Result(string? text = "Antwort", decimal cost = 0.01m, params LlmToolCall[] calls)
        => new(text, calls, new LlmUsage(100, 20, 120, 0, 0, cost), "Baidu", "vendor/model", "stop", "gen-1", 1, 42);

    private static async Task<(NooseiGateway Gateway, ILlmService Llm, LlmQuotaService Quota)> BuildAsync(
        SqliteTestContext ctx, Rank rank = Rank.SpecialAgent, long? over = null, Action<LlmOptions>? configure = null)
    {
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(AgentId, rank, configure: a =>
            {
                a.Rank = rank;
                a.LlmQuotaOverride = over;
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

        var gateway = new NooseiGateway(llm, quota, providers, tuning, NullLogger<NooseiGateway>.Instance);
        return (gateway, llm, quota);
    }

    private static NooseiCall Call(
        IReadOnlyList<LlmToolDefinition>? tools = null, NooseiToolExecutor? executor = null)
        => new(LlmFeature.Chat, [LlmMessage.System("sys"), LlmMessage.User("Wer führt die Ballas?")],
            LoggedPrompt: "Wer führt die Ballas?", Tools: tools, ToolExecutor: executor);

    private static LlmToolDefinition Tool(string name = "suche_akten")
        => new(name, "Sucht Akten.", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    // ---- metering ----

    [Fact]
    public async Task Ask_ChargesTheRealCost_AfterTheAnswer()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result(cost: 0.0123m));

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.Equal("Antwort", answer.Text);
        Assert.Equal(1_230L, answer.Charge.QuotaTokens);
        Assert.Equal(35_000L - 1_230L, answer.Charge.Status.Remaining);
    }

    [Fact]
    public async Task Ask_WritesTheLogRow_WithPromptAnswerAndReferences()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result());

        await gateway.AskAsync(Call() with { ContextRefs = [new LlmContextRef("Faction", "f1", "Ballas")] }, Agent());

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal("Wer führt die Ballas?", row.Prompt);
        Assert.Equal("Antwort", row.Answer);
        Assert.Contains("Ballas", row.ContextRefsJson);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task Ask_BlocksWhenExhausted_AndNeverCallsTheEndpoint()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, over: 0);

        await Assert.ThrowsAsync<LlmQuotaExceededException>(() => gateway.AskAsync(Call(), Agent()));

        await llm.DidNotReceive().CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        await using var db = ctx.NewContext();
        Assert.Empty(await db.LlmRequests.ToListAsync());
    }

    [Fact]
    public async Task Ask_AllowsAtOneTokenLeft_AndMayEndNegative()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, over: 1);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result(cost: 0.05m));

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.Equal(5_000L, answer.Charge.QuotaTokens);
        Assert.Equal(1L - 5_000L, answer.Charge.Status.Remaining);
        Assert.True(answer.Charge.Status.IsBlocked);
    }

    [Fact]
    public async Task Ask_LogsAFailedCall_WithZeroTokensAndTheError()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<LlmResult>(_ => throw new InvalidOperationException("NOOSEI antwortete nicht (Fehler 502)."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AskAsync(Call(), Agent()));

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.False(row.Success);
        Assert.Equal(0L, row.QuotaTokens);
        Assert.Contains("502", row.ErrorMessage);
        Assert.Null(row.Answer);
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("demo")]
    [InlineData("supervision")]
    public async Task Ask_DeniesTheRolesWithoutNooseiAccess(string role)
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var builder = ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.SpecialAgent);
        var actor = role switch
        {
            "partner" => builder.AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            "demo" => builder.AsDemo().Build(),
            _ => builder.AsTeamLead().Build(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.AskAsync(Call(), actor));
        await llm.DidNotReceive().CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    // ---- tool loop ----

    [Fact]
    public async Task Ask_RunsToolsAndBillsEveryRoundAsOneRequest()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.01m, new LlmToolCall("c1", "suche_akten", "{}"))
                : Result("Fertig", 0.01m));

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("• Person | Max Mustermann"))), Agent());

        Assert.Equal("Fertig", answer.Text);
        Assert.Equal(2, answer.Rounds);
        Assert.Equal(2_000L, answer.Charge.QuotaTokens);

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal(1, row.ToolRounds);
        Assert.Contains("suche_akten", row.ContextRefsJson);
    }

    [Fact]
    public async Task Ask_WithdrawsToolsOnTheLastRound_SoTheModelAlwaysAnswers()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 2);
        var seenWithoutTools = false;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LlmRequest>();
                if (!request.OffersTools)
                {
                    seenWithoutTools = true;
                    return Result("Endlich", 0.001m);
                }
                return Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"));
            });

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nichts gefunden"))), Agent());

        Assert.True(seenWithoutTools);
        Assert.Equal("Endlich", answer.Text);
        Assert.Equal(3, answer.Rounds);
    }

    [Fact]
    public async Task Ask_HandsTheModelAGermanErrorWhenAToolFails()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var toolReply = string.Empty;
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LlmRequest>();
                if (++round == 1)
                {
                    return Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"));
                }
                toolReply = request.Messages.Last(m => m.Role == LlmRole.Tool).Content ?? string.Empty;
                return Result("Trotzdem geantwortet", 0.001m);
            });

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => throw new InvalidOperationException("kaputt")), Agent());

        Assert.Equal("Trotzdem geantwortet", answer.Text);
        Assert.Contains("Werkzeug konnte nicht ausgeführt werden", toolReply);
    }

    [Fact]
    public async Task Ask_KeepsTheToolBlockInThePayload_WhenTheChoiceIsWithdrawn()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 1);
        var shapes = new List<(bool HasTools, bool Offers)>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LlmRequest>();
                shapes.Add((request.Tools is { Count: > 0 }, request.OffersTools));
                return request.OffersTools
                    ? Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"))
                    : Result("Endlich", 0.001m);
            });

        await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nichts gefunden"))), Agent());

        // a transcript carrying tool roles without a tool block is an invalid request shape, and dropping the
        // block would also break the cached prefix on the round with the largest transcript
        Assert.All(shapes, s => Assert.True(s.HasTools));
        Assert.Equal([true, false], shapes.Select(s => s.Offers));
    }

    [Fact]
    public async Task Ask_TellsTheModelTheToolsAreGone_SoItAnswersInsteadOfAnnouncing()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 1);
        var lastMessages = new List<LlmMessage>();
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LlmRequest>();
                lastMessages = [.. request.Messages];
                return request.OffersTools
                    ? Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"))
                    : Result("Endlich", 0.001m);
            });

        await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nichts gefunden"))), Agent());

        var notice = Assert.Single(lastMessages.Where(m => m.Content == NooseiPrompts.ToolsGoneRounds));
        Assert.Equal(LlmRole.System, notice.Role);
        // at the end, so the stable prompt prefix in front of it stays cacheable
        Assert.Equal(lastMessages[^1], notice);
    }

    [Fact]
    public async Task Ask_RefusesARepeatedToolCall_WithoutRunningItAgain()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 5);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                // the same lookup over and over: a weak model's favourite way to burn every round
                ? Result(null, 0.001m, new LlmToolCall("c", "suche_akten", """{"suchtext":"Ballas"}"""))
                : Result("Aufgegeben", 0.001m));
        var runs = 0;

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) =>
            {
                runs++;
                return Task.FromResult(NooseiToolOutcome.Plain("nichts gefunden"));
            }), Agent());

        Assert.Equal(1, runs);
        Assert.Equal("Aufgegeben", answer.Text);
        // round 1 executes, round 2 repeats and is refused, round 3 answers without tools
        Assert.Equal(3, answer.Rounds);
        Assert.Contains(answer.Transcript, m => m.Content == NooseiPrompts.RepeatedToolCall);
        Assert.Contains(answer.Transcript, m => m.Content == NooseiPrompts.ToolsGoneLoop);
    }

    [Fact]
    public async Task Ask_MarksARefusedRepeatAsBarren_SoItIsNotStoredAsAResult()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 5);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                ? Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"))
                : Result("Fertig", 0.001m));

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nichts"))), Agent());

        Assert.Equal("c", Assert.Single(answer.BarrenTools!));
    }

    [Fact]
    public async Task Ask_MarksADeadToolAsBarren_ButLeavesTheTextForTheModel()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 1);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                ? Result(null, 0.001m, new LlmToolCall("c", "lies_akte", "{}"))
                : Result("Trotzdem geantwortet", 0.001m));

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => throw new InvalidOperationException("Tool kaputt")), Agent());

        Assert.Equal("c", Assert.Single(answer.BarrenTools!));
        // the model still has to learn that the lookup failed, it just must not be replayed forever
        Assert.Contains(answer.Transcript, m => m.Role == LlmRole.Tool && m.Content!.Contains("nicht ausgeführt"));
    }

    [Fact]
    public async Task Ask_CapsTheAnswerLength_FromTheFeatureConfiguration()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx,
            configure: o => o.MaxAnswerTokensByFeature[LlmFeature.Chat] = 900);
        LlmRequest? sent = null;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<LlmRequest>();
                return Result();
            });

        await gateway.AskAsync(Call(), Agent());

        // without a ceiling the provider default applies and "length" can never be detected at all
        Assert.Equal(900, sent!.MaxTokens);
    }

    [Fact]
    public async Task Ask_KeepsACallersOwnAnswerCeiling()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx,
            configure: o => o.MaxAnswerTokensByFeature[LlmFeature.Chat] = 900);
        LlmRequest? sent = null;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<LlmRequest>();
                return Result();
            });

        await gateway.AskAsync(Call() with { MaxTokens = 4_000 }, Agent());

        Assert.Equal(4_000, sent!.MaxTokens);
    }

    [Fact]
    public async Task Ask_SaysTheAnswerWasCutOff_WhenTheEndpointStoppedOnLength()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResult("Halber Sat", [], new LlmUsage(100, 20, 120, 0, 0, 0.01m),
                "Baidu", "vendor/model", "length", "gen-1", 1, 42));

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.True(answer.Truncated);
    }

    private static LlmResult Piece(string text, string finish)
        => new(text, [], new LlmUsage(100, 20, 120, 0, 0, 0.01m), "Baidu", "vendor/model", finish, "gen-1", 1, 42);

    [Fact]
    public async Task Ask_ContinuesACutOffAnswer_InsteadOfHandingOverATorso()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var sent = new List<LlmRequest>();
        var pieces = new Queue<LlmResult>([Piece("Der Vorfall begann ", "length"), Piece("am Pier und endete dort.", "stop")]);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add(call.Arg<LlmRequest>());
                return pieces.Dequeue();
            });

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.Equal("Der Vorfall begann am Pier und endete dort.", answer.Text);
        Assert.False(answer.Truncated);
        Assert.Equal(2, sent.Count);
        // the second round sees its own torso and the request to resume
        Assert.Equal("Der Vorfall begann ", sent[1].Messages[^2].Content);
        Assert.Equal(NooseiPrompts.ContinueAnswer, sent[1].Messages[^1].Content);
        // both rounds are billed as one request
        Assert.Equal(240, answer.Usage.TotalTokens);
        // the resume prompt is scaffolding, never part of the stored conversation
        Assert.DoesNotContain(answer.Transcript, m => m.Content == NooseiPrompts.ContinueAnswer);
    }

    [Fact]
    public async Task Ask_StopsContinuingAtTheLimit_AndSaysTheAnswerIsStillCut()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxAnswerContinuations = 2);
        var calls = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return Piece("Teil ", "length");
            });

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.Equal(3, calls);
        Assert.Equal("Teil Teil Teil ", answer.Text);
        Assert.True(answer.Truncated);
    }

    [Fact]
    public async Task Ask_LeavesACallersOwnCeilingAlone_EvenWhenCut()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var calls = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return Piece("Kurz", "length");
            });

        var answer = await gateway.AskAsync(Call() with { MaxTokens = 300 }, Agent());

        Assert.Equal(1, calls);
        Assert.True(answer.Truncated);
    }

    [Fact]
    public async Task Ask_CountsARepeatedCallOnce_InTheTouchedToolRefs()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx, configure: o => o.MaxToolRounds = 5);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                ? Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"))
                : Result("Fertig", 0.001m));

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nichts"))), Agent());

        // a refused repeat is not a tool invocation, so the request log must not count it as one
        Assert.Single(answer.Refs!.Where(r => r.Kind == "tool"));
    }

    [Fact]
    public async Task Ask_AnswersWithoutFileAccess_WhenTheEndpointRefusesTools()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<LlmRequest>().OffersTools
                ? throw new LlmCapabilityException(schemaRelated: false, toolsRelated: true)
                : Result("Ohne Akten", 0.001m));

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("nie erreicht"))), Agent());

        Assert.True(answer.Degraded);
        Assert.Equal("Ohne Akten", answer.Text);
    }

    [Fact]
    public async Task Ask_DropsToolRolesFromTheDowngrade_SoTheRetryIsAValidShape()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var round = 0;
        IReadOnlyList<LlmMessage> retry = [];
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LlmRequest>();
                round++;
                if (round == 1)
                {
                    return Result(null, 0.001m, new LlmToolCall("c", "suche_akten", "{}"));
                }
                // the endpoint loses tool support between rounds; the retry must not carry the tool rows
                if (request.OffersTools)
                {
                    throw new LlmCapabilityException(schemaRelated: false, toolsRelated: true);
                }
                retry = request.Messages;
                return Result("Ohne Akten", 0.001m);
            });

        var answer = await gateway.AskAsync(
            Call([Tool()], (_, _) => Task.FromResult(NooseiToolOutcome.Plain("Ballas gefunden"))), Agent());

        Assert.True(answer.Degraded);
        Assert.DoesNotContain(retry, m => m.Role == LlmRole.Tool);
        Assert.DoesNotContain(retry, m => m.ToolCalls is { Count: > 0 });
        // the text survives as plain context rather than being thrown away
        Assert.Contains(retry, m => m.Content is { } c && c.Contains("Ballas gefunden"));
    }

    // ---- what a turn touched ----

    [Fact]
    public async Task Ask_CarriesTheRecordsATooltouched_IntoTheAnswerAndTheLog()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        var round = 0;
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1
                ? Result(null, 0.01m, new LlmToolCall("c1", "lies_akte", "{}"))
                : Result("Fertig", 0.01m));

        var answer = await gateway.AskAsync(
            Call([Tool("lies_akte")], (_, _) => Task.FromResult(
                new NooseiToolOutcome("Akte …", [new LlmContextRef("Person", "p1", "Max Mustermann")]))),
            Agent());

        // without this the source chips have nothing to render and the log names only the tool
        var record = Assert.Single(answer.Refs!, r => r.Id is not null);
        Assert.Equal("Max Mustermann", record.Name);

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Contains("Max Mustermann", row.ContextRefsJson);
        Assert.Contains("lies_akte", row.ContextRefsJson);
    }

    [Fact]
    public async Task Ask_ReportsNoRecords_WhenNoToolWasCalled()
    {
        using var ctx = new SqliteTestContext();
        var (gateway, llm, _) = await BuildAsync(ctx);
        llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Result("Direkt"));

        var answer = await gateway.AskAsync(Call(), Agent());

        Assert.Empty(answer.Refs!);
    }
}
