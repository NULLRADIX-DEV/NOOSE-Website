using System.Text;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Services;

/// <summary>What a NOOSEI correction changed, ready to render.</summary>
public sealed record HtmlDiffResult(
    string Html,
    int Added,
    int Removed,
    bool StructureChanged,
    bool Degraded)
{
    public int Changes => Added + Removed;

    public double ChangedRatio { get; init; }

    public bool Unchanged => Changes == 0 && !StructureChanged;
}

/// <summary>Word-level diff between two versions of a document, rendered as ins/del marks on the new text.</summary>
/// <remarks>
/// Hand-rolled rather than pulled in: a diff package gives a word diff over plain strings, while the actual work
/// here is the HTML-aware tokenize and re-render — which you would write either way. Same call the repo already
/// made for Levenshtein in <see cref="TextSimilarity"/>.
/// </remarks>
public static partial class HtmlDiff
{
    /// <summary>Above this edit distance the greedy trace is abandoned and the whole middle is shown as one change.</summary>
    /// <remarks>
    /// Also the memory ceiling: the trace keeps one row of <c>2·cap+1</c> ints per step, so the cap squares.
    /// At 5.000 a wholesale rewrite allocated ~200 MB on the circuit before giving up; at 1.000 the worst case
    /// is ~8 MB. A proofreading pass on a 12.000-character document needs a distance in the low hundreds, so
    /// nothing that is actually a correction reaches this — only a rewrite does, and that degrades visibly.
    /// </remarks>
    private const int MaxEditDistance = 1_000;

    [GeneratedRegex(@"\s+|[^\s\w]+|[\w']+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"<(p|h1|h2|h3|li|blockquote|pre|td|th|caption)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    /// <summary>Diffs the plain text of two documents and marks the changes inside the new one.</summary>
    public static HtmlDiffResult Compare(string? oldHtml, string? newHtml)
    {
        var oldDoc = TextBlocks.Parse(oldHtml);
        var newDoc = TextBlocks.Parse(newHtml);

        var oldTokens = Tokenize(string.Join("\n", oldDoc.Blocks.Select(b => b.Text)));
        var newTokens = Tokenize(string.Join("\n", newDoc.Blocks.Select(b => b.Text)));

        var structureChanged = !string.Equals(Fingerprint(oldHtml), Fingerprint(newHtml), StringComparison.Ordinal);
        var (ops, degraded) = Diff(oldTokens, newTokens);

        var added = ops.Count(o => o.Kind == EditKind.Insert);
        var removed = ops.Count(o => o.Kind == EditKind.Delete);
        var html = Render(ops);
        var ratio = oldTokens.Count == 0 ? (added > 0 ? 1d : 0d) : (double)(added + removed) / oldTokens.Count;

        return new HtmlDiffResult(html, added, removed, structureChanged, degraded) { ChangedRatio = Math.Min(ratio, 1d) };
    }

    /// <summary>Ordered block-tag list; a word diff over text shows nothing when only the structure moved.</summary>
    public static string Fingerprint(string? html)
        => string.IsNullOrWhiteSpace(html)
            ? string.Empty
            : string.Join(",", BlockTagRegex().Matches(html).Select(m => m.Groups[1].Value.ToLowerInvariant()));

    /// <summary>Words, whitespace runs and punctuation runs, in document order.</summary>
    public static List<string> Tokenize(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : TokenRegex().Matches(text).Select(m => m.Value).ToList();

    private enum EditKind
    {
        Equal,
        Insert,
        Delete,
    }

    private readonly record struct Edit(EditKind Kind, string Text);

    /// <summary>Myers greedy diff over interned tokens, after stripping the common prefix and suffix.</summary>
    private static (List<Edit> Ops, bool Degraded) Diff(List<string> oldTokens, List<string> newTokens)
    {
        var ops = new List<Edit>();

        var head = 0;
        while (head < oldTokens.Count && head < newTokens.Count
            && string.Equals(oldTokens[head], newTokens[head], StringComparison.Ordinal))
        {
            head++;
        }
        var tail = 0;
        while (tail < oldTokens.Count - head && tail < newTokens.Count - head
            && string.Equals(oldTokens[^(tail + 1)], newTokens[^(tail + 1)], StringComparison.Ordinal))
        {
            tail++;
        }

        for (var i = 0; i < head; i++)
        {
            ops.Add(new Edit(EditKind.Equal, newTokens[i]));
        }

        var oldMiddle = oldTokens.Skip(head).Take(oldTokens.Count - head - tail).ToList();
        var newMiddle = newTokens.Skip(head).Take(newTokens.Count - head - tail).ToList();

        var degraded = false;
        if (oldMiddle.Count > 0 || newMiddle.Count > 0)
        {
            var middle = Myers(oldMiddle, newMiddle);
            if (middle is null)
            {
                // too far apart to align word by word: show the whole middle as one replacement and say so
                degraded = true;
                ops.AddRange(oldMiddle.Select(t => new Edit(EditKind.Delete, t)));
                ops.AddRange(newMiddle.Select(t => new Edit(EditKind.Insert, t)));
            }
            else
            {
                ops.AddRange(middle);
            }
        }

        for (var i = newTokens.Count - tail; i < newTokens.Count; i++)
        {
            ops.Add(new Edit(EditKind.Equal, newTokens[i]));
        }

        return (ops, degraded);
    }

    /// <summary>Classic O(ND) trace; null when the edit distance exceeds the cap.</summary>
    private static List<Edit>? Myers(List<string> a, List<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var max = Math.Min(n + m, MaxEditDistance);
        var offset = max;
        var size = 2 * max + 1;
        var v = new int[size];
        var trace = new List<int[]>();

        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var index = k + offset;
                if (index < 0 || index >= size)
                {
                    continue;
                }
                int x;
                if (k == -d || (k != d && v[index - 1] < v[index + 1]))
                {
                    x = v[index + 1];
                }
                else
                {
                    x = v[index - 1] + 1;
                }
                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }
                v[index] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(a, b, trace, d, offset, size);
                }
            }
        }
        return null;
    }

    private static List<Edit> Backtrack(List<string> a, List<string> b, List<int[]> trace, int d, int offset, int size)
    {
        var ops = new List<Edit>();
        var x = a.Count;
        var y = b.Count;

        for (var step = d; step > 0; step--)
        {
            var v = trace[step];
            var k = x - y;
            var index = k + offset;
            int prevK;
            if (k == -step || (k != step && index - 1 >= 0 && index + 1 < size && v[index - 1] < v[index + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }
            var prevX = v[prevK + offset];
            var prevY = prevX - prevK;

            while (x > prevX && y > prevY)
            {
                ops.Add(new Edit(EditKind.Equal, a[--x]));
                y--;
            }
            if (y > prevY)
            {
                ops.Add(new Edit(EditKind.Insert, b[--y]));
            }
            else if (x > prevX)
            {
                ops.Add(new Edit(EditKind.Delete, a[--x]));
            }
        }
        while (x > 0 && y > 0)
        {
            ops.Add(new Edit(EditKind.Equal, a[--x]));
            y--;
        }

        ops.Reverse();
        return ops;
    }

    /// <summary>Kept text, or one change hunk shown as removed then added.</summary>
    private sealed class Segment(string equal)
    {
        public bool IsEqual { get; init; } = true;
        public StringBuilder Equal { get; } = new(equal);
        public StringBuilder Removed { get; } = new();
        public StringBuilder Added { get; } = new();
        public int Weight => Math.Max(Removed.Length, Added.Length);
    }

    /// <summary>Equal runs and change hunks; neighbouring deletes and inserts share one hunk.</summary>
    private static List<Segment> Segments(List<Edit> ops)
    {
        var segments = new List<Segment>();
        foreach (var op in ops)
        {
            var last = segments.Count > 0 ? segments[^1] : null;
            if (op.Kind == EditKind.Equal)
            {
                if (last is { IsEqual: true })
                {
                    last.Equal.Append(op.Text);
                }
                else
                {
                    segments.Add(new Segment(op.Text));
                }
                continue;
            }
            if (last is null or { IsEqual: true })
            {
                segments.Add(last = new Segment("") { IsEqual = false });
            }
            (op.Kind == EditKind.Insert ? last.Added : last.Removed).Append(op.Text);
        }
        return segments;
    }

    /// <summary>Folds short kept text between two hunks into one hunk, so a rewrite reads as old then new.</summary>
    /// <remarks>Rule from diff-match-patch's semantic cleanup: kept text no longer than the change on either side.</remarks>
    private static List<Segment> Cleanup(List<Segment> segments)
    {
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 1; i < segments.Count - 1; i++)
            {
                var (left, equal, right) = (segments[i - 1], segments[i], segments[i + 1]);
                if (!equal.IsEqual || left.IsEqual || right.IsEqual)
                {
                    continue;
                }
                var kept = equal.Equal.ToString();
                // paragraph breaks stay visible
                if (kept.Contains('\n') || kept.Length > Math.Min(left.Weight, right.Weight))
                {
                    continue;
                }
                left.Removed.Append(kept).Append(right.Removed);
                left.Added.Append(kept).Append(right.Added);
                segments.RemoveRange(i, 2);
                merged = true;
                i--;
            }
        }
        return segments;
    }

    /// <summary>Renders the hunks as text with del then ins marks, at the place of the change.</summary>
    private static string Render(List<Edit> ops)
    {
        var sb = new StringBuilder();
        foreach (var segment in Cleanup(Segments(ops)))
        {
            if (segment.IsEqual)
            {
                sb.Append(System.Net.WebUtility.HtmlEncode(segment.Equal.ToString()));
                continue;
            }
            if (segment.Removed.Length > 0)
            {
                sb.Append("<del class=\"noosei-weg\">").Append(System.Net.WebUtility.HtmlEncode(segment.Removed.ToString())).Append("</del>");
            }
            if (segment.Added.Length > 0)
            {
                sb.Append("<ins class=\"noosei-neu\">").Append(System.Net.WebUtility.HtmlEncode(segment.Added.ToString())).Append("</ins>");
            }
        }
        return HtmlCleanup.CleanDiff("<p>" + sb.ToString().Replace("\n", "</p><p>") + "</p>");
    }
}
