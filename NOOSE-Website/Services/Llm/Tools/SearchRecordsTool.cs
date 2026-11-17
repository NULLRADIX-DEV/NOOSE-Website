using System.Text;
using System.Text.Json;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Full-text search over the record database, scoped to the asking agent.</summary>
public sealed class SearchRecordsTool(ISearchService search, ITagService tags) : INooseiTool
{
    public string Name => "suche_akten";

    public string Description =>
        "Durchsucht die Aktendatenbank nach Namen, Aktenzeichen, Aliassen und Stichworten. "
        + "Liefert Treffer mit Typ, Titel, Aktenzeichen und Id — die Id brauchst du für lies_akte.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["suchtext"],
          "properties": {
            "suchtext": { "type": "string", "description": "Freitext: Name, Aktenzeichen, Alias oder Stichwort." },
            "typen": { "type": "array", "items": { "type": "string", "enum": {{NooseiRecordTypes.SearchableEnumJson}} },
                       "description": "Auf diese Aktentypen einschränken; leer lassen für alle." },
            "stichworte": { "type": "array", "items": { "type": "string" },
                            "description": "Nur Akten mit diesen Stichworten (Tags). Achtung: Mit Stichworten werden Gesetze, Asservate, Kassenbuchungen, Finanzierungsanträge, Verpflegungsanforderungen und Entführungen gar nicht durchsucht — aus 0 Treffern folgt dort nichts." },
            "unscharf": { "type": "boolean", "description": "Tippfehler-Toleranz einschalten." },
            "max": { "type": "integer", "minimum": 1, "maximum": 25 }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var text = NooseiLimits.Text(arguments, "suchtext");
        if (text is null)
        {
            return new NooseiToolResult("Bitte einen Suchtext angeben.", null, true);
        }

        var wanted = NooseiLimits.Strings(arguments, "typen");
        var categories = wanted
            .Select(t => NooseiRecordTypes.Clr(t, NooseiUse.Search))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        // dropping them all silently would answer a different question than the one asked: an unrestricted search
        if (wanted.Count > 0 && categories.Count == 0)
        {
            return new NooseiToolResult("Diese Aktentypen sind nicht durchsuchbar. " + Searchable(), null, true);
        }
        var droppedTypes = wanted.Count - categories.Count;

        var (tagIds, unknownTags) = await TagsAsync(NooseiLimits.Strings(arguments, "stichworte"), cancellationToken);

        var criteria = new SearchCriteria
        {
            Text = text,
            Fuzzy = arguments.TryGetProperty("unscharf", out var fuzzy) && fuzzy.ValueKind == JsonValueKind.True,
            Categories = categories,
            TagIds = tagIds,
        };
        var max = NooseiLimits.Count(arguments, "max", 10, 25);

        var results = await search.SearchAsync(criteria, context.Actor, cancellationToken);
        // round-robin, not the flat list: taking the first N end to end returns "the first N people"
        var hits = SearchProviderKit
            .Interleave(results.Groups.Select(g => (IReadOnlyList<SearchHit>)g.Hit).ToList())
            .Take(max).ToList();
        if (hits.Count == 0)
        {
            return new NooseiToolResult(Note("Keine Treffer gefunden.", unknownTags, droppedTypes, results));
        }

        var sb = new StringBuilder();
        var refs = new List<LlmContextRef>(hits.Count);
        sb.Append("Treffer (").Append(hits.Count).AppendLine("):");
        foreach (var hit in hits)
        {
            var type = string.IsNullOrEmpty(hit.TargetType) ? hit.Category : hit.TargetType;
            // name the category, not only the target: otherwise a comment hit prints as "Person | Max Mustermann"
            // and the model cannot tell it found a comment
            sb.Append("• ").Append(SearchCatalog.German(hit.Category));
            if (!string.Equals(type, hit.Category, StringComparison.Ordinal))
            {
                sb.Append(" (in ").Append(SearchCatalog.German(type)).Append(')');
            }
            sb.Append(" | ").Append(hit.Title)
                .Append(" | Aktenzeichen: ").Append(string.IsNullOrWhiteSpace(hit.CaseNumber) ? "—" : hit.CaseNumber);
            // an id lies_akte would reject costs the model a round to find out; say so instead of handing one over
            sb.Append(NooseiRecordTypes.IsReadable(type)
                ? " | id=" + hit.TargetId
                : " | (nur Suchtreffer, nicht als Akte lesbar)");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                var snippet = hit.Snippet.Length > NooseiLimits.MaxSnippetChars
                    ? hit.Snippet[..NooseiLimits.MaxSnippetChars] + "…"
                    : hit.Snippet;
                sb.Append(" | ").Append(snippet.Replace('\n', ' '));
            }
            sb.AppendLine();
            refs.Add(new LlmContextRef(type, hit.TargetId, hit.Title));
        }

        return new NooseiToolResult(NooseiLimits.Clip(Note(sb.ToString(), unknownTags, droppedTypes, results)), refs);
    }

    /// <summary>Resolves tag names to ids, and reports the ones that do not exist.</summary>
    /// <remarks>An unknown tag silently dropped is the worse failure: the search then runs without it and the
    /// model reads the result as "no record carries this keyword".</remarks>
    private async Task<(List<string> Ids, List<string> Unknown)> TagsAsync(
        IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return ([], []);
        }
        var all = await tags.GetAllAsync(cancellationToken);
        var ids = new List<string>();
        var unknown = new List<string>();
        foreach (var name in names)
        {
            var hit = all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                unknown.Add(name);
            }
            else
            {
                ids.Add(hit.Id);
            }
        }
        return (ids, unknown);
    }

    /// <summary>Appends what was asked for but not applied. Both cases are the same failure if left out: the model
    /// reads the result as an answer about something the search never looked at.</summary>
    private static string Note(string text, List<string> unknownTags, int droppedTypes, SearchResults results)
    {
        var notes = new List<string>(3);
        if (unknownTags.Count > 0)
        {
            notes.Add("Diese Stichworte gibt es nicht und sie wurden nicht angewendet: "
                + string.Join(", ", unknownTags) + ".");
        }
        if (droppedTypes > 0)
        {
            notes.Add($"{droppedTypes} der angegebenen Aktentypen sind nicht durchsuchbar und wurden weggelassen. "
                + Searchable());
        }
        if (results.Incomplete.Count > 0)
        {
            notes.Add("Das Zeitbudget der Suche war erschöpft. Diese Kategorien wurden nicht zu Ende durchsucht: "
                + string.Join(", ", results.Incomplete.Select(SearchCatalog.Plural))
                + ". Aus 0 Treffern folgt dort nichts.");
        }
        return notes.Count == 0 ? text : text.TrimEnd() + "\nHinweis: " + string.Join(" ", notes);
    }

    private static string Searchable()
        => "Durchsuchbar sind: " + string.Join(", ", NooseiRecordTypes.Names(NooseiUse.Search)) + ".";
}
