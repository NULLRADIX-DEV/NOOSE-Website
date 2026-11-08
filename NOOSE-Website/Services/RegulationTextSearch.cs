using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Regulation;

namespace NOOSE_Website.Services;

/// <summary>One block of hits on the regulation page: a section, or the repealed paragraphs.</summary>
/// <param name="SectionId">Null for the repealed block.</param>
public sealed record RegulationSearchGroup(string? SectionId, string Caption, IReadOnlyList<RegulationParagraphRow> Paragraphs);

/// <summary>The in-page search of the service regulation; filters the document the reader already has.</summary>
/// <remarks>
/// Every word of the query must appear somewhere in the paragraph: its heading, its section's title or the wording the
/// page shows. Case, accents and ß are folded, so "ausrustung" finds "Ausrüstung" and "strasse" finds "Straße". The
/// page highlights the same words in the browser with the same folding (<c>js/textsearch.js</c>).
/// </remarks>
public static partial class RegulationTextSearch
{
    /// <summary>A single letter matches nearly every paragraph; below this the page shows the document as usual.</summary>
    public const int MinLength = 2;

    /// <summary>Words beyond this are ignored; a pasted sentence should not turn into dozens of checks per keystroke.</summary>
    public const int MaxTerms = 8;

    /// <summary>The folded, distinct words of a query; empty when the query is too short to search.</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < MinLength)
        {
            return [];
        }
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Fold)
            // "a b c" would otherwise pass the length check and match nearly everything
            .Where(t => t.Length >= MinLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTerms)
            .ToList();
    }

    /// <summary>Lower case without accents, ß as ss; the one comparison form for query and text.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (c is 'ß' or 'ẞ')
            {
                builder.Append("ss");
                continue;
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>The shown wording of a paragraph, stripped of markup and folded; what <see cref="Filter"/> searches.</summary>
    /// <remarks>Inline tags go without a space, so "&lt;b&gt;Funk&lt;/b&gt;disziplin" stays one word; block tags keep
    /// theirs through <see cref="HtmlCleanup.PlainText"/>, so two paragraphs never glue together.</remarks>
    public static string FoldedText(RegulationParagraphRow paragraph)
        => Fold(HtmlCleanup.PlainText(InlineTag().Replace(paragraph.ShownHtml ?? string.Empty, string.Empty)));

    [GeneratedRegex(@"</?(?:b|strong|i|em|u|s|strike|span|a|sub|sup|mark|code|small|font)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineTag();

    /// <summary>True when every term sits in at least one of the folded fields.</summary>
    public static bool Matches(IReadOnlyList<string> terms, params string[] foldedFields)
        => terms.Count > 0 && terms.All(t => foldedFields.Any(f => f.Contains(t, StringComparison.Ordinal)));

    /// <summary>The paragraphs that match, in document order, grouped by section; repealed ones last.</summary>
    /// <param name="foldedText">The shown wording of a paragraph as folded plain text (<see cref="FoldedText"/>); the
    /// page caches it per load, so a keystroke does not strip and fold the whole document again.</param>
    public static List<RegulationSearchGroup> Filter(RegulationDocument document, Func<RegulationParagraphRow, string> foldedText, string? query)
    {
        var terms = Terms(query);
        var groups = new List<RegulationSearchGroup>();
        if (terms.Count == 0)
        {
            return groups;
        }
        foreach (var section in document.Sections)
        {
            var title = Fold(section.Title);
            var hits = section.Paragraphs.Where(p => Hit(p, title)).ToList();
            if (hits.Count > 0)
            {
                groups.Add(new RegulationSearchGroup(section.Id, section.Caption, hits));
            }
        }
        var titles = document.Sections.ToDictionary(s => s.Id, s => Fold(s.Title));
        var repealed = document.Repealed.Where(p => Hit(p, titles.GetValueOrDefault(p.SectionId) ?? string.Empty)).ToList();
        if (repealed.Count > 0)
        {
            groups.Add(new RegulationSearchGroup(null, "Aufgehoben", repealed));
        }
        return groups;

        bool Hit(RegulationParagraphRow p, string sectionTitle)
            => Matches(terms, Fold(p.Heading), sectionTitle, foldedText(p));
    }
}
