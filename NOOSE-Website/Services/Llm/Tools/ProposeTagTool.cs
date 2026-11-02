using System.Text.Json;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Proposes catalog tags for a record; the agent executes it from the card.</summary>
public sealed class ProposeTagTool(INooseiProposalService proposals) : INooseiProposingTool
{
    public string Name => "vorschlag_stichwort";

    public string Description =>
        "Schlägt dem Agenten vor, einer Akte Stichwörter zu geben. Schreibt NICHTS: unter deiner Antwort erscheint "
        + "eine Karte, und erst der Agent führt sie aus. Nur vorhandene Stichwörter; ein unbekanntes lehnt das "
        + "Werkzeug ab und nennt die vorhandenen. Ein Stichwort wird nur hinzugefügt, nie entfernt.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "typ": { "type": "string", "enum": {{NooseiProposalRules.EnumJson(NooseiProposalRules.TagTypes)}} },
            "id": { "type": "string", "description": "id der Akte aus einem Werkzeug-Ergebnis." },
            "stichwoerter": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 10,
                              "description": "Namen vorhandener Stichwörter." }
          },
          "required": ["typ", "id", "stichwoerter"]
        }
        """);

    public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
        => proposals.ProposeAsync(new NooseiProposalDraft(NooseiProposalKind.Tag)
        {
            Type = NooseiLimits.Text(arguments, "typ"),
            Id = NooseiLimits.Text(arguments, "id"),
            Tags = NooseiLimits.Strings(arguments, "stichwoerter").Take(10).ToList(),
        }, context, cancellationToken);
}
