using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Reads one law paragraph: the law itself only, never the comments, sources or links attached to it.</summary>
public sealed class LawReadTool(ILawService laws, IPartnerVisibilityPolicyService policy) : INooseiTool
{
    public const string ToolName = "lies_gesetz";

    public string Name => ToolName;

    public string Description =>
        "Liefert den Wortlaut eines Paragrafen mit Gesetzbuch, Nummer, Titel, Abschnitt und Strafmaß. "
        + "Die id kommt aus suche_gesetz oder liste_gesetzbuecher.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id"],
          "properties": {
            "id": { "type": "string", "description": "id des Paragrafen." }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        if (!await LegalToolGate.MayReadLawsAsync(context, policy, cancellationToken))
        {
            return new NooseiToolResult(LegalToolGate.NotReleased, null, true);
        }
        if (NooseiLimits.Text(arguments, "id") is not { } id)
        {
            return new NooseiToolResult("Bitte die id des Paragrafen angeben.", null, true);
        }
        // service applies release
        var law = await laws.GetAsync(id, cancellationToken, context.Scope.PartnerAgency, context.Scope.MeId);
        if (law is null)
        {
            return NooseiToolResult.NotFound();
        }

        var sb = new StringBuilder();
        sb.Append("Gesetzbuch: ").AppendLine(law.LawBook);
        sb.Append("Paragraf: ").AppendLine(law.Paragraph);
        sb.Append("Titel: ").AppendLine(law.Title);
        if (!string.IsNullOrWhiteSpace(law.Section))
        {
            sb.Append("Abschnitt: ").AppendLine(law.Section);
        }
        if (!string.IsNullOrWhiteSpace(law.Sentence))
        {
            sb.Append("Strafmaß: ").AppendLine(law.Sentence);
        }
        if (HtmlCleanup.PlainText(law.Text) is { Length: > 0 } text)
        {
            sb.AppendLine("— Wortlaut —").AppendLine(text);
        }
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString(), NooseiLimits.MaxContentResultChars),
            [new LlmContextRef(nameof(Law), law.Id, LegalToolGate.Cite(law))]);
    }
}
