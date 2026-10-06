using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>Thin OpenAI-compatible chat client. One call = one round = one HTTP request; the tool loop lives a
/// layer up. All calls are gated by <see cref="Permission.RequireLlmUse"/>.</summary>
/// <remarks>Deliberately database-free: metering, quota and the choice of upstream belong to the gateway, so this
/// stays testable with nothing but a stubbed HttpMessageHandler and a charge never lands inside the retry loop.
/// The upstream arrives on <see cref="LlmCallContext.Provider" /> already resolved; address, key and model are
/// then read from the options under it, so feature and model still cannot drift apart.</remarks>
public interface ILlmService
{
    bool IsConfigured { get; }

    Task<LlmResult> CompleteAsync(LlmRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILlmService" />
public class LlmService(IHttpClientFactory httpFactory, IOptions<LlmOptions> options, ILogger<LlmService> logger) : ILlmService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly IReadOnlyList<LlmToolCall> NoToolCalls = [];

    private readonly LlmOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLlmUse(actor, request.Context.Feature);
        var provider = request.Context.Provider;
        if (!_o.IsConfiguredFor(provider))
        {
            throw new InvalidOperationException("NOOSEI ist nicht konfiguriert.");
        }

        var model = _o.ModelFor(provider, request.Context.Feature);
        var payload = Payload(request, provider, model);
        // absolute per request, not a client BaseAddress: the upstream can change between two rounds of the same
        // deployment, and a pooled client that carried one endpoint's key would then hand it to the other
        var endpoint = new Uri(_o.BaseUrlFor(provider).TrimEnd('/') + "/chat/completions");
        var key = _o.ApiKeyFor(provider);
        var client = httpFactory.CreateClient("llm");
        var attempts = Math.Max(0, _o.Retries) + 1;
        var watch = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt == attempts;
            try
            {
                // Own budget per attempt: a provider that never answers is dropped instead of burning the whole request.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _o.AttemptTimeoutSeconds)));

                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await client.SendAsync(message, attemptCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(attemptCts.Token);
                    var detail = ExtractError(body);
                    var status = (int)response.StatusCode;

                    // must be classified before the transient check: with require_parameters a 404 means
                    // "no CAPABLE provider" and is permanent for this shape, not worth three more attempts
                    if (Capability(response.StatusCode, detail, request) is { } capability)
                    {
                        logger.LogWarning("KI-Endpunkt lehnt die Anfrageform ab: HTTP {Status} {Detail} (Schema {Schema}, Werkzeuge {Tools})",
                            status, detail, capability.SchemaRelated, capability.ToolsRelated);
                        throw capability;
                    }

                    if (!last && IsTransient(response.StatusCode))
                    {
                        logger.LogWarning("KI-Aufruf {Attempt}/{Attempts} verworfen: HTTP {Status} {Detail}",
                            attempt, attempts, status, detail);
                        await DelayAsync(attempt, response, cancellationToken);
                        continue;
                    }

                    logger.LogError("KI-Aufruf endgültig fehlgeschlagen: HTTP {Status} {Detail} (Anbieter {Provider}, Modell {Model}, {Elapsed} ms)",
                        status, detail, provider, model, watch.ElapsedMilliseconds);
                    throw new InvalidOperationException(Public(
                        $"NOOSEI antwortete nicht (Fehler {status}). Bitte später erneut versuchen.", detail));
                }

                var doc = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, attemptCts.Token);
                var choice = doc?.Choices?.FirstOrDefault();
                // only OpenRouter names who served the round; a direct endpoint IS the answer to that question
                var served = doc?.Provider ?? LlmProviderDisplay.Name(provider);
                logger.LogInformation("KI-Antwort ok: Modell {Model}, Anbieter {Provider}, Versuch {Attempt}/{Attempts}, {Elapsed} ms, Kosten {Cost}",
                    doc?.Model ?? model, served, attempt, attempts, watch.ElapsedMilliseconds, doc?.Usage?.Cost ?? 0m);

                return new LlmResult(
                    // raw on purpose: an empty answer must stay empty, never a sentence that could end up in a document
                    Text: choice?.Message?.Content,
                    ToolCalls: ToolCalls(choice?.Message?.ToolCalls),
                    Usage: Usage(doc?.Usage),
                    Provider: served,
                    Model: doc?.Model ?? model,
                    FinishReason: choice?.FinishReason,
                    GenerationId: doc?.Id,
                    Attempts: attempt,
                    ElapsedMs: watch.ElapsedMilliseconds);
            }
            // caller cancelled (circuit gone, navigation) — never retry that
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // attempt budget or HttpClient ceiling elapsed
            catch (OperationCanceledException)
            {
                if (!last)
                {
                    logger.LogWarning("KI-Aufruf {Attempt}/{Attempts} nach {Seconds} s ohne Antwort abgebrochen",
                        attempt, attempts, _o.AttemptTimeoutSeconds);
                    await DelayAsync(attempt, null, cancellationToken);
                    continue;
                }
                logger.LogError("KI-Endpunkt hat in {Attempts} Versuchen nicht geantwortet (Anbieter {Provider}, Modell {Model}, {Elapsed} ms)",
                    attempts, provider, model, watch.ElapsedMilliseconds);
                throw new InvalidOperationException(
                    "NOOSEI hat nicht rechtzeitig geantwortet. Bitte später erneut versuchen.");
            }
            catch (HttpRequestException ex)
            {
                if (!last)
                {
                    logger.LogWarning(ex, "KI-Aufruf {Attempt}/{Attempts} auf Transportebene fehlgeschlagen", attempt, attempts);
                    await DelayAsync(attempt, null, cancellationToken);
                    continue;
                }
                logger.LogError(ex, "KI-Endpunkt nicht erreichbar (Anbieter {Provider}, Modell {Model})", provider, model);
                throw new InvalidOperationException(Public("NOOSEI ist derzeit nicht erreichbar.", ex.Message));
            }
        }
    }

    /// <summary>Wait before the next attempt: the endpoint's own hint when it sent one, otherwise exponential
    /// with jitter so simultaneous circuits do not retry in lockstep.</summary>
    /// <remarks>Zero keeps meaning "do not wait at all" — the tests rely on it to stay fast.</remarks>
    private Task DelayAsync(int attempt, HttpResponseMessage? response, CancellationToken cancellationToken)
    {
        if (_o.RetryDelayMs <= 0)
        {
            return Task.CompletedTask;
        }

        var ceiling = TimeSpan.FromMilliseconds(Math.Max(_o.RetryDelayMs, _o.RetryMaxDelayMs));
        if (RetryAfter(response) is { } hinted)
        {
            return Task.Delay(hinted < ceiling ? hinted : ceiling, cancellationToken);
        }

        var backoff = _o.RetryDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(backoff, ceiling.TotalMilliseconds);
        // ±25 %, so three clients that failed together do not come back together
        var jittered = capped * (0.75 + Random.Shared.NextDouble() * 0.5);
        return Task.Delay(TimeSpan.FromMilliseconds(jittered), cancellationToken);
    }

    /// <summary>The endpoint's own retry hint, as delta or absolute date; null when it sent none or it has passed.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is not { } after)
        {
            return null;
        }
        var wait = after.Delta ?? (after.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { TotalMilliseconds: > 0 } ? wait : null;
    }

    /// <summary>Message an agent may see; upstream detail can name the model, so it is opt-in.</summary>
    private string Public(string message, string? detail)
        => _o.ExposeUpstreamDetail && !string.IsNullOrWhiteSpace(detail) ? message + " (" + detail + ")" : message;

    // ---- wire format ----

    private Dictionary<string, object?> Payload(LlmRequest request, LlmProvider provider, string model)
    {
        var payload = new Dictionary<string, object?>
        {
            // the feature decides the model, so proofreading need not run on the analysis model
            ["model"] = model,
            ["temperature"] = request.Temperature,
            ["messages"] = request.Messages.Select(Wire).ToArray(),
        };
        if (provider == LlmProvider.OpenRouter)
        {
            // cost accounting for the quota subsystem; an OpenRouter extension, and a direct endpoint rejects
            // the field outright — DeepSeek is metered from the token floor in LlmQuotaMath instead
            payload["usage"] = new { include = true };
        }
        if (request.MaxTokens is { } max)
        {
            payload["max_tokens"] = max;
        }
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParameterSchema },
            }).ToArray();
            payload["tool_choice"] = request.ToolChoice switch
            {
                LlmToolChoice.None => "none",
                LlmToolChoice.Required => "required",
                _ => "auto",
            };
        }
        if (request.ResponseFormat is { } format)
        {
            payload["response_format"] = Format(format);
        }
        // routing picks who serves a model behind OpenRouter; a direct endpoint has nothing to route
        if (provider == LlmProvider.OpenRouter && Routing(request.RequireCapableProviders) is { } routing)
        {
            payload["provider"] = routing;
        }
        return payload;
    }

    private static object Wire(LlmMessage message)
    {
        var row = new Dictionary<string, object?>
        {
            ["role"] = message.Role switch
            {
                LlmRole.System => "system",
                LlmRole.User => "user",
                LlmRole.Assistant => "assistant",
                _ => "tool",
            },
            ["content"] = message.Content,
        };
        if (message.ToolCalls is { Count: > 0 })
        {
            row["tool_calls"] = message.ToolCalls.Select(c => new
            {
                id = c.Id,
                type = "function",
                function = new { name = c.Name, arguments = c.ArgumentsJson },
            }).ToArray();
        }
        if (message.ToolCallId is not null)
        {
            row["tool_call_id"] = message.ToolCallId;
        }
        if (message.Name is not null)
        {
            row["name"] = message.Name;
        }
        return row;
    }

    private static object Format(LlmResponseFormat format) => format.Kind == LlmResponseFormatKind.JsonObject
        ? new Dictionary<string, object?> { ["type"] = "json_object" }
        : new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = format.Name,
                ["strict"] = format.Strict,
                ["schema"] = format.Schema,
            },
        };

    /// <summary>Provider routing sent with every request; null when nothing is constrained.</summary>
    private object? Routing(bool requireCapableProviders)
    {
        var routing = new Dictionary<string, object?>();
        if (_o.Providers.Count > 0)
        {
            routing["order"] = _o.Providers;
            routing["allow_fallbacks"] = _o.AllowProviderFallbacks;
        }
        else if (!string.IsNullOrWhiteSpace(_o.ProviderSort))
        {
            routing["sort"] = _o.ProviderSort;
        }
        if (_o.IgnoreProviders.Count > 0)
        {
            routing["ignore"] = _o.IgnoreProviders;
        }
        if (requireCapableProviders)
        {
            routing["require_parameters"] = true;
        }
        return routing.Count == 0 ? null : routing;
    }

    private static IReadOnlyList<LlmToolCall> ToolCalls(List<ToolCallDto>? calls)
    {
        if (calls is not { Count: > 0 })
        {
            return NoToolCalls;
        }
        return calls
            .Where(c => !string.IsNullOrWhiteSpace(c.Function?.Name))
            .Select((c, i) => new LlmToolCall(
                c.Id ?? "call_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.Function!.Name!,
                string.IsNullOrWhiteSpace(c.Function.Arguments) ? "{}" : c.Function.Arguments!))
            .ToList();
    }

    private static LlmUsage Usage(UsageDto? usage) => usage is null
        ? LlmUsage.Empty
        : new LlmUsage(
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens > 0 ? usage.TotalTokens : usage.PromptTokens + usage.CompletionTokens,
            usage.PromptTokensDetails?.CachedTokens ?? 0,
            usage.CompletionTokensDetails?.ReasoningTokens ?? 0,
            usage.Cost ?? 0m);

    // ---- failure classification ----

    /// <summary>Upstream failures worth another attempt. 404 belongs here: OpenRouter answers it when no provider can currently serve the request, not only for a wrong model.</summary>
    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.NotFound
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>Recognises "this endpoint cannot do schema/tools" so the caller can downgrade instead of retrying.</summary>
    private static LlmCapabilityException? Capability(HttpStatusCode status, string? detail, LlmRequest request)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity))
        {
            return null;
        }

        var hasSchema = request.ResponseFormat is not null;
        var hasTools = request.Tools is { Count: > 0 };
        if (!hasSchema && !hasTools)
        {
            return null;
        }

        var text = detail ?? string.Empty;
        var schemaRelated = hasSchema && (Mentions(text, "response_format") || Mentions(text, "json_schema")
            || Mentions(text, "structured output") || Mentions(text, "json mode"));
        var toolsRelated = hasTools && (Mentions(text, "tool") || Mentions(text, "function call")
            || Mentions(text, "function_call"));

        // the router filtered every provider out because none supports what we asked for
        if (Mentions(text, "no endpoints found") || Mentions(text, "no allowed providers"))
        {
            schemaRelated = hasSchema;
            toolsRelated = hasTools;
        }

        return schemaRelated || toolsRelated ? new LlmCapabilityException(schemaRelated, toolsRelated) : null;
    }

    private static bool Mentions(string text, string needle) => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pull a human-readable message out of an OpenAI-style error body; falls back to the raw body.</summary>
    private static string? ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                {
                    return Trim(msg.GetString());
                }
                return Trim(err.ToString());
            }
        }
        catch (JsonException) { /* not json — fall through */ }
        return Trim(body);
    }

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim();
        return text.Length > 300 ? text[..300] : text;
    }

    private sealed record ChatResponse(string? Id, List<Choice>? Choices, string? Provider, string? Model, UsageDto? Usage);
    private sealed record Choice(Message? Message, string? FinishReason);
    private sealed record Message(string? Content, List<ToolCallDto>? ToolCalls);
    private sealed record ToolCallDto(string? Id, string? Type, FunctionDto? Function);
    private sealed record FunctionDto(string? Name, string? Arguments);
    private sealed record UsageDto(
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        decimal? Cost,
        PromptDetails? PromptTokensDetails,
        CompletionDetails? CompletionTokensDetails);
    private sealed record PromptDetails(int CachedTokens);
    private sealed record CompletionDetails(int ReasoningTokens);
}
