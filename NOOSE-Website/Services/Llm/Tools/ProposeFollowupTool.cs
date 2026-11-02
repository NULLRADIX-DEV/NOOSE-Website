using System.Text.Json;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Proposes a followup on a record; the agent executes it from the card.</summary>
public sealed class ProposeFollowupTool(INooseiProposalService proposals) : INooseiProposingTool
{
    public string Name => "vorschlag_wiedervorlage";

    public string Description =>
        "Schlägt dem Agenten eine Wiedervorlage an einer Akte vor. Schreibt NICHTS: unter deiner Antwort erscheint "
        + "eine Karte, und erst der Agent führt sie aus. Nutze es, wenn der Agent an etwas erinnert werden will "
        + "oder darum bittet, eine Akte nachzuhalten. Fälligkeit mit in_tagen (ab heute) oder faellig_am.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "typ": { "type": "string", "enum": {{NooseiProposalRules.EnumJson(NooseiProposalRules.FollowupTypes)}} },
            "id": { "type": "string", "description": "id der Akte aus einem Werkzeug-Ergebnis." },
            "in_tagen": { "type": "integer", "minimum": 0, "maximum": {{NooseiProposalRules.MaxDays}},
                          "description": "Fällig in so vielen Tagen ab heute; 0 = heute." },
            "faellig_am": { "type": "string", "description": "Alternativ ein Datum als JJJJ-MM-TT." },
            "notiz": { "type": "string", "maxLength": {{NooseiProposalRules.MaxNote}} },
            "zustaendig": { "type": "string", "description": "Codename des Zuständigen; weglassen = der fragende Agent." }
          },
          "required": ["typ", "id"]
        }
        """);

    public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
        => proposals.ProposeAsync(new NooseiProposalDraft(NooseiProposalKind.Followup)
        {
            Type = NooseiLimits.Text(arguments, "typ"),
            Id = NooseiLimits.Text(arguments, "id"),
            InDays = NooseiLimits.Number(arguments, "in_tagen"),
            DueDate = NooseiLimits.Text(arguments, "faellig_am"),
            Text = NooseiLimits.Text(arguments, "notiz"),
            Agents = NooseiLimits.Text(arguments, "zustaendig") is { } agent ? [agent] : [],
        }, context, cancellationToken);
}
