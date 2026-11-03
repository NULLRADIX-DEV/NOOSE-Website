using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The skeleton an automatic press draft starts from.</summary>
public class PressDraftTextTests
{
    private static PublicWantedCard Card(string name, PublicWantedKind kind = PublicWantedKind.Fahndung)
        => new("FIB-FA-2026-0001", kind, name, null, false, HazardLevel.Medium, DateTime.UtcNow, []);

    [Fact]
    public void ForCapture_NamesTheSubjectAndThePublicCaseNumber()
    {
        var (title, teaser, html) = PressDraftText.ForCapture(Card("Frank Miller"));

        Assert.Contains("Frank Miller", title, StringComparison.Ordinal);
        Assert.Contains("FIB-FA-2026-0001", teaser, StringComparison.Ordinal);
        Assert.Contains("FIB-FA-2026-0001", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_EncodesTheSubstitutedName()
    {
        // the boundary where text becomes markup: an unencoded ampersand would be broken HTML on an anonymous page
        var (_, _, html) = PressDraftText.ForCapture(Card("Meyer & Sohn"));

        Assert.Contains("Meyer &amp; Sohn", html, StringComparison.Ordinal);
        // the bare character is gone, so nothing downstream has to guess whether it was already encoded
        Assert.DoesNotContain("Meyer & Sohn", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_EncodesUmlautsAsEntities()
    {
        // WebUtility.HtmlEncode is what the recruiting letters use; it writes non-ASCII as numeric entities, which
        // renders correctly and keeps the draft byte-safe whatever the column collation does
        var (_, _, html) = PressDraftText.ForCapture(Card("Müller"));

        Assert.Contains("M&#252;ller", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_EncodesMarkupInADisplayName()
    {
        var (_, _, html) = PressDraftText.ForCapture(Card("<script>alert(1)</script>"));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_CarriesNoTokenOfAnyOtherSystem()
    {
        // not built on the citizen template renderer, so none of the four token sets may appear in the result
        var (title, teaser, html) = PressDraftText.ForCapture(Card("Frank Miller"));

        foreach (var text in new[] { title, teaser, html })
        {
            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("@{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NAME", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BUERGER", text, StringComparison.Ordinal);
            Assert.DoesNotContain(PublicTemplateRenderer.Redaction, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(PublicWantedKind.Fahrzeug)]
    [InlineData(PublicWantedKind.Waffe)]
    public void ForCapture_SaysSecuredForAnItem(PublicWantedKind kind)
    {
        var (title, _, html) = PressDraftText.ForCapture(Card("XZ-42-1", kind));

        Assert.Contains("Sachfahndung", title, StringComparison.Ordinal);
        Assert.Contains("sichergestellt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("gefasst", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_SaysCaughtForAPerson()
    {
        var (title, _, html) = PressDraftText.ForCapture(Card("Frank Miller"));

        Assert.DoesNotContain("Sachfahndung", title, StringComparison.Ordinal);
        Assert.Contains("gefasst", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCapture_WrapsEveryLineInAParagraph()
    {
        var (_, _, html) = PressDraftText.ForCapture(Card("Frank Miller"));

        Assert.StartsWith("<p>", html, StringComparison.Ordinal);
        Assert.EndsWith("</p>", html, StringComparison.Ordinal);
    }
}
