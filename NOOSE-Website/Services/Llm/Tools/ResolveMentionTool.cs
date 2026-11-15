using System.Text;
using System.Text.Json;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Resolves @-mention tokens an agent pasted into a question into readable record names.</summary>
public sealed class ResolveMentionTool(IMentionService mentions) : INooseiTool
{
    public string Name => "loese_erwaehnung_auf";

    public string Description =>
        "Löst @-Erwähnungen der Form @{Typ:Id} in einem Text zu Aktennamen auf. "
        + "Nutze das, wenn der Agent eine Erwähnung in seiner Frage mitgeschickt hat.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["text"],
          "properties": {
            "text": { "type": "string", "description": "Text mit @{Typ:Id}-Erwähnungen." }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var text = NooseiLimits.Text(arguments, "text");
        if (text is null)
        {
            return new NooseiToolResult("Bitte einen Text angeben.", null, true);
        }
        var tokens = MentionParser.Parse(text);
        if (tokens.Count == 0)
        {
            return NooseiToolResult.Empty("Erwähnungen");
        }

        // investigation titles never reach the model, not even for leadership
        var segments = await mentions.ResolveAsync(
            text, context.Scope.MayClassifiedRead, context.Scope.MeId, cancellationToken, context.Scope.PartnerAgency,
            investigations: false);

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append(segment.Text);
        }

        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString().Trim()), Refs(tokens, segments));
    }

    /// <summary>The records the mentions actually resolved to, so they reach the source chips like any other read.</summary>
    /// <remarks>
    /// A segment carries no id, so it is matched to its token by position — <see cref="IMentionService" /> emits
    /// exactly one reference segment per token, in order. If that ever stops holding, the count check drops all
    /// refs rather than pairing a name with the wrong id; the resolved text is unaffected either way.
    /// A missing <c>Href</c> is the service's own way of saying "withheld or gone", so those produce no ref.
    /// </remarks>
    private static List<LlmContextRef> Refs(
        IReadOnlyList<MentionToken> tokens, IReadOnlyList<MentionSegment> segments)
    {
        var resolved = segments.Where(s => s.IsReference).ToList();
        if (resolved.Count != tokens.Count)
        {
            return [];
        }
        var refs = new List<LlmContextRef>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (resolved[i].Href is { Length: > 0 })
            {
                refs.Add(new LlmContextRef(tokens[i].Type, tokens[i].Id, resolved[i].Text));
            }
        }
        return refs;
    }
}
