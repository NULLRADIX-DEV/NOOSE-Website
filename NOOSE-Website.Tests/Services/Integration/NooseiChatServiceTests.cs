using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>NOOSEI conversations: owner-private, replayed with history, and tool results dropped after a rights change.</summary>
public sealed class NooseiChatServiceTests
{
    private const string Owner = "agent-owner";
    private const string Other = "agent-other";

    private static ClaimsPrincipal Actor(string id = Owner, Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).Build();

    /// <summary>An answer whose transcript has the real shape: everything that was sent, then what the turn added.
    /// The service stores only the tail, so a test that skips the sent part stores nothing.</summary>
    private static NooseiAnswer Answer(
        string text = "Antwort", IReadOnlyList<LlmMessage>? added = null, NooseiCall? call = null,
        bool truncated = false, bool degraded = false, IReadOnlyList<string>? barren = null)
    {
        IReadOnlyList<LlmMessage> sent = call?.Messages ?? [];
        return new NooseiAnswer(text, LlmUsage.Empty,
            new LlmQuotaCharge(120, 0.0012m, LlmQuotaStatus.Empty, null, true),
            1, [.. sent, .. added ?? []], degraded, truncated, null, barren);
    }

    /// <summary>One tool round as the gateway builds it: the call, then its result.</summary>
    private static List<LlmMessage> Round(string id, string tool, string result) =>
    [
        LlmMessage.Assistant(null, [new LlmToolCall(id, tool, """{"suchtext":"Otto"}""")]),
        LlmMessage.Tool(id, tool, result),
    ];

    private static (NooseiChatService Svc, INooseiGateway Gateway) Build(
        SqliteTestContext ctx, Func<NooseiCall, NooseiAnswer>? respond = null, Action<LlmOptions>? tune = null)
    {
        var options = new LlmOptions();
        tune?.Invoke(options);
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => respond is null ? Answer() : respond(call.Arg<NooseiCall>()));

        var settings = Substitute.For<INooseiSettingsService>();
        settings.GetAddendumAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var registry = new NooseiToolRegistry([new ResolveMentionTool(Substitute.For<IMentionService>())]);

        return (new NooseiChatService(ctx.Factory, gateway, settings, registry, Options.Create(options),
            NullLogger<NooseiChatService>.Instance), gateway);
    }

    private static async Task SeedAgentsAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(Owner, Rank.SpecialAgent, configure: a => a.Rank = Rank.SpecialAgent));
        db.Users.Add(Seed.Agent(Other, Rank.SpecialAgent, configure: a => a.Rank = Rank.SpecialAgent));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Ask_CreatesTheConversation_AndStoresBothTurns()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);

        var turn = await svc.AskAsync(null, "Wer führt die Ballas?", Actor());

        Assert.False(string.IsNullOrEmpty(turn.ConversationId));
        Assert.Equal("Antwort", turn.Answer.Text);
        Assert.Equal(120L, turn.QuotaTokens);

        var messages = await svc.GetMessagesAsync(turn.ConversationId, Actor());
        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].FromUser);
        Assert.False(messages[1].FromUser);
    }

    [Fact]
    public async Task Ask_RendersTheAnswersMarkdownToHtml()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, _ => Answer("**Ballas** führt:\n\n- Max Mustermann\n- Erika Muster"));

        var turn = await svc.AskAsync(null, "Wer führt die Ballas?", Actor());

        Assert.Contains("<strong>Ballas</strong>", turn.Answer.Html);
        Assert.Contains("<li>", turn.Answer.Html);
        // the raw Markdown stays available, and the DB keeps it too
        Assert.Contains("**Ballas**", turn.Answer.Text);
    }

    [Fact]
    public async Task Messages_ComeBackRenderedFromStorage()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, _ => Answer("## Lage\n\nAlles *ruhig*."));

        var turn = await svc.AskAsync(null, "Wie ist die Lage?", Actor());
        var messages = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        var answer = Assert.Single(messages, m => !m.FromUser);
        Assert.Contains("<h2", answer.Html);
        Assert.Contains("<em>ruhig</em>", answer.Html);
    }

    [Fact]
    public async Task OwnTurns_StayVerbatim_AndAreNeverTreatedAsMarkdown()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);

        var turn = await svc.AskAsync(null, "Was bedeutet **das** hier?", Actor());
        var messages = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        var question = Assert.Single(messages, m => m.FromUser);
        Assert.Null(question.Html);
        Assert.Equal("Was bedeutet **das** hier?", question.Text);
    }

    [Fact]
    public async Task RenderedAnswer_NeutralisesRawHtmlFromTheModel()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, _ => Answer("Text <script>alert(1)</script> und <img src=x onerror=alert(1)>"));

        var turn = await svc.AskAsync(null, "Frage", Actor());

        // the renderer escapes rather than deletes: the markup shows as text and can never execute
        Assert.DoesNotContain("<script", turn.Answer.Html);
        Assert.DoesNotContain("<img", turn.Answer.Html);
        Assert.Contains("&lt;script&gt;", turn.Answer.Html);
    }

    [Fact]
    public async Task Ask_TitlesTheConversationFromTheFirstQuestion()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);

        await svc.AskAsync(null, "Wer führt die Ballas?", Actor());

        var row = Assert.Single(await svc.GetConversationsAsync(Actor()));
        Assert.Equal("Wer führt die Ballas?", row.Title);
    }

    [Fact]
    public async Task Ask_ReplaysTheHistoryIntoTheFollowUp()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var seen = new List<int>();
        var (svc, _) = Build(ctx, call =>
        {
            seen.Add(call.Messages.Count);
            return Answer();
        });

        var first = await svc.AskAsync(null, "Erste Frage", Actor());
        await svc.AskAsync(first.ConversationId, "Und was ist mit deren Fraktion?", Actor());

        // turn 1: system + question. turn 2: system + question + answer + new question
        Assert.Equal(2, seen[0]);
        Assert.Equal(4, seen[1]);
    }

    [Fact]
    public async Task Ask_KeepsToolResults_AndReplaysThemWhileTheScopeIsUnchanged()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var replayed = new List<string>();
        var (svc, _) = Build(ctx, call =>
        {
            replayed.Add(string.Join("\n", call.Messages.Select(m => m.Content)));
            return Answer(added: Round("c1", "suche_akten", "• Person | Otto Offen"), call: call);
        });

        var first = await svc.AskAsync(null, "Erste Frage", Actor());
        await svc.AskAsync(first.ConversationId, "Zweite Frage", Actor());

        Assert.Contains("Otto Offen", replayed[1]);
    }

    [Fact]
    public async Task Ask_DropsToolResultsFromTheReplay_WhenTheScopeChanged()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var replayed = new List<string>();
        var (svc, _) = Build(ctx, call =>
        {
            replayed.Add(string.Join("\n", call.Messages.Select(m => m.Content)));
            return Answer(added: Round("c1", "lies_akte", "Geheime Akteninhalte"), call: call);
        });

        // turn 1 as leadership, turn 2 after losing that right
        var first = await svc.AskAsync(null, "Erste Frage", Actor(rank: Rank.Director));
        await svc.AskAsync(first.ConversationId, "Zweite Frage", Actor(rank: Rank.Trainee));

        // the tool text was authorised under rights the agent no longer has
        Assert.DoesNotContain("Geheime Akteninhalte", replayed[1]);
        // the agent's own turn survives; only the tool result is withheld
        Assert.Contains("Erste Frage", replayed[1]);
    }

    [Fact]
    public async Task Answer_CarriesTheToolsItRests_On_LiveAndReopened()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(added:
            [
                .. Round("c1", "suche_akten", "• Person | Otto Offen"),
                .. Round("c2", "lies_akte", "Akte Otto Offen"),
                .. Round("c3", "suche_akten", "• Person | Erika Muster"),
            ], call: call));

        var turn = await svc.AskAsync(null, "Wer ist Otto?", Actor());

        // in call order, repeats folded into a count — and the German label, not the identifier
        Assert.Equal(["suche_akten", "lies_akte"], turn.Answer.Tools.Select(t => t.Name));
        Assert.Equal(2, turn.Answer.Tools[0].Count);
        Assert.Equal("Aktensuche", turn.Answer.Tools[0].Label);

        // the same list must come back from storage, or the trace vanishes on reopening
        var reopened = await svc.GetMessagesAsync(turn.ConversationId, Actor());
        var answer = Assert.Single(reopened, m => !m.FromUser);
        Assert.Equal(["suche_akten", "lies_akte"], answer.Tools.Select(t => t.Name));
        Assert.Equal(2, answer.Tools[0].Count);
        // the agent's own turn rests on nothing
        Assert.Empty(reopened.First(m => m.FromUser).Tools);
    }

    [Fact]
    public async Task AToolCallThatProducedNothing_LeavesNoTraceEntry()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(added:
            [
                .. Round("c1", "suche_akten", "• Person | Otto Offen"),
                .. Round("c2", "lies_akte", "Werkzeug konnte nicht ausgeführt werden."),
            ], call: call, barren: ["c2"]));

        var turn = await svc.AskAsync(null, "Wer ist Otto?", Actor());

        // barren rows are deliberately not stored, so the trace shows what the answer actually rests on
        Assert.Equal(["suche_akten"], turn.Answer.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Conversations_AreOwnerPrivate()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);
        var turn = await svc.AskAsync(null, "Meine Frage", Actor());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetMessagesAsync(turn.ConversationId, Actor(Other)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(turn.ConversationId, Actor(Other)));
        Assert.Empty(await svc.GetConversationsAsync(Actor(Other)));
    }

    [Fact]
    public async Task Conversations_AreReadableByTheAiOwner()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);
        var turn = await svc.AskAsync(null, "Meine Frage", Actor());
        var aiOwner = ClaimsPrincipalBuilder.Agent(Other).WithRank(Rank.Director).AsAiOwner().Build();

        var messages = await svc.GetMessagesAsync(turn.ConversationId, aiOwner);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Rename_AndDelete_WorkForTheOwner()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);
        var turn = await svc.AskAsync(null, "Meine Frage", Actor());

        await svc.RenameAsync(turn.ConversationId, "Ballas-Recherche", Actor());
        Assert.Equal("Ballas-Recherche", (await svc.GetConversationsAsync(Actor())).Single().Title);

        await svc.DeleteAsync(turn.ConversationId, Actor());
        Assert.Empty(await svc.GetConversationsAsync(Actor()));
    }

    [Fact]
    public async Task Delete_RemovesTheThreadAndItsMessages()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);
        var turn = await svc.AskAsync(null, "Meine Frage", Actor());

        await svc.DeleteAsync(turn.ConversationId, Actor());

        await using var db = ctx.NewContext();
        Assert.Empty(await db.NooseiConversations.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetMessagesAsync(turn.ConversationId, Actor()));
    }

    [Fact]
    public async Task Ask_RejectsAnEmptyQuestion()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, gateway) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync(null, "   ", Actor()));

        await gateway.DidNotReceive().AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("demo")]
    [InlineData("supervision")]
    public async Task Ask_DeniesTheRolesWithoutNooseiAccess(string role)
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx);
        var builder = ClaimsPrincipalBuilder.Agent(Owner).WithRank(Rank.SpecialAgent);
        var actor = role switch
        {
            "partner" => builder.AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            "demo" => builder.AsDemo().Build(),
            _ => builder.AsTeamLead().Build(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AskAsync(null, "Frage", actor));
    }

    [Fact]
    public async Task Ask_OffersTheToolCatalogue()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        NooseiCall? seen = null;
        var (svc, _) = Build(ctx, call =>
        {
            seen = call;
            return Answer();
        });

        await svc.AskAsync(null, "Frage", Actor());

        Assert.NotNull(seen?.Tools);
        Assert.NotEmpty(seen!.Tools!);
        Assert.NotNull(seen.ToolExecutor);
    }

    // ---- record anchor ----

    private static async Task SeedRecordsAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.People.Add(Seed.Person(id: "p-open", name: "Otto Offen"));
        db.People.Add(Seed.Person(id: "p-secret", name: "Gerd Geheim", configure: p => p.IsClassified = true));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Ask_StoresAVisibleAnchor_AndNamesItToTheModel()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        await SeedRecordsAsync(ctx);
        NooseiCall? seen = null;
        var (svc, _) = Build(ctx, call => { seen = call; return Answer(); });

        var turn = await svc.AskAsync(null, "Was ist hier los?", Actor(), null, new NooseiAnchor("Person", "p-open"));

        await using var db = ctx.NewContext();
        var conversation = await db.NooseiConversations.SingleAsync(c => c.Id == turn.ConversationId);
        Assert.Equal("Person", conversation.AnchorEntityType);
        Assert.Equal("p-open", conversation.AnchorEntityId);
        Assert.Contains(seen!.Messages, m => m.Content is not null && m.Content.Contains("Otto Offen"));
    }

    [Fact]
    public async Task Ask_DropsAnAnchorTheAskerMayNotSee()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        await SeedRecordsAsync(ctx);
        NooseiCall? seen = null;
        var (svc, _) = Build(ctx, call => { seen = call; return Answer(); });

        // a hand-typed ?akte= must not confirm a classified record by naming it back
        var turn = await svc.AskAsync(null, "Was ist hier los?", Actor(), null, new NooseiAnchor("Person", "p-secret"));

        await using var db = ctx.NewContext();
        var conversation = await db.NooseiConversations.SingleAsync(c => c.Id == turn.ConversationId);
        Assert.Null(conversation.AnchorEntityType);
        Assert.Null(conversation.AnchorEntityId);
        Assert.DoesNotContain(seen!.Messages, m => m.Content is not null && m.Content.Contains("Gerd Geheim"));
    }

    [Fact]
    public async Task Ask_KeepsTheAnchorOnFollowUps()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        await SeedRecordsAsync(ctx);
        NooseiCall? seen = null;
        var (svc, _) = Build(ctx, call => { seen = call; return Answer(); });

        var first = await svc.AskAsync(null, "Erste Frage", Actor(), null, new NooseiAnchor("Person", "p-open"));
        await svc.AskAsync(first.ConversationId, "Und weiter?", Actor());

        Assert.Contains(seen!.Messages, m => m.Content is not null && m.Content.Contains("Otto Offen"));
        Assert.Equal("Person", seen.EntityType);
    }

    [Fact]
    public async Task Ask_DropsATaskforceAnchor_ForANonMember()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        await using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new NOOSE_Website.Data.Entities.Taskforces.Taskforce
            {
                Id = "tf1",
                Name = "Operation Nachtfalke",
                CaseNumber = "FIB-TF-2026-0001",
                Scope = TaskforceScope.InternalAgency,
                Status = TaskforceStatus.Approved,
            });
            await db.SaveChangesAsync();
        }
        NooseiCall? seen = null;
        var (svc, _) = Build(ctx, call => { seen = call; return Answer(); });

        // Visibility.IsRecordVisibleAsync does not know Taskforce and answers true for unknown types,
        // so the second gate is the one doing the work here
        var turn = await svc.AskAsync(null, "Was läuft da?", Actor(), null, new NooseiAnchor("Taskforce", "tf1"));

        await using var check = ctx.NewContext();
        var conversation = await check.NooseiConversations.SingleAsync(c => c.Id == turn.ConversationId);
        Assert.Null(conversation.AnchorEntityId);
        Assert.DoesNotContain(seen!.Messages, m => m.Content is not null && m.Content.Contains("Nachtfalke"));
    }

    [Theory]
    [InlineData("Person:p1", "Person", "p1")]
    [InlineData("Fraktion:f1", "Faction", "f1")]
    [InlineData("Faction:f1", "Faction", "f1")]
    public void Anchor_ParsesBothTheGermanAndTheClrTypeName(string token, string type, string id)
    {
        var anchor = NooseiAnchor.Parse(token);

        Assert.NotNull(anchor);
        Assert.Equal(type, anchor!.EntityType);
        Assert.Equal(id, anchor.EntityId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Person")]
    [InlineData(":p1")]
    [InlineData("Person:")]
    [InlineData("Hausmeister:p1")]
    public void Anchor_RejectsAnythingItCannotUse(string? token) => Assert.Null(NooseiAnchor.Parse(token));

    // ---- sources under an answer ----

    [Fact]
    public async Task Ask_StoresTheRecordsRead_OnceEach_AndWithoutTheToolEntries()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, gateway) = Build(ctx);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new NooseiAnswer("Antwort", LlmUsage.Empty,
                new LlmQuotaCharge(120, 0.0012m, LlmQuotaStatus.Empty, null, true), 2, [], false, false,
                [
                    new LlmContextRef("tool", null, "lies_akte"),
                    new LlmContextRef("Person", "p1", "Max Mustermann"),
                    new LlmContextRef("Person", "p1", "Max Mustermann"),
                    new LlmContextRef("Faction", "f1", "Ballas"),
                ]));

        var turn = await svc.AskAsync(null, "Frage", Actor());

        Assert.Equal(2, turn.Answer.Sources.Count);
        Assert.Contains(turn.Answer.Sources, s => s.Id == "p1" && s.Name == "Max Mustermann");
        Assert.Contains(turn.Answer.Sources, s => s.Id == "f1");
        Assert.DoesNotContain(turn.Answer.Sources, s => s.Type == "tool");
    }

    [Fact]
    public async Task Ask_DropsARecordTypeThatHasNoRouteOfItsOwn()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, gateway) = Build(ctx);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new NooseiAnswer("Antwort", LlmUsage.Empty,
                new LlmQuotaCharge(120, 0.0012m, LlmQuotaStatus.Empty, null, true), 2, [], false, false,
                [
                    new LlmContextRef("Meeting", "m1", "Wochenbesprechung"),
                    new LlmContextRef("Hausmeister", "h1", "Hans Hausmeister"),
                ]));

        var turn = await svc.AskAsync(null, "Frage", Actor());

        // an unknown type would fall through to the person route and link into the wrong record
        Assert.Equal("Meeting", Assert.Single(turn.Answer.Sources).Type);
    }

    [Fact]
    public async Task Ask_CapsTheSources_SoOneFilterCallCannotBuryTheAnswer()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, gateway) = Build(ctx);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new NooseiAnswer("Antwort", LlmUsage.Empty,
                new LlmQuotaCharge(120, 0.0012m, LlmQuotaStatus.Empty, null, true), 2, [], false, false,
                Enumerable.Range(0, 120).Select(i => new LlmContextRef("Person", $"p{i}", $"Person {i}")).ToList()));

        var turn = await svc.AskAsync(null, "Frage", Actor());

        Assert.Equal(24, turn.Answer.Sources.Count);
    }

    [Fact]
    public async Task Messages_KeepTheirSources_WhenTheConversationIsReopened()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, gateway) = Build(ctx);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new NooseiAnswer("Antwort", LlmUsage.Empty,
                new LlmQuotaCharge(120, 0.0012m, LlmQuotaStatus.Empty, null, true), 2, [], false, false,
                [new LlmContextRef("Person", "p1", "Max Mustermann")]));

        var turn = await svc.AskAsync(null, "Frage", Actor());
        var reopened = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        var answer = Assert.Single(reopened, m => !m.FromUser);
        Assert.Equal("Max Mustermann", Assert.Single(answer.Sources).Name);
        Assert.Empty(reopened.Single(m => m.FromUser).Sources);
    }

    // ---- the tool exchange in the history ----

    [Fact]
    public async Task Ask_ReplaysTheToolExchangeAsRealToolRoles_WithTheArgumentsIntact()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var seen = new List<IReadOnlyList<LlmMessage>>();
        var (svc, _) = Build(ctx, call =>
        {
            seen.Add(call.Messages);
            return Answer(added: Round("c1", "suche_akten", "• Person | Otto Offen"), call: call);
        });

        var first = await svc.AskAsync(null, "Erste Frage", Actor());
        await svc.AskAsync(first.ConversationId, "Zweite Frage", Actor());

        var result = Assert.Single(seen[1], m => m.Role == LlmRole.Tool);
        Assert.Equal("c1", result.ToolCallId);
        Assert.Equal("suche_akten", result.Name);
        var asked = Assert.Single(seen[1], m => m.ToolCalls is { Count: > 0 });
        // the arguments are the point: "Keine Treffer." without what was searched for invites the same call again
        Assert.Contains("Otto", Assert.Single(asked.ToolCalls!).ArgumentsJson);
    }

    [Fact]
    public async Task Ask_StoresEachTurnOnce_NotTheReplayedHistoryAgain()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(added: Round("c1", "suche_akten", "Treffer"), call: call));

        var first = await svc.AskAsync(null, "Eins", Actor());
        await svc.AskAsync(first.ConversationId, "Zwei", Actor());
        await svc.AskAsync(first.ConversationId, "Drei", Actor());

        await using var check = ctx.NewContext();
        var rows = await check.NooseiMessages.Where(m => m.ConversationId == first.ConversationId).ToListAsync();
        Assert.Equal(3, rows.Count(r => r.Role == "tool"));
        Assert.Equal(3, rows.Count(r => r.Role == NooseiHistoryWindow.ToolCallRole));
        Assert.Equal(3, rows.Count(r => r.Role == "user"));
    }

    [Fact]
    public async Task Ask_DropsAToolRowThatProducedNothing_TogetherWithTheCallThatAskedForIt()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(
            added: Round("c1", "lies_akte", "Werkzeug konnte nicht ausgeführt werden."), call: call, barren: ["c1"]));

        var turn = await svc.AskAsync(null, "Frage", Actor());

        await using var check = ctx.NewContext();
        var rows = await check.NooseiMessages.Where(m => m.ConversationId == turn.ConversationId).ToListAsync();
        Assert.DoesNotContain(rows, r => r.Role == "tool");
        // the call row goes with it: a tool_calls message without an answer is the same invalid shape
        Assert.DoesNotContain(rows, r => r.Role == NooseiHistoryWindow.ToolCallRole);
    }

    [Fact]
    public async Task Ask_HidesTheToolCallRowsFromTheChat()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(added: Round("c1", "suche_akten", "Treffer"), call: call));

        var turn = await svc.AskAsync(null, "Frage", Actor());
        var messages = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        // a round that only carried tool calls has nothing to show; under "assistant" it would be an empty bubble
        Assert.Equal(2, messages.Count);
    }

    // ---- caveats that outlive the snackbar ----

    [Fact]
    public async Task Ask_KeepsTheTruncatedAndDegradedMarks_WhenTheConversationIsReopened()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(call: call, truncated: true, degraded: true));

        var turn = await svc.AskAsync(null, "Frage", Actor());
        var reopened = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        Assert.True(turn.Answer.Truncated);
        var answer = Assert.Single(reopened, m => !m.FromUser);
        Assert.True(answer.Truncated);
        Assert.True(answer.Degraded);
        // truncation must not use IsError, which would drop the row from every later replay
        Assert.False(answer.IsError);
    }

    [Fact]
    public async Task Ask_NotesACaseNumberNoToolReturned_AndStillKeepsTheAnswer()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call =>
            Answer("Die Ballas [Fraktion Ballas · FIB-F-2026-0099] sind aktiv.", call: call));

        var turn = await svc.AskAsync(null, "Was ist mit den Ballas?", Actor());
        var reopened = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        Assert.Contains("FIB-F-2026-0099", turn.Answer.UnsupportedNote);
        Assert.DoesNotContain("existiert", turn.Answer.UnsupportedNote!);
        // warned, not rejected: unlike proofreading there is no correct alternative to fall back on
        Assert.Contains("Ballas", turn.Answer.Text);
        Assert.Equal(turn.Answer.UnsupportedNote, Assert.Single(reopened, m => !m.FromUser).UnsupportedNote);
    }

    [Fact]
    public async Task Ask_DoesNotComplain_WhenTheCitedRecordCameOutOfATool()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var (svc, _) = Build(ctx, call => Answer(
            "Die Ballas [Fraktion Ballas · FIB-F-2026-0099] sind aktiv.",
            added: Round("c1", "suche_akten", "• Fraktion | Ballas | Aktenzeichen: FIB-F-2026-0099 | id=f1"),
            call: call));

        var turn = await svc.AskAsync(null, "Was ist mit den Ballas?", Actor());

        Assert.Null(turn.Answer.UnsupportedNote);
    }

    [Fact]
    public async Task Ask_ReportsTheRightsChange_AndTellsTheModelAboutIt()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var seen = new List<IReadOnlyList<LlmMessage>>();
        var (svc, _) = Build(ctx, call =>
        {
            seen.Add(call.Messages);
            return Answer(added: Round("c1", "lies_akte", "Geheime Akteninhalte"), call: call);
        });

        var first = await svc.AskAsync(null, "Erste Frage", Actor(rank: Rank.Director));
        var second = await svc.AskAsync(first.ConversationId, "Zweite Frage", Actor(rank: Rank.Trainee));

        Assert.True(second.ScopeChanged);
        Assert.Contains(seen[1], m => m.Role == LlmRole.System && m.Content == NooseiPrompts.ScopeChanged);
    }

    [Fact]
    public async Task Ask_StaysQuiet_WhenTheRightsDidNotChange()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx);
        var seen = new List<IReadOnlyList<LlmMessage>>();
        var (svc, _) = Build(ctx, call =>
        {
            seen.Add(call.Messages);
            return Answer(added: Round("c1", "lies_akte", "Akteninhalt"), call: call);
        });

        var first = await svc.AskAsync(null, "Erste Frage", Actor());
        var second = await svc.AskAsync(first.ConversationId, "Zweite Frage", Actor());

        Assert.False(second.ScopeChanged);
        Assert.DoesNotContain(seen[1], m => m.Content == NooseiPrompts.ScopeChanged);
    }
}
