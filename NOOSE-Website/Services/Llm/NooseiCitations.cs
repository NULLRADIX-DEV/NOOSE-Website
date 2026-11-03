using System.Text.RegularExpressions;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>Checks after the fact what the chat prompt only asks for: that every case number in an answer came out
/// of the record database.</summary>
/// <remarks>The source chips are built from what the tools returned, not from what the answer cited, so nothing
/// here was ever verified. The reaction is a note, never a rejection: unlike proofreading there is no correct
/// alternative to fall back on, and the wording says "not evidenced", never "does not exist" — the same rule
/// <see cref="Llm.Tools.NooseiToolResult" /> enforces for a record the asker may not see.</remarks>
public static partial class NooseiCitations
{
    /// <summary>Shape of a case number, e.g. <c>FIB-P-2026-0001</c>; the form <see cref="CaseNumberService" /> issues.
    /// The legacy <c>NOOSE-</c> prefix still counts.</summary>
    public const string CaseNumberPattern = @"(?:FIB|NOOSE)-[A-Z]+-\d{4}-\d{4}";

    /// <summary>How many unevidenced numbers are reported. A model that invents twenty has one problem, not twenty.</summary>
    public const int MaxReported = 5;

    [GeneratedRegex(CaseNumberPattern, RegexOptions.Compiled)]
    private static partial Regex CaseNumbers();

    /// <summary>Case numbers the answer cites that no piece of evidence mentions, each once, in order of use.</summary>
    public static IReadOnlyList<string> Unsupported(string? answer, IEnumerable<string?> evidence)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }
        var cited = CaseNumbers().Matches(answer)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cited.Count == 0)
        {
            return [];
        }

        var known = evidence.Where(e => !string.IsNullOrEmpty(e)).ToList();
        return cited
            .Where(number => !known.Any(text => text!.Contains(number, StringComparison.Ordinal)))
            .Take(MaxReported)
            .ToList();
    }

    /// <summary>Everything in a transcript that counts as evidence: tool output, the questions, and tool output that
    /// had to be replayed as plain context.</summary>
    /// <remarks>
    /// Two exclusions, both load-bearing. Earlier answers are out because a case number the model invented once
    /// would otherwise vouch for itself in every follow-up, and the check would go quiet exactly where it matters.
    /// System lines are out because the chat prompt shows the citation form by example, and that example carries a
    /// case number — counting it would license the one fake number the model is most likely to reach for.
    /// </remarks>
    public static IEnumerable<string?> Evidence(IEnumerable<LlmMessage>? transcript, string question)
    {
        yield return question;
        if (transcript is null)
        {
            yield break;
        }
        foreach (var message in transcript)
        {
            if (message.Role is LlmRole.Tool or LlmRole.User
                || message.Content?.StartsWith(NooseiHistoryWindow.FlattenedToolPrefix, StringComparison.Ordinal) == true)
            {
                yield return message.Content;
            }
        }
    }

    /// <summary>The note shown under the answer, or null when everything checked out.</summary>
    public static string? Notice(IReadOnlyList<string> unsupported) => unsupported.Count == 0
        ? null
        : "Nicht belegt: " + string.Join(", ", unsupported)
            + (unsupported.Count == 1
                ? " — dieses Aktenzeichen stammt aus keinem Werkzeug-Ergebnis dieser Unterhaltung."
                : " — diese Aktenzeichen stammen aus keinem Werkzeug-Ergebnis dieser Unterhaltung.");
}
