using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>What one tool call produced: the German plain text the model reads, plus the records it touched.</summary>
/// <remarks>The refs are the reason this is not a bare string — they carry the source chips of an answer and the
/// record list of a log row, and a tool that returns only text loses both.</remarks>
/// <param name="IsError">No result was produced at all — a timeout, a dead tool, unusable arguments. Decides only
/// whether the row is worth storing, and never reaches the answer, the chips or the agent.</param>
/// <remarks>A tool's own <see cref="Llm.Tools.NooseiToolResult.IsError" /> deliberately does not feed this. "Not
/// found" and "no hits" are answers: they belong in the conversation, so a follow-up turn knows the lookup was
/// already made. Only what never reached a tool is barren.</remarks>
public sealed record NooseiToolOutcome(string Text, IReadOnlyList<LlmContextRef>? Refs = null, bool IsError = false)
{
    public static NooseiToolOutcome Plain(string text) => new(text);

    public static NooseiToolOutcome Failed(string text) => new(text, null, true);
}

/// <summary>Executes one tool call the model asked for.</summary>
public delegate Task<NooseiToolOutcome> NooseiToolExecutor(LlmToolCall call, CancellationToken cancellationToken);

/// <summary>One user-visible NOOSEI request. May cost several rounds; it is billed exactly once.</summary>
public sealed record NooseiCall(
    LlmFeature Feature,
    IReadOnlyList<LlmMessage> Messages,
    string? LoggedPrompt = null,
    LlmResponseFormat? ResponseFormat = null,
    IReadOnlyList<LlmToolDefinition>? Tools = null,
    NooseiToolExecutor? ToolExecutor = null,
    double Temperature = 0.3,
    int? MaxTokens = null,
    string? EntityType = null,
    string? EntityId = null,
    string? ConversationId = null,
    IReadOnlyList<LlmContextRef>? ContextRefs = null,
    bool RequireCapableProviders = false);

/// <summary>What one request produced, plus what it cost the agent's weekly quota.</summary>
/// <remarks><paramref name="Truncated" /> means the token cap cut the answer off; callers that parse a fixed
/// answer shape can say so instead of blaming the model for a broken reply.</remarks>
/// <param name="BarrenTools">Ids of tool calls that returned no record content — a failure, or a repeat answered
/// from the transcript. Kept out of storage so a transient failure is not replayed forever.</param>
public sealed record NooseiAnswer(
    string? Text,
    LlmUsage Usage,
    LlmQuotaCharge Charge,
    int Rounds,
    IReadOnlyList<LlmMessage> Transcript,
    bool Degraded,
    bool Truncated = false,
    IReadOnlyList<LlmContextRef>? Refs = null,
    IReadOnlyList<string>? BarrenTools = null,
    // the model this turn actually ran on. A caller that resolves the upstream a second time for its label can
    // catch the ten-second cache mid-turn over and record a name the request never used.
    string? Model = null);

/// <summary>The only way to reach the model. Enforces use permission, the weekly quota and the request log,
/// so a new feature cannot get free tokens by calling the transport directly.</summary>
public interface INooseiGateway
{
    bool IsConfigured { get; }

    Task<NooseiAnswer> AskAsync(NooseiCall call, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiGateway" />
public class NooseiGateway(
    ILlmService llm,
    ILlmQuotaService quota,
    INooseiProviderService providerService,
    IOptions<LlmOptions> options,
    ILogger<NooseiGateway> logger) : INooseiGateway
{
    /// <summary>Shown to the agent under a rescued partial answer. The stored <c>Truncated</c> chip says an answer was
    /// cut off; only this says the clock did it, which is the one case where asking again is cheaper than it looks.</summary>
    private const string RanOutOfTime =
        "\n\n_Die Zeit für diese Antwort ist abgelaufen, während NOOSEI noch gelesen hat — der Text oben ist "
        + "unvollständig. Frag noch einmal nach; das bereits Gelesene liegt dieser Unterhaltung schon vor._";

    private const string TurnTimedOut = "Zeitbudget des Turns abgelaufen; Teilantwort ausgeliefert.";

    private readonly LlmOptions _o = options.Value;

    public bool IsConfigured => llm.IsConfigured;

    /// <summary>One turn at a time per agent, because the check and the charge are minutes apart.</summary>
    /// <remarks>
    /// EnsureAvailableAsync reads the remaining allowance, the model then works for several seconds, and only
    /// afterwards is anything booked. Two tabs asking at once both saw the same remainder and both went through,
    /// so a week could be overspent by as many questions as there were tabs - and the bill is real money.
    /// Striped so the table is bounded; two agents who collide only cost each other a turn.
    /// </remarks>
    private static readonly SemaphoreSlim[] Turns =
        [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1))];

    private static SemaphoreSlim TurnFor(string agentId)
        => Turns[(int)((uint)StringComparer.Ordinal.GetHashCode(agentId) % Turns.Length)];

    public async Task<NooseiAnswer> AskAsync(NooseiCall call, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var id = actor.GetAgentId() ?? throw new UnauthorizedAccessException("NOOSEI steht in dieser Rolle nicht zur Verfügung.");
        var turn = TurnFor(id);
        await turn.WaitAsync(cancellationToken);
        try
        {
            return await AskTurnAsync(call, actor, cancellationToken);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<NooseiAnswer> AskTurnAsync(NooseiCall call, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        // resolved once, then carried through every round AND into the allowance check: a turn that straddles a
        // switch of the upstream would otherwise send half its transcript to one endpoint under one model id and
        // half to the other, and be judged against a boost it never spent under
        var providerState = await providerService.GetStateAsync(cancellationToken);
        var provider = providerState.Active;
        var status = await quota.EnsureAvailableAsync(actor, cancellationToken, providerState.BoostPercent);
        var agentId = actor.GetAgentId() ?? throw new UnauthorizedAccessException("NOOSEI steht in dieser Rolle nicht zur Verfügung.");

        // the whole turn gets one budget; HttpClient.Timeout only bounds a single round
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _o.TurnTimeoutSeconds)));

        var watch = Stopwatch.StartNew();
        var messages = new List<LlmMessage>(call.Messages);
        var refs = new List<LlmContextRef>(call.ContextRefs ?? []);
        // one turn never pays twice for the same lookup
        var madeCalls = new HashSet<string>(StringComparer.Ordinal);
        // rows that carry no record content; they stay out of the conversation's storage
        var barren = new List<string>();
        var total = LlmUsage.Empty;
        var rounds = 0;
        var degraded = false;
        var looping = false;
        var announced = false;
        var toolCalls = 0;
        var attempts = 0;
        var modelMs = 0L;
        LlmToolWithdrawal? withdrawal = null;
        LlmResult? last = null;
        // the answer so far, continued pieces included; what a timeout rescues
        string? answered = null;

        try
        {
            while (true)
            {
                rounds++;
                var offerTools = !looping && OffersTools(call, rounds, total);
                if (!offerTools && Withdrawn(call, rounds, total, looping) is { } reason)
                {
                    withdrawal ??= reason;
                    if (!announced && messages.Any(m => m.Role == LlmRole.Tool))
                    {
                        messages.Add(LlmMessage.System(Notice(reason)));
                        announced = true;
                    }
                }

                LlmResult result;
                try
                {
                    result = await llm.CompleteAsync(Round(call, provider, messages, offerTools), actor, turnCts.Token);
                }
                catch (LlmCapabilityException ex) when (ex.ToolsRelated && offerTools)
                {
                    // the endpoint cannot do tools at all — answer without file access rather than failing
                    logger.LogWarning(ex, "NOOSEI: Endpunkt unterstützt keine Werkzeuge, Anfrage ohne Aktenzugriff wiederholt.");
                    degraded = true;
                    result = await llm.CompleteAsync(
                        Round(call, provider, Flattened(messages), offerTools: false, sendTools: false), actor, turnCts.Token);
                }

                total += result.Usage;
                attempts += result.Attempts;
                modelMs += result.ElapsedMs;
                last = result;

                if (!result.HasToolCalls || !offerTools || call.ToolExecutor is null)
                {
                    break;
                }

                messages.Add(LlmMessage.Assistant(result.Text, result.ToolCalls));
                var cap = Math.Max(1, _o.MaxToolCallsPerRound);
                // the repeat decision stays sequential and in the model's own order: two identical calls in one
                // round must always resolve the same way, and the refs must keep an order the source list can trust
                var planned = result.ToolCalls.Take(cap)
                    .Select(c => (Call: c, Repeat: !madeCalls.Add(c.Name + "|" + c.ArgumentsJson.Trim())))
                    .ToList();

                // run them together: nine of the tools are one database read, and four of those in sequence can
                // outlast the whole turn while each one alone is quick
                var outcomes = new NooseiToolOutcome[planned.Count];
                await Task.WhenAll(planned.Select(async (entry, index) =>
                {
                    outcomes[index] = entry.Repeat
                        // a repeat cannot return anything new: hand back the pointer, not the cost
                        ? NooseiToolOutcome.Failed(NooseiPrompts.RepeatedToolCall)
                        : await RunToolAsync(call.ToolExecutor, entry.Call, turnCts.Token);
                }));
                // after the batch, never inside it: a tool task left unawaited resurfaces as an unobserved
                // exception with no request left to attribute it to
                turnCts.Token.ThrowIfCancellationRequested();

                for (var index = 0; index < planned.Count; index++)
                {
                    var (toolCall, repeat) = planned[index];
                    var outcome = outcomes[index];
                    messages.Add(LlmMessage.Tool(toolCall.Id, toolCall.Name, outcome.Text));
                    toolCalls++;
                    if (outcome.IsError)
                    {
                        barren.Add(toolCall.Id);
                    }
                    if (repeat)
                    {
                        // it never ran: a tool entry would put a phantom call in the request log
                        continue;
                    }
                    // the tool entry carries the per-tool statistics, the record refs carry the sources
                    refs.Add(new LlmContextRef("tool", null, toolCall.Name));
                    if (outcome.Refs is { Count: > 0 } touched)
                    {
                        refs.AddRange(touched);
                    }
                }
                foreach (var dropped in result.ToolCalls.Skip(cap))
                {
                    messages.Add(LlmMessage.Tool(dropped.Id, dropped.Name,
                        "Zu viele Werkzeugaufrufe in einer Runde. Bitte auf das Wesentliche beschränken."));
                    barren.Add(dropped.Id);
                    toolCalls++;
                }
                // a round of nothing but repeats means the model is stuck; force the answer
                looping = planned.All(p => p.Repeat);
            }

            answered = last?.Text;
            // the ceiling exists to detect "length", not to end the answer: a free-text answer is continued on a
            // copy of the transcript, so the resume prompt never reaches the stored conversation
            var continuations = 0;
            while (MayContinue(call, last, answered) && continuations < Math.Clamp(_o.MaxAnswerContinuations, 0, 10))
            {
                continuations++;
                var resume = new List<LlmMessage>(messages)
                {
                    LlmMessage.Assistant(answered!),
                    LlmMessage.System(NooseiPrompts.ContinueAnswer),
                };
                var more = await llm.CompleteAsync(Round(call, provider, resume, offerTools: false), actor, turnCts.Token);
                total += more.Usage;
                attempts += more.Attempts;
                modelMs += more.ElapsedMs;
                last = more;
                answered += more.Text;
            }

            watch.Stop();
            if (withdrawal is null && CanUseTools(call))
            {
                withdrawal = LlmToolWithdrawal.Answered;
            }
            var trace = Trace(last, attempts, modelMs, toolCalls, barren.Count, degraded, withdrawal, null);
            var charge = await ChargeAsync(agentId, call, provider, total, last, refs, rounds, watch, success: true, error: null, trace);
            return new NooseiAnswer(answered, total, charge, rounds, messages, degraded,
                IsCutOff(last),
                refs,
                barren,
                last?.Model ?? _o.ModelFor(provider, call.Feature));
        }
        // the turn clock ran out with text already in hand. Handing that over beats a two-minute spinner that ends
        // in nothing while the quota was charged anyway — the agent's own cancel is not this case and falls through.
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && (answered ?? last?.Text) is { Length: > 0 } partial)
        {
            watch.Stop();
            var trace = Trace(last, attempts, modelMs, toolCalls, barren.Count, degraded,
                LlmToolWithdrawal.TimeSpent, LlmFailureKind.Timeout);
            // booked as a success because the agent did get an answer; the failure kind is what says it was cut short
            var charge = await ChargeAsync(
                agentId, call, provider, total, last, refs, rounds, watch, success: true, error: TurnTimedOut, trace);
            logger.LogWarning(
                "NOOSEI-Turn nach {Elapsed} ms abgelaufen, Teilantwort ausgeliefert (Funktion {Feature}, {Rounds} Runden)",
                watch.ElapsedMilliseconds, call.Feature, rounds);
            return new NooseiAnswer(partial + RanOutOfTime, total, charge, rounds, messages, degraded,
                true, refs, barren, last?.Model ?? _o.ModelFor(provider, call.Feature));
        }
        catch (Exception ex)
        {
            watch.Stop();
            var trace = Trace(last, attempts, modelMs, toolCalls, barren.Count, degraded, withdrawal,
                LlmRequestTrace.Classify(ex, cancellationToken.IsCancellationRequested));
            // a failed call still gets a log row: it may have cost real money, and it must show up in the overview
            var charge = await ChargeAsync(agentId, call, provider, total, last, refs, rounds, watch, success: false, error: ex.Message, trace);
            logger.LogWarning(ex, "NOOSEI-Anfrage fehlgeschlagen (Funktion {Feature}, {Rounds} Runden, Kontingent {Tokens})",
                call.Feature, rounds, charge.QuotaTokens);
            throw;
        }
        finally
        {
            _ = status;
        }
    }

    private static bool IsCutOff(LlmResult? result)
        => string.Equals(result?.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a cut-off answer may be continued. Only free text under the feature's own ceiling: glued
    /// pieces of a structured answer are no longer valid JSON, and a caller's own ceiling is a decision.</summary>
    private static bool MayContinue(NooseiCall call, LlmResult? last, string? answered)
        => IsCutOff(last) && !string.IsNullOrEmpty(answered) && call.ResponseFormat is null && call.MaxTokens is null;

    /// <summary>Whether this call could use tools at all. Separate from the per-round decision, because a feature
    /// that never had tools must report no withdrawal rather than one it never hit.</summary>
    private bool CanUseTools(NooseiCall call)
        => _o.ToolsEnabled && call.Tools is { Count: > 0 } && call.ToolExecutor is not null;

    private bool OffersTools(NooseiCall call, int round, LlmUsage spent)
        => CanUseTools(call)
            && round <= Math.Clamp(_o.MaxToolRounds, 1, 10)
            && spent.CostUsd < _o.MaxCostPerTurnUsd
            && spent.TotalTokens < _o.MaxTokensPerTurn;

    /// <summary>Which ceiling took the tools away this round; null when this call never had any.</summary>
    private LlmToolWithdrawal? Withdrawn(NooseiCall call, int round, LlmUsage spent, bool looping)
    {
        if (!CanUseTools(call))
        {
            return null;
        }
        if (looping)
        {
            return LlmToolWithdrawal.Looping;
        }
        return round > Math.Clamp(_o.MaxToolRounds, 1, 10)
            ? LlmToolWithdrawal.RoundsSpent
            : LlmToolWithdrawal.BudgetSpent;
    }

    /// <summary>The withdrawal in the model's own language.</summary>
    private static string Notice(LlmToolWithdrawal withdrawal) => withdrawal switch
    {
        LlmToolWithdrawal.Looping => NooseiPrompts.ToolsGoneLoop,
        LlmToolWithdrawal.RoundsSpent => NooseiPrompts.ToolsGoneRounds,
        _ => NooseiPrompts.ToolsGoneBudget,
    };

    /// <summary>How the turn ran, for the log row. Zero is a measurement here, not a gap — a missing column on an
    /// older row is what reads as "not recorded".</summary>
    private static LlmRequestTrace Trace(
        LlmResult? last, int attempts, long modelMs, int toolCalls, int toolFailures, bool degraded,
        LlmToolWithdrawal? withdrawal, LlmFailureKind? failure)
        => new(
            last?.FinishReason,
            attempts,
            (int)Math.Min(modelMs, int.MaxValue),
            toolCalls,
            toolFailures,
            degraded,
            withdrawal,
            failure);

    /// <summary>One round. Withdrawing the tools means <c>tool_choice: none</c>, never dropping the tool block:
    /// a transcript that still carries tool roles without it is an invalid request shape, and omitting it breaks
    /// the cached prompt prefix on exactly the round with the largest transcript.</summary>
    private LlmRequest Round(
        NooseiCall call, LlmProvider provider, IReadOnlyList<LlmMessage> messages, bool offerTools, bool sendTools = true) => new(
        messages,
        new LlmCallContext(call.Feature, call.ConversationId, call.EntityType, call.EntityId, Provider: provider),
        sendTools ? call.Tools : null,
        call.ResponseFormat,
        call.Temperature,
        // a caller that names its own ceiling keeps it; otherwise the feature's, so "length" can be detected at all
        call.MaxTokens ?? _o.MaxAnswerTokensFor(call.Feature),
        offerTools ? LlmToolChoice.Auto : LlmToolChoice.None,
        call.RequireCapableProviders);

    /// <summary>History without tool roles, for an endpoint that cannot take a tool block at all. Their text
    /// survives as plain context — the same shape the chat replay uses when a scope change invalidates them.</summary>
    private static List<LlmMessage> Flattened(IReadOnlyList<LlmMessage> messages) => messages
        .Select(m => m.Role switch
        {
            LlmRole.Tool => LlmMessage.Assistant(NooseiHistoryWindow.Flatten(m.Name, m.Content)),
            LlmRole.Assistant when m.ToolCalls is { Count: > 0 } => LlmMessage.Assistant(m.Content),
            _ => m,
        })
        .Where(m => m.Role != LlmRole.Assistant || !string.IsNullOrWhiteSpace(m.Content))
        .ToList();

    /// <summary>One tool call under its own timeout. Never throws — not even on the turn's own cancellation.</summary>
    /// <remarks>Swallowing the cancellation is what lets the calls of a round run together: an abandoned task among
    /// them would leave its siblings unawaited. The turn budget is checked once after the whole batch instead.</remarks>
    private async Task<NooseiToolOutcome> RunToolAsync(NooseiToolExecutor executor, LlmToolCall call, CancellationToken turnToken)
    {
        using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(turnToken);
        toolCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _o.ToolTimeoutSeconds)));
        try
        {
            return await executor(call, toolCts.Token);
        }
        // a dead tool must not kill the turn: hand the model a German error it can recover from
        catch (OperationCanceledException)
        {
            return NooseiToolOutcome.Failed("Werkzeug hat nicht rechtzeitig geantwortet.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NOOSEI-Werkzeug {Tool} fehlgeschlagen", call.Name);
            return NooseiToolOutcome.Failed("Werkzeug konnte nicht ausgeführt werden.");
        }
    }

    private async Task<LlmQuotaCharge> ChargeAsync(
        string agentId, NooseiCall call, LlmProvider provider, LlmUsage total, LlmResult? last,
        IReadOnlyList<LlmContextRef> refs, int rounds, Stopwatch watch, bool success, string? error,
        LlmRequestTrace? trace = null)
    {
        var input = new LlmChargeInput(
            agentId,
            call.Feature,
            total,
            // the model of the upstream this turn actually ran on; the price table is keyed by exactly that
            last?.Model ?? _o.ModelFor(provider, call.Feature),
            last?.Provider ?? LlmProviderDisplay.Name(provider),
            (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue),
            Math.Max(0, rounds - 1),
            success,
            error,
            call.LoggedPrompt,
            success ? last?.Text : null,
            refs,
            trace);

        // charged with None on purpose: a circuit torn down mid-answer must not skip the meter
        return await quota.TryChargeAsync(input, CancellationToken.None);
    }
}
