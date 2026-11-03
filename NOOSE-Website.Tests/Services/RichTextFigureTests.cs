using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The figure round trip: the editor types a caption line, the stored row carries figcaption.</summary>
public sealed class RichTextFigureTests
{
    [Fact]
    public void ToStored_WrapsAnImageWithItsCaptionLine()
    {
        var stored = RichTextFigure.ToStored(
            "<p class=\"ql-align-center\"><img src=\"/dateien/textbilder/a\"></p>"
            + "<p class=\"noose-bildtext\">Lagebild Hafen</p>");

        Assert.Contains("<figure", stored);
        Assert.Contains("figcaption", stored);
        Assert.Contains("Lagebild Hafen", stored);
        Assert.Contains("ql-align-center", stored);
        Assert.DoesNotContain(RichTextFigure.CaptionClass, stored);
        Assert.True(stored.IndexOf("<img", StringComparison.Ordinal) < stored.IndexOf("</figure>", StringComparison.Ordinal));
    }

    /// <summary>Two pictures pasted into one line without an Enter between them. Folding that line used to keep
    /// the first and throw the rest away with the paragraph it replaced — before they were ever written to a file.</summary>
    [Fact]
    public void ToStored_LeavesALineCarryingMoreThanOneImageAlone()
    {
        var stored = RichTextFigure.ToStored(
            "<p><img src=\"/dateien/textbilder/a\"><img src=\"/dateien/textbilder/b\"></p>"
            + "<p class=\"noose-bildtext\">Vergleich</p>");

        Assert.DoesNotContain("<figure", stored);
        Assert.Contains("textbilder/a", stored);
        Assert.Contains("textbilder/b", stored);
    }

    /// <summary>Quill leaves a stray break in a block often enough. Counting it as content would switch the
    /// whole caption feature off, which is a worse failure than the one the strict count prevents.</summary>
    [Fact]
    public void ToStored_FoldsAnImageLineThatAlsoCarriesAStrayBreak()
    {
        var stored = RichTextFigure.ToStored(
            "<p><img src=\"/dateien/textbilder/a\"><br></p>"
            + "<p class=\"noose-bildtext\">Lagebild Hafen</p>");

        Assert.Contains("<figure", stored);
        Assert.Contains("figcaption", stored);
        Assert.Contains("Lagebild Hafen", stored);
    }

    /// <summary>A linked picture: the anchor is part of the line and must not be dropped either.</summary>
    [Fact]
    public void ToStored_LeavesALinkedImageAlone()
    {
        var stored = RichTextFigure.ToStored(
            "<p><a href=\"https://fibhq.com\"><img src=\"/dateien/textbilder/a\"></a></p>"
            + "<p class=\"noose-bildtext\">Quelle</p>");

        Assert.DoesNotContain("<figure", stored);
        Assert.Contains("href=\"https://fibhq.com\"", stored);
    }

    [Fact]
    public void ToStored_LeavesAnImageWithoutCaptionAlone()
    {
        const string html = "<p><img src=\"/dateien/textbilder/a\"></p>";

        Assert.Equal(html, RichTextFigure.ToStored(html));
    }

    [Fact]
    public void ToStored_LeavesTextBesideTheImageAlone()
    {
        var html = "<p>Vergleich <img src=\"/dateien/textbilder/a\"></p>"
            + "<p class=\"noose-bildtext\">kein Bildtext</p>";

        Assert.Equal(html, RichTextFigure.ToStored(html));
    }

    [Fact]
    public void ToStored_LeavesAnExistingFigureAlone()
    {
        const string html = "<figure><img src=\"/dateien/textbilder/a\"><figcaption>x</figcaption></figure>";

        Assert.Equal(html, RichTextFigure.ToStored(html));
    }

    [Fact]
    public void ToStored_LeavesHtmlWithoutPicturesAndCaptionsUntouched()
    {
        const string html = "<P>GROSS</P>";

        Assert.Equal(html, RichTextFigure.ToStored(html));
    }

    [Fact]
    public void ToEditor_UnwrapsIntoTypableLines()
    {
        var editor = RichTextFigure.ToEditor(
            "<figure class=\"ql-align-right\"><img src=\"/dateien/textbilder/a\"><figcaption>Lagebild</figcaption></figure>");

        Assert.DoesNotContain("<figure", editor);
        Assert.Contains("<img", editor);
        Assert.Contains("ql-align-right", editor);
        Assert.Contains(RichTextFigure.CaptionClass, editor);
        Assert.Contains("Lagebild", editor);
    }

    [Fact]
    public void ToEditor_UnwrapsAFigureWithoutCaption()
    {
        var editor = RichTextFigure.ToEditor("<figure><img src=\"/dateien/textbilder/a\"></figure>");

        Assert.DoesNotContain("<figure", editor);
        Assert.Contains("<img", editor);
        Assert.DoesNotContain(RichTextFigure.CaptionClass, editor);
    }

    [Fact]
    public void ToEditor_LeavesPlainHtmlUntouched()
    {
        // no figure in the input: the value must not be re-serialized, or every save would look like a change
        const string html = "<P>GROSS</P>";

        Assert.Equal(html, RichTextFigure.ToEditor(html));
    }

    [Fact]
    public void ToEditor_ReversesToStored()
    {
        var editor = "<p class=\"ql-align-center\"><img src=\"/dateien/textbilder/a\"></p>"
            + "<p class=\"noose-bildtext\">Lagebild Hafen</p>";

        var back = RichTextFigure.ToEditor(RichTextFigure.ToStored(editor));

        Assert.DoesNotContain("<figure", back);
        Assert.Contains(RichTextFigure.CaptionClass, back);
        Assert.Contains("Lagebild Hafen", back);
        Assert.Contains("ql-align-center", back);
    }
}
