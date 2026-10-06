using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Finds law paragraphs by keyword, over the same release-aware search as the search page.</summary>
public sealed class LawSearchTool(ISearchService search, IPartnerVisibilityPolicyService policy) : INooseiTool
{
    public const string ToolName = "suche_gesetz";

    public string Name => ToolName;

    public string Description =>
        "Sucht Paragrafen in den Gesetzbüchern nach Stichworten, Titel, Paragrafennummer, Wortlaut oder Strafmaß. "
        + "Liefert Buch, Paragraf, Titel und id — den Wortlaut liest du mit lies_gesetz.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["suchtext"],
          "properties": {
            "suchtext": { "type": "string", "description": "Stichwort, Titel oder Paragrafennummer." },
            "unscharf": { "type": "boolean", "description": "Tippfehler-Toleranz einschalten." },
            "max": { "type": "integer", "minimum": 1, "maximum": 25 }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        if (!await LegalToolGate.MayReadLawsAsync(context, policy, cancellationToken))
        {
            return new NooseiToolResult(LegalToolGate.NotReleased, null, true);
        }
        var text = NooseiLimits.Text(arguments, "suchtext");
        if (text is null)
        {
            return new NooseiToolResult("Bitte einen Suchtext angeben.", null, true);
        }

        var criteria = new SearchCriteria
        {
            Text = text,
            Fuzzy = arguments.TryGetProperty("unscharf", out var fuzzy) && fuzzy.ValueKind == JsonValueKind.True,
            Categories = [nameof(Law)],
        };
        var max = NooseiLimits.Count(arguments, "max", 10, 25);
        var results = await search.SearchAsync(criteria, context.Actor, cancellationToken);
        var hits = results.Groups.SelectMany(g => g.Hit).Where(h => h.Category == nameof(Law)).Take(max).ToList();
        if (hits.Count == 0)
        {
            return NooseiToolResult.Empty("passenden Paragrafen");
        }

        var sb = new StringBuilder();
        var refs = new List<LlmContextRef>(hits.Count);
        sb.Append("Treffer (").Append(hits.Count).AppendLine("):");
        foreach (var hit in hits)
        {
            sb.Append("• ").Append(hit.Title);
            if (!string.IsNullOrWhiteSpace(hit.CaseNumber))
            {
                sb.Append(" | ").Append(hit.CaseNumber);
            }
            sb.Append(" | id=").Append(hit.TargetId);
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                var snippet = hit.Snippet.Length > NooseiLimits.MaxSnippetChars
                    ? hit.Snippet[..NooseiLimits.MaxSnippetChars] + "…"
                    : hit.Snippet;
                sb.Append(" | ").Append(snippet.Replace('\n', ' '));
            }
            sb.AppendLine();
            refs.Add(new LlmContextRef(nameof(Law), hit.TargetId, hit.Title));
        }
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString()), refs);
    }
}
