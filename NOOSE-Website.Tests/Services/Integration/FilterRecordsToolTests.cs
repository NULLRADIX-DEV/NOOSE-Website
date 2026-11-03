using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Abductions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Feedback;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The counting tool. A count is an assertion about the stock, so the interesting cases are the ones
/// where a number could say more than the rows are allowed to.</summary>
public sealed class FilterRecordsToolTests
{
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Real list services, not stubs: the visibility this tool relies on lives in them.</summary>
    private static FilterRecordsTool Tool(SqliteTestContext ctx, ISystemSettingService? settings = null)
    {
        var people = new PersonService(ctx.Factory, Substitute.For<IFileStorageService>(),
            Substitute.For<IProfileSuggestionService>(), Substitute.For<ICaseNumberService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(),
            Substitute.For<IPublicWantedService>());
        var factions = new FactionService(ctx.Factory, Substitute.For<ICaseNumberService>(),
            Substitute.For<IProfileSuggestionService>(), people, Substitute.For<IFactionPhotoStorageService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(),
            Substitute.For<IPublicFactionProfileService>());
        return NooseiToolHost.Filter(
            people: people, factions: factions, laws: new LawService(ctx.Factory, Substitute.For<IPublicLawService>()), settings: settings);
    }

    /// <summary>Settings that report a fixed wanted-board threshold, so auf_fahndungsliste has a rule to apply.</summary>
    private static ISystemSettingService Settings(HazardLevel threshold)
    {
        var settings = Substitute.For<ISystemSettingService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new SystemConfiguration(
            false, null, null, BannerLevels.Info, null, null, null, null, null, false,
            threshold, 5, 2, 3)));
        return settings;
    }

    private static void Seed(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.People.Add(Infrastructure.Seed.Person(id: "p-open", name: "Otto Offen", configure: p =>
        {
            p.Classification = Classification.SuspicionCase;
            p.ThreatScore = 40;
        }));
        db.People.Add(Infrastructure.Seed.Person(id: "p-plain", name: "Paula Prüf", configure: p =>
        {
            p.Classification = Classification.ReviewCase;
            p.ThreatScore = 80;
        }));
        db.People.Add(Infrastructure.Seed.Person(id: "p-secret", name: "Gerd Geheim", configure: p =>
        {
            p.Classification = Classification.SuspicionCase;
            p.IsClassified = true;
            p.ThreatScore = 90;
        }));
        db.SaveChanges();
    }

    [Fact]
    public async Task Count_LeavesOutWhatTheAskerMayNotSee()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var tool = Tool(ctx);
        var args = Args("""{"typ":"Person","einstufung":"Verdachtsfall","nur_anzahl":true}""");

        var junior = await tool.InvokeAsync(args, NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(args, NooseiToolContext.From(Leader()));

        // the classified suspicion case must not show up as a +1 either — a count is an answer too
        Assert.StartsWith("1 Person entspricht", junior.Text);
        Assert.StartsWith("2 Personen", leader.Text);
        Assert.DoesNotContain("Gerd Geheim", junior.Text);
    }

    [Fact]
    public async Task CountOnly_MatchesTheNumberOfListedRecords()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var tool = Tool(ctx);

        var listed = await tool.InvokeAsync(Args("""{"typ":"Person"}"""), NooseiToolContext.From(Leader()));
        var counted = await tool.InvokeAsync(Args("""{"typ":"Person","nur_anzahl":true}"""), NooseiToolContext.From(Leader()));

        Assert.Equal(3, listed.Text.Split('•').Length - 1);
        Assert.StartsWith("3 Personen", counted.Text);
        Assert.DoesNotContain("•", counted.Text);
    }

    [Fact]
    public async Task ScoreRange_Filters()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","score_min":70}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Paula Prüf", result.Text);
        Assert.Contains("Gerd Geheim", result.Text);
        Assert.DoesNotContain("Otto Offen", result.Text);
    }

    [Fact]
    public async Task Rows_AreLabelledInGerman()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","einstufung":"Prüffall"}"""), NooseiToolContext.From(Leader()));

        // the model copies names verbatim, so a raw enum identifier would end up in the answer
        Assert.Contains("Prüffall", result.Text);
        Assert.Contains("Lebensstatus: Lebend", result.Text);
        Assert.DoesNotContain("ReviewCase", result.Text);
        Assert.DoesNotContain("Alive", result.Text);
    }

    [Fact]
    public async Task Matches_CarryRefs_SoTheAnswerCanCiteThem()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","score_min":80}"""), NooseiToolContext.From(Leader()));

        Assert.NotNull(result.Refs);
        Assert.Equal(2, result.Refs!.Count);
        Assert.All(result.Refs, r => Assert.Equal(nameof(NOOSE_Website.Data.Entities.People.Person), r.Kind));
    }

    [Fact]
    public async Task ChangedWithin_FindsARecordThatWasNeverEditedSinceItWasCreated()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            // the audit interceptor stamps ModifiedAt only on an update, so a fresh record has none at all
            db.People.Add(Infrastructure.Seed.Person(id: "p-neu", name: "Neu Angelegt", configure: p =>
            {
                p.CreatedAt = DateTime.UtcNow.AddDays(-2);
                p.ModifiedAt = null;
            }));
            db.People.Add(Infrastructure.Seed.Person(id: "p-alt", name: "Alt Vergessen", configure: p =>
            {
                p.CreatedAt = DateTime.UtcNow.AddDays(-400);
                p.ModifiedAt = DateTime.UtcNow.AddDays(-300);
            }));
            await db.SaveChangesAsync();
        }

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","geaendert_seit_tagen":7}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Neu Angelegt", result.Text);
        Assert.DoesNotContain("Alt Vergessen", result.Text);
    }

    [Fact]
    public async Task SingleMatch_IsCountedInGrammaticalGerman()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","einstufung":"Prüffall","nur_anzahl":true}"""), NooseiToolContext.From(Leader()));

        Assert.StartsWith("1 Person entspricht", result.Text);
    }

    [Fact]
    public async Task WantedOnly_FiltersAndCarriesTheReason()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Infrastructure.Seed.Person(id: "p-wanted", name: "Willi Wanted", configure: p =>
            {
                p.IsWanted = true;
                p.WantedReason = "Bewaffneter Raubüberfall";
            }));
            db.People.Add(Infrastructure.Seed.Person(id: "p-quiet", name: "Ruhiger Rudi"));
            await db.SaveChangesAsync();
        }

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","nur_fahndung":true}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Willi Wanted", result.Text);
        Assert.Contains("Zur Fahndung: Bewaffneter Raubüberfall", result.Text);
        Assert.DoesNotContain("Ruhiger Rudi", result.Text);
        // the applied filter is repeated, so a bare count cannot be read as a different question's answer
        Assert.Contains("nur zur Fahndung ausgeschrieben", result.Text);
    }

    [Fact]
    public async Task WantedOnly_StaysInsideTheAskersScope()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Infrastructure.Seed.Person(id: "p-secret-wanted", name: "Gerd Geheim", configure: p =>
            {
                p.IsClassified = true;
                p.IsWanted = true;
            }));
            await db.SaveChangesAsync();
        }

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Person","nur_fahndung":true,"nur_anzahl":true}"""), NooseiToolContext.From(Junior()));

        Assert.StartsWith("0 Personen", result.Text);
        Assert.DoesNotContain("Gerd Geheim", result.Text);
    }

    [Fact]
    public async Task OnWantedBoard_IncludesTheAutomaticEntriesTheManualFilterMisses()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            // manually wanted, low score — on the board by the flag
            db.People.Add(Infrastructure.Seed.Person(id: "p-manual", name: "Manni Manuell", configure: p =>
            {
                p.IsWanted = true;
                p.ThreatScore = 10;
            }));
            // never manually flagged, but a critical score — on the board by the threshold, the case nur_fahndung misses
            db.People.Add(Infrastructure.Seed.Person(id: "p-auto", name: "Auto Achtzig", configure: p => p.ThreatScore = 90));
            // low score, not wanted — off the board
            db.People.Add(Infrastructure.Seed.Person(id: "p-off", name: "Otto Ohne", configure: p => p.ThreatScore = 20));
            await db.SaveChangesAsync();
        }
        var tool = Tool(ctx, Settings(HazardLevel.Critical));

        var manual = await tool.InvokeAsync(
            Args("""{"typ":"Person","nur_fahndung":true}"""), NooseiToolContext.From(Leader()));
        var board = await tool.InvokeAsync(
            Args("""{"typ":"Person","auf_fahndungsliste":true}"""), NooseiToolContext.From(Leader()));

        // nur_fahndung is the manual flag only — it never sees the automatic entry
        Assert.Contains("Manni Manuell", manual.Text);
        Assert.DoesNotContain("Auto Achtzig", manual.Text);

        // auf_fahndungsliste is the whole Fahndung page: manual and score-driven, but not the quiet one
        Assert.Contains("Manni Manuell", board.Text);
        Assert.Contains("Auto Achtzig", board.Text);
        Assert.DoesNotContain("Otto Ohne", board.Text);
    }

    [Fact]
    public async Task Feedback_IsEnumerated_ThroughTheInbox()
    {
        using var ctx = new SqliteTestContext();
        var feedback = Substitute.For<IFeedbackService>();
        feedback.GetInboxAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FeedbackRow>>(new[]
            {
                new FeedbackRow("f1", FeedbackKind.Bug, FeedbackStatus.New, null, null,
                    "Absturz beim Speichern", "a1", "Falke", DateTime.UtcNow, null, null, null),
            }));

        var result = await NooseiToolHost.Filter(feedback: feedback)
            .InvokeAsync(Args("""{"typ":"Feedback"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Bug-Meldung", result.Text);
        Assert.Contains("Falke", result.Text);
        Assert.Contains("Absturz beim Speichern", result.Text);
    }

    [Fact]
    public async Task TrainingModules_AreEnumerated()
    {
        using var ctx = new SqliteTestContext();
        var training = Substitute.For<ITrainingModuleService>();
        training.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TrainingModule> { new() { Id = "m1", Name = "Nahkampf", IsActive = true } }));

        var result = await NooseiToolHost.Filter(trainingModules: training)
            .InvokeAsync(Args("""{"typ":"Ausbildungsmodul"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("Nahkampf", result.Text);
    }

    [Fact]
    public async Task Abductions_AreEnumeratedWithTheirOutcome()
    {
        using var ctx = new SqliteTestContext();
        var abductions = Substitute.For<IAbductionService>();
        abductions.GetListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<AbductionDisplay>
            {
                new(new AgentAbduction
                {
                    Id = "a1", CaseNumber = "FIB-E-2026-0001", Outcome = AbductionOutcome.Killed,
                    Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                }, "Falke", "Ganoven-Gerd", null, 0),
            }));

        var result = await NooseiToolHost.Filter(abductions: abductions)
            .InvokeAsync(Args("""{"typ":"Entführung"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("Entführung Falke", result.Text);
        Assert.Contains("Getötet", result.Text);
        Assert.Contains("Ganoven-Gerd", result.Text);
    }

    [Fact]
    public async Task SituationReports_AreLeadershipOnly()
    {
        using var ctx = new SqliteTestContext();
        var reports = Substitute.For<ISituationReportService>();
        reports.GetArchiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<SituationReportHead>
            {
                new("r1", 2026, 1, "Lagebericht Januar", DateTime.UtcNow, "Falke"),
            }));
        var tool = NooseiToolHost.Filter(situationReports: reports);

        var junior = await tool.InvokeAsync(Args("""{"typ":"Lagebericht"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(Args("""{"typ":"Lagebericht"}"""), NooseiToolContext.From(Leader()));

        // gated exactly like the record: a plain agent sees a clean zero, never the report
        Assert.StartsWith("0 Lageberichte", junior.Text);
        Assert.DoesNotContain("Januar", junior.Text);
        Assert.Contains("Lagebericht Januar", leader.Text);
    }

    [Fact]
    public async Task UnknownType_IsRefusedWithoutTouchingAService()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Hausmeister"}"""), NooseiToolContext.From(Leader()));

        Assert.True(result.IsError);
    }
}
