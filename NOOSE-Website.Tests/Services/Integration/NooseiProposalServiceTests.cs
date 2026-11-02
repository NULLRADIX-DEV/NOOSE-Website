using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>NOOSEI proposes, the agent executes: nothing is written before the click, and the click checks again.</summary>
public sealed class NooseiProposalServiceTests
{
    private const string Owner = "agent-owner";
    private const string Colleague = "agent-colleague";

    private static ClaimsPrincipal Leader(string id = Owner)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior(string id = Owner)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static NooseiProposalService Build(SqliteTestContext ctx) => new(ctx.Factory);

    private static NooseiProposalExecutor Executor(SqliteTestContext ctx, ITagService? tags = null,
        IWatchlistService? watchlist = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var counter = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"NOOSE-A-2026-{++counter:0000}");
        var notifications = Substitute.For<INotificationService>();
        return new NooseiProposalExecutor(ctx.Factory,
            new FollowupService(ctx.Factory, notifications),
            tags ?? new TagService(ctx.Factory),
            watchlist ?? new WatchlistService(ctx.Factory),
            new LinkService(ctx.Factory, Substitute.For<IThreatScoreService>()),
            new JobService(ctx.Factory, caseNo, notifications),
            NullLogger<NooseiProposalExecutor>.Instance);
    }

    private static async Task<(SqliteTestContext Ctx, string PersonId, string SecretId, string FactionId)> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(Owner, Rank.Director, configure: a => a.Codename = "Falcon"));
        db.Users.Add(Seed.Agent(Colleague, Rank.SpecialAgent, configure: a => a.Codename = "Raven"));
        var person = Seed.Person(name: "Max Mustermann");
        var secret = Seed.Person(name: "Erika Geheim", configure: p => p.IsClassified = true);
        var faction = Seed.Faction(name: "Ballas");
        db.People.AddRange(person, secret);
        db.Factions.Add(faction);
        db.Tags.Add(new Tag { Id = "tag-drogen", Name = "Drogenhandel" });
        await db.SaveChangesAsync();
        return (ctx, person.Id, secret.Id, faction.Id);
    }

    private static Task<NooseiToolResult> ProposeAsync(NooseiProposalService svc, ClaimsPrincipal actor,
        NooseiProposalDraft draft)
        => svc.ProposeAsync(draft, NooseiToolContext.From(actor));

    /// <summary>Stores the proposal under an assistant row of the owner's conversation, as the chat would.</summary>
    private static Task<string> StoreAsync(SqliteTestContext ctx, NooseiProposal proposal, string owner = Owner,
        NooseiChatMode mode = NooseiChatMode.Agency)
        => StoreAsync(ctx, [proposal], owner, mode);

    private static async Task<string> StoreAsync(SqliteTestContext ctx, IReadOnlyList<NooseiProposal> proposals,
        string owner = Owner, NooseiChatMode mode = NooseiChatMode.Agency)
    {
        await using var db = ctx.NewContext();
        var conversation = new NooseiConversation { AgentId = owner, Title = "t", LastMessageAt = DateTime.UtcNow, Mode = mode };
        var message = new NooseiMessage
        {
            ConversationId = conversation.Id,
            Sequence = 1,
            Role = "assistant",
            Content = "Vorschlag",
            ProposalsJson = NooseiProposalJson.Serialize(proposals),
            CreatedAt = DateTime.UtcNow,
        };
        db.NooseiConversations.Add(conversation);
        db.NooseiMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private static async Task<NooseiProposal> StoredAsync(SqliteTestContext ctx, string messageId)
        => Assert.Single(await AllStoredAsync(ctx, messageId));

    private static async Task<IReadOnlyList<NooseiProposal>> AllStoredAsync(SqliteTestContext ctx, string messageId)
    {
        await using var db = ctx.NewContext();
        var json = await db.NooseiMessages.Where(m => m.Id == messageId).Select(m => m.ProposalsJson).SingleAsync();
        return NooseiProposalJson.Read(json);
    }

    private static NooseiProposalDraft Followup(string id, int inDays = 3) =>
        new(NooseiProposalKind.Followup) { Type = "Person", Id = id, InDays = inDays, Text = "Nachhaken" };

    [Fact]
    public async Task AProposal_WritesNothing_AndCarriesTheCard()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var result = await ProposeAsync(svc, Leader(), Followup(personId));

        var proposal = Assert.IsType<NooseiProposal>(result.Proposal);
        Assert.Equal(NooseiProposalKind.Followup, proposal.Kind);
        Assert.True(proposal.IsOpen);
        Assert.Contains(proposal.Lines, l => l.Value.Contains("Max Mustermann"));
        Assert.Contains("NICHT ausgeführt", result.Text);
        await using var db = ctx.NewContext();
        Assert.False(await db.Followups.AnyAsync());
    }

    [Fact]
    public async Task AClassifiedRecord_ReadsLikeAMissingOne_ForAJunior()
    {
        var (ctx, _, secretId, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var hidden = await ProposeAsync(svc, Junior(), Followup(secretId));
        var missing = await ProposeAsync(svc, Junior(), Followup("gibt-es-nicht"));

        Assert.Null(hidden.Proposal);
        Assert.Equal(missing.Text, hidden.Text);
        Assert.Equal(NooseiToolResult.NotFound().Text, hidden.Text);
    }

    [Fact]
    public async Task ATypeTheProposalDoesNotOffer_IsRefused()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var result = await ProposeAsync(svc, Leader(), Followup(personId) with { Type = "Gesetz" });

        Assert.Null(result.Proposal);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task UnknownCodenames_AndDatesOutOfRange_AreRefused()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var stranger = await ProposeAsync(svc, Leader(), Followup(personId) with { Agents = ["Phantom"] });
        var tooFar = await ProposeAsync(svc, Leader(), Followup(personId, inDays: 500));
        var past = await ProposeAsync(svc, Leader(), Followup(personId) with { InDays = null, DueDate = "2020-01-01" });

        Assert.Null(stranger.Proposal);
        Assert.Contains("Phantom", stranger.Text);
        Assert.Null(tooFar.Proposal);
        Assert.Null(past.Proposal);
    }

    [Fact]
    public async Task ACodename_ResolvesCaseInsensitively_ToTheResponsible()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var result = await ProposeAsync(svc, Leader(), Followup(personId) with { Agents = ["raven"] });

        Assert.Equal([Colleague], result.Proposal!.Args.AgentIds);
        Assert.Contains(result.Proposal.Lines, l => l is { Label: "Zuständig", Value: "Raven" });
    }

    [Fact]
    public async Task Tags_ComeOnlyFromTheCatalog_AndAPresentTagProposesNothing()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var draft = new NooseiProposalDraft(NooseiProposalKind.Tag) { Type = "Person", Id = personId };

        var invented = await ProposeAsync(svc, Leader(), draft with { Tags = ["Waffenschmuggel"] });
        var known = await ProposeAsync(svc, Leader(), draft with { Tags = ["drogenhandel"] });
        await using (var db = ctx.NewContext())
        {
            db.TagMappings.Add(new TagMapping { TagId = "tag-drogen", EntityType = "Person", EntityId = personId });
            await db.SaveChangesAsync();
        }
        var present = await ProposeAsync(svc, Leader(), draft with { Tags = ["Drogenhandel"] });

        Assert.Null(invented.Proposal);
        Assert.Contains("Drogenhandel", invented.Text);
        Assert.Equal(["tag-drogen"], known.Proposal!.Args.TagIds);
        Assert.Null(present.Proposal);
        Assert.False(present.IsError);
    }

    [Fact]
    public async Task ALink_PutsTheRecordThatListsLinksFirst_AndAnExistingLinkProposesNothing()
    {
        var (ctx, personId, _, factionId) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var draft = new NooseiProposalDraft(NooseiProposalKind.Link)
        {
            Type = "Fraktion", Id = factionId, TargetType = "Person", TargetId = personId,
        };

        var result = await ProposeAsync(svc, Leader(), draft);
        await using (var db = ctx.NewContext())
        {
            db.Links.Add(new Link { SourceType = "Person", SourceId = personId, TargetType = "Faction", TargetId = factionId });
            await db.SaveChangesAsync();
        }
        var again = await ProposeAsync(svc, Leader(), draft);

        Assert.Equal("Person", result.Proposal!.Args.EntityType);
        Assert.Equal("Faction", result.Proposal.Args.TargetType);
        Assert.Null(again.Proposal);
    }

    [Fact]
    public async Task AJob_WithoutAssignees_GoesToTheAsker()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);

        var result = await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Job)
        {
            Title = "Observation planen", TargetType = "Person", TargetId = personId, Priority = "hoch",
        });

        Assert.Equal([Owner], result.Proposal!.Args.AgentIds);
        Assert.Equal(JobPriority.High, result.Proposal.Args.Priority);
    }

    [Fact]
    public async Task Executing_WritesThroughTheService_AndOnlyOnce()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var proposal = (await ProposeAsync(svc, Leader(), Followup(personId))).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);

        var done = await Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader()));

        Assert.Equal(NooseiProposalState.Executed, done.State);
        Assert.NotNull(done.ResultHref);
        Assert.Contains("bereits", second.Message);
        Assert.Equal(NooseiProposalState.Executed, (await StoredAsync(ctx, messageId)).State);
        await using var db = ctx.NewContext();
        var followup = await db.Followups.SingleAsync();
        Assert.Equal(personId, followup.EntityId);
        Assert.Equal("Nachhaken", followup.Note);
    }

    [Fact]
    public async Task Executing_ChecksTheRightsOfTheClick_NotOfTheProposal()
    {
        var (ctx, _, secretId, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var proposal = (await ProposeAsync(svc, Leader(), Followup(secretId))).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);

        // demoted since: the classified record is out of reach now
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Junior()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
        await using var db = ctx.NewContext();
        Assert.False(await db.Followups.AnyAsync());
    }

    [Fact]
    public async Task OnlyTheOwner_MayExecute_NotAnotherAgentNorTheAiOwnerNorTheSupervision()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var proposal = (await ProposeAsync(svc, Leader(), Followup(personId))).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader(Colleague)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx).ExecuteAsync(messageId, proposal.Id, ClaimsPrincipalBuilder.Agent(Colleague).WithRank(Rank.Director).AsAiOwner().Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx).ExecuteAsync(messageId, proposal.Id, ClaimsPrincipalBuilder.Agent(Owner).WithRank(Rank.Director).AsTeamLead().Build()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
    }

    [Fact]
    public async Task ADismissedProposal_CannotBeExecutedAnymore()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var proposal = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Watch)
        {
            Type = "Person", Id = personId,
        })).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);

        var dismissed = await Executor(ctx).DismissAsync(messageId, proposal.Id, Leader());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader()));

        Assert.Equal(NooseiProposalState.Dismissed, dismissed.State);
        await using var db = ctx.NewContext();
        Assert.False(await db.Set<WatchlistEntry>().AnyAsync());
    }

    [Fact]
    public async Task AVanishedTag_IsRefusedAtTheClick_AndTheCardStaysOpen()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var proposal = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Tag)
        {
            Type = "Person", Id = personId, Tags = ["Drogenhandel"],
        })).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);
        await using (var db = ctx.NewContext())
        {
            db.Tags.Remove(await db.Tags.SingleAsync());
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
    }

    [Fact]
    public async Task AJob_IsCreatedWithItsReference_AndAWatchAndALinkLand()
    {
        var (ctx, personId, _, factionId) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var job = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Job)
        {
            Title = "Observation planen", TargetType = "Person", TargetId = personId, Agents = ["Raven"],
        })).Proposal!;
        var watch = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Watch)
        {
            Type = "Fraktion", Id = factionId,
        })).Proposal!;
        var link = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Link)
        {
            Type = "Person", Id = personId, TargetType = "Fraktion", TargetId = factionId, Text = "Mitläufer",
        })).Proposal!;

        var jobDone = await Executor(ctx).ExecuteAsync(await StoreAsync(ctx, job), job.Id, Leader());
        await Executor(ctx).ExecuteAsync(await StoreAsync(ctx, watch), watch.Id, Leader());
        await Executor(ctx).ExecuteAsync(await StoreAsync(ctx, link), link.Id, Leader());

        await using var db = ctx.NewContext();
        var created = await db.Jobs.SingleAsync();
        Assert.Equal($"/aufgaben/{created.Id}", jobDone.ResultHref);
        Assert.Equal(Colleague, (await db.JobAssignments.SingleAsync()).AgentId);
        Assert.True(await db.Links.AnyAsync(l => l.SourceType == "Job" && l.SourceId == created.Id && l.TargetId == personId));
        Assert.True(await db.Set<WatchlistEntry>().AnyAsync(w => w.EntityId == factionId && w.AgentId == Owner));
        Assert.True(await db.Links.AnyAsync(l => l.SourceId == personId && l.TargetId == factionId && l.Label == "Mitläufer"));
    }

    [Fact]
    public void TheStoredForm_RoundTrips_AndACorruptRowReadsEmpty()
    {
        var proposal = new NooseiProposal("p1", NooseiProposalKind.Followup, "Wiedervorlage setzen",
            [new NooseiProposalLine("Akte", "Person X")],
            new NooseiProposalArgs { EntityType = "Person", EntityId = "x", Due = new DateOnly(2026, 11, 5) });

        var back = Assert.Single(NooseiProposalJson.Read(NooseiProposalJson.Serialize([proposal])));

        Assert.Equal(proposal.Args, back.Args with { AgentIds = proposal.Args.AgentIds, TagIds = proposal.Args.TagIds });
        Assert.Equal(new DateOnly(2026, 11, 5), back.Args.Due);
        Assert.Empty(NooseiProposalJson.Read("{kaputt"));
        Assert.Null(NooseiProposalJson.Serialize([]));
    }

    [Fact]
    public void EveryProposalTool_OffersOnlyOpenableTypes_AndIsAnAgencyTool()
    {
        var svc = Substitute.For<INooseiProposalService>();
        INooseiProposingTool[] tools =
        [
            new ProposeFollowupTool(svc), new ProposeTagTool(svc), new ProposeJobTool(svc),
            new ProposeWatchTool(svc), new ProposeLinkTool(svc),
        ];
        var registry = new NooseiToolRegistry(tools.Cast<INooseiTool>());

        foreach (var tool in tools)
        {
            Assert.StartsWith("vorschlag_", tool.Name);
            Assert.Contains("NICHTS", tool.Description);
            Assert.NotNull(registry.Find(tool.Name, NooseiChatMode.Agency));
            Assert.Null(registry.Find(tool.Name, NooseiChatMode.Legal));
            foreach (var property in tool.ParameterSchema.GetProperty("properties").EnumerateObject()
                         .Where(p => p.Value.TryGetProperty("enum", out _) && p.Name.EndsWith("typ")))
            {
                var offered = property.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.NotEmpty(offered);
                Assert.All(offered, name => Assert.NotNull(NooseiRecordTypes.Clr(name, NooseiUse.Read)));
            }
        }
    }

    [Fact]
    public async Task TheFollowupTool_HandsItsArgumentsToTheService()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var tool = new ProposeFollowupTool(Build(ctx));
        using var args = System.Text.Json.JsonDocument.Parse(
            $$"""{"typ":"Person","id":"{{personId}}","in_tagen":2,"notiz":"Anrufen","zustaendig":"Raven"}""");

        var result = await tool.InvokeAsync(args.RootElement, NooseiToolContext.From(Leader()));

        var proposal = Assert.IsType<NooseiProposal>(result.Proposal);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).AddDays(2), proposal.Args.Due);
        Assert.Equal("Anrufen", proposal.Args.Text);
        Assert.Equal([Colleague], proposal.Args.AgentIds);
    }

    private static async Task<(string MessageId, NooseiProposal Proposal)> StoredTagAsync(SqliteTestContext ctx, string personId)
    {
        var proposal = (await ProposeAsync(Build(ctx), Leader(), new NooseiProposalDraft(NooseiProposalKind.Tag)
        {
            Type = "Person", Id = personId, Tags = ["Drogenhandel"],
        })).Proposal!;
        return (await StoreAsync(ctx, proposal), proposal);
    }

    private static ITagService Failing(Exception exception)
    {
        var tags = Substitute.For<ITagService>();
        tags.AddManyAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs<Task<BatchOutcome>>(_ => throw exception);
        return tags;
    }

    [Fact]
    public async Task ARefusalDuringTheWrite_OpensTheCardAgain()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var (messageId, proposal) = await StoredTagAsync(ctx, personId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx, tags: Failing(new UnauthorizedAccessException("nein"))).ExecuteAsync(messageId, proposal.Id, Leader()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
    }

    [Fact]
    public async Task AnUnexpectedFailureDuringTheWrite_KeepsTheCardClosed_AgainstADuplicate()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var (messageId, proposal) = await StoredTagAsync(ctx, personId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(ctx, tags: Failing(new TimeoutException())).ExecuteAsync(messageId, proposal.Id, Leader()));

        var stored = await StoredAsync(ctx, messageId);
        Assert.Equal(NooseiProposalState.Executed, stored.State);
        Assert.NotNull(stored.Warning);
    }

    [Fact]
    public async Task ASiblingDecidedMidWrite_KeepsBothOutcomes()
    {
        var (ctx, personId, _, factionId) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        var watch = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Watch)
        {
            Type = "Person", Id = personId,
        })).Proposal!;
        var sibling = (await ProposeAsync(svc, Leader(), new NooseiProposalDraft(NooseiProposalKind.Watch)
        {
            Type = "Fraktion", Id = factionId,
        })).Proposal!;
        var messageId = await StoreAsync(ctx, [watch, sibling]);
        var real = new WatchlistService(ctx.Factory);
        var slow = Substitute.For<IWatchlistService>();
        slow.FollowManyAsync(default!, default!, default).ReturnsForAnyArgs(async call =>
        {
            // the other card is dismissed while this write runs
            await Executor(ctx).DismissAsync(messageId, sibling.Id, Leader());
            return await real.FollowManyAsync(call.ArgAt<IReadOnlyCollection<(string, string)>>(0), Leader());
        });

        var done = await Executor(ctx, watchlist: slow).ExecuteAsync(messageId, watch.Id, Leader());

        var stored = await AllStoredAsync(ctx, messageId);
        Assert.NotNull(done.ResultHref);
        Assert.Equal(NooseiProposalState.Executed, stored.Single(p => p.Id == watch.Id).State);
        Assert.Equal(done.ResultHref, stored.Single(p => p.Id == watch.Id).ResultHref);
        Assert.Equal(NooseiProposalState.Dismissed, stored.Single(p => p.Id == sibling.Id).State);
    }

    [Fact]
    public async Task Dismissing_IsTheOwnersAlone_AndNeverTheSupervisions()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var proposal = (await ProposeAsync(Build(ctx), Leader(), Followup(personId))).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx).DismissAsync(messageId, proposal.Id, Leader(Colleague)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Executor(ctx).DismissAsync(messageId, proposal.Id, ClaimsPrincipalBuilder.Agent(Owner).WithRank(Rank.Director).AsTeamLead().Build()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
    }

    [Fact]
    public async Task AProposalInALawChat_CannotBeExecuted()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var proposal = (await ProposeAsync(Build(ctx), Leader(), Followup(personId))).Proposal!;
        var messageId = await StoreAsync(ctx, proposal, mode: NooseiChatMode.Legal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader()));
    }

    [Fact]
    public async Task AnAssigneeNoLongerSelectable_StopsTheClick()
    {
        var (ctx, personId, _, _) = await SeededAsync();
        using var owned = ctx;
        var proposal = (await ProposeAsync(Build(ctx), Leader(), Followup(personId) with { Agents = ["Raven"] })).Proposal!;
        var messageId = await StoreAsync(ctx, proposal);
        await using (var db = ctx.NewContext())
        {
            (await db.Users.SingleAsync(u => u.Id == Colleague)).Status = AgentStatus.Terminated;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(ctx).ExecuteAsync(messageId, proposal.Id, Leader()));

        Assert.True((await StoredAsync(ctx, messageId)).IsOpen);
    }

    [Fact]
    public async Task TheCard_ShowsEveryTextTheClickWrites()
    {
        var (ctx, personId, _, factionId) = await SeededAsync();
        using var owned = ctx;
        var svc = Build(ctx);
        NooseiProposalDraft[] drafts =
        [
            Followup(personId) with { Text = new string('n', NooseiProposalRules.MaxNote) },
            new(NooseiProposalKind.Job) { Title = "Observation", Text = new string('x', NooseiProposalRules.MaxDescription) },
            new(NooseiProposalKind.Link)
            {
                Type = "Person", Id = personId, TargetType = "Fraktion", TargetId = factionId,
                Text = new string('l', NooseiProposalRules.MaxLabel),
            },
        ];

        foreach (var draft in drafts)
        {
            var proposal = (await ProposeAsync(svc, Leader(), draft)).Proposal!;
            foreach (var written in new[] { proposal.Args.Text, proposal.Args.Title }.OfType<string>())
            {
                Assert.Contains(proposal.Lines, l => l.Value == written);
            }
        }
    }

    [Fact]
    public async Task AJobAboutAClassifiedRecord_IsRestricted_AndSaysSo()
    {
        var (ctx, _, secretId, _) = await SeededAsync();
        using var owned = ctx;

        var result = await ProposeAsync(Build(ctx), Leader(), new NooseiProposalDraft(NooseiProposalKind.Job)
        {
            Title = "Nachhaken", TargetType = "Person", TargetId = secretId,
        });

        Assert.True(result.Proposal!.Args.Restricted);
        Assert.Contains(result.Proposal.Lines, l => l.Label == "Sichtbar für" && l.Value.StartsWith("nur"));
    }

    [Fact]
    public void Tools_HoldOnlyTheProposingSide()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NOOSE-Website", "Services", "Llm"));
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Tools"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("INooseiProposalExecutor", File.ReadAllText(file));
        }
        var proposing = File.ReadAllText(Path.Combine(root, "NooseiProposalService.cs"));
        foreach (var writer in new[] { "IFollowupService", "ITagService", "IWatchlistService", "ILinkService", "IJobService" })
        {
            Assert.DoesNotContain(writer, proposing);
        }
    }

    [Fact]
    public void TheChatPrompt_SaysNooseiOnlyProposes_AndTheLawChatKnowsNoProposals()
    {
        Assert.Contains("Du änderst nie selbst etwas", NooseiPrompts.Chat);
        Assert.Contains("vorschlag_", NooseiPrompts.Chat);
        Assert.DoesNotContain("vorschlag_", NooseiPrompts.LegalChat);
    }

    [Fact]
    public void ACallIdRepeatedInALaterRound_KeepsBothCards()
    {
        var tray = new NooseiProposalTray();
        static NooseiProposal Card(string id) => new(id, NooseiProposalKind.Watch, "t", [], new NooseiProposalArgs());

        Assert.True(tray.TryReserve());
        tray.Fill("call_0", Card("a"));
        Assert.True(tray.TryReserve());
        tray.Fill("call_0", Card("b"));

        Assert.Equal(["a", "b"], tray.For(["call_0", "call_0"]).Select(p => p.Id));
    }
}
