using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Threat;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The tools added to close the coverage gaps. Each one reaches a stock NOOSEI could not name before, so
/// the tests are about what still must not travel with it.</summary>
public sealed class NooseiCoverageToolTests
{
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A complete search envelope around the given groups.</summary>
    private static SearchResults Results(params SearchResultGroup[] groups)
        => new(groups, groups.Sum(g => g.Hit.Count), groups.Select(g => g.Category).ToList(), [],
            groups.Length, TimeSpan.Zero);

    // ---- lies_kalender ----

    private static CalendarEntry Entry(
        string id, string title, CalendarSource source, int inDays = 1,
        bool obsolete = false, string? type = null, string? entityId = null)
        => new(id, title, DateTime.Now.Date.AddDays(inDays).AddHours(14), null, false, source,
            null, obsolete, type, entityId);

    private static ICalendarService Calendar(params CalendarEntry[] entries)
    {
        var calendar = Substitute.For<ICalendarService>();
        calendar.GetEntriesAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CalendarMode>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalendarEntry>>(entries));
        return calendar;
    }

    [Fact]
    public async Task ReadCalendar_GroupsBySourceAndNamesEachKindInGerman()
    {
        var tool = new ReadCalendarTool(Calendar(
            Entry("bes:1", "Lagebesprechung", CalendarSource.Meeting, type: "Meeting", entityId: "m1"),
            Entry("auf:1", "Bericht abgeben", CalendarSource.Job, inDays: 2, type: "Job", entityId: "j1")));

        var result = await tool.InvokeAsync(Args("{}"), NooseiToolContext.From(Leader()));

        Assert.Contains("Besprechungen (1)", result.Text);
        Assert.Contains("Aufgaben (fällig) (1)", result.Text);
        Assert.Contains("Lagebesprechung", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ReadCalendar_AsksTheServiceForTheWindowAndTheScope()
    {
        var calendar = Calendar();
        var tool = new ReadCalendarTool(calendar);

        await tool.InvokeAsync(Args("""{"tage":30,"umfang":"meine"}"""), NooseiToolContext.From(Junior()));

        await calendar.Received(1).GetEntriesAsync(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Any<ClaimsPrincipal>(),
            CalendarMode.My,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadCalendar_StartsAtLocalMidnight_SoTodayIsNotHalfMissing()
    {
        DateTime from = default, until = default;
        var calendar = Substitute.For<ICalendarService>();
        calendar.GetEntriesAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CalendarMode>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                from = call.ArgAt<DateTime>(0);
                until = call.ArgAt<DateTime>(1);
                return Task.FromResult<IReadOnlyList<CalendarEntry>>([]);
            });

        await new ReadCalendarTool(calendar).InvokeAsync(Args("""{"tage":7}"""), NooseiToolContext.From(Leader()));

        Assert.Equal(DateTime.Now.Date.ToUniversalTime(), from);
        Assert.Equal(7, (until - from).TotalDays);
    }

    [Fact]
    public async Task ReadCalendar_ReferencesOnlyEntriesThatCarryARecord()
    {
        // a followup on a classified parent arrives without type and id; it must not turn into a chip
        var tool = new ReadCalendarTool(Calendar(
            Entry("wv:1", "Wiedervorlage fällig", CalendarSource.Followup),
            Entry("op:1", "Zugriff", CalendarSource.Operation, type: "Operation", entityId: "o1")));

        var result = await tool.InvokeAsync(Args("{}"), NooseiToolContext.From(Leader()));

        var reference = Assert.Single(result.Refs!);
        Assert.Equal("Operation", reference.Kind);
        Assert.Equal("o1", reference.Id);
    }

    [Fact]
    public async Task ReadCalendar_DropsCancelledEntriesAndSaysSoWhenNothingIsLeft()
    {
        var tool = new ReadCalendarTool(Calendar(
            Entry("bes:1", "Abgesagt", CalendarSource.Meeting, obsolete: true)));

        var result = await tool.InvokeAsync(Args("""{"tage":3}"""), NooseiToolContext.From(Leader()));

        Assert.DoesNotContain("Abgesagt", result.Text);
        Assert.Contains("3 Tage", result.Text);
        Assert.False(result.IsError);
    }

    // ---- erklaere_bedrohungsscore ----

    private static string DetailJson() => JsonSerializer.Serialize(new ThreatScoreDetail
    {
        PartialScores =
        [
            new ThreatPartialScore("S1 Struktur", 34, 12.5, 20, ["Mitglied: Gerd Geheim", "Konflikt: Vagos"]),
        ],
        Content = 42.5,
        ClassificationName = "Verdachtsfall",
        Base = 50,
        BandHint = "Band 50–74",
        Score = 63,
        Confidence = 71,
        CalculatedAtUtc = new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc),
    }, ThreatScoreService.JsonOptions);

    private static void SeedScored(SqliteTestContext ctx, string? detailJson = null)
    {
        using var db = ctx.NewContext();
        db.Factions.Add(Seed.Faction(id: "f-open", name: "Ballas", configure: f =>
        {
            f.ThreatScore = 63;
            f.ThreatConfidence = 71;
            f.ThreatDetailJson = detailJson;
        }));
        db.Factions.Add(Seed.Faction(id: "f-secret", name: "Geheimbund", configure: f =>
        {
            f.IsClassified = true;
            f.ThreatScore = 90;
            f.ThreatDetailJson = detailJson;
        }));
        db.SaveChanges();
    }

    [Fact]
    public async Task ExplainThreatScore_KeepsTheNumbersButWithholdsTheDriversFromATrainee()
    {
        using var ctx = new SqliteTestContext();
        SeedScored(ctx, DetailJson());
        var tool = new ExplainThreatScoreTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Fraktion","id":"f-open"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("S1 Struktur", result.Text);
        Assert.Contains("12,5 von 20", result.Text);
        Assert.Contains("Mindestband 50", result.Text);
        // the drivers name other records, and they were written against nobody's scope
        Assert.DoesNotContain("Gerd Geheim", result.Text);
        Assert.DoesNotContain("Vagos", result.Text);
        Assert.Contains("Führung vorbehalten", result.Text);
    }

    [Fact]
    public async Task ExplainThreatScore_ShowsTheDriversToLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedScored(ctx, DetailJson());
        var tool = new ExplainThreatScoreTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Fraktion","id":"f-open"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Treiber: Mitglied: Gerd Geheim; Konflikt: Vagos", result.Text);
        Assert.DoesNotContain("Führung vorbehalten", result.Text);
    }

    [Fact]
    public async Task ExplainThreatScore_GivesTheSameAnswer_ForMissingAndForbidden()
    {
        using var ctx = new SqliteTestContext();
        SeedScored(ctx, DetailJson());
        var tool = new ExplainThreatScoreTool(ctx.Factory);

        var forbidden = await tool.InvokeAsync(
            Args("""{"typ":"Fraktion","id":"f-secret"}"""), NooseiToolContext.From(Junior()));
        var missing = await tool.InvokeAsync(
            Args("""{"typ":"Fraktion","id":"gibt-es-nicht"}"""), NooseiToolContext.From(Junior()));

        Assert.Equal(missing.Text, forbidden.Text);
        Assert.DoesNotContain("Geheimbund", forbidden.Text);
    }

    [Fact]
    public async Task ExplainThreatScore_RefusesATypeThatCarriesNoScore()
    {
        using var ctx = new SqliteTestContext();
        var tool = new ExplainThreatScoreTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Vorgang","id":"v1"}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
        // a wrong type reveals nothing about any record, so it does not borrow the not-found wording
        Assert.Contains("nur Personen und Fraktionen", result.Text);
        Assert.DoesNotContain("nicht sichtbar", result.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{kein json")]
    public async Task ExplainThreatScore_SaysThereIsNothingToExplain_WithoutAUsableBreakdown(string? detailJson)
    {
        using var ctx = new SqliteTestContext();
        SeedScored(ctx, detailJson);
        var tool = new ExplainThreatScoreTool(ctx.Factory);

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Fraktion","id":"f-open"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("keine Score-Berechnung", result.Text);
        // the record itself was reachable, so it stays a source
        Assert.Single(result.Refs!);
    }

    // ---- meine_akten ----

    [Fact]
    public async Task MyRecords_DropsInaccessibleEntriesWithoutCountingThem()
    {
        var watchlist = Substitute.For<IWatchlistService>();
        watchlist.GetFollowedResolvedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<FollowedRecord>
            {
                new("Person", "p1", "Otto Offen", "/personen/p1", new DateTime(2026, 7, 1), true),
                new("Person", "p2", "(nicht mehr zugänglich)", null, new DateTime(2026, 7, 2), false),
            }));

        var result = await new MyRecordsTool(watchlist)
            .InvokeAsync(Args("{}"), NooseiToolContext.From(Junior()));

        Assert.Contains("Beobachtungsliste (1)", result.Text);
        Assert.Contains("Person | Otto Offen", result.Text);
        Assert.Contains("id=p1", result.Text);
        // a watchlist keeps what an agent once had access to; naming or counting the rest is an existence oracle
        Assert.DoesNotContain("nicht mehr zugänglich", result.Text);
        Assert.DoesNotContain("p2", result.Text);
        Assert.Single(result.Refs!);
    }

    [Fact]
    public async Task MyRecords_WithholdsTheIdOfATypeLiesAkteCannotOpen()
    {
        var watchlist = Substitute.For<IWatchlistService>();
        watchlist.GetFollowedResolvedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<FollowedRecord>
            {
                new("PersonDoc", "d1", "Vernehmung Mustermann", "/personen/p1?tab=doks", new DateTime(2026, 7, 1), true),
            }));

        var result = await new MyRecordsTool(watchlist)
            .InvokeAsync(Args("{}"), NooseiToolContext.From(Junior()));

        // a dok is read through the person it sits in, so it never carries an id of its own
        Assert.Contains("Personen-Dok | Vernehmung Mustermann", result.Text);
        Assert.DoesNotContain("id=d1", result.Text);
    }

    [Fact]
    public async Task MyRecords_SaysSoWhenTheListIsEmpty()
    {
        var watchlist = Substitute.For<IWatchlistService>();
        watchlist.GetFollowedResolvedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<FollowedRecord>()));

        var result = await new MyRecordsTool(watchlist)
            .InvokeAsync(Args("{}"), NooseiToolContext.From(Junior()));

        Assert.Contains("Keine Akten auf der Beobachtungsliste", result.Text);
        Assert.False(result.IsError);
    }

    // ---- laws as a readable record ----

    [Fact]
    public async Task ReadRecord_OpensALaw_WhichSearchCouldOnlyNameBefore()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(new Law
            {
                Id = "l1",
                LawBook = "StGB",
                Paragraph = "§ 52",
                Title = "Unerlaubter Waffenbesitz",
                Text = "Wer eine Waffe ohne Erlaubnis führt …",
                Sentence = "Freiheitsstrafe bis zu drei Jahren",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var tool = new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>());

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Gesetz","id":"l1"}"""), NooseiToolContext.From(Junior()));

        Assert.False(result.IsError);
        Assert.Contains("Gesetzbuch: StGB", result.Text);
        Assert.Contains("Unerlaubter Waffenbesitz", result.Text);
        Assert.Contains("Freiheitsstrafe", result.Text);
        Assert.Contains("Wer eine Waffe", result.Text);
        var reference = Assert.Single(result.Refs!);
        Assert.Equal("Law", reference.Kind);
    }

    [Fact]
    public async Task FilterRecords_ListsLawsWithoutInventingAClassification()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(new Law
            {
                Id = "l1", LawBook = "StGB", Paragraph = "§ 52", Title = "Waffenbesitz", Text = "…",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var tool = NooseiToolHost.Filter(laws: new LawService(ctx.Factory, Substitute.For<IPublicLawService>()));

        var result = await tool.InvokeAsync(
            Args("""{"typ":"Gesetz"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("§ 52 Waffenbesitz", result.Text);
        Assert.Contains("Gesetzbuch: StGB", result.Text);
        // a law has no classification at all; "Unbekannt" would read like one nobody set
        Assert.DoesNotContain("Einstufung", result.Text);
    }

    [Fact]
    public async Task FilterRecords_NamesWhatItCanEnumerate_WhenAskedForSomethingElse()
    {
        using var ctx = new SqliteTestContext();
        var tool = NooseiToolHost.Filter(laws: new LawService(ctx.Factory, Substitute.For<IPublicLawService>()));

        // an appointment is readable but has no plain list service; lies_kalender is what answers about it
        var result = await tool.InvokeAsync(
            Args("""{"typ":"Termin"}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
        Assert.Contains("Person", result.Text);
        Assert.DoesNotContain("Termin", result.Text);
    }

    // ---- keywords and mention refs ----

    [Fact]
    public async Task SearchRecords_TranslatesKeywordsToTagIds_AndReportsTheOnesThatDoNotExist()
    {
        SearchCriteria? criteria = null;
        var search = Substitute.For<ISearchService>();
        search.SearchAsync(Arg.Any<SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                criteria = call.ArgAt<SearchCriteria>(0);
                return Task.FromResult(SearchResults.None);
            });
        var tags = Substitute.For<ITagService>();
        tags.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Tag> { new() { Id = "t1", Name = "Waffenhandel" } }));

        var result = await new SearchRecordsTool(search, tags).InvokeAsync(
            Args("""{"suchtext":"x","stichworte":["waffenhandel","gibtesnicht"]}"""),
            NooseiToolContext.From(Leader()));

        Assert.Equal(["t1"], criteria!.TagIds);
        // silently dropping it would let the model read the result as "no record carries this keyword"
        Assert.Contains("gibtesnicht", result.Text);
    }

    [Fact]
    public async Task SearchRecords_RefusesATypeItCannotSearch_RatherThanSearchingEverything()
    {
        var search = Substitute.For<ISearchService>();
        var tool = new SearchRecordsTool(search, Substitute.For<ITagService>());

        // every catalog category is searchable now, so the case left is the one that matters most: a kind the
        // model made up. Searching everything instead would answer a question nobody asked.
        var result = await tool.InvokeAsync(
            Args("""{"suchtext":"x","typen":["Hausmeister"]}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
        Assert.Contains("nicht durchsuchbar", result.Text);
        await search.DidNotReceive().SearchAsync(
            Arg.Any<SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchRecords_SaysSoWhenOnlySomeOfTheTypesWereApplied()
    {
        var search = Substitute.For<ISearchService>();
        search.SearchAsync(Arg.Any<SearchCriteria>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Results(
                new SearchResultGroup("Person", "Personen",
                    [new SearchHit("Person", "p1", "Otto Offen", "", "FIB-P-2026-0001")]))));

        var result = await new SearchRecordsTool(search, Substitute.For<ITagService>()).InvokeAsync(
            Args("""{"suchtext":"Otto","typen":["Person","Hausmeister"]}"""), NooseiToolContext.From(Leader()));

        // without the note the model reads "one person, no caretakers" out of a search that never looked at them
        Assert.Contains("Otto Offen", result.Text);
        Assert.Contains("nicht durchsuchbar und wurden weggelassen", result.Text);
    }

    [Fact]
    public async Task ResolveMention_ReportsTheResolvedRecordsAsSources()
    {
        var id = Guid.NewGuid().ToString();
        var hidden = Guid.NewGuid().ToString();
        var mentions = Substitute.For<IMentionService>();
        mentions.ResolveAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<PartnerAgency?>())
            .Returns(Task.FromResult<IReadOnlyList<MentionSegment>>(
            [
                new(true, "Otto Offen", "Person", $"/personen/{id}"),
                new(false, " und "),
                new(true, "Verschlusssache", "Person", null, Hidden: true),
            ]));

        var result = await new ResolveMentionTool(mentions).InvokeAsync(
            Args($$"""{"text":"@{Person:{{id}}} und @{Person:{{hidden}}}"}"""),
            NooseiToolContext.From(Junior()));

        // the withheld mention has no link, so it produces no source either
        var reference = Assert.Single(result.Refs!);
        Assert.Equal("Person", reference.Kind);
        Assert.Equal(id, reference.Id);
        Assert.Equal("Otto Offen", reference.Name);
    }
}
