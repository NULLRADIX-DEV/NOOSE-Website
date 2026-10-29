using System.Text;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>A person name split into comparable words, prepared once for many comparisons.</summary>
public sealed class PreparedName
{
    internal PreparedName(string[] words, string[] codes)
    {
        Words = words;
        Codes = codes;
    }

    internal string[] Words { get; }
    internal string[] Codes { get; }

    /// <summary>Words left after dropping titles; names only match at equal counts.</summary>
    public int WordCount => Words.Length;
}

/// <summary>Duplicate name rule: no titles, any word order, each word paired equal/sound-alike/typo. Pure, DB-free.</summary>
public static class PersonNameMatch
{
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "dr", "doktor", "med", "prof", "professor", "jr", "sr",
    };

    private static readonly DuplicateReason[] Levels =
        [DuplicateReason.SameName, DuplicateReason.SoundsAlike, DuplicateReason.SpelledAlike];

    /// <summary>Splits a name into lowercase words without titles.</summary>
    public static PreparedName Prepare(string? name)
    {
        var words = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var word = new StringBuilder();
            foreach (var c in name + " ")
            {
                if (char.IsLetterOrDigit(c))
                {
                    word.Append(char.ToLowerInvariant(c));
                    continue;
                }
                if (word.Length > 0 && !Titles.Contains(word.ToString()))
                {
                    words.Add(word.ToString());
                }
                word.Clear();
            }
        }
        return new PreparedName(words.ToArray(), words.Select(ColognePhonetic.Encode).ToArray());
    }

    /// <summary>Candidate keys: a match always shares one between the first word of one side and any word of the other.</summary>
    public static IReadOnlyList<string> BlockingKeys(PreparedName name, bool firstWordOnly = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var count = firstWordOnly ? Math.Min(1, name.Words.Length) : name.Words.Length;
        for (var i = 0; i < count; i++)
        {
            var word = name.Words[i];
            keys.Add("w:" + word);
            if (word.Length < TextSimilarity.MinWordLength)
            {
                continue; // short words only ever match exactly
            }
            if (name.Codes[i].Length > 0)
            {
                keys.Add("p:" + name.Codes[i]);
            }
            // level by level, so a variant reached early still gets its own deletions
            var level = new HashSet<string>(StringComparer.Ordinal) { word };
            for (var depth = TextSimilarity.Threshold(word.Length); depth > 0; depth--)
            {
                level = level.SelectMany(w => Enumerable.Range(0, w.Length).Select(at => w.Remove(at, 1)))
                    .ToHashSet(StringComparer.Ordinal);
                keys.UnionWith(level.Select(w => "w:" + w));
            }
        }
        return keys.ToList();
    }

    /// <summary>How alike two names are, or null when they are different people.</summary>
    public static DuplicateReason? Compare(string? a, string? b) => Compare(Prepare(a), Prepare(b));

    /// <inheritdoc cref="Compare(string?, string?)" />
    public static DuplicateReason? Compare(PreparedName a, PreparedName b)
    {
        var n = a.Words.Length;
        if (n == 0 || n != b.Words.Length)
        {
            return null;
        }

        var kinds = new DuplicateReason?[n, n];
        for (var i = 0; i < n; i++)
        {
            var any = false;
            for (var j = 0; j < n; j++)
            {
                kinds[i, j] = CompareWord(a.Words[i], a.Codes[i], b.Words[j], b.Codes[j]);
                any |= kinds[i, j] is not null;
            }
            if (!any)
            {
                return null; // this word has no partner at all
            }
        }

        // the weakest word decides, so take the strictest level that still pairs every word off
        foreach (var level in Levels)
        {
            if (PairsOff(kinds, n, level))
            {
                return level;
            }
        }
        return null;
    }

    private static DuplicateReason? CompareWord(string a, string codeA, string b, string codeB)
    {
        if (a == b)
        {
            return DuplicateReason.SameName;
        }
        if (a.Length < TextSimilarity.MinWordLength || b.Length < TextSimilarity.MinWordLength)
        {
            return null;
        }
        if (codeA.Length > 0 && codeA == codeB)
        {
            return DuplicateReason.SoundsAlike;
        }
        var threshold = TextSimilarity.Threshold(Math.Min(a.Length, b.Length));
        if (Math.Abs(a.Length - b.Length) > threshold)
        {
            return null;
        }
        return TextSimilarity.Distance(a, b, threshold) <= threshold ? DuplicateReason.SpelledAlike : null;
    }

    // perfect bipartite matching (augmenting paths) over word pairs no weaker than the level
    private static bool PairsOff(DuplicateReason?[,] kinds, int n, DuplicateReason level)
    {
        var owner = new int[n];
        Array.Fill(owner, -1);
        for (var i = 0; i < n; i++)
        {
            if (!Assign(i, new bool[n]))
            {
                return false;
            }
        }
        return true;

        bool Assign(int i, bool[] seen)
        {
            for (var j = 0; j < n; j++)
            {
                if (seen[j] || kinds[i, j] is not { } kind || kind > level)
                {
                    continue;
                }
                seen[j] = true;
                if (owner[j] < 0 || Assign(owner[j], seen))
                {
                    owner[j] = i;
                    return true;
                }
            }
            return false;
        }
    }
}
