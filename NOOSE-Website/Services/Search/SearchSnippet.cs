using System.Text;

namespace NOOSE_Website.Services.Search;

/// <summary>Builds the bit of text a result row shows under the title.</summary>
public static class SearchSnippet
{
    /// <summary>Characters kept on each side of the match.</summary>
    public const int Radius = 90;

    /// <summary>Hard ceiling for a snippet without a match to centre on.</summary>
    public const int HeadMax = 200;

    /// <summary>Plain-text window around the first occurrence of the query, with ellipsis markers.</summary>
    /// <remarks>The head of a 40 KB document is almost never the reason it matched, so a snippet that just takes the
    /// first 160 characters reads as a wrong hit. Falls back to the head when the term is not in the plain projection
    /// — it may have sat inside an attribute, which <see cref="HtmlCleanup.PlainText"/> drops.</remarks>
    public static string Around(string? plain, string? query, int radius = Radius)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }
        var (at, length) = FirstMatch(plain, query);
        if (at < 0)
        {
            return plain.Length <= HeadMax ? plain : plain[..Back(plain, HeadMax)].TrimEnd() + "…";
        }

        var from = Back(plain, Math.Max(0, at - radius));
        var to = Back(plain, Math.Min(plain.Length, at + length + radius));
        var window = plain[from..to].Trim();
        return (from > 0 ? "…" : string.Empty) + window + (to < plain.Length ? "…" : string.Empty);
    }

    /// <summary>Splits text into runs, flagging the ones that hold a query word; accents, case and ß are ignored.</summary>
    public static IReadOnlyList<(string Part, bool Hit)> Segments(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var spans = Spans(text, query);
        if (spans.Count == 0)
        {
            return [(text, false)];
        }

        var result = new List<(string, bool)>();
        var cursor = 0;
        foreach (var (start, end) in spans)
        {
            if (start > cursor)
            {
                result.Add((text[cursor..start], false));
            }
            result.Add((text[start..end], true));
            cursor = end;
        }
        if (cursor < text.Length)
        {
            result.Add((text[cursor..], false));
        }
        return result;
    }

    /// <summary>The whole phrase if it stands in the text, else the earliest single word; (-1, 0) when neither does.</summary>
    private static (int At, int Length) FirstMatch(string plain, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (-1, 0);
        }
        var phrase = query.Trim();
        var exact = plain.IndexOf(phrase, StringComparison.CurrentCultureIgnoreCase);
        if (exact >= 0)
        {
            return (exact, phrase.Length);
        }
        var spans = Spans(plain, query);
        return spans.Count == 0 ? (-1, 0) : (spans[0].Start, spans[0].End - spans[0].Start);
    }

    /// <summary>Merged, ordered hit ranges in the original text.</summary>
    private static List<(int Start, int End)> Spans(string text, string? query)
    {
        var terms = RegulationTextSearch.Terms(query);
        var spans = new List<(int Start, int End)>();
        if (terms.Count == 0)
        {
            return spans;
        }

        // fold per unit so every folded char keeps a way back to the original (ß becomes two chars)
        var folded = new StringBuilder(text.Length);
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var size = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var unit = text.Substring(i, size);
            // a lone surrogate would throw in Normalize
            var fold = size == 1 && char.IsSurrogate(text[i]) ? unit : RegulationTextSearch.Fold(unit);
            foreach (var c in fold)
            {
                folded.Append(c);
                starts.Add(i);
                ends.Add(i + size);
            }
            // a unit that folds to nothing (a combining mark) rides with the letter before it
            if (fold.Length == 0 && ends.Count > 0)
            {
                ends[^1] = i + size;
            }
            i += size;
        }

        var haystack = folded.ToString();
        foreach (var term in terms)
        {
            for (var at = haystack.IndexOf(term, StringComparison.Ordinal); at >= 0;
                 at = haystack.IndexOf(term, at + 1, StringComparison.Ordinal))
            {
                spans.Add((starts[at], ends[at + term.Length - 1]));
            }
        }
        spans.Sort();

        var merged = new List<(int Start, int End)>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }
        return merged;
    }

    /// <summary>Moves a cut off the middle of a surrogate pair, always backwards.</summary>
    /// <remarks>
    /// Both cuts are UTF-16 indices, and the haystack comes from the database, so it can hold non-BMP characters:
    /// slicing between the two halves of a pair emits a replacement character. Backwards rather than forwards, so
    /// a snippet never grows past the length its callers assert on.
    /// </remarks>
    private static int Back(string text, int index)
        => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;
}
