using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Models.Handbook;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Handbook;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Looks an operating question up in the handbook and the glossary instead of guessing at it.</summary>
/// <remarks>
/// The one tool with no visibility gate, and on purpose: the handbook is open to every internal agent, and the
/// gateway has already refused anyone else. It reads the same rows the page reads - hidden articles, hidden
/// chapters and hidden terms are filtered by <see cref="IHandbookService"/>, not here.
/// <para>
/// Candidates are scored on titles, summaries and chapter names, which is what the handbook's own search field
/// matches too; only the few best then have their body loaded. Scoring against every body would mean reading
/// eighty longtext columns to answer one question, and the titles were written to be descriptive precisely so
/// this is unnecessary.
/// </para>
/// </remarks>
public sealed class HandbookLookupTool(IHandbookService handbook) : INooseiTool
{
    /// <summary>Articles whose body is loaded, however many the caller asks for.</summary>
    private const int MaxArticleBodies = 5;

    /// <summary>Source chips under one answer.</summary>
    private const int MaxRefs = 8;

    public string Name => "schlage_nach";

    public string Description =>
        "Schlägt im Handbuch und im Glossar nach: wie eine Seite bedient wird, wie ein Ablauf funktioniert, "
        + "was ein Fachwort bedeutet. Liefert den Artikeltext samt Schritten und die passenden Begriffe. "
        + "Nutze es für Fragen wie „wie lege ich eine Fahndung an?“ oder „was ist ein Prüffall?“ - "
        + "rate solche Antworten nie.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["frage"],
          "properties": {
            "frage": { "type": "string" },
            "max": { "type": "integer", "minimum": 1, "maximum": 40 }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        if (NooseiLimits.Text(arguments, "frage") is not { } question)
        {
            return new NooseiToolResult("Bitte eine Frage angeben.", null, true);
        }
        var max = NooseiLimits.Count(arguments, "max", 3);
        // the body load is the expensive half - three queries per article - so it is bounded on its own.
        // A model asking for forty would otherwise cost more than a hundred sequential round trips.
        var bodies = Math.Min(max, MaxArticleBodies);

        var tokens = Tokens(question);
        if (tokens.Count == 0)
        {
            return new NooseiToolResult("Die Frage enthält kein Wort, nach dem sich suchen lässt.", null, true);
        }

        var chapters = await handbook.GetChaptersAsync(cancellationToken);
        var cards = chapters
            .SelectMany(c => c.Articles.Select(a => (Card: a, Chapter: c.Title)))
            .Select(x => (x.Card, x.Chapter, Score: Score(tokens, x.Card.Title, 3)
                + Score(tokens, x.Card.Summary, 2)
                + Score(tokens, x.Chapter, 1)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Card.Title, StringComparer.CurrentCulture)
            .Take(bodies)
            .ToList();

        var terms = (await handbook.GetGlossaryAsync(cancellationToken))
            .Select(t => (Term: t, Score: Score(tokens, t.Term, 3)
                + Score(tokens, t.Synonyms, 2)
                + Score(tokens, t.ShortDefinition, 1)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Term.Term, StringComparer.CurrentCulture)
            .Take(max)
            .ToList();

        if (cards.Count == 0 && terms.Count == 0)
        {
            return new NooseiToolResult(
                "Dazu steht nichts im Handbuch. Sag das offen, statt eine Bedienung zu erfinden.");
        }

        var sb = new StringBuilder();
        // kept apart on purpose. The text is written glossary-first because the clip cuts the tail, but the chip
        // list is capped from the front - so a broad question that matched eight terms used to leave the articles
        // the answer was actually built from without a single place to look them up.
        var articleRefs = new List<LlmContextRef>(cards.Count);
        var termRefs = new List<LlmContextRef>(terms.Count);

        // the glossary first: it is a line per term, while an article body can fill the whole budget, and
        // the clip cuts the tail. Last, a definition could be trimmed away while its source chip survived.
        if (terms.Count > 0)
        {
            sb.AppendLine("Glossar:");
            foreach (var (term, _) in terms)
            {
                sb.Append("• ").Append(term.Term).Append(" — ").AppendLine(term.ShortDefinition);
                termRefs.Add(new LlmContextRef(nameof(GlossaryTerm), term.Id, term.Term));
            }
            sb.AppendLine();
        }

        foreach (var (card, chapter, _) in cards)
        {
            // only now the body: the scoring pass reads cards, which carry no longtext
            var article = await handbook.GetArticleAsync(card.Slug, cancellationToken);
            if (article is null)
            {
                continue;
            }
            Append(sb, article, chapter);
            articleRefs.Add(new LlmContextRef(nameof(HandbookArticle), article.Slug, article.Title));
        }

        // Asked again after the bodies were fetched. The guard above judges the scoring pass, and an article can
        // be withdrawn between the two - the answer would then be an empty string instead of the honest "nothing
        // in the handbook", and the model fills an empty answer with an invention.
        if (articleRefs.Count == 0 && terms.Count == 0)
        {
            return new NooseiToolResult(
                "Dazu steht nichts im Handbuch. Sag das offen, statt eine Bedienung zu erfinden.");
        }

        // articles keep their slots, the rest goes to terms: the article is what the answer quotes, and it is
        // capped at MaxArticleBodies anyway, so it can never crowd the list out on its own
        var refs = articleRefs.Take(MaxRefs).ToList();
        refs.AddRange(termRefs.Take(Math.Max(0, MaxRefs - refs.Count)));

        return new NooseiToolResult(
            NooseiLimits.Clip(sb.ToString(), NooseiLimits.MaxContentResultChars),
            // capped: the chips under an answer are a handful of places to look, not a bibliography
            refs.Count == 0 ? null : refs);
    }

    private static void Append(StringBuilder sb, HandbookArticleView article, string chapter)
    {
        sb.Append("Artikel: ").Append(article.Title).Append(" (Kapitel ").Append(chapter).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(article.Summary))
        {
            sb.AppendLine(article.Summary);
        }
        if (HtmlCleanup.PlainText(article.ContentHtml) is { Length: > 0 } body)
        {
            sb.AppendLine(body);
        }
        if (article.Steps.Count > 0)
        {
            sb.AppendLine("Schritte:");
            foreach (var step in article.Steps)
            {
                sb.Append("  ").Append(step.Number).Append(". ").Append(step.Title)
                    .Append(" — ").AppendLine(step.Text);
            }
        }
        // kept apart in the answer too: an instruction and a rule of the game are different claims
        if (HtmlCleanup.PlainText(article.RoleplayHtml) is { Length: > 0 } roleplay)
        {
            sb.Append("Im Rollenspiel: ").AppendLine(roleplay);
        }
        sb.AppendLine();
    }

    /// <summary>Words worth searching for, from a question asked in whole sentences.</summary>
    /// <remarks>
    /// Three letters and up, minus the German filler a question is made of. Without the filler list
    /// "Wie lege ich eine Akte an?" scores every article that contains "eine"; without the low floor the
    /// glossary's own abbreviations - TRU, HRB, VS, Dok - could never be asked about at all. A two-letter
    /// word survives only when it was written in capitals, which is what an abbreviation looks like.
    /// </remarks>
    internal static List<string> Tokens(string question)
        => question
            .Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '"', '\u201e', '\u201c', '(', ')', '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(Worthwhile)
            .Select(w => w.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool Worthwhile(string word)
    {
        if (word.Length == 2)
        {
            return !word.Any(char.IsLower);
        }
        return word.Length >= 3 && !Filler.Contains(word.ToLowerInvariant());
    }

    /// <summary>German function words a question is built from. Never a glossary term - "Dok" stays.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "der", "die", "das", "dem", "den", "des", "ein", "uns", "ich", "mir", "man", "wie", "was", "wer",
        "wem", "wen", "und", "ist", "war", "bin", "hat", "hab", "mit", "von", "vom", "für", "auf", "aus",
        "bei", "bis", "nun", "nur", "als", "zum", "zur", "mal", "ihr", "ihm", "ihn", "sie", "wir", "ihre",
        "eine", "einen", "einem", "einer", "eines", "dies", "diese", "diesem", "diesen", "dieser",
        "welche", "welcher", "welches", "wieso", "warum", "wann", "wohin", "woher", "kann", "kannst",
        "muss", "musst", "darf", "darfst", "soll", "sollte", "will", "wird", "werden", "wurde",
        "nicht", "noch", "auch", "aber", "oder", "denn", "dann", "beim", "damit", "dafür",
        "mich", "sich", "sind", "sein", "seine", "ihrer", "über", "unter", "nach",
        "vor", "durch", "gegen", "ohne", "immer", "schon", "etwas", "alles", "wenn",
        "dass", "hier", "dort", "wieder", "genau", "eigentlich", "bitte", "geht", "macht", "mache",
    };

    /// <summary>Points for every token the field contains; a longer field is not worth more.</summary>
    internal static int Score(IReadOnlyList<string> tokens, string? field, int weight)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return 0;
        }
        var hits = tokens.Count(t => field.Contains(t, StringComparison.CurrentCultureIgnoreCase));
        return hits * weight;
    }
}
