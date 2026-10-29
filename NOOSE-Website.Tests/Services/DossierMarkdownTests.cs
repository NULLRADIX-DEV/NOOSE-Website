using NOOSE_Website.Infrastructure.Export;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The NOOSEI dossier text turned into the Markdown file the leadership downloads.</summary>
public class DossierMarkdownTests
{
    private static readonly DateTime ExportedAt = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private static string Markdown(string text, bool classified = false)
        => DossierMarkdown.Render(new DossierContext("Max Mustermann (NOOSE-P-2026-0001)", text, classified),
            "Hawk", ExportedAt);

    private static string[] Render(string text, bool classified = false)
        => Markdown(text, classified).TrimEnd('\n').Split('\n');

    [Fact]
    public void File_EndsWithOneNewline()
    {
        var markdown = Markdown("Personenakte");

        Assert.EndsWith("*\n", markdown);
        Assert.DoesNotContain("\r", markdown);
    }

    [Fact]
    public void Head_NamesTitleKindAndExporter()
    {
        var lines = Render("Personenakte\nName: Max");

        Assert.Equal("# Max Mustermann (NOOSE-P-2026-0001)", lines[0]);
        Assert.Contains("> Personenakte", lines);
        // Berlin wall clock, not UTC
        Assert.Contains("> Exportiert von Hawk am 29.10.2026 13:00", lines);
    }

    [Fact]
    public void Head_MarksAClassifiedRecord()
    {
        Assert.Contains("> Personenakte · Verschlusssache", Render("Personenakte", classified: true));
        Assert.DoesNotContain(Render("Personenakte"), l => l.Contains("Verschlusssache"));
    }

    [Fact]
    public void Fields_BecomeBoldListItems()
    {
        var lines = Render("Personenakte\nAktenzeichen: NOOSE-P-2026-0001\nBedrohungs-Score: 80 (Konfidenz 70%)");

        Assert.Contains("- **Aktenzeichen:** NOOSE-P-2026-0001", lines);
        Assert.Contains("- **Bedrohungs-Score:** 80 (Konfidenz 70%)", lines);
    }

    [Fact]
    public void SectionMarkers_BecomeHeadings()
    {
        var lines = Render("Personenakte\n— Aliase (2) —\nAli, Bob");

        var heading = Array.IndexOf(lines, "## Aliase (2)");
        Assert.True(heading > 0);
        // a blank line on both sides, or a renderer glues it to the paragraph
        Assert.Equal("", lines[heading - 1]);
        Assert.Equal("", lines[heading + 1]);
        Assert.Equal("Ali, Bob", lines[heading + 2]);
    }

    [Fact]
    public void Bullets_BecomeListItems()
    {
        Assert.Contains("- Hawk: Erste Zeile", Render("Personenakte\n— Kommentare (1) —\n• Hawk: Erste Zeile"));
    }

    [Fact]
    public void FreeText_ThatWouldOpenABlock_IsEscaped()
    {
        var lines = Render("Personenakte\n— Kommentare (1) —\n• > zitiert\n# keine Überschrift\n1. kein Punkt");

        Assert.Contains("- \\> zitiert", lines);
        Assert.Contains("\\# keine Überschrift", lines);
        Assert.Contains("1\\. kein Punkt", lines);
    }

    [Fact]
    public void Foot_CarriesTheReleaseNote()
    {
        var lines = Render("Personenakte");

        Assert.Equal("*NOOSE-interne Akte · Weitergabe nur nach Freigabe durch die Führung.*", lines[^1]);
        Assert.Equal("---", lines[^3]);
    }

    [Fact]
    public void BlankLines_NeverPileUp()
    {
        var lines = Render("Personenakte\n\n\n— Aliase (1) —\n\nAli");

        Assert.DoesNotContain(lines.Zip(lines.Skip(1)), pair => pair.First == "" && pair.Second == "");
    }

    [Theory]
    [InlineData("Max Mustermann (NOOSE-P-2026-0001)", "dossier-max-mustermann-noose-p-2026-0001.md")]
    [InlineData("Müller & Söhne (NOOSE-F-2026-0002)", "dossier-mueller-soehne-noose-f-2026-0002.md")]
    [InlineData("Straße ÄÖÜ", "dossier-strasse-aeoeue.md")]
    [InlineData("   ", "dossier.md")]
    [InlineData("★★★", "dossier.md")]
    public void FileName_IsPlainLowercaseAscii(string title, string expected)
    {
        Assert.Equal(expected, DossierMarkdown.FileName(title));
    }

    [Fact]
    public void FileName_StaysShort()
    {
        Assert.True(DossierMarkdown.FileName(new string('a', 300)).Length <= 100);
    }
}
