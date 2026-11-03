using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Graph;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Every NOOSEI tool answers within the asking agent's own scope. These tests are about what a tool must NOT return.</summary>
public sealed class NooseiToolTests
{
    private const string OpenPerson = "p-open";
    private const string SecretPerson = "p-secret";
    private const string FactionId = "f1";

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    /// <summary>A complete search envelope around the given groups.</summary>
    private static NOOSE_Website.Models.Common.SearchResults Results(
        params NOOSE_Website.Models.Common.SearchResultGroup[] groups)
        => new(groups, groups.Sum(g => g.Hit.Count), groups.Select(g => g.Category).ToList(), [],
            groups.Length, TimeSpan.Zero);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void Seed(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.People.Add(NOOSE_Website.Tests.Infrastructure.Seed.Person(id: OpenPerson, name: "Otto Offen"));
        db.People.Add(NOOSE_Website.Tests.Infrastructure.Seed.Person(id: SecretPerson, name: "Gerd Geheim",
            configure: p => p.IsClassified = true));
        db.Factions.Add(NOOSE_Website.Tests.Infrastructure.Seed.Faction(id: FactionId, name: "Ballas"));
        db.SaveChanges();
    }

    // ---- lies_akte ----

    [Fact]
    public async Task ReadRecord_GivesTheSameAnswer_ForMissingAndForbidden()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var tool = new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>());

        var forbidden = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}"}"""), NooseiToolContext.From(Junior()));
        var missing = await tool.InvokeAsync(
            Args("""{"typ":"Person","id":"gibt-es-nicht"}"""), NooseiToolContext.From(Junior()));

        // identical wording on purpose: anything else turns the tool into an existence oracle
        Assert.Equal(missing.Text, forbidden.Text);
        Assert.True(forbidden.IsError);
        Assert.DoesNotContain("Gerd Geheim", forbidden.Text);
    }

    [Fact]
    public async Task ReadRecord_ReturnsTheDossier_ForAVisibleRecord()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var accessLog = Substitute.For<IAccessLogService>();
        var tool = new ReadRecordTool(ctx.Factory, accessLog);

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}"}"""), NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("Otto Offen", result.Text);
        // a read through NOOSEI is still a read
        await accessLog.Received(1).LogViewAsync("Person", OpenPerson, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadRecord_LetsLeadershipSeeAClassifiedRecord()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var tool = new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>());

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Gerd Geheim", result.Text);
    }

    [Fact]
    public async Task ReadRecord_RespectsTheCompactBudget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(NOOSE_Website.Tests.Infrastructure.Seed.Person(id: "p-long", name: "Lang",
                configure: p => p.Description = new string('x', 20_000)));
            db.SaveChanges();
        }
        var tool = new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>());

        var compact = await tool.InvokeAsync(
            Args("""{"typ":"Person","id":"p-long","umfang":"kompakt"}"""), NooseiToolContext.From(Leader()));
        var full = await tool.InvokeAsync(
            Args("""{"typ":"Person","id":"p-long","umfang":"voll"}"""), NooseiToolContext.From(Leader()));

        Assert.True(compact.Text.Length < full.Text.Length);
        Assert.True(full.Text.Length <= NooseiLimits.MaxToolResultChars + 32);
    }

    // ---- zeige_verbindungen ----

    [Fact]
    public async Task ListRelated_MasksAClassifiedLink_ForANonLeader()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            db.Links.Add(new Link
            {
                SourceType = nameof(Faction), SourceId = FactionId,
                TargetType = nameof(Person), TargetId = SecretPerson, Label = "Mitglied",
            });
            db.SaveChanges();
        }
        var tool = new ListRelatedTool(ctx.Factory);

        var junior = await tool.InvokeAsync(
            Args($$"""{"typ":"Fraktion","id":"{{FactionId}}"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(
            Args($$"""{"typ":"Fraktion","id":"{{FactionId}}"}"""), NooseiToolContext.From(Leader()));

        Assert.DoesNotContain("Gerd Geheim", junior.Text);
        Assert.Contains("(Verschlusssache)", junior.Text);
        Assert.Contains("Gerd Geheim", leader.Text);
    }

    [Fact]
    public async Task ListRelated_HidesALinkedTaskforce_ForANonMember()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce
            {
                Id = "tf1", Name = "Operation Nachtfalke", CaseNumber = "FIB-TF-2026-0001",
                Scope = TaskforceScope.InternalAgency, Status = TaskforceStatus.Approved,
            });
            db.Links.Add(new Link
            {
                SourceType = nameof(Faction), SourceId = FactionId,
                TargetType = nameof(Taskforce), TargetId = "tf1",
            });
            db.SaveChanges();
        }
        var tool = new ListRelatedTool(ctx.Factory);

        var outsider = await tool.InvokeAsync(
            Args($$"""{"typ":"Fraktion","id":"{{FactionId}}"}"""), NooseiToolContext.From(Junior()));

        Assert.DoesNotContain("Operation Nachtfalke", outsider.Text);
        Assert.DoesNotContain("FIB-TF-2026-0001", outsider.Text);
    }

    [Fact]
    public async Task ListRelated_RefusesAnInvisibleRoot()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var tool = new ListRelatedTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{SecretPerson}}"}"""), NooseiToolContext.From(Junior()));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ListRelated_ShowsTheFactionsAPersonBelongsTo()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            db.FactionMembers.Add(new FactionMember
            {
                FactionId = FactionId, PersonId = OpenPerson, Rank = "Enforcer", IsLead = true,
            });
            db.SaveChanges();
        }
        var tool = new ListRelatedTool(ctx.Factory);

        // a membership is not a link row: reading only the link table made this connection invisible
        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}"}"""), NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("Zugehörigkeiten", result.Text);
        Assert.Contains("Ballas", result.Text);
        Assert.Contains("Enforcer", result.Text);
        Assert.Contains("Leitung", result.Text);
    }

    [Fact]
    public async Task ListRelated_ShowsAFactionsMembers()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            db.FactionMembers.Add(new FactionMember { FactionId = FactionId, PersonId = OpenPerson, Rank = "Soldat" });
            db.FactionMembers.Add(new FactionMember { FactionId = FactionId, PersonId = SecretPerson });
            db.SaveChanges();
        }
        var tool = new ListRelatedTool(ctx.Factory);

        var junior = await tool.InvokeAsync(
            Args($$"""{"typ":"Fraktion","id":"{{FactionId}}"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(
            Args($$"""{"typ":"Fraktion","id":"{{FactionId}}"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Mitglieder", junior.Text);
        Assert.Contains("Otto Offen", junior.Text);
        Assert.DoesNotContain("Gerd Geheim", junior.Text);
        Assert.Contains("(Verschlusssache)", junior.Text);
        Assert.Contains("Gerd Geheim", leader.Text);
    }

    [Fact]
    public async Task ListRelated_NamesTheKindOfAPersonToPersonRelation()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            db.People.Add(NOOSE_Website.Tests.Infrastructure.Seed.Person(id: "p-foe", name: "Rudi Rivale"));
            db.PersonRelations.Add(new PersonRelation
            {
                PersonAId = OpenPerson, PersonBId = "p-foe", Type = RelationType.Enemy,
            });
            db.SaveChanges();
        }
        var tool = new ListRelatedTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args($$"""{"typ":"Person","id":"{{OpenPerson}}"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("Beziehungen", result.Text);
        Assert.Contains("Feind", result.Text);
        Assert.Contains("Rudi Rivale", result.Text);
    }

    // ---- finde_verbindungsweg ----

    private static GraphNode Node(string type, string id, string name, string caseNumber)
        => new($"{type}:{id}", type, name, caseNumber, null, 0, false, null, 0);

    [Fact]
    public async Task FindPath_RendersTheChainWithTheKindOfEveryStep()
    {
        var graph = Substitute.For<IGraphService>();
        graph.FindPathAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new PathResult(true,
                [
                    Node(nameof(Person), "p1", "Max Mustermann", "FIB-P-2026-0001"),
                    Node(nameof(Faction), "f1", "Ballas", "FIB-F-2026-0001"),
                    Node(nameof(Faction), "f2", "Vagos", "FIB-F-2026-0002"),
                ],
                [
                    new GraphEdge("Person:p1", "Faction:f1", "Leitung", LinkKind.Default, true),
                    new GraphEdge("Faction:f1", "Faction:f2", null, LinkKind.Conflict, false),
                ]));
        var tool = new FindPathTool(graph);

        var result = await tool.InvokeAsync(
            Args("""{"von_typ":"Person","von_id":"p1","nach_typ":"Fraktion","nach_id":"f2"}"""),
            NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("2 Schritte", result.Text);
        Assert.Contains("Max Mustermann", result.Text);
        Assert.Contains("Leitung", result.Text);
        Assert.Contains("Konflikt", result.Text);
        Assert.Contains("Vagos", result.Text);
        // the ids come back bare, so a follow-up tool call can use them
        Assert.Contains("id=f2", result.Text);
        Assert.Equal(3, result.Refs!.Count);
    }

    [Fact]
    public async Task FindPath_SaysNothingAboutExistence_WhenNoPathIsVisible()
    {
        var graph = Substitute.For<IGraphService>();
        graph.FindPathAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new PathResult(false, [], []));
        var tool = new FindPathTool(graph);

        var result = await tool.InvokeAsync(
            Args("""{"von_typ":"Person","von_id":"p1","nach_typ":"Fraktion","nach_id":"f2"}"""),
            NooseiToolContext.From(Junior()));

        // an invisible endpoint lands here too, so the wording must not confirm or deny either record
        Assert.Contains("Kein sichtbarer Verbindungsweg", result.Text);
        Assert.DoesNotContain("existiert", result.Text);
    }

    [Fact]
    public async Task FindPath_PassesTheAskingAgentThrough()
    {
        var graph = Substitute.For<IGraphService>();
        ClaimsPrincipal? seen = null;
        graph.FindPathAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.ArgAt<ClaimsPrincipal>(4);
                return Task.FromResult(new PathResult(false, [], []));
            });
        var tool = new FindPathTool(graph);
        var junior = Junior();

        await tool.InvokeAsync(
            Args("""{"von_typ":"Person","von_id":"p1","nach_typ":"Fraktion","nach_id":"f2"}"""),
            NooseiToolContext.From(junior));

        // the graph filters on the live principal; a stored scope would answer at yesterday's rights
        Assert.Same(junior, seen);
    }

    // ---- suche_akten ----

    [Fact]
    public async Task SearchRecords_PassesTheAskingAgentsScopeThrough()
    {
        using var ctx = new SqliteTestContext();
        var search = Substitute.For<ISearchService>();
        ClaimsPrincipal? seen = null;
        search.SearchAsync(Arg.Any<NOOSE_Website.Models.Common.SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.ArgAt<ClaimsPrincipal>(1);
                return Task.FromResult(NOOSE_Website.Models.Common.SearchResults.None);
            });
        var tool = new SearchRecordsTool(search, Substitute.For<ITagService>());

        await tool.InvokeAsync(Args("""{"suchtext":"Ballas"}"""), NooseiToolContext.From(Junior()));

        Assert.NotNull(seen);
        Assert.False(seen!.MayClassifiedRead());
    }

    [Fact]
    public async Task SearchRecords_MapsGermanTypeNamesToTheSearchCategories()
    {
        using var ctx = new SqliteTestContext();
        var search = Substitute.For<ISearchService>();
        NOOSE_Website.Models.Common.SearchCriteria? criteria = null;
        search.SearchAsync(Arg.Any<NOOSE_Website.Models.Common.SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                criteria = call.ArgAt<NOOSE_Website.Models.Common.SearchCriteria>(0);
                return Task.FromResult(NOOSE_Website.Models.Common.SearchResults.None);
            });
        var tool = new SearchRecordsTool(search, Substitute.For<ITagService>());

        await tool.InvokeAsync(Args("""{"suchtext":"x","typen":["Fraktion","Vorgang"]}"""), NooseiToolContext.From(Leader()));

        Assert.Equal(["Faction", "Case"], criteria!.Categories);
    }

    [Fact]
    public async Task SearchRecords_RejectsAnEmptyQuery()
    {
        var tool = new SearchRecordsTool(Substitute.For<ISearchService>(), Substitute.For<ITagService>());

        var result = await tool.InvokeAsync(Args("""{"suchtext":"   "}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SearchRecords_WithholdsTheIdOfARecordLiesAkteCannotOpen()
    {
        var search = Substitute.For<ISearchService>();
        search.SearchAsync(Arg.Any<NOOSE_Website.Models.Common.SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Results(
                new NOOSE_Website.Models.Common.SearchResultGroup("Faction", "Fraktionen",
                    [new NOOSE_Website.Models.Common.SearchHit("Faction", "f1", "Ballas", "", "FIB-F-2026-0001")]),
                new NOOSE_Website.Models.Common.SearchResultGroup("Comment", "Kommentare",
                    [new NOOSE_Website.Models.Common.SearchHit("Comment", "c1", "Waffenlager prüfen", "", "")]))));
        var tool = new SearchRecordsTool(search, Substitute.For<ITagService>());

        var result = await tool.InvokeAsync(Args("""{"suchtext":"Waffen"}"""), NooseiToolContext.From(Leader()));

        // the faction can be opened, so it gets an id
        Assert.Contains("Fraktion | Ballas", result.Text);
        Assert.Contains("id=f1", result.Text);
        // a comment cannot: it is read through its parent, and an id here would cost the model a round to
        // discover that lies_akte rejects it
        Assert.Contains("Kommentar | Waffenlager prüfen", result.Text);
        Assert.DoesNotContain("id=c1", result.Text);
        Assert.Contains("nicht als Akte lesbar", result.Text);
        // and never the English CLR name
        Assert.DoesNotContain("Comment", result.Text);
    }

    [Fact]
    public async Task SearchRecords_KeepsRefsForRecordsItCannotOpen()
    {
        var search = Substitute.For<ISearchService>();
        search.SearchAsync(Arg.Any<NOOSE_Website.Models.Common.SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Results(
                new NOOSE_Website.Models.Common.SearchResultGroup("Job", "Aufgaben",
                    [new NOOSE_Website.Models.Common.SearchHit("Job", "j1", "Waffenlager prüfen", "", "")]))));
        var tool = new SearchRecordsTool(search, Substitute.For<ITagService>());

        var result = await tool.InvokeAsync(Args("""{"suchtext":"Waffen"}"""), NooseiToolContext.From(Leader()));

        // withholding the id from the model does not withhold the source chip from the agent
        var reference = Assert.Single(result.Refs!);
        Assert.Equal("Job", reference.Kind);
        Assert.Equal("j1", reference.Id);
    }

    // ---- registry ----

    [Fact]
    public void Registry_ExposesEveryToolOnceAndInAStableOrder()
    {
        var search = new SearchRecordsTool(Substitute.For<ISearchService>(), Substitute.For<ITagService>());
        var mention = new ResolveMentionTool(Substitute.For<IMentionService>());
        var registry = new NooseiToolRegistry([mention, search]);

        Assert.Equal(["loese_erwaehnung_auf", "suche_akten"], registry.Definitions.Select(d => d.Name).ToArray());
        Assert.NotNull(registry.Find("suche_akten"));
        Assert.Null(registry.Find("gibt_es_nicht"));
    }
}
