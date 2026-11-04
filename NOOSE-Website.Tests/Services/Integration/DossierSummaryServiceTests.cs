using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Cached structured NOOSEI briefs: the model is called only when the source changed or a regenerate is forced,
/// and an answer that is not valid against the schema is never stored.</summary>
public sealed class DossierSummaryServiceTests
{
    private const string BriefJson = """
        {"tldr":"Kernaussage der Akte.",
         "kernpunkte":["Erster Punkt","Zweiter Punkt"],
         "einstufung_bewertung":"Die Einstufung passt zur Aktenlage.",
         "verbindungen":[{"wer":"Ballas","art":"Mitglied","relevanz":"Führungsebene"}],
         "verlauf":[{"wann":"2026-07-14","was":"Festnahme"}],
         "offene_punkte":["Verbleib der Waffe ungeklärt"],
         "risiko":{"stufe":"hoch","begruendung":"Bewaffnet und einschlägig."}}
        """;

    private static NooseiAnswer Answer(string? text = BriefJson)
        => new(text, LlmUsage.Empty, new LlmQuotaCharge(0, 0m, LlmQuotaStatus.Empty, null, true), 1, [], false);

    private static INooseiGateway Gateway(string? text = BriefJson)
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Answer(text));
        return gateway;
    }

    private static DossierSummaryService Svc(SqliteTestContext ctx, INooseiGateway gateway, bool allowClassifiedEgress = false,
        LlmProvider provider = LlmProvider.OpenRouter, IMemoryCache? cache = null)
        => new(ctx.Factory, gateway, NooseiProviderStub.Returning(provider), cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new LlmOptions
            {
                Enabled = true,
                ApiKey = "k",
                Model = "test-model",
                DeepSeek = new LlmDeepSeekOptions { ApiKey = "d" },
                AllowClassifiedEgress = allowClassifiedEgress,
            }));

    /// <summary>Gateway that refuses every enforced schema, like an endpoint without structured output.</summary>
    private static INooseiGateway SchemaRefusingGateway(List<LlmResponseFormatKind?> seenFormats)
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var format = call.Arg<NooseiCall>().ResponseFormat;
                seenFormats.Add(format?.Kind);
                return format?.Kind == LlmResponseFormatKind.JsonSchema
                    ? throw new LlmCapabilityException(schemaRelated: true, toolsRelated: false)
                    : Answer();
            });
        return gateway;
    }

    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static void SeedPerson(SqliteTestContext ctx, string id, string name, Action<Person>? configure = null)
    {
        using var db = ctx.NewContext();
        db.People.Add(Seed.Person(id: id, name: name, configure: configure));
        db.SaveChanges();
    }

    private static void Rename(SqliteTestContext ctx, string id, string name)
    {
        using var db = ctx.NewContext();
        var p = db.People.First(x => x.Id == id);
        p.Name = name;
        db.SaveChanges();
    }

    private static Task<int> Calls(INooseiGateway gateway, int expected)
    {
        gateway.Received(expected).AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        return Task.FromResult(expected);
    }

    // ---- caching ----

    [Fact]
    public async Task Generate_Twice_UnchangedSource_CallsTheModelOnce()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Gateway();
        var svc = Svc(ctx, gateway);

        var v1 = await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);
        var v2 = await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        await Calls(gateway, 1);
        Assert.True(v1.Exists);
        Assert.False(v2.IsStale);
        Assert.NotNull(v2.Brief);
        Assert.Equal("Kernaussage der Akte.", v2.Brief!.Tldr);
    }

    [Fact]
    public async Task Generate_Force_CallsTheModelAgain()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Gateway();
        var svc = Svc(ctx, gateway);

        await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);
        await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: true);

        await Calls(gateway, 2);
    }

    [Fact]
    public async Task Generate_AfterContentChange_CallsTheModelAgain()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Gateway();
        var svc = Svc(ctx, gateway);

        await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);
        Rename(ctx, "p1", "Moritz Mustermann");
        await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        await Calls(gateway, 2);
    }

    [Fact]
    public async Task Get_AfterContentChange_MarksStale_WithoutAModelCall()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Gateway();
        var svc = Svc(ctx, gateway);

        await svc.GenerateAsync(nameof(Person), "p1", Leader(), force: false);
        Rename(ctx, "p1", "Moritz Mustermann");
        var view = await svc.GetAsync(nameof(Person), "p1", Leader());

        Assert.NotNull(view);
        Assert.True(view!.Exists);
        Assert.True(view.IsStale);
        await Calls(gateway, 1);
    }

    [Fact]
    public async Task Get_NoBriefYet_ReturnsNotExists()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");

        var view = await Svc(ctx, Gateway()).GetAsync(nameof(Person), "p1", Leader());

        Assert.NotNull(view);
        Assert.False(view!.Exists);
        Assert.True(view.Configured);
    }

    // ---- the structured payload ----

    [Fact]
    public async Task Generate_StoresEveryFieldOfTheSchema()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");

        var view = await Svc(ctx, Gateway()).GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        var brief = Assert.IsType<DossierBrief>(view.Brief);
        Assert.Equal(["Erster Punkt", "Zweiter Punkt"], brief.Kernpunkte);
        Assert.Equal("Die Einstufung passt zur Aktenlage.", brief.EinstufungBewertung);
        var link = Assert.Single(brief.Verbindungen);
        Assert.Equal("Ballas", link.Wer);
        var step = Assert.Single(brief.Verlauf);
        Assert.Equal("Festnahme", step.Was);
        Assert.Single(brief.OffenePunkte);
        Assert.Equal(BriefRiskLevel.Hoch, brief.Risiko.Level);
    }

    [Fact]
    public async Task Generate_StampsTheSchemaAndPromptVersion()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");

        await Svc(ctx, Gateway()).GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        await using var db = ctx.NewContext();
        var row = Assert.Single(db.DossierSummaries.ToList());
        Assert.Equal(NooseiSchemas.KurzbriefVersion, row.SchemaVersion);
        Assert.Equal(NooseiPrompts.BriefPromptVersion, row.PromptVersion);
        Assert.Equal("test-model", row.Model);
        Assert.NotNull(row.BriefJson);
    }

    [Fact]
    public async Task Generate_StoresNothing_WhenTheAnswerIsNotUsable()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx, Gateway("Tut mir leid, das kann ich nicht.")).GenerateAsync(nameof(Person), "p1", Leader(), force: false));

        await using var db = ctx.NewContext();
        Assert.Empty(db.DossierSummaries.ToList());
    }

    // ---- parsing and repair ----

    [Fact]
    public void Parse_ReadsAStrictAnswer()
    {
        var brief = DossierSummaryService.Parse(BriefJson);

        Assert.NotNull(brief);
        Assert.Equal("Kernaussage der Akte.", brief!.Tldr);
    }

    [Fact]
    public void Parse_RepairsCodeFences()
    {
        var brief = DossierSummaryService.Parse("```json\n" + BriefJson + "\n```");

        Assert.NotNull(brief);
        Assert.Equal(2, brief!.Kernpunkte.Count);
    }

    [Fact]
    public void Parse_IgnoresChatterAroundTheObject()
    {
        var brief = DossierSummaryService.Parse("Gern! Hier ist der Kurzbrief:\n" + BriefJson + "\nSoll ich mehr liefern?");

        Assert.NotNull(brief);
        Assert.Equal("Kernaussage der Akte.", brief!.Tldr);
    }

    [Fact]
    public void Parse_SurvivesBracesInsideStrings()
    {
        var brief = DossierSummaryService.Parse(
            """{"tldr":"Ein } in Anführungszeichen","kernpunkte":[],"einstufung_bewertung":"","verbindungen":[],"verlauf":[],"offene_punkte":[],"risiko":{"stufe":"mittel","begruendung":""}}""");

        Assert.NotNull(brief);
        Assert.Equal("Ein } in Anführungszeichen", brief!.Tldr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Kein JSON weit und breit.")]
    [InlineData("{ kaputt")]
    public void Parse_RejectsUnusableAnswers(string? answer)
        => Assert.Null(DossierSummaryService.Parse(answer));

    [Fact]
    public void Parse_RejectsAnEmptyButValidObject()
        => Assert.Null(DossierSummaryService.Parse(
            """{"tldr":"","kernpunkte":[],"einstufung_bewertung":"","verbindungen":[],"verlauf":[],"offene_punkte":[],"risiko":{"stufe":"mittel","begruendung":""}}"""));

    [Fact]
    public void Parse_FallsBackToMittel_ForAnUnknownRiskLevel()
    {
        var brief = DossierSummaryService.Parse(
            """{"tldr":"Etwas","kernpunkte":[],"einstufung_bewertung":"","verbindungen":[],"verlauf":[],"offene_punkte":[],"risiko":{"stufe":"katastrophal","begruendung":""}}""");

        Assert.NotNull(brief);
        Assert.Equal(BriefRiskLevel.Mittel, brief!.Risiko.Level);
    }

    // ---- the fallback ladder ----

    [Fact]
    public async Task Generate_DropsToPlainJsonMode_WhenTheSchemaIsRefused()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        var seenFormats = new List<LlmResponseFormatKind?>();
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var format = call.Arg<NooseiCall>().ResponseFormat;
                seenFormats.Add(format?.Kind);
                return format?.Kind == LlmResponseFormatKind.JsonSchema
                    ? throw new LlmCapabilityException(schemaRelated: true, toolsRelated: false)
                    : Answer();
            });

        var view = await Svc(ctx, gateway).GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        Assert.True(view.Exists);
        Assert.Contains(LlmResponseFormatKind.JsonSchema, seenFormats);
        Assert.Contains(LlmResponseFormatKind.JsonObject, seenFormats);
    }

    [Fact]
    public async Task Generate_RetriesOnceWithAWiderProviderPool()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        var required = new List<bool>();
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var noosei = call.Arg<NooseiCall>();
                required.Add(noosei.RequireCapableProviders);
                return noosei.RequireCapableProviders
                    ? throw new LlmCapabilityException(schemaRelated: true, toolsRelated: false)
                    : Answer();
            });

        await Svc(ctx, gateway).GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        Assert.Equal([true, false], required);
    }

    [Fact]
    public async Task Generate_DirectEndpoint_DoesNotRepeatTheRefusedSchema()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var seenFormats = new List<LlmResponseFormatKind?>();
        var gateway = SchemaRefusingGateway(seenFormats);

        var view = await Svc(ctx, gateway, provider: LlmProvider.DeepSeek).GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        Assert.True(view.Exists);
        Assert.Equal([LlmResponseFormatKind.JsonSchema, LlmResponseFormatKind.JsonObject], seenFormats);
    }

    [Fact]
    public async Task Generate_RemembersTheRungThatAnswered()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        SeedPerson(ctx, "p2", "Erika Musterfrau");
        var seenFormats = new List<LlmResponseFormatKind?>();
        var gateway = SchemaRefusingGateway(seenFormats);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Svc(ctx, gateway, cache: cache).GenerateAsync(nameof(Person), "p1", Leader(), force: false);
        seenFormats.Clear();
        var view = await Svc(ctx, gateway, cache: cache).GenerateAsync(nameof(Person), "p2", Leader(), force: false);

        // the second brief goes straight to the rung that worked, without a refused call first
        Assert.True(view.Exists);
        Assert.Equal([LlmResponseFormatKind.JsonObject], seenFormats);
    }

    [Fact]
    public async Task Generate_RememberedRung_IsPerUpstream()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p1", "Max Mustermann");
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Svc(ctx, SchemaRefusingGateway([]), provider: LlmProvider.DeepSeek, cache: cache)
            .GenerateAsync(nameof(Person), "p1", Leader(), force: false);

        // what DeepSeek refused says nothing about OpenRouter
        var openRouter = Gateway();
        await Svc(ctx, openRouter, cache: cache).GenerateAsync(nameof(Person), "p1", Leader(), force: true);
        await openRouter.Received(1).AskAsync(
            Arg.Is<NooseiCall>(c => c.ResponseFormat != null && c.ResponseFormat.Kind == LlmResponseFormatKind.JsonSchema),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    // ---- visibility and egress gates ----

    [Fact]
    public async Task Get_ClassifiedRecord_InvisibleToNonLeadership_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p2", "Geheim", p => p.IsClassified = true);
        var junior = ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.Trainee).Build();

        Assert.Null(await Svc(ctx, Gateway()).GetAsync(nameof(Person), "p2", junior));
    }

    [Fact]
    public async Task Generate_ClassifiedRecord_BlockedByTheEgressKillSwitch()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p3", "Geheim", p => p.IsClassified = true);
        var gateway = Gateway();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx, gateway, allowClassifiedEgress: false).GenerateAsync(nameof(Person), "p3", Leader(), force: false));

        await Calls(gateway, 0);
    }

    [Fact]
    public async Task Generate_ClassifiedRecord_AllowedByDefault()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p3", "Geheim", p => p.IsClassified = true);
        var gateway = Gateway();

        await Svc(ctx, gateway, allowClassifiedEgress: true).GenerateAsync(nameof(Person), "p3", Leader(), force: false);

        await Calls(gateway, 1);
    }

    [Fact]
    public async Task Generate_OnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        SeedPerson(ctx, "p4", "Max Mustermann");
        var onlyReader = ClaimsPrincipalBuilder.Agent("or").AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Svc(ctx, Gateway()).GenerateAsync(nameof(Person), "p4", onlyReader, force: false));
    }

    // ---- rendering helpers ----

    [Fact]
    public void Markdown_RendersFormatting()
    {
        var html = MarkdownRenderer.ToSafeHtml("**fett**\n\n- eins\n- zwei");
        Assert.Contains("<strong>fett</strong>", html);
        Assert.Contains("<li>", html);
    }

    [Fact]
    public void Markdown_DropsRawHtml()
    {
        var html = MarkdownRenderer.ToSafeHtml("Text <script>alert(1)</script> mehr");
        Assert.DoesNotContain("<script", html);
    }
}
