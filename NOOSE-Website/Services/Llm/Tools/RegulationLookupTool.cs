using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Models.Regulation;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Looks a rule question up in the service regulation instead of guessing at it.</summary>
/// <remarks>
/// The regulation has no classification and every internal agent reads all of it, so the tool reads exactly what
/// <see cref="IRegulationService.GetDocumentAsync"/> shows the asking agent; a partner gets nothing there. Paragraphs
/// are scored on heading, section title and plain text, the corpus is small enough to score in memory.
/// </remarks>
public sealed class RegulationLookupTool(IRegulationService regulation) : INooseiTool
{
    /// <summary>Paragraphs returned, however many the caller asks for.</summary>
    private const int MaxParagraphs = 6;

    public string Name => "schlage_dvo_nach";

    public string Description =>
        "Schlägt in der Dienstverordnung des FIB nach: welche Regel gilt, wer was darf oder muss, Dienstgrade, "
        + "Ausrüstung, Funk, Disziplinarwesen. Liefert die passenden Absätze im geltenden Wortlaut. Nutze es für "
        + "Fragen wie „was regelt die DVO zur Kleiderordnung?“ - rate solche Antworten nie.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["frage"],
          "properties": {
            "frage": { "type": "string" },
            "max": { "type": "integer", "minimum": 1, "maximum": 20 }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        if (NooseiLimits.Text(arguments, "frage") is not { } question)
        {
            return new NooseiToolResult("Bitte eine Frage angeben.", null, true);
        }
        var max = Math.Min(NooseiLimits.Count(arguments, "max", 3), MaxParagraphs);
        var tokens = HandbookLookupTool.Tokens(question);
        if (tokens.Count == 0)
        {
            return new NooseiToolResult("Die Frage enthält kein Wort, nach dem sich suchen lässt.", null, true);
        }

        var doc = await regulation.GetDocumentAsync(context.Actor, cancellationToken);
        var hits = doc.Sections
            .SelectMany(s => s.Paragraphs.Select(p => (Section: s, Paragraph: p, Text: HtmlCleanup.PlainText(p.ShownHtml))))
            .Select(x => (x.Section, x.Paragraph, x.Text, Score: HandbookLookupTool.Score(tokens, x.Paragraph.Heading, 3)
                + HandbookLookupTool.Score(tokens, x.Section.Title, 2)
                + HandbookLookupTool.Score(tokens, x.Text, 1)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Paragraph.Heading, StringComparer.CurrentCulture)
            .Take(max)
            .ToList();
        if (hits.Count == 0)
        {
            return new NooseiToolResult(
                "Dazu steht nichts in der Dienstverordnung. Sag das offen, statt eine Regel zu erfinden.");
        }

        var sb = new StringBuilder();
        var refs = new List<LlmContextRef>(hits.Count);
        foreach (var (section, paragraph, text, _) in hits)
        {
            var label = RegulationRules.ParagraphLabel(section.Number, paragraph.Heading);
            sb.Append("## ").Append(section.Caption).Append(" – ").AppendLine(paragraph.Heading);
            if (paragraph.Audience != Models.Enums.RegulationAudience.Bureau)
            {
                sb.Append("Gilt für: ").AppendLine(Models.Enums.RegulationAudienceDisplay.Label(paragraph.Audience));
            }
            sb.Append("Fassung ").Append(paragraph.ShownVersion).Append(", gültig ab ")
                .AppendLine(paragraph.ShownValidFrom.ToString("dd.MM.yyyy"));
            sb.AppendLine(text).AppendLine();
            refs.Add(new LlmContextRef(nameof(RegulationParagraph), paragraph.Id, label));
        }
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString(), NooseiLimits.MaxContentResultChars), refs);
    }
}
