using System.Text;
using System.Text.Json;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Graph;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Finds the shortest known chain of connections between two records.</summary>
/// <remarks>
/// Answers the question a list of neighbours cannot: how a person and a faction hang together when nothing
/// links them directly. The graph is walked over memberships, person relations and links at once, and only
/// over records this agent may see — an invisible record simply is not a usable stepping stone.
/// </remarks>
public sealed class FindPathTool(IGraphService graph) : INooseiTool
{
    public string Name => "finde_verbindungsweg";

    public string Description =>
        "Findet den kürzesten bekannten Weg zwischen zwei Akten, über Mitgliedschaften, Beziehungen und "
        + "Verknüpfungen hinweg. Beantwortet Fragen wie „Wie hängen diese Person und diese Fraktion zusammen?“ "
        + "auch dann, wenn beide nicht direkt miteinander verbunden sind.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["von_typ", "von_id", "nach_typ", "nach_id"],
          "properties": {
            "von_typ": { "type": "string", "enum": {{NooseiRecordTypes.GraphEnumJson}} },
            "von_id": { "type": "string" },
            "nach_typ": { "type": "string", "enum": {{NooseiRecordTypes.GraphEnumJson}} },
            "nach_id": { "type": "string" }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var fromType = NooseiRecordTypes.Clr(NooseiLimits.Text(arguments, "von_typ"), NooseiUse.Read);
        var fromId = NooseiLimits.Text(arguments, "von_id");
        var toType = NooseiRecordTypes.Clr(NooseiLimits.Text(arguments, "nach_typ"), NooseiUse.Read);
        var toId = NooseiLimits.Text(arguments, "nach_id");
        if (fromType is null || fromId is null || toType is null || toId is null)
        {
            return NooseiToolResult.NotFound();
        }
        // the schema is a hint; else a false "unconnected". Names the kind, never the record
        if (new[] { fromType, toType }.FirstOrDefault(t => !GraphService.NodeTypes.Contains(t)) is { } outside)
        {
            return new NooseiToolResult($"{NooseiRecordTypes.Plural(outside)} sind nicht Teil des Beziehungsgraphen. "
                + "Ihre direkten Verbindungen liefert zeige_verbindungen.");
        }

        var path = await graph.FindPathAsync(fromType, fromId, toType, toId, context.Actor, cancellationToken);
        if (!path.Found || path.Node.Count == 0)
        {
            // says nothing about whether either record exists — an invisible endpoint lands here too
            return new NooseiToolResult("Kein sichtbarer Verbindungsweg zwischen den beiden Akten gefunden.");
        }
        if (path.Node.Count == 1)
        {
            return new NooseiToolResult("Beide Angaben bezeichnen dieselbe Akte.");
        }

        var sb = new StringBuilder();
        var steps = path.Edges.Count;
        sb.Append("Verbindungsweg über ").Append(steps).Append(steps == 1 ? " Schritt" : " Schritte").AppendLine(":");

        for (var i = 0; i < path.Node.Count; i++)
        {
            var node = path.Node[i];
            if (i > 0 && i - 1 < path.Edges.Count)
            {
                sb.Append("  --[").Append(EdgeLabel(path.Edges[i - 1])).AppendLine("]-->");
            }
            sb.Append(i + 1).Append(". ").Append(node.Designation)
                .Append(" (").Append(TypeName(node.Type));
            if (!string.IsNullOrWhiteSpace(node.Subtitle))
            {
                sb.Append(", ").Append(node.Subtitle);
            }
            sb.Append(") | id=").AppendLine(node.Id[(node.Id.IndexOf(':') + 1)..]);
        }

        var refs = path.Node
            .Select(n => new LlmContextRef(n.Type, n.Id[(n.Id.IndexOf(':') + 1)..], n.Designation))
            .ToList();
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString()), refs);
    }

    /// <summary>The edge's own wording, or its kind when it carries none.</summary>
    private static string EdgeLabel(GraphEdge edge)
        => !string.IsNullOrWhiteSpace(edge.Label)
            ? MentionParser.Strip(edge.Label).Trim()
            : LinkKindDisplay.Name(edge.Kind);

    /// <summary>The graph reaches types the tool schema does not offer; <see cref="NooseiRecordTypes.German" />
    /// names those too, so there is nothing left to special-case here.</summary>
    private static string TypeName(string clr) => NooseiRecordTypes.German(clr);
}
