using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Handbook;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The tool that answers an operating question out of the handbook instead of guessing at it.</summary>
public sealed class HandbookLookupToolTests
{
    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.Trainee).WithCodename("Wren").Build();

    private static NooseiToolContext Context() => NooseiToolContext.From(Agent());

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HandbookLookupTool NewTool(SqliteTestContext ctx)
        => new(new HandbookService(ctx.Factory, new MemoryCache(new MemoryCacheOptions())));

    /// <summary>One visible chapter with one article, plus one term. Flags let a test hide any of them.</summary>
    private static void Seed(
        SqliteTestContext ctx,
        bool chapterVisible = true,
        bool articleVisible = true,
        bool termVisible = true)
    {
        using var db = ctx.NewContext();
        db.HandbuchKapitel.Add(new HandbookChapter
        {
            Id = "k1", Slug = "fahndung", Title = "Fahndung", Description = "Nach außen.",
            SortOrder = 0, IsVisible = chapterVisible,
        });
        db.HandbuchArtikel.Add(new HandbookArticle
        {
            Id = "a1", ChapterId = "k1", Slug = "fahndung-ausschreiben",
            Title = "Eine Fahndung ausschreiben",
            Summary = "Vom Entwurf zur Veröffentlichung.",
            ContentHtml = "<p>Ab Senior Special Agent veröffentlichst du selbst.</p>",
            RoleplayHtml = "<p>Nach außen tritt die Behörde auf, nicht der Agent.</p>",
            SortOrder = 0, IsVisible = articleVisible,
        });
        db.HandbuchSchritte.Add(new HandbookStep
        {
            Id = "s1", ArticleId = "a1", Number = 1, Title = "Entwurf schreiben",
            Text = "Vorwurf in einem Satz.",
        });
        db.HandbuchBegriffe.Add(new GlossaryTerm
        {
            Id = "t1", Term = "Prüffall", ShortDefinition = "Die unterste Einstufung.",
            Synonyms = "Pruefung", IsVisible = termVisible,
        });
        db.SaveChanges();
    }

    // --- what it answers --------------------------------------------------

    [Fact]
    public async Task A_question_about_a_procedure_returns_the_article_with_its_body()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie schreibe ich eine Fahndung aus?" }"""), Context());

        Assert.False(result.IsError);
        Assert.Contains("Eine Fahndung ausschreiben", result.Text, StringComparison.Ordinal);
        Assert.Contains("Ab Senior Special Agent", result.Text, StringComparison.Ordinal);
    }

    /// <summary>The walkthrough is the part an agent actually follows; leaving it out halves the answer.</summary>
    [Fact]
    public async Task The_walkthrough_and_the_roleplay_box_come_along()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Fahndung ausschreiben" }"""), Context());

        Assert.Contains("Entwurf schreiben", result.Text, StringComparison.Ordinal);
        Assert.Contains("Im Rollenspiel", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_about_a_word_returns_the_glossary_entry()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Was bedeutet Prüffall?" }"""), Context());

        Assert.Contains("Prüffall", result.Text, StringComparison.Ordinal);
        Assert.Contains("Die unterste Einstufung.", result.Text, StringComparison.Ordinal);
    }

    /// <summary>The chips under the answer have to lead somewhere: an article by slug, a term by id.</summary>
    [Fact]
    public async Task The_sources_carry_what_the_route_needs()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Fahndung und Prüffall" }"""), Context());

        Assert.NotNull(result.Refs);
        Assert.Contains(result.Refs!, r => r.Kind == nameof(HandbookArticle) && r.Id == "fahndung-ausschreiben");
        Assert.Contains(result.Refs!, r => r.Kind == nameof(GlossaryTerm) && r.Id == "t1");
    }

    // --- what it must not answer ------------------------------------------

    /// <summary>Saying so is the point: an invented button is worse than an admitted gap.</summary>
    [Fact]
    public async Task Nothing_in_the_handbook_says_so_plainly_and_is_not_an_error()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie starte ich einen Hubschrauber?" }"""), Context());

        Assert.False(result.IsError);
        Assert.Contains("nichts im Handbuch", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_question_is_an_error()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(Args("""{ "max": 3 }"""), Context());

        Assert.True(result.IsError);
    }

    /// <summary>Without the filler list "Wie mache ich das?" would score every article that contains "eine".</summary>
    [Fact]
    public async Task A_question_made_only_of_filler_is_refused()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie kann man das eigentlich?" }"""), Context());

        Assert.True(result.IsError);
    }

    // --- the gate is the service's, and it holds --------------------------

    [Fact]
    public async Task A_hidden_article_is_not_answered_from()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, articleVisible: false);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie schreibe ich eine Fahndung aus?" }"""), Context());

        Assert.DoesNotContain("Ab Senior Special Agent", result.Text, StringComparison.Ordinal);
    }

    /// <summary>A hidden chapter takes its articles with it, exactly as the page shows them.</summary>
    [Fact]
    public async Task A_hidden_chapter_takes_its_article_with_it()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, chapterVisible: false);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie schreibe ich eine Fahndung aus?" }"""), Context());

        Assert.DoesNotContain("Ab Senior Special Agent", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hidden_term_is_not_answered_from()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, termVisible: false);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Was bedeutet Prüffall?" }"""), Context());

        Assert.DoesNotContain("Die unterste Einstufung.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_soft_deleted_article_is_not_answered_from()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        using (var db = ctx.NewContext())
        {
            var row = db.HandbuchArtikel.Single(a => a.Id == "a1");
            row.IsDeleted = true;
            db.SaveChanges();
        }

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Wie schreibe ich eine Fahndung aus?" }"""), Context());

        Assert.DoesNotContain("Ab Senior Special Agent", result.Text, StringComparison.Ordinal);
    }

    // --- the contract -----------------------------------------------------

    /// <summary>The glossary's own abbreviations are shorter than a word: TRU, HRB, VS, Dok.</summary>
    [Theory]
    [InlineData("Was ist die TRU?")]
    [InlineData("Was bedeutet VS?")]
    public async Task A_short_abbreviation_is_still_searchable(string frage)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.HandbuchBegriffe.Add(new GlossaryTerm
            {
                Id = "t9", Term = "TRU", ShortDefinition = "Tactical Response Unit.",
                Synonyms = "VS", IsVisible = true,
            });
            db.SaveChanges();
        }

        var result = await NewTool(ctx).InvokeAsync(
            Args($$"""{ "frage": "{{frage}}" }"""), Context());

        Assert.Contains("Tactical Response Unit.", result.Text, StringComparison.Ordinal);
    }

    /// <summary>A body can fill the whole budget; a definition trimmed away with its chip left behind is worse.</summary>
    [Fact]
    public async Task The_glossary_is_written_before_the_articles()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Fahndung und Prüffall" }"""), Context());

        var glossary = result.Text.IndexOf("Glossar:", StringComparison.Ordinal);
        var article = result.Text.IndexOf("Artikel:", StringComparison.Ordinal);
        Assert.True(glossary >= 0 && article > glossary, result.Text);
    }

    /// <summary>The chips are a handful of places to look, not a bibliography.</summary>
    [Fact]
    public async Task The_sources_are_capped()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 20; i++)
            {
                db.HandbuchBegriffe.Add(new GlossaryTerm
                {
                    Id = $"t{i}", Term = $"Fahndung{i}", ShortDefinition = "Eine Ausschreibung.",
                    IsVisible = true,
                });
            }
            db.SaveChanges();
        }

        var result = await NewTool(ctx).InvokeAsync(
            Args("""{ "frage": "Fahndung", "max": 40 }"""), Context());

        Assert.NotNull(result.Refs);
        Assert.True(result.Refs!.Count <= 8, $"{result.Refs.Count} Quellen");
    }

    [Fact]
    public void The_tool_declares_its_name_and_requires_a_question()
    {
        using var ctx = new SqliteTestContext();
        var tool = NewTool(ctx);

        Assert.Equal("schlage_nach", tool.Name);
        var required = tool.ParameterSchema.GetProperty("required").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        Assert.Contains("frage", required);
    }
}
