using System.Globalization;
using NOOSE_Website.Models.Handbook;

namespace NOOSE_Website.Services.Handbook;

/// <summary>The glossary prepared for matching: every term and synonym, longest first, bucketed by first letter.</summary>
/// <remarks>
/// Built once and cached, because it is asked the same question by every rich-text block on a page. Longest-first
/// is not a nicety: the glossary ships "Agent", "Special Agent" and "Special Agent in Charge", and matching the
/// short one first would leave "<em>Special Agent</em> in Charge" cut in two.
/// <para>
/// Bucketing by first letter keeps the scan linear in practice - roughly ten candidates per position instead of
/// two hundred - which matters because the walk runs over every text node of every document on screen.
/// </para>
/// </remarks>
public sealed class GlossaryMatcher
{
    /// <param name="Phrase">The term or one of its synonyms.</param>
    /// <param name="TermId">Identity of the term, so the same term is not bubbled twice under two spellings.</param>
    public sealed record Entry(string Phrase, string TermId, string Term, string Definition);

    /// <param name="Length">Characters consumed in the text, which is not the phrase length once whitespace differs.</param>
    public sealed record Match(Entry Entry, int Length);

    public static readonly GlossaryMatcher Empty = new(new Dictionary<char, List<Entry>>());

    private readonly Dictionary<char, List<Entry>> _byFirstLetter;

    private GlossaryMatcher(Dictionary<char, List<Entry>> byFirstLetter) => _byFirstLetter = byFirstLetter;

    public bool IsEmpty => _byFirstLetter.Count == 0;

    public static GlossaryMatcher Build(IEnumerable<GlossaryTermView> terms)
    {
        var entries = new List<Entry>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Term) || string.IsNullOrWhiteSpace(term.ShortDefinition))
            {
                continue;
            }
            Add(entries, term.Term, term);
            // synonyms arrive as one comma-separated string; nothing else in the codebase splits it
            foreach (var synonym in (term.Synonyms ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(entries, synonym, term);
            }
        }

        if (entries.Count == 0)
        {
            return Empty;
        }

        var buckets = entries
            .GroupBy(e => char.ToLowerInvariant(e.Phrase[0]))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.Phrase.Length)
                      .ThenBy(e => e.Phrase, StringComparer.Ordinal)
                      .ToList());
        return new GlossaryMatcher(buckets);
    }

    private static void Add(List<Entry> entries, string? phrase, GlossaryTermView term)
    {
        var trimmed = phrase?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }
        entries.Add(new Entry(trimmed, term.Id, term.Term, term.ShortDefinition));
    }

    /// <summary>The longest phrase starting exactly at <paramref name="index"/>, or null.</summary>
    /// <param name="before">Character preceding this text in the rendered flow, across inline tags.</param>
    /// <param name="after">Character following this text in the rendered flow, across inline tags.</param>
    /// <remarks>
    /// Both ends must sit on a word boundary. German compounds are the reason: without it "Fahndung" lights up
    /// inside "Fahndungsliste" and "Agent" inside "Agententätigkeit". A term split across inline tags
    /// (<c>Ver&lt;b&gt;schluss&lt;/b&gt;sache</c>) is missed on purpose - matching across nodes would mean
    /// re-cutting the markup.
    /// <para>
    /// The boundary is NOT the end of the text node, which is why the caller passes the neighbours in. A word can
    /// continue in the next node (<c>Fahndung&lt;b&gt;sliste&lt;/b&gt;</c>, which the editor writes as soon as
    /// somebody bolds a syllable), and judging the edge against the node alone put the bubble on "Fahndung" in the
    /// middle of "Fahndungsliste" - a wrong explanation, not a missing one.
    /// </para>
    /// <para>
    /// Every candidate is measured rather than the first hit taken, because with flexible whitespace a longer
    /// phrase does not necessarily consume more characters.
    /// </para>
    /// </remarks>
    public Match? LongestAt(string text, int index, char? before = null, char? after = null)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
        {
            return null;
        }
        if (index > 0 ? IsWordCharacter(text[index - 1]) : before is { } b && IsWordCharacter(b))
        {
            return null;
        }
        if (!_byFirstLetter.TryGetValue(char.ToLowerInvariant(text[index]), out var candidates))
        {
            return null;
        }

        Match? best = null;
        foreach (var entry in candidates)
        {
            var length = MatchLength(text, index, entry.Phrase);
            if (length < 0)
            {
                continue;
            }
            var end = index + length;
            if (end < text.Length ? IsWordCharacter(text[end]) : after is { } a && IsWordCharacter(a))
            {
                continue;
            }
            if (best is null || length > best.Length)
            {
                best = new Match(entry, length);
            }
        }
        return best;
    }

    /// <summary>Characters consumed if the phrase matches at the index, or -1.</summary>
    /// <remarks>
    /// A run of whitespace in the phrase matches any run of whitespace in the text. Without that, a stored
    /// "Senior&amp;nbsp;Special&amp;nbsp;Agent" - which is what the editor writes for repeated or trailing
    /// spaces, and what pasted content carries - fails to match the three-word term, and the bubble lands on
    /// the contained word "Agent" instead. A wrong definition, not a missing one.
    /// </remarks>
    private static int MatchLength(string text, int index, string phrase)
    {
        var t = index;
        var p = 0;

        while (p < phrase.Length)
        {
            if (char.IsWhiteSpace(phrase[p]))
            {
                while (p < phrase.Length && char.IsWhiteSpace(phrase[p]))
                {
                    p++;
                }
                if (t >= text.Length || !char.IsWhiteSpace(text[t]))
                {
                    return -1;
                }
                while (t < text.Length && char.IsWhiteSpace(text[t]))
                {
                    t++;
                }
                continue;
            }

            if (t >= text.Length || char.ToLowerInvariant(text[t]) != char.ToLowerInvariant(phrase[p]))
            {
                return -1;
            }
            t++;
            p++;
        }
        return t - index;
    }

    /// <summary>What counts as part of a word for the boundary test.</summary>
    /// <remarks>
    /// Letters and digits, plus combining marks: a hyphen has to count as a boundary or "Nur-Lese-Aufsicht"
    /// could never match, but a decomposed "Akte&#x0300;" must not look like a finished word, or the accent
    /// ends up orphaned outside the bubble.
    /// </remarks>
    private static bool IsWordCharacter(char c)
        => char.IsLetterOrDigit(c)
           || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
               or UnicodeCategory.SpacingCombiningMark
               or UnicodeCategory.EnclosingMark;
}
