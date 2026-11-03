using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services;

/// <summary>Tests the LLM boundary: payload shape, usage accounting, retries on transient upstream failures,
/// no retry on permanent ones, and the capability refusal that must escape the retry loop immediately.</summary>
public class LlmServiceTests
{
    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static LlmOptions Options(Action<LlmOptions>? configure = null)
    {
        var o = new LlmOptions
        {
            Enabled = true,
            BaseUrl = "https://openrouter.test/api/v1",
            ApiKey = "test-key",
            Model = "vendor/model",
            Retries = 2,
            RetryDelayMs = 0,
            AttemptTimeoutSeconds = 5,
        };
        configure?.Invoke(o);
        return o;
    }

    private static (LlmService Svc, StubHandler Handler) Build(LlmOptions options, params HttpResponseMessage[] responses)
    {
        var handler = new StubHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("llm").Returns(client);
        return (new LlmService(factory, new OptionsWrapper<LlmOptions>(options), NullLogger<LlmService>.Instance), handler);
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static LlmRequest Req(
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmResponseFormat? format = null,
        LlmToolChoice choice = LlmToolChoice.Auto,
        bool requireCapableProviders = false)
        => new(
            [LlmMessage.System("sys"), LlmMessage.User("user")],
            new LlmCallContext(LlmFeature.Chat),
            tools,
            format,
            ToolChoice: choice,
            RequireCapableProviders: requireCapableProviders);

    // placeholders instead of interpolation: the raw JSON already ends in three closing braces
    private static HttpResponseMessage Ok(string content = "Kurzbrief", string provider = "Baidu")
        => Json(HttpStatusCode.OK, """
            {"id":"gen-1","provider":"#PROVIDER#","model":"vendor/model",
             "choices":[{"message":{"content":"#CONTENT#"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,"cost":0.0012,
                      "prompt_tokens_details":{"cached_tokens":40},
                      "completion_tokens_details":{"reasoning_tokens":5}}}
            """.Replace("#PROVIDER#", provider).Replace("#CONTENT#", content));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- payload

    [Fact]
    public async Task CompleteAsync_AlwaysAsksForUsageAccounting()
    {
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(), Agent());

        Assert.True(handler.LastBody!.RootElement.GetProperty("usage").GetProperty("include").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_SendsTheMessagesInOrder()
    {
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(), Agent());

        var messages = handler.LastBody!.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString() ?? "").ToArray());
        Assert.Equal("sys", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsToolsAndToolChoice()
    {
        var tools = new[] { new LlmToolDefinition("suche_akten", "Sucht Akten.", Schema("""{"type":"object"}""")) };
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(tools), Agent());

        var tool = handler.LastBody!.RootElement.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("suche_akten", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("auto", handler.LastBody.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task CompleteAsync_WithdrawsTools_WhenChoiceIsNone()
    {
        var tools = new[] { new LlmToolDefinition("suche_akten", "Sucht Akten.", Schema("""{"type":"object"}""")) };
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(tools, choice: LlmToolChoice.None), Agent());

        Assert.Equal("none", handler.LastBody!.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsAStrictJsonSchema()
    {
        var format = LlmResponseFormat.ForSchema("noose_kurzbrief", Schema("""{"type":"object","required":["tldr"]}"""));
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(format: format), Agent());

        var body = handler.LastBody!.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", body.GetProperty("type").GetString());
        Assert.Equal("noose_kurzbrief", body.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(body.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("object", body.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsPlainJsonMode_OnTheLastRung()
    {
        var (svc, handler) = Build(Options(), Ok());

        await svc.CompleteAsync(Req(format: LlmResponseFormat.JsonObject), Agent());

        var body = handler.LastBody!.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("json_schema", out _));
    }

    [Fact]
    public async Task CompleteAsync_RequiresCapableProviders_OnlyWhenAsked()
    {
        var (with, withHandler) = Build(Options(), Ok());
        await with.CompleteAsync(Req(requireCapableProviders: true), Agent());
        Assert.True(withHandler.LastBody!.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());

        var (without, withoutHandler) = Build(Options(), Ok());
        await without.CompleteAsync(Req(), Agent());
        Assert.False(withoutHandler.LastBody!.RootElement.GetProperty("provider").TryGetProperty("require_parameters", out _));
    }

    // ---------------------------------------------------------------- provider routing

    [Fact]
    public async Task CompleteAsync_SortsProvidersByLatency_WhenNoExplicitOrder()
    {
        var (svc, handler) = Build(Options(o => o.ProviderSort = "latency"), Ok());

        await svc.CompleteAsync(Req(), Agent());

        var provider = handler.LastBody!.RootElement.GetProperty("provider");
        Assert.Equal("latency", provider.GetProperty("sort").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsProviderOrderAndFallbackFlag_WhenConfigured()
    {
        var (svc, handler) = Build(Options(o =>
        {
            o.Providers = ["Baidu", "Cloudflare"];
            o.AllowProviderFallbacks = true;
        }), Ok());

        await svc.CompleteAsync(Req(), Agent());

        var provider = handler.LastBody!.RootElement.GetProperty("provider");
        Assert.Equal(["Baidu", "Cloudflare"],
            provider.GetProperty("order").EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
        Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_ExcludesIncompatibleProviders_WhenIgnoreListConfigured()
    {
        // Morph rejects the system+user shape with HTTP 400 "Multi-turn conversations are not supported"
        var (svc, handler) = Build(Options(o => o.IgnoreProviders = ["Morph"]), Ok());

        await svc.CompleteAsync(Req(), Agent());

        var ignore = handler.LastBody!.RootElement.GetProperty("provider").GetProperty("ignore");
        Assert.Equal(["Morph"], ignore.EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
    }

    // ---------------------------------------------------------------- response

    [Fact]
    public async Task CompleteAsync_ParsesUsageAndCost()
    {
        var (svc, _) = Build(Options(), Ok());

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal(100, result.Usage.PromptTokens);
        Assert.Equal(20, result.Usage.CompletionTokens);
        Assert.Equal(120, result.Usage.TotalTokens);
        Assert.Equal(40, result.Usage.CachedPromptTokens);
        Assert.Equal(5, result.Usage.ReasoningTokens);
        Assert.Equal(0.0012m, result.Usage.CostUsd);
        Assert.Equal("Baidu", result.Provider);
        Assert.Equal("gen-1", result.GenerationId);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_ParsesToolCalls()
    {
        var (svc, _) = Build(Options(), Json(HttpStatusCode.OK, """
            {"id":"gen-2","choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"suche_akten","arguments":"{\"suchtext\":\"Ballas\"}"}}]}}]}
            """));

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.True(result.HasToolCalls);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("suche_akten", call.Name);
        Assert.Contains("Ballas", call.ArgumentsJson);
    }

    [Fact]
    public async Task CompleteAsync_ReturnsEmptyContentRaw_WithoutASubstitutedSentence()
    {
        // a placeholder sentence here would end up pasted into an agent's document
        var (svc, _) = Build(Options(), Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":""}}]}"""));

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.HasToolCalls);
    }

    [Fact]
    public async Task CompleteAsync_DefaultsUsage_WhenTheProviderReportsNone()
    {
        var (svc, _) = Build(Options(), Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}"""));

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal(LlmUsage.Empty, result.Usage);
    }

    // ---------------------------------------------------------------- transient failures

    [Fact]
    public async Task CompleteAsync_RetriesOn429_AndSucceeds()
    {
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Provider returned error"}}"""),
            Ok("Zweiter Versuch"));

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal("Zweiter Versuch", result.Text);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task CompleteAsync_RetriesOn404_BecauseOpenRouterUsesItForNoAvailableProvider()
    {
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.NotFound, """{"error":{"message":"No endpoints found that support your request"}}"""),
            Ok());

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal("Kurzbrief", result.Text);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_RetriesOnServerError_ThenGivesUp_WithoutLeakingTheUpstreamDetail()
    {
        var (svc, handler) = Build(Options(o => o.Retries = 1),
            Json(HttpStatusCode.BadGateway, """{"error":{"message":"upstream kaputt"}}"""),
            Json(HttpStatusCode.BadGateway, """{"error":{"message":"upstream kaputt"}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(Req(), Agent()));

        Assert.Contains("502", ex.Message);
        Assert.Contains("FIBI", ex.Message);
        Assert.DoesNotContain("upstream kaputt", ex.Message);
        Assert.DoesNotContain("vendor/model", ex.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_AppendsTheUpstreamDetail_OnlyWhenAdminEnabledIt()
    {
        var (svc, _) = Build(Options(o => { o.Retries = 0; o.ExposeUpstreamDetail = true; }),
            Json(HttpStatusCode.BadGateway, """{"error":{"message":"upstream kaputt"}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(Req(), Agent()));

        Assert.Contains("upstream kaputt", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_RetriesAfterAttemptTimeout_AndSucceeds()
    {
        var (svc, handler) = Build(Options(o => o.AttemptTimeoutSeconds = 1),
            StubHandler.Hang, Ok("nach Timeout"));

        var result = await svc.CompleteAsync(Req(), Agent());

        Assert.Equal("nach Timeout", result.Text);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsFriendlyMessage_WhenEveryAttemptTimesOut()
    {
        var (svc, _) = Build(Options(o => { o.AttemptTimeoutSeconds = 1; o.Retries = 1; }),
            StubHandler.Hang, StubHandler.Hang);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(Req(), Agent()));

        Assert.Contains("nicht rechtzeitig", ex.Message);
        // no raw HttpClient wording and no model name leaking into the UI
        Assert.DoesNotContain("HttpClient", ex.Message);
        Assert.DoesNotContain("vendor/model", ex.Message);
    }

    // ---------------------------------------------------------------- permanent failures

    [Fact]
    public async Task CompleteAsync_DoesNotRetryOnBadRequest()
    {
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.BadRequest, """{"error":{"message":"Multi-turn conversations are not supported"}}"""),
            Ok());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(Req(), Agent()));

        Assert.Contains("400", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetry_WhenCallerCancels()
    {
        var (svc, handler) = Build(Options(), StubHandler.Hang);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.CompleteAsync(Req(), Agent(), cts.Token));

        Assert.Equal(1, handler.Calls);
    }

    // ---------------------------------------------------------------- capability refusals

    [Fact]
    public async Task CompleteAsync_ThrowsCapability_WhenTheSchemaIsRefused_AndDoesNotRetry()
    {
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            Ok());

        var ex = await Assert.ThrowsAsync<LlmCapabilityException>(
            () => svc.CompleteAsync(Req(format: LlmResponseFormat.ForSchema("x", Schema("""{"type":"object"}"""))), Agent()));

        Assert.True(ex.SchemaRelated);
        Assert.False(ex.ToolsRelated);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsCapability_WhenRequireParametersFilteredEveryProviderOut()
    {
        // the very same 404 body that is transient for a plain request is permanent once we demand a schema
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.NotFound, """{"error":{"message":"No endpoints found that support your request"}}"""),
            Ok());

        var ex = await Assert.ThrowsAsync<LlmCapabilityException>(
            () => svc.CompleteAsync(
                Req(format: LlmResponseFormat.ForSchema("x", Schema("""{"type":"object"}""")), requireCapableProviders: true),
                Agent()));

        Assert.True(ex.SchemaRelated);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsCapability_WhenToolsAreRefused()
    {
        var tools = new[] { new LlmToolDefinition("suche_akten", "Sucht Akten.", Schema("""{"type":"object"}""")) };
        var (svc, handler) = Build(Options(),
            Json(HttpStatusCode.BadRequest, """{"error":{"message":"This model does not support tool use"}}"""),
            Ok());

        var ex = await Assert.ThrowsAsync<LlmCapabilityException>(() => svc.CompleteAsync(Req(tools), Agent()));

        Assert.True(ex.ToolsRelated);
        Assert.False(ex.SchemaRelated);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_TreatsAnUnrelatedBadRequestAsPermanent_NotAsACapabilityRefusal()
    {
        var tools = new[] { new LlmToolDefinition("suche_akten", "Sucht Akten.", Schema("""{"type":"object"}""")) };
        var (svc, _) = Build(Options(),
            Json(HttpStatusCode.BadRequest, """{"error":{"message":"context length exceeded"}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(Req(tools), Agent()));

        Assert.IsNotType<LlmCapabilityException>(ex);
    }

    // ---- model per feature ----

    [Fact]
    public async Task Complete_UsesTheModelConfiguredForTheFeature()
    {
        var options = Options(o => o.ModelByFeature[LlmFeature.Proofread] = "vendor/klein");
        var (svc, handler) = Build(options, Ok());

        await svc.CompleteAsync(
            new LlmRequest([LlmMessage.User("text")], new LlmCallContext(LlmFeature.Proofread)), Agent());

        Assert.Equal("vendor/klein", handler.LastBody!.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Complete_FallsBackToTheDefaultModel_ForAFeatureWithoutAnOverride()
    {
        var options = Options(o => o.ModelByFeature[LlmFeature.Proofread] = "vendor/klein");
        var (svc, handler) = Build(options, Ok());

        await svc.CompleteAsync(Req(), Agent());

        Assert.Equal("vendor/model", handler.LastBody!.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Complete_IgnoresABlankOverride()
    {
        // an env var set to nothing must not send an empty model name upstream
        var options = Options(o => o.ModelByFeature[LlmFeature.Chat] = "   ");
        var (svc, handler) = Build(options, Ok());

        await svc.CompleteAsync(Req(), Agent());

        Assert.Equal("vendor/model", handler.LastBody!.RootElement.GetProperty("model").GetString());
    }

    // ---------------------------------------------------------------- upstream

    private static LlmRequest DeepSeekReq()
        => new(
            [LlmMessage.System("sys"), LlmMessage.User("user")],
            new LlmCallContext(LlmFeature.Chat, Provider: LlmProvider.DeepSeek));

    private static LlmOptions BothUpstreams(Action<LlmOptions>? configure = null) => Options(o =>
    {
        o.DeepSeek.BaseUrl = "https://deepseek.test/v1";
        o.DeepSeek.ApiKey = "deepseek-key";
        o.DeepSeek.Model = "deepseek-flash";
        configure?.Invoke(o);
    });

    [Fact]
    public async Task Complete_AddressesTheUpstreamNamedOnTheContext()
    {
        var (svc, handler) = Build(BothUpstreams(), Ok());

        await svc.CompleteAsync(DeepSeekReq(), Agent());

        Assert.Equal("https://deepseek.test/v1/chat/completions", handler.LastUri!.ToString());
        Assert.Equal("deepseek-flash", handler.LastBody!.RootElement.GetProperty("model").GetString());
    }

    /// <summary>The key travels with the request, never on the pooled client — otherwise one upstream's
    /// credentials reach the other the moment the switch is flipped.</summary>
    [Fact]
    public async Task Complete_SendsEachUpstreamsOwnKey()
    {
        var (deepSeek, deepSeekHandler) = Build(BothUpstreams(), Ok());
        await deepSeek.CompleteAsync(DeepSeekReq(), Agent());
        Assert.Equal("deepseek-key", deepSeekHandler.LastAuthorization);

        var (router, routerHandler) = Build(BothUpstreams(), Ok());
        await router.CompleteAsync(Req(), Agent());
        Assert.Equal("test-key", routerHandler.LastAuthorization);
    }

    /// <summary>Both fields are OpenRouter extensions; a direct endpoint rejects the request outright.</summary>
    [Fact]
    public async Task Complete_OmitsTheRouterOnlyFields_ForADirectUpstream()
    {
        var options = BothUpstreams(o => o.Providers.Add("deepinfra"));
        var (svc, handler) = Build(options, Ok());

        await svc.CompleteAsync(DeepSeekReq() with { RequireCapableProviders = true }, Agent());

        var payload = handler.LastBody!.RootElement;
        Assert.False(payload.TryGetProperty("usage", out _));
        Assert.False(payload.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Complete_KeepsTheRouterOnlyFields_ForOpenRouter()
    {
        var options = BothUpstreams(o => o.Providers.Add("deepinfra"));
        var (svc, handler) = Build(options, Ok());

        await svc.CompleteAsync(Req(requireCapableProviders: true), Agent());

        var payload = handler.LastBody!.RootElement;
        Assert.True(payload.GetProperty("usage").GetProperty("include").GetBoolean());
        Assert.True(payload.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
    }

    /// <summary>A direct endpoint names no upstream of its own, so the log row would otherwise say nothing.</summary>
    [Fact]
    public async Task Complete_NamesTheUpstream_WhenTheAnswerDoesNot()
    {
        var (svc, _) = Build(BothUpstreams(), Json(HttpStatusCode.OK, """
            {"id":"gen-1","model":"deepseek-flash",
             "choices":[{"message":{"content":"Antwort"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}
            """));

        var result = await svc.CompleteAsync(DeepSeekReq(), Agent());

        Assert.Equal("DeepSeek", result.Provider);
        Assert.Equal("deepseek-flash", result.Model);
    }

    [Fact]
    public async Task Complete_Throws_WhenTheSelectedUpstreamIsNotConfigured()
    {
        var (svc, handler) = Build(Options(), Ok());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(DeepSeekReq(), Agent()));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>Replays queued responses in order; <see cref="Hang"/> blocks until the attempt's token fires.</summary>
    private sealed class StubHandler(params HttpResponseMessage?[] responses) : HttpMessageHandler
    {
        /// <summary>Marker for "server never answers".</summary>
        internal static readonly HttpResponseMessage? Hang = null;

        private int _index;

        public int Calls { get; private set; }

        public JsonDocument? LastBody { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            // read off the message now: it is disposed the moment the attempt's using-scope ends
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.Parameter;

            var response = _index < responses.Length ? responses[_index] : Ok();
            _index++;
            if (response is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return response!;
        }
    }
}
