using System.Text.Json;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Proposes a manual link between two records; the agent executes it from the card.</summary>
public sealed class ProposeLinkTool(INooseiProposalService proposals) : INooseiProposingTool
{
    private static readonly string TypesJson = NooseiProposalRules.EnumJson(NooseiProposalRules.LinkTypes);

    public string Name => "vorschlag_verknuepfung";

    public string Description =>
        "Schlägt dem Agenten eine Verknüpfung zwischen zwei Akten vor, etwa Person zu Vorgang. Schreibt NICHTS: unter "
        + "deiner Antwort erscheint eine Karte, und erst der Agent führt sie aus. Für Mitgliedschaften und "
        + "Beziehungen zwischen Personen gibt es keinen Vorschlag; die pflegt der Agent in der Akte.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "typ": { "type": "string", "enum": {{TypesJson}} },
            "id": { "type": "string", "description": "id der ersten Akte aus einem Werkzeug-Ergebnis." },
            "ziel_typ": { "type": "string", "enum": {{TypesJson}} },
            "ziel_id": { "type": "string", "description": "id der zweiten Akte aus einem Werkzeug-Ergebnis." },
            "bezeichnung": { "type": "string", "maxLength": {{NooseiProposalRules.MaxLabel}},
                             "description": "Optional, etwa „Tatverdächtiger“." }
          },
          "required": ["typ", "id", "ziel_typ", "ziel_id"]
        }
        """);

    public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
        => proposals.ProposeAsync(new NooseiProposalDraft(NooseiProposalKind.Link)
        {
            Type = NooseiLimits.Text(arguments, "typ"),
            Id = NooseiLimits.Text(arguments, "id"),
            TargetType = NooseiLimits.Text(arguments, "ziel_typ"),
            TargetId = NooseiLimits.Text(arguments, "ziel_id"),
            Text = NooseiLimits.Text(arguments, "bezeichnung"),
        }, context, cancellationToken);
}
