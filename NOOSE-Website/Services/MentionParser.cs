using System.Text.RegularExpressions;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Parses and builds @-mention tokens of the form <c>@{Type:Id}</c> where Id is a 36-char GUID.</summary>
public static partial class MentionParser
{
    [GeneratedRegex(@"@\{(?<typ>\w+):(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}")]
    private static partial Regex TokenRegex();

    /// <summary>Finds all mention tokens in the text, with position.</summary>
    public static IReadOnlyList<MentionToken> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<MentionToken>();
        }
        var list = new List<MentionToken>();
        foreach (Match m in TokenRegex().Matches(text))
        {
            list.Add(new MentionToken(m.Groups["typ"].Value, m.Groups["id"].Value, m.Index, m.Length));
        }
        return list;
    }

    /// <summary>Builds the storage token for a reference: <c>@{Type:Id}</c>.</summary>
    public static string Token(string type, string id) => $"@{{{type}:{id}}}";

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex GapRegex();

    /// <summary>Stands in for a neutralized token; says a reference was there, never which.</summary>
    public const string NeutralReference = "[Verweis]";

    /// <summary>Replaces the tokens whose type matches with <see cref="NeutralReference"/>; other tokens stay.</summary>
    public static string Neutralize(string? text, Func<string, bool> typeFilter)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("@{", StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }
        return TokenRegex().Replace(text, m => typeFilter(m.Groups["typ"].Value) ? NeutralReference : m.Value);
    }

    /// <summary>Clips to at most <paramref name="max"/> chars without cutting a token; one that would not fit goes whole.</summary>
    public static string ClipSafe(string text, int max)
    {
        if (max <= 0)
        {
            return string.Empty;
        }
        if (text.Length <= max)
        {
            return text;
        }
        var cut = max;
        foreach (var token in Parse(text))
        {
            if (token.Start < max && token.Start + token.Length > max)
            {
                cut = token.Start;
                break;
            }
        }
        // a half token would stay in the field as broken literal text
        return text[..cut].TrimEnd();
    }

    /// <summary>Drops all tokens for contexts that never resolve them (search snippets, fuzzy matching, exports).</summary>
    public static string Strip(string? text)
    {
        // no '@' means no token; skips the regex on the overwhelming majority of texts
        if (string.IsNullOrEmpty(text) || !text.Contains('@'))
        {
            return text ?? string.Empty;
        }
        var bare = TokenRegex().Replace(text, " ");
        return GapRegex().Replace(bare, " ").Trim();
    }
}
