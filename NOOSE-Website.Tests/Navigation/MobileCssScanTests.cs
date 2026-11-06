using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>The phone rules at the end of app.css only ever apply on narrow screens, so desktop and print stay as they were.</summary>
/// <remarks>
/// Everything after the marker sits in one of the two narrow wrappers. The min-width and print wrappers exist only for
/// the helper classes that hide the narrow variant on desktop and on paper, and for the desktop gutter.
/// </remarks>
public sealed class MobileCssScanTests
{
    private const string Marker = "/* === Handy-Ansicht === */";

    private static readonly string[] NarrowWrappers =
    [
        "@media screen and (max-width: 959.98px)",
        "@media screen and (max-width: 599.98px)",
    ];

    private static readonly string[] HelperWrappers =
    [
        "@media screen and (min-width: 960px)",
        "@media screen and (min-width: 600px)",
        "@media print",
    ];

    private static readonly string[] HelperSelectors = [".noose-nur-schmal", ".noose-nur-handy", ":root"];

    private static readonly string[] HelperClasses = ["noose-mobil-aus", "noose-schmal-aus", "noose-nur-schmal", "noose-nur-handy"];

    private static string AppCss([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "wwwroot", "app.css"));

    private static string MobileSection()
    {
        var css = File.ReadAllText(AppCss());
        var start = css.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Abschnitt {Marker} fehlt in app.css");
        return Regex.Replace(css[(start + Marker.Length)..], @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    /// <summary>Splits CSS into its top-level blocks as (prelude, body); text outside any block comes back with a null body.</summary>
    private static List<(string Prelude, string? Body)> Blocks(string css)
    {
        var blocks = new List<(string, string?)>();
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0)
            {
                if (!string.IsNullOrWhiteSpace(css[i..])) blocks.Add((css[i..].Trim(), null));
                break;
            }

            var depth = 0;
            var close = open;
            for (; close < css.Length; close++)
            {
                if (css[close] == '{') depth++;
                else if (css[close] == '}' && --depth == 0) break;
            }

            Assert.True(close < css.Length, "Ungeschlossene Klammer im Handy-Abschnitt");
            blocks.Add((Regex.Replace(css[i..open], @"\s+", " ").Trim(), css[(open + 1)..close]));
            i = close + 1;
        }

        return blocks;
    }

    [Fact]
    public void New_mobile_rules_are_screen_only()
    {
        var offenders = new List<string>();

        foreach (var (prelude, body) in Blocks(MobileSection()))
        {
            if (body is null)
            {
                offenders.Add($"Text außerhalb einer Regel: {prelude}");
            }
            else if (NarrowWrappers.Contains(prelude))
            {
                offenders.AddRange(Blocks(body).Where(b => b.Body is null || b.Prelude.StartsWith('@'))
                    .Select(b => $"{prelude}: verschachtelt oder lose: {b.Prelude}"));
            }
            else if (HelperWrappers.Contains(prelude))
            {
                foreach (var (selectors, inner) in Blocks(body))
                {
                    var wrong = inner is null
                        ? [selectors]
                        : selectors.Split(',').Select(s => s.Trim()).Where(s => !HelperSelectors.Contains(s)).ToArray();
                    offenders.AddRange(wrong.Select(s => $"{prelude}: nur Hilfsklassen erlaubt, gefunden {s}"));
                }
            }
            else
            {
                offenders.Add($"Regel ohne schmale Hülle: {prelude}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Classes the shared components set and later page migrations rely on, each with its rule in the section.</summary>
    private static readonly string[] BuildingBlocks =
    [
        "noose-kachel", "noose-kachel-wert", "record-rail-leiste", "record-rail-anker", "noose-steckbrief-kopf",
        "noose-steckbrief-bild", "noose-steckbrief-zu", "noose-formleiste", "noose-dialog-lang", "noose-filterleiste",
        "noose-filter-feld", "noose-filter-zu", "noose-reiter-handy", "noose-eintrag", "noose-eintrag-titel",
        "noose-eintrag-chips", "noose-breite-frei", "noose-min-breite-frei",
    ];

    [Fact]
    public void Building_block_classes_have_rules()
    {
        var section = MobileSection();
        var missing = BuildingBlocks.Where(c => !Regex.IsMatch(section, $@"\.{Regex.Escape(c)}(?![\w-])")).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Helper_classes_and_gutter_exist()
    {
        var section = MobileSection();

        foreach (var name in HelperClasses)
        {
            Assert.Contains($".{name}", section, StringComparison.Ordinal);
        }

        Assert.Equal(3, Regex.Matches(section, @"--noose-gutter\s*:").Count);
    }
}
