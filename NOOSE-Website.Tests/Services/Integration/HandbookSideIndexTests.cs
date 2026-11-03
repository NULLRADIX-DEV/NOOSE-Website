using System.Security.Claims;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Search;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>
/// A handbook hit must not arrive twice. The plain recall and the phonetic side-index run as two waves, and the
/// second dedupes its candidates against the target ids the first already produced - so the key the index stores
/// and the key a hit carries have to be the same one. They were not: the index held the row id while the hit
/// carried the slug, and every article the first wave found came back a second time.
/// </summary>
public sealed class HandbookSideIndexTests
{
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("verify-agent").WithRank(Rank.Trainee).Build();

    private const string ArticleSlug = "fahndung-anlegen";
    private const string TermId = "99999999-8888-7777-6666-555555555555";

    // index rows exactly as SearchIndexProjection writes them: the article by SLUG, the term by id
    private static void Seed(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.HandbuchKapitel.Add(new HandbookChapter
        {
            Id = "chapter-1", Slug = "kapitel-eins", Title = "Kapitel Eins", IsVisible = true,
        });
        db.HandbuchArtikel.Add(new HandbookArticle
        {
            Id = "11111111-2222-3333-4444-555555555555",
            ChapterId = "chapter-1",
            Slug = ArticleSlug,
            Title = "Fahndung anlegen",
            Summary = "Wie eine Fahndung entsteht",
            IsVisible = true,
        });
        db.HandbuchBegriffe.Add(new GlossaryTerm
        {
            Id = TermId,
            Term = "Fahndung",
            ShortDefinition = "Die oeffentliche Suche nach einer Person",
            IsVisible = true,
        });

        foreach (var stem in SearchTokenizer.Stems("Fahndung anlegen", "Wie eine Fahndung entsteht", "fahndung-anlegen"))
        {
            db.SearchStemTokens.Add(new SearchStemToken
            {
                EntityType = nameof(HandbookArticle),
                EntityId = ArticleSlug,
                SourceId = "11111111-2222-3333-4444-555555555555",
                Stem = stem,
            });
        }
        foreach (var stem in SearchTokenizer.Stems("Fahndung", "Die oeffentliche Suche nach einer Person"))
        {
            db.SearchStemTokens.Add(new SearchStemToken
            {
                EntityType = nameof(GlossaryTerm),
                EntityId = TermId,
                SourceId = TermId,
                Stem = stem,
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task Handbook_article_is_listed_only_once_when_both_recall_paths_match()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var results = await SearchTestHost.NewService(ctx)
            .SearchAsync(new SearchCriteria { Text = "Fahndung", Fuzzy = true }, Junior());

        var article = results.Groups.Single(g => g.Category == nameof(HandbookArticle));
        var targets = article.Hit.Select(h => h.TargetId).ToList();
        Assert.Equal(targets.Distinct(StringComparer.Ordinal).Count(), targets.Count);
    }

    [Fact]
    public async Task Glossary_term_is_listed_only_once_when_both_recall_paths_match()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);

        var results = await SearchTestHost.NewService(ctx)
            .SearchAsync(new SearchCriteria { Text = "Fahndung", Fuzzy = true }, Junior());

        var glossary = results.Groups.Single(g => g.Category == nameof(GlossaryTerm));
        var targets = glossary.Hit.Select(h => h.TargetId).ToList();
        Assert.Equal(targets.Distinct(StringComparer.Ordinal).Count(), targets.Count);
    }
}
