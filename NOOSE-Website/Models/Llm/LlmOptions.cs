using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Llm;

/// <summary>LLM endpoint configuration. Secrets (ApiKey) come from user-secrets / env, never the repo.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Master switch; when false the assistant stays fully inert.</summary>
    public bool Enabled { get; set; }

    /// <summary>Upstream used when nothing is stored in the database yet, and the fallback when the stored one
    /// lost its key or model. Deploy configuration; the runtime choice belongs to the AI owner.</summary>
    public LlmProvider DefaultProvider { get; set; } = LlmProvider.OpenRouter;

    /// <summary>OpenRouter's base URL, key and model. Flat on purpose — the production environment sets
    /// <c>Llm__ApiKey</c> and <c>Llm__Model</c>, and moving them under a section would silently unconfigure it.</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>DeepSeek's own endpoint, as an alternative to routing through OpenRouter.</summary>
    public LlmDeepSeekOptions DeepSeek { get; set; } = new();

    /// <summary>Per-feature model override; anything unset falls back to <see cref="Model"/>. Proofreading a few
    /// paragraphs and a six-round analysis are very different jobs and do not need the same model.</summary>
    public Dictionary<LlmFeature, string> ModelByFeature { get; set; } = new();

    /// <summary>The model a feature runs on at one upstream: its override when configured, otherwise that
    /// upstream's default. Provider-aware because a model id is not portable — OpenRouter addresses the same
    /// model as <c>deepseek/deepseek-v4.1-flash</c> that DeepSeek itself calls <c>deepseek-flash</c>.</summary>
    public string ModelFor(LlmProvider provider, LlmFeature feature) => provider == LlmProvider.DeepSeek
        ? Pick(DeepSeek.ModelByFeature, feature, DeepSeek.Model)
        : Pick(ModelByFeature, feature, Model);

    private static string Pick(Dictionary<LlmFeature, string> overrides, LlmFeature feature, string fallback)
        => overrides.TryGetValue(feature, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : fallback;

    /// <summary>Endpoint address of an upstream.</summary>
    public string BaseUrlFor(LlmProvider provider)
        => provider == LlmProvider.DeepSeek ? DeepSeek.BaseUrl : BaseUrl;

    /// <summary>Key of an upstream. Never rendered, never stored — user-secrets and env only.</summary>
    public string ApiKeyFor(LlmProvider provider)
        => provider == LlmProvider.DeepSeek ? DeepSeek.ApiKey : ApiKey;

    /// <summary>True when this upstream could serve a request right now.</summary>
    public bool IsConfiguredFor(LlmProvider provider)
        => Enabled
            && !string.IsNullOrWhiteSpace(ApiKeyFor(provider))
            && !string.IsNullOrWhiteSpace(provider == LlmProvider.DeepSeek ? DeepSeek.Model : Model);

    /// <summary>The upstream a request actually goes to. A stored choice that lost its key or model falls back
    /// rather than failing every agent's next question — the switch can only ever select a usable endpoint.</summary>
    public LlmProvider Resolve(LlmProvider? stored)
    {
        if (stored is { } choice && IsConfiguredFor(choice))
        {
            return choice;
        }
        if (IsConfiguredFor(DefaultProvider))
        {
            return DefaultProvider;
        }
        foreach (var provider in LlmProviderDisplay.All)
        {
            if (IsConfiguredFor(provider))
            {
                return provider;
            }
        }
        return DefaultProvider;
    }

    /// <summary>List price per model, for the token-based cost floor in <see cref="LlmQuotaMath" />. Deploy
    /// configuration like the model and the key — a price is money and never belongs in the database.</summary>
    public Dictionary<string, LlmModelPrice> PriceByModel { get; set; } = new();

    /// <summary>Configured price of a model, matched case-insensitively; null when none is set.</summary>
    /// <remarks>One lookup across both tables: model ids are upstream-specific, so they cannot collide, and the
    /// charge only ever knows the model it actually ran on.</remarks>
    public LlmModelPrice? PriceFor(string? model)
    {
        if(string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        return Lookup(PriceByModel, model) ?? Lookup(DeepSeek.PriceByModel, model);
    }

    private static LlmModelPrice? Lookup(Dictionary<string, LlmModelPrice> prices, string model)
    {
        foreach (var (key, price) in prices)
        {
            if (string.Equals(key, model, StringComparison.OrdinalIgnoreCase))
            {
                return price;
            }
        }
        return null;
    }

    /// <summary>Deployment-wide egress kill switch for classified/VS content. The per-record decision is made by the
    /// viewer's own scope long before this point; flip it off only when pointing at an endpoint you do not trust.</summary>
    public bool AllowClassifiedEgress { get; set; } = true;

    /// <summary>Preferred upstream providers in order (OpenRouter `provider.order`). Empty = let the router choose by <see cref="ProviderSort"/>. A popular model is served by many providers of very different capability, health and latency — pinning the ones that work keeps a routing roll of the dice from surfacing as a timeout.</summary>
    public List<string> Providers { get; set; } = new();

    /// <summary>Providers that must never serve this deployment (OpenRouter `provider.ignore`), e.g. because they reject the system+user request shape.</summary>
    public List<string> IgnoreProviders { get; set; } = new();

    /// <summary>Fall back to other providers when the preferred ones fail; only meaningful with <see cref="Providers"/>.</summary>
    public bool AllowProviderFallbacks { get; set; } = true;

    /// <summary>Routing preference when no explicit order is set: latency, throughput or price.</summary>
    public string ProviderSort { get; set; } = "latency";

    /// <summary>Budget per attempt; a stalled provider is abandoned instead of eating the whole request.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 25;

    /// <summary>Hard ceiling over all attempts (HttpClient timeout).</summary>
    public int TotalTimeoutSeconds { get; set; } = 90;

    /// <summary>Extra attempts after the first on transient upstream failures (429/404/5xx/timeout).</summary>
    public int Retries { get; set; } = 2;

    /// <summary>Delay before the first retry; doubles per attempt. Zero disables waiting entirely.</summary>
    public int RetryDelayMs { get; set; } = 750;

    /// <summary>Ceiling on the backoff, and on an endpoint's own Retry-After hint.</summary>
    public int RetryMaxDelayMs { get; set; } = 6_000;

    /// <summary>Offer the record-database tools to the model at all; off makes NOOSEI answer without file access.</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>Tool rounds one turn may spend before the tools are withdrawn and an answer is forced.</summary>
    /// <remarks>Raised from six when the offering grew past twenty record kinds and gained the content and area
    /// tools: opening a record and then reading its comments is two rounds where it used to be one.</remarks>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>Tool calls executed per round; extras get a German refusal the model can recover from.</summary>
    public int MaxToolCallsPerRound { get; set; } = 4;

    /// <summary>Budget of a single tool invocation.</summary>
    public int ToolTimeoutSeconds { get; set; } = 15;

    /// <summary>Ceiling over the whole turn; the HttpClient timeout only bounds one round.</summary>
    public int TurnTimeoutSeconds { get; set; } = 120;

    /// <summary>Token budget of the replayed conversation history. Measured in tokens, not rows: one turn with four
    /// tool calls fills six rows, so a row count is both too short for a conversation and too expensive per round.</summary>
    public int HistoryTokenBudget { get; set; } = 12_000;

    /// <summary>Ceiling on how many past turns are replayed, whatever the budget allows.</summary>
    public int HistoryTurns { get; set; } = 8;

    /// <summary>Ceiling on the answer of a feature, in tokens; anything unset leaves it to the endpoint's default.
    /// Without one <see cref="LlmResult.FinishReason" /> never says "length" and a cut-off answer is stored as if
    /// it were whole — the model then reads its own torso back as a finished statement.</summary>
    /// <remarks>Raised from 1,200: a reasoning model spends part of the ceiling thinking, and after several tool
    /// rounds on a complex incident the answer itself was left with a sentence or two.</remarks>
    public Dictionary<LlmFeature, int> MaxAnswerTokensByFeature { get; set; } = new()
    {
        [LlmFeature.Chat] = 4_000,
        [LlmFeature.LegalChat] = 4_000,
    };

    /// <summary>How often a free-text answer cut off by its feature's ceiling is continued before it is handed over
    /// as truncated. The turn timeout still bounds the whole.</summary>
    public int MaxAnswerContinuations { get; set; } = 3;

    /// <summary>Answer ceiling of a feature, or null when it has none.</summary>
    public int? MaxAnswerTokensFor(LlmFeature feature)
        => MaxAnswerTokensByFeature.TryGetValue(feature, out var max) && max > 0 ? max : null;

    /// <summary>Beyond this the tools are withdrawn for the rest of the turn.</summary>
    public decimal MaxCostPerTurnUsd { get; set; } = 0.05m;

    /// <summary>Second budget guard, for a provider that reports no cost.</summary>
    public int MaxTokensPerTurn { get; set; } = 120_000;

    /// <summary>Where the structured-output ladder starts.</summary>
    public StructuredOutputMode StructuredOutput { get; set; } = StructuredOutputMode.Strict;

    /// <summary>Send `provider.require_parameters` so only providers that actually support schema/tools are routed to.</summary>
    public bool RequireCapableProviders { get; set; } = true;

    /// <summary>Append the upstream error detail to the message an agent sees. Off by default: it can name the model.</summary>
    public bool ExposeUpstreamDetail { get; set; }

    /// <summary>True when enabled and at least one upstream carries a key and a model. Which one a request takes
    /// is a runtime choice, so the sync surfaces that ask this can only answer "NOOSEI is operable"; the active
    /// upstream is guaranteed to be a configured one by <see cref="Resolve" />.</summary>
    public bool IsConfigured => LlmProviderDisplay.All.Any(IsConfiguredFor);
}

/// <summary>DeepSeek's own OpenAI-compatible endpoint. Same three fields as the flat OpenRouter ones, plus its
/// own price table — DeepSeek sends no <c>usage.cost</c>, so the token floor in <see cref="LlmQuotaMath" /> is
/// the only thing that meters a call at all.</summary>
public sealed class LlmDeepSeekOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "deepseek-flash";

    /// <summary>Per-feature model override; anything unset falls back to <see cref="Model"/>.</summary>
    public Dictionary<LlmFeature, string> ModelByFeature { get; set; } = new();

    /// <summary>Seeded with the peak-hour list price, not the off-peak one: DeepSeek reports no cost, so this
    /// table is the whole meter, and a rate that undercharges hands out tokens for free. Deploy configuration
    /// overrides it whenever the published price moves.</summary>
    public Dictionary<string, LlmModelPrice> PriceByModel { get; set; } = new()
    {
        ["deepseek-flash"] = new LlmModelPrice { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 1.20m },
    };
}

/// <summary>Per-million-token list price of one model.</summary>
public sealed class LlmModelPrice
{
    public decimal InputPerMillionUsd { get; set; }

    public decimal OutputPerMillionUsd { get; set; }
}

/// <summary>Where the structured-output fallback ladder starts for a deployment.</summary>
public enum StructuredOutputMode
{
    /// <summary>Enforced JSON schema first, then widen, then JSON mode.</summary>
    Strict = 0,

    /// <summary>Skip the capable-provider filter; schema first, then JSON mode.</summary>
    Lenient = 1,

    /// <summary>Straight to JSON mode with the schema in the prompt; for endpoints known not to support schemas.</summary>
    PromptOnly = 2,
}
