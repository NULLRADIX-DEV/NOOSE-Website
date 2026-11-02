using System.Text;
using System.Text.RegularExpressions;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Export;

/// <summary>Turns the plain dossier text into the Markdown file the leadership downloads.</summary>
/// <remarks>
/// The text is <see cref="DossierContextBuilder"/>'s, written for NOOSEI. Reading its three line shapes back here
/// keeps one builder per record type instead of two. A line it does not recognise passes as text, so a new shape in
/// the builder loses nothing; it only renders plainer.
/// </remarks>
public static partial class DossierMarkdown
{
    private const string ReleaseNote = "NOOSE-interne Akte · Weitergabe nur nach Freigabe durch die Führung.";

    private const int MaxSlugLength = 80;

    public static string Render(DossierContext dossier, string exportedBy, DateTime exportedAtUtc)
    {
        var lines = dossier.Text.Replace("\r", "").Split('\n');
        // the builder opens with the record kind ("Personenakte"); it moves into the head
        var kind = lines.Length > 0 && Plain(lines[0]) ? lines[0].Trim() : null;
        var body = kind is null ? lines : lines[1..];

        var output = new List<string> { $"# {OneLine(dossier.Title)}", "" };
        var label = string.Join(" · ", new[] { kind, dossier.IsClassified ? "Verschlusssache" : null }
            .Where(s => !string.IsNullOrEmpty(s)));
        if (label.Length > 0)
        {
            output.Add($"> {label}");
            output.Add(">");
        }
        output.Add($"> Exportiert von {OneLine(exportedBy)} am {MeetingTime.Text(exportedAtUtc)}");
        output.Add("");

        foreach (var line in body)
        {
            if (SectionPattern().Match(line) is { Success: true } section)
            {
                output.Add("");
                output.Add($"## {section.Groups[1].Value}");
                output.Add("");
            }
            else if (line.StartsWith("• ", StringComparison.Ordinal))
            {
                output.Add($"- {Escape(line[2..])}");
            }
            else if (FieldPattern().Match(line) is { Success: true } field)
            {
                output.Add($"- **{field.Groups[1].Value}:** {field.Groups[2].Value}");
            }
            else
            {
                output.Add(Escape(line));
            }
        }

        output.Add("");
        output.Add("---");
        output.Add("");
        output.Add($"*{ReleaseNote}*");
        return Join(output);
    }

    /// <summary>"dossier-" and the title as lowercase ASCII, e.g. dossier-max-mustermann-noose-p-2026-0001.md.</summary>
    public static string FileName(string title)
    {
        var lowered = title.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var slug = SlugPattern().Replace(lowered, "-").Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }
        return slug.Length == 0 ? "dossier.md" : $"dossier-{slug}.md";
    }

    private static bool Plain(string line)
        => !SectionPattern().IsMatch(line) && !line.StartsWith("• ", StringComparison.Ordinal)
           && !FieldPattern().IsMatch(line) && line.Trim().Length > 0;

    private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ');

    /// <summary>A backslash before a line start Markdown would read as structure; "1." escapes at its dot.</summary>
    private static string Escape(string text)
    {
        var match = BlockStartPattern().Match(text);
        if (!match.Success)
        {
            return text;
        }
        var marker = match.Groups[2];
        var at = char.IsDigit(marker.Value[0]) ? marker.Index + marker.Length - 1 : marker.Index;
        return text.Insert(at, "\\");
    }

    /// <summary>One line per entry; blank runs collapse to one and never open the file, which ends in a newline.</summary>
    private static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blank = sb.Length > 0;
                continue;
            }
            if (blank)
            {
                sb.Append('\n');
            }
            sb.Append(line).Append('\n');
            blank = false;
        }
        return sb.ToString();
    }

    // "— Aliase (2) —", the builder's section marker
    [GeneratedRegex(@"^— (.+) —$")]
    private static partial Regex SectionPattern();

    // "Aktenzeichen: …", what the builder's Line() writes: a short label starting with a letter or digit
    [GeneratedRegex(@"^([\p{L}\p{N}][\p{L}\p{N} ./()-]{0,39}): (.*)$")]
    private static partial Regex FieldPattern();

    // a heading, quote, list, table or rule
    [GeneratedRegex(@"^(\s*)([#>*+|=-]|\d+[.)])")]
    private static partial Regex BlockStartPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
