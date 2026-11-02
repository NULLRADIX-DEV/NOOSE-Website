using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Proposal cards ride on the answer row: stored with it, read back with it, and only from calls that kept their row.</summary>
public sealed class NooseiChatProposalTests
{
    private const string Owner = "agent-owner";
    private const string ToolName = "vorschlag_test";

    private static ClaimsPrincipal Actor() => ClaimsPrincipalBuilder.Agent(Owner).WithRank(Rank.SpecialAgent).Build();

    /// <summary>Hands out one fresh proposal per call, like a real proposal tool.</summary>
    private sealed class ProposingTool : INooseiProposingTool
    {
        public string Name => ToolName;
        public string Description => "Test";
        public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""{"type":"object"}""");

        public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new NooseiToolResult("Vorschlag vorbereitet, NICHT ausgeführt.", null, false,
                new NooseiProposal(Guid.NewGuid().ToString(), NooseiProposalKind.Watch, "Akte beobachten",
                    [new NooseiProposalLine("Akte", "Person X")], new NooseiProposalArgs { EntityType = "Person", EntityId = "x" })));
    }

    /// <summary>A gateway that really runs the executor for each call id, then returns the rounds it built.</summary>
    private static NooseiChatService Build(SqliteTestContext ctx, IReadOnlyList<string> callIds, IReadOnlyList<string>? barren = null)
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(async info =>
            {
                var call = info.Arg<NooseiCall>();
                var added = new List<LlmMessage>();
                foreach (var id in callIds)
                {
                    var toolCall = new LlmToolCall(id, ToolName, "{}");
                    var outcome = await call.ToolExecutor!(toolCall, CancellationToken.None);
                    added.Add(LlmMessage.Assistant(null, [toolCall]));
                    added.Add(LlmMessage.Tool(id, ToolName, outcome.Text));
                }
                return new NooseiAnswer("Ich habe es vorgeschlagen.", LlmUsage.Empty,
                    new LlmQuotaCharge(10, 0.0001m, LlmQuotaStatus.Empty, null, true),
                    1, [.. call.Messages, .. added], false, false, null, barren);
            });
        var settings = Substitute.For<INooseiSettingsService>();
        settings.GetAddendumAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        return new NooseiChatService(ctx.Factory, gateway, settings, new NooseiToolRegistry([new ProposingTool()]),
            Options.Create(new LlmOptions()), NullLogger<NooseiChatService>.Instance);
    }

    private static async Task SeedAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(Owner, Rank.SpecialAgent));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ACard_IsStoredUnderTheAnswer_AndComesBackOnReopen()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var svc = Build(ctx, ["c1"]);

        var turn = await svc.AskAsync(null, "Beobachte X", Actor());
        var reopened = await svc.GetMessagesAsync(turn.ConversationId, Actor());

        var live = Assert.Single(turn.Answer.Proposals!);
        var stored = Assert.Single(Assert.Single(reopened, m => !m.FromUser).Proposals!);
        Assert.Equal(live.Id, stored.Id);
        Assert.True(stored.IsOpen);
        Assert.Empty(Assert.Single(reopened, m => m.FromUser).Proposals ?? []);
    }

    [Fact]
    public async Task ACallTheGatewayGaveUpOn_ProposesNothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var svc = Build(ctx, ["c1", "c2"], barren: ["c2"]);

        var turn = await svc.AskAsync(null, "Beobachte X und Y", Actor());

        Assert.Single(turn.Answer.Proposals!);
    }

    [Fact]
    public async Task OneAnswer_CarriesAtMostFiveCards()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var svc = Build(ctx, ["c1", "c2", "c3", "c4", "c5", "c6"]);

        var turn = await svc.AskAsync(null, "Beobachte alles", Actor());

        Assert.Equal(NooseiProposalTray.Max, turn.Answer.Proposals!.Count);
    }
}
