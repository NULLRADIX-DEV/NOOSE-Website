using System.Text.Json;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Proposes following a record; the agent executes it from the card.</summary>
public sealed class ProposeWatchTool(INooseiProposalService proposals) : INooseiProposingTool
{
    public string Name => "vorschlag_beobachten";

    public string Description =>
        "Schlägt dem Agenten vor, eine Akte zu beobachten (Benachrichtigung bei Änderungen). Schreibt NICHTS: unter "
        + "deiner Antwort erscheint eine Karte, und erst der Agent führt sie aus.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "typ": { "type": "string", "enum": {{NooseiProposalRules.EnumJson(NooseiProposalRules.WatchTypes)}} },
            "id": { "type": "string", "description": "id der Akte aus einem Werkzeug-Ergebnis." }
          },
          "required": ["typ", "id"]
        }
        """);

    public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
        => proposals.ProposeAsync(new NooseiProposalDraft(NooseiProposalKind.Watch)
        {
            Type = NooseiLimits.Text(arguments, "typ"),
            Id = NooseiLimits.Text(arguments, "id"),
        }, context, cancellationToken);
}
