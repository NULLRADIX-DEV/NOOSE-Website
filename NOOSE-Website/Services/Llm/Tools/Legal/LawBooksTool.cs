using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Lists the law books the asker may read, or the paragraphs of one book.</summary>
public sealed class LawBooksTool(ILawService laws, IPartnerVisibilityPolicyService policy) : INooseiTool
{
    public const string ToolName = "liste_gesetzbuecher";

    public string Name => ToolName;

    public string Description =>
        "Nennt die Gesetzbücher mit Kürzel und Zahl der Paragrafen. Mit „buch“ (Kürzel) liefert es stattdessen die "
        + "Paragrafen dieses Buchs mit Nummer, Titel und id — den Wortlaut liest du mit lies_gesetz.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "buch": { "type": "string", "description": "Kürzel eines Gesetzbuchs, z. B. StGB. Leer lassen für die Übersicht." }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        if (!await LegalToolGate.MayReadLawsAsync(context, policy, cancellationToken))
        {
            return new NooseiToolResult(LegalToolGate.NotReleased, null, true);
        }
        var scope = context.Scope;
        var sb = new StringBuilder();

        if (NooseiLimits.Text(arguments, "buch") is { } abbreviation)
        {
            var book = await laws.GetBookAsync(abbreviation, cancellationToken, scope.PartnerAgency, scope.MeId);
            if (book is null || book.Paragraphs.Count == 0)
            {
                return NooseiToolResult.Empty($"Paragrafen im Gesetzbuch „{abbreviation}“");
            }
            sb.Append(book.Book.Abbreviation).Append(" – ").Append(book.Book.Name)
                .Append(" (").Append(book.Paragraphs.Count).AppendLine(" Paragrafen):");
            foreach (var law in book.Paragraphs.Take(NooseiLimits.MaxRowsPerTool * 2))
            {
                sb.Append("• ").Append(law.Paragraph).Append(" – ").Append(law.Title).Append(" | id=").AppendLine(law.Id);
            }
            if (book.Paragraphs.Count > NooseiLimits.MaxRowsPerTool * 2)
            {
                sb.AppendLine("… weitere Paragrafen findest du mit suche_gesetz.");
            }
            return new NooseiToolResult(NooseiLimits.Clip(sb.ToString()));
        }

        var books = await laws.GetBooksAsync(cancellationToken, scope.PartnerAgency, scope.MeId);
        if (books.Count == 0)
        {
            return NooseiToolResult.Empty("Gesetzbücher");
        }
        sb.AppendLine("Gesetzbücher:");
        foreach (var b in books)
        {
            sb.Append("• ").Append(b.Abbreviation).Append(" – ").Append(b.Name)
                .Append(" (").Append(b.ParagraphCount).AppendLine(" Paragrafen)");
        }
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString()));
    }
}
