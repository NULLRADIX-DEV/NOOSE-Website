using System.Text.Json;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Proposes a new job; the agent executes it from the card.</summary>
public sealed class ProposeJobTool(INooseiProposalService proposals) : INooseiProposingTool
{
    public string Name => "vorschlag_aufgabe";

    public string Description =>
        "Schlägt dem Agenten vor, eine Aufgabe anzulegen. Schreibt NICHTS: unter deiner Antwort erscheint eine Karte, "
        + "und erst der Agent führt sie aus. Ohne zustaendige wird der fragende Agent zuständig. Mit bezug_typ und "
        + "bezug_id wird die Aufgabe mit einer Akte verknüpft.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "titel": { "type": "string", "maxLength": {{NooseiProposalRules.MaxTitle}} },
            "beschreibung": { "type": "string", "maxLength": {{NooseiProposalRules.MaxDescription}} },
            "in_tagen": { "type": "integer", "minimum": 0, "maximum": {{NooseiProposalRules.MaxDays}},
                          "description": "Frist in so vielen Tagen ab heute; weglassen = ohne Frist." },
            "faellig_am": { "type": "string", "description": "Alternativ eine Frist als JJJJ-MM-TT." },
            "prioritaet": { "type": "string", "enum": ["niedrig", "normal", "hoch"] },
            "zustaendige": { "type": "array", "items": { "type": "string" }, "maxItems": 10,
                             "description": "Codenamen der Zuständigen." },
            "bezug_typ": { "type": "string", "enum": {{NooseiProposalRules.EnumJson(NooseiProposalRules.JobReferenceTypes)}} },
            "bezug_id": { "type": "string", "description": "id der Bezugsakte aus einem Werkzeug-Ergebnis." },
            "eingeschraenkt": { "type": "boolean", "description": "Nur Zuständige, Ersteller und Führung sehen sie." }
          },
          "required": ["titel"]
        }
        """);

    public Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
        => proposals.ProposeAsync(new NooseiProposalDraft(NooseiProposalKind.Job)
        {
            Title = NooseiLimits.Text(arguments, "titel"),
            Text = NooseiLimits.Text(arguments, "beschreibung"),
            InDays = NooseiLimits.Number(arguments, "in_tagen"),
            DueDate = NooseiLimits.Text(arguments, "faellig_am"),
            Priority = NooseiLimits.Text(arguments, "prioritaet"),
            Agents = NooseiLimits.Strings(arguments, "zustaendige").Take(10).ToList(),
            TargetType = NooseiLimits.Text(arguments, "bezug_typ"),
            TargetId = NooseiLimits.Text(arguments, "bezug_id"),
            Restricted = NooseiLimits.Flag(arguments, "eingeschraenkt"),
        }, context, cancellationToken);
}
