using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The law chat of a partner agency: law tools only, no records, separate conversations.</summary>
public sealed class NooseiLegalChatTests : IDisposable
{
    private const string PartnerId = "partner";
    private readonly SqliteTestContext _ctx = new();
    private NooseiCall? _lastCall;

    public void Dispose() => _ctx.Dispose();

    private static ClaimsPrincipal Partner() => ClaimsPrincipalBuilder.Agent(PartnerId).AsPartner(PartnerAgency.Parlament, PartnerRank.Member).Build();
    private static ClaimsPrincipal Agent() => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).Build();

    private PartnerVisibilityPolicyService Policy() => new(_ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private NooseiToolRegistry Registry() => new(
    [
        new ResolveMentionTool(Substitute.For<IMentionService>()),
        new LawReadTool(new LawService(_ctx.Factory, Substitute.For<NOOSE_Website.Services.Public.IPublicLawService>()), Policy()),
        new LawBooksTool(new LawService(_ctx.Factory, Substitute.For<NOOSE_Website.Services.Public.IPublicLawService>()), Policy()),
        new LawSearchTool(Substitute.For<ISearchService>(), Policy()),
    ]);

    private NooseiChatService Chat()
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _lastCall = call.Arg<NooseiCall>();
                return new NooseiAnswer("Antwort", LlmUsage.Empty, new LlmQuotaCharge(10, 0m, LlmQuotaStatus.Empty, null, true),
                    1, [.. _lastCall.Messages], false, false, null, null);
            });
        var settings = Substitute.For<INooseiSettingsService>();
        settings.GetAddendumAsync(Arg.Any<CancellationToken>()).Returns("INTERNER ZUSATZ");
        return new NooseiChatService(_ctx.Factory, gateway, settings, Registry(), Options.Create(new LlmOptions()),
            NullLogger<NooseiChatService>.Instance);
    }

    private void GrantNoosei()
    {
        using var db = _ctx.NewContext();
        db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.Parlament, Features = PartnerFeature.Noosei });
        db.SaveChanges();
    }

    private static NooseiToolContext Context(ClaimsPrincipal user) => NooseiToolContext.From(user);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Permission_KeepsTheTwoChatsApart()
    {
        Permission.RequireLlmUse(Partner(), LlmFeature.LegalChat);
        Permission.RequireLlmUse(Agent(), LlmFeature.Chat);
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireLlmUse(Partner(), LlmFeature.Chat));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireLlmUse(Partner(), LlmFeature.Brief));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireLlmUse(Agent(), LlmFeature.LegalChat));
        Assert.Throws<UnauthorizedAccessException>(() =>
            Permission.RequireLlmUse(ClaimsPrincipalBuilder.Agent("demo").AsDemo().Build(), LlmFeature.Chat));
    }

    [Fact]
    public void Registry_OffersEachModeOnlyItsOwnTools()
    {
        var registry = Registry();

        Assert.Equal(new[] { "lies_gesetz", "liste_gesetzbuecher", "suche_gesetz" },
            registry.DefinitionsFor(NooseiChatMode.Legal).Select(d => d.Name));
        Assert.DoesNotContain(registry.Definitions, d => LegalToolGate.ToolNames.Contains(d.Name));
        Assert.Null(registry.Find("loese_erwaehnung_auf", NooseiChatMode.Legal));
        Assert.Null(registry.Find("lies_gesetz", NooseiChatMode.Agency));
        Assert.NotNull(registry.Find("lies_gesetz", NooseiChatMode.Legal));
    }

    [Fact]
    public async Task Ask_ByAPartnerWithoutTheFunction_IsRefused()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat().AskAsync(null, "Was ist Mord?", Partner()));
    }

    [Fact]
    public async Task Ask_ByAPartner_RunsTheLawChat_WithoutAnchorOrInternalAddendum()
    {
        GrantNoosei();

        var turn = await Chat().AskAsync(null, "Was ist Mord?", Partner(), anchor: new NooseiAnchor("Law", "l1"));

        Assert.Equal(LlmFeature.LegalChat, _lastCall!.Feature);
        Assert.Equal(new[] { "lies_gesetz", "liste_gesetzbuecher", "suche_gesetz" }, _lastCall.Tools!.Select(t => t.Name));
        var system = _lastCall.Messages.Where(m => m.Role == LlmRole.System).Select(m => m.Content).ToList();
        Assert.Equal(NooseiPrompts.LegalChat, Assert.Single(system));
        await using var db = _ctx.NewContext();
        var conversation = await db.NooseiConversations.SingleAsync(c => c.Id == turn.ConversationId);
        Assert.Equal(NooseiChatMode.Legal, conversation.Mode);
        Assert.Null(conversation.AnchorEntityType);
    }

    [Fact]
    public async Task Ask_ExecutorRefusesAToolTheLawChatWasNeverOffered()
    {
        GrantNoosei();
        await Chat().AskAsync(null, "Was ist Mord?", Partner());

        var outcome = await _lastCall!.ToolExecutor!(new LlmToolCall("c1", "loese_erwaehnung_auf", """{"text":"x"}"""), CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Contains("Unbekanntes Werkzeug", outcome.Text);
    }

    [Fact]
    public async Task Conversations_NeverCrossModes()
    {
        await using (var db = _ctx.NewContext())
        {
            db.NooseiConversations.Add(new NooseiConversation { Id = "old", AgentId = PartnerId, Title = "intern", Mode = NooseiChatMode.Agency });
            db.NooseiConversations.Add(new NooseiConversation { Id = "law", AgentId = PartnerId, Title = "Recht", Mode = NooseiChatMode.Legal });
            await db.SaveChangesAsync();
        }

        var rows = await Chat().GetConversationsAsync(Partner());

        Assert.Equal(new[] { "law" }, rows.Select(r => r.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Chat().GetMessagesAsync("old", Partner()));
    }

    [Fact]
    public async Task LawRead_GivesAPartnerOnlyReleasedParagraphs_AndNothingAttached()
    {
        await using (var db = _ctx.NewContext())
        {
            db.Laws.Add(new Law { Id = "open", LawBook = "StGB", Paragraph = "§ 1", Title = "Mord", Text = "<p>Wer einen Menschen tötet</p>", Sentence = "lebenslang" });
            db.Laws.Add(new Law { Id = "closed", LawBook = "StGB", Paragraph = "§ 2", Title = "Totschlag", Text = "geheim" });
            db.PartnerShares.Add(new PartnerShare { EntityType = "Law", EntityId = "open", Agency = PartnerAgency.Parlament });
            db.Comments.Add(new Comment { EntityType = "Law", EntityId = "open", Text = "INTERNE NOTIZ" });
            await db.SaveChangesAsync();
        }
        var tool = new LawReadTool(new LawService(_ctx.Factory, Substitute.For<NOOSE_Website.Services.Public.IPublicLawService>()), Policy());

        var open = await tool.InvokeAsync(Args("""{"id":"open"}"""), Context(Partner()));
        var closed = await tool.InvokeAsync(Args("""{"id":"closed"}"""), Context(Partner()));

        Assert.Contains("Wer einen Menschen tötet", open.Text);
        Assert.Contains("lebenslang", open.Text);
        Assert.DoesNotContain("INTERNE NOTIZ", open.Text);
        Assert.True(closed.IsError);
        Assert.Equal(NooseiToolResult.NotFound().Text, closed.Text);
    }

    [Fact]
    public async Task LawBooks_ListOnlyBooksWithReleasedParagraphs()
    {
        await using (var db = _ctx.NewContext())
        {
            db.Laws.Add(new Law { Id = "a", LawBook = "StGB", Paragraph = "§ 1", Title = "Mord" });
            db.Laws.Add(new Law { Id = "b", LawBook = "StVO", Paragraph = "§ 1", Title = "Vorfahrt" });
            db.PartnerReleaseRules.Add(new PartnerReleaseRule { Agency = PartnerAgency.Parlament, EntityType = "Law", Scope = PartnerRuleScope.All });
            await db.SaveChangesAsync();
        }
        var lsmd = ClaimsPrincipalBuilder.Agent("lsmd").AsPartner(PartnerAgency.LSMD, PartnerRank.Member).Build();
        var tool = new LawBooksTool(new LawService(_ctx.Factory, Substitute.For<NOOSE_Website.Services.Public.IPublicLawService>()), Policy());

        var parlament = await tool.InvokeAsync(Args("{}"), Context(Partner()));
        var other = await tool.InvokeAsync(Args("{}"), Context(lsmd));

        Assert.Contains("StGB", parlament.Text);
        Assert.Contains("StVO", parlament.Text);
        Assert.DoesNotContain("StGB", other.Text);
    }

    [Fact]
    public void Quota_ForAPartnerAccount_IsItsAgencysRule()
    {
        var config = LlmQuotaConfig.Default();
        config.PartnerAgencies[LlmQuotaConfig.AgencyKey(PartnerAgency.Parlament)] = new LlmRankQuota { BaseWeekly = 15_000 };

        Assert.Equal(15_000, config.For(null, PartnerAgency.Parlament).BaseWeekly);
        Assert.Equal(0, config.For(null, PartnerAgency.DoJ).BaseWeekly);
        Assert.Equal(config.For(Rank.Director).BaseWeekly, config.For(Rank.Director, null).BaseWeekly);
    }
}
