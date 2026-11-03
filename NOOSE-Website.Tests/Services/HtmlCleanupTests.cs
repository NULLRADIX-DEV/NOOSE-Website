using NOOSE_Website.Services;
using Xunit;

namespace NOOSE_Website.Tests.Services;

public class HtmlCleanupTests
{
    // ---- null / empty / whitespace guard ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t \r\n  ")]
    public void Clean_NullEmptyOrWhitespace_ReturnsEmptyString(string? input)
    {
        Assert.Equal(string.Empty, HtmlCleanup.Clean(input));
    }

    [Fact]
    public void Clean_Null_NeverReturnsNull()
    {
        Assert.NotNull(HtmlCleanup.Clean(null));
    }

    // ---- the NOOSEI image placeholder ----

    [Fact]
    public void CleanAiPayload_KeepsTheImagePlaceholder_WhileCleanDropsIt()
    {
        const string html = "<p>Text</p><p><img data-noosei-bild=\"0\"></p>";

        Assert.Contains("data-noosei-bild=\"0\"", HtmlCleanup.CleanAiPayload(html));
        // everywhere else the marker is meaningless and has no business being stored
        Assert.DoesNotContain("data-noosei-bild", HtmlCleanup.Clean(html));
    }

    [Fact]
    public void CleanAiPayload_StillDropsAnUnknownScheme()
    {
        // why the marker is an attribute and not a fake src: src is a URI attribute and gets sanitized
        Assert.DoesNotContain("noosei-bild", HtmlCleanup.CleanAiPayload("<p><img src=\"noosei-bild:0\"></p>"));
        Assert.DoesNotContain("javascript", HtmlCleanup.CleanAiPayload("<p><a href=\"javascript:alert(1)\">x</a></p>"));
    }

    // ---- PlainText: markup out, readable text in ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public void PlainText_NullEmptyOrWhitespace_ReturnsEmptyString(string? input)
    {
        Assert.Equal(string.Empty, HtmlCleanup.PlainText(input));
    }

    [Fact]
    public void PlainText_StripsTagsAndCollapsesWhitespace()
    {
        Assert.Equal("Hallo Welt", HtmlCleanup.PlainText("<p>Hallo</p>\n\n<p>  Welt  </p>"));
    }

    [Fact]
    public void PlainText_SeparatesAdjacentInlineElements()
    {
        // replacing a tag with "" glues neighbouring words: "abc" instead of "ab c"
        Assert.Equal("ab c", HtmlCleanup.PlainText("<b>ab</b> <i>c</i>"));
        Assert.Equal("ab c", HtmlCleanup.PlainText("<b>ab</b><i>c</i>"));
    }

    [Fact]
    public void PlainText_DecodesEntities()
    {
        Assert.Equal("Müller & Sohn", HtmlCleanup.PlainText("<p>M&uuml;ller &amp; Sohn</p>"));
        Assert.Equal("a b", HtmlCleanup.PlainText("<p>a&nbsp;b</p>"));
    }

    [Fact]
    public void PlainText_OfAnEmptyQuillDocument_IsEmpty()
    {
        // the emptiness probe the personnel notes rely on
        Assert.Equal(string.Empty, HtmlCleanup.PlainText("<p><br></p>"));
    }

    [Fact]
    public void PlainText_DropsAttributeValues()
    {
        // a term that only matched inside an attribute is not in the snippet — SearchSnippet falls back for it
        Assert.Equal("Text", HtmlCleanup.PlainText("<p style=\"color:red\" title=\"geheim\">Text</p>"));
    }

    // ---- plain text passthrough ----

    [Fact]
    public void Clean_PlainText_ReturnedUnchanged()
    {
        const string input = "Just plain text here";
        Assert.Equal(input, HtmlCleanup.Clean(input));
    }

    [Fact]
    public void Clean_PlainTextWithDigitsAndPunctuation_Preserved()
    {
        const string input = "Report 42, updated. All good!";
        Assert.Equal(input, HtmlCleanup.Clean(input));
    }

    // ---- safe formatting tags are kept ----

    [Theory]
    [InlineData("p")]
    [InlineData("span")]
    [InlineData("b")]
    [InlineData("strong")]
    [InlineData("i")]
    [InlineData("em")]
    [InlineData("u")]
    [InlineData("s")]
    [InlineData("h1")]
    [InlineData("h2")]
    [InlineData("h3")]
    [InlineData("blockquote")]
    [InlineData("pre")]
    [InlineData("code")]
    [InlineData("div")]
    public void Clean_AllowedFormattingTag_IsPreserved(string tag)
    {
        var result = HtmlCleanup.Clean($"<{tag}>content</{tag}>");

        Assert.Contains($"<{tag}", result);
        Assert.Contains("content", result);
    }

    [Fact]
    public void Clean_CustomAllowedContainTag_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<contain>wrapped</contain>");

        Assert.Contains("contain", result);
        Assert.Contains("wrapped", result);
    }

    [Fact]
    public void Clean_UnorderedList_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<ul><li>one</li><li>two</li></ul>");

        Assert.Contains("<ul", result);
        Assert.Contains("<li", result);
        Assert.Contains("one", result);
        Assert.Contains("two", result);
    }

    [Fact]
    public void Clean_OrderedList_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<ol><li>alpha</li></ol>");

        Assert.Contains("<ol", result);
        Assert.Contains("<li", result);
        Assert.Contains("alpha", result);
    }

    [Fact]
    public void Clean_Table_IsPreserved()
    {
        var result = HtmlCleanup.Clean(
            "<table><thead><tr><th>H</th></tr></thead><tbody><tr><td>cell</td></tr></tbody></table>");

        Assert.Contains("<table", result);
        Assert.Contains("<td", result);
        Assert.Contains("cell", result);
    }

    [Fact]
    public void Clean_LineBreak_IsPreserved()
    {
        var result = HtmlCleanup.Clean("first<br>second");

        Assert.Contains("<br", result);
        Assert.Contains("first", result);
        Assert.Contains("second", result);
    }

    [Fact]
    public void Clean_NestedFormatting_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<p><strong>Hi</strong> <em>there</em></p>");

        Assert.Contains("<strong", result);
        Assert.Contains("<em", result);
        Assert.Contains("Hi", result);
        Assert.Contains("there", result);
    }

    // ---- dangerous / disallowed tags are stripped ----

    [Fact]
    public void Clean_ScriptTag_RemovesScriptAndPayload()
    {
        var result = HtmlCleanup.Clean("<p>Hello</p><script>alert('xss')</script>");

        Assert.DoesNotContain("<script", result);
        Assert.DoesNotContain("alert", result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void Clean_ScriptOnly_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlCleanup.Clean("<script>doEvil()</script>"));
    }

    [Theory]
    [InlineData("iframe")]
    [InlineData("object")]
    [InlineData("embed")]
    [InlineData("form")]
    [InlineData("input")]
    [InlineData("button")]
    [InlineData("style")]
    [InlineData("link")]
    [InlineData("meta")]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("svg")]
    public void Clean_DisallowedTag_IsStripped(string tag)
    {
        var result = HtmlCleanup.Clean($"<{tag}>x</{tag}>");

        Assert.DoesNotContain($"<{tag}", result);
    }

    [Fact]
    public void Clean_ImageTag_IsPreservedWithSiblingText()
    {
        var result = HtmlCleanup.Clean("<img src=\"x.jpg\">Caption");

        Assert.Contains("<img", result);
        Assert.Contains("Caption", result);
    }

    // ---- images ----

    [Fact]
    public void Clean_ImageWithHttpsUrl_KeepsSrc()
    {
        var result = HtmlCleanup.Clean("<img src=\"https://example.com/bild.png\">");

        Assert.Contains("<img", result);
        Assert.Contains("https://example.com/bild.png", result);
    }

    [Fact]
    public void Profile_NamesTheAllowlistTheEditorCleansAgainst()
    {
        // richtext.js builds its paste cleaner from this list; a missing entry makes editor and storage disagree
        var profile = HtmlCleanup.Profile;

        Assert.Contains("figure", profile.Tags);
        Assert.Contains("figcaption", profile.Tags);
        Assert.Contains("hr", profile.Tags);
        Assert.Contains("img", profile.Tags);
        // deliberately absent: an anchor is assigned on save, never carried in from a paste
        Assert.DoesNotContain("id", profile.Attributes);
        Assert.Contains("data-checked", profile.Attributes);
        Assert.Contains("src", profile.Attributes);
        Assert.Contains("width", profile.CssProperties);
        Assert.Contains("color", profile.CssProperties);
        Assert.Contains("data", profile.Schemes);
        Assert.DoesNotContain("script", profile.Tags);
        Assert.DoesNotContain("iframe", profile.Tags);
        Assert.DoesNotContain("onerror", profile.Attributes);
    }

    [Fact]
    public void Clean_HeadingIdAndDivider_AreKept()
    {
        // the divider embed survives the ordinary pass; the anchor does not - only the pass that has just
        // written it does, so no other rich-text field can carry a document-wide name in from outside
        var result = HtmlCleanup.Clean("<h2 id=\"lagebild\">Lagebild</h2><hr>");

        Assert.DoesNotContain("id=\"lagebild\"", result);
        Assert.Contains("Lagebild", result);
        Assert.Contains("<hr", result);
    }

    [Fact]
    public void CleanWithAnchors_KeepsAHeadingIdAndNothingElses()
    {
        // the interceptor's pass: RichTextAnchors has just assigned these ids and they have to survive
        var result = HtmlCleanup.CleanWithAnchors(
            "<h2 id=\"lagebild\">Lagebild</h2><p id=\"absatz\">Text</p><div id=\"kasten\">Rand</div>");

        Assert.Contains("id=\"lagebild\"", result);
        Assert.DoesNotContain("id=\"absatz\"", result);
        Assert.DoesNotContain("id=\"kasten\"", result);
    }

    [Fact]
    public void Clean_FigureWithCaptionAndScript_KeepsTheFigureOnly()
    {
        // the stored shape of a caption is a real figure
        var result = HtmlCleanup.Clean(
            "<figure class=\"ql-align-center\"><img src=\"/dateien/textbilder/a\">"
            + "<figcaption>Lagebild<script>steal()</script></figcaption></figure>");

        Assert.Contains("<figure", result);
        Assert.Contains("<figcaption", result);
        Assert.Contains("Lagebild", result);
        Assert.DoesNotContain("script", result);
    }

    [Fact]
    public void Clean_ImageWithRelativeFileUrl_KeepsSrc()
    {
        // the rich-text interceptor rewrites base64 into this shape
        var result = HtmlCleanup.Clean("<p>x</p><img src=\"/dateien/textbilder/abc-123\">");

        Assert.Contains("<img", result);
        Assert.Contains("/dateien/textbilder/abc-123", result);
    }

    [Fact]
    public void Clean_ImageWithDataUri_KeepsSrc()
    {
        // quill embeds images as base64 data URIs
        const string src = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        var result = HtmlCleanup.Clean($"<img src=\"{src}\">");

        Assert.Contains("<img", result);
        Assert.Contains(src, result);
    }

    [Fact]
    public void Clean_ImageAltAndSizeAttributes_ArePreserved()
    {
        var result = HtmlCleanup.Clean("<img src=\"x.jpg\" alt=\"Lagebild\" width=\"200\">");

        Assert.Contains("alt", result);
        Assert.Contains("Lagebild", result);
        Assert.Contains("width", result);
    }

    [Fact]
    public void Clean_ImageEventHandler_IsRemoved()
    {
        var result = HtmlCleanup.Clean("<img src=\"x.jpg\" onerror=\"evil()\">");

        Assert.Contains("<img", result);
        Assert.DoesNotContain("onerror", result);
        Assert.DoesNotContain("evil", result);
    }

    [Fact]
    public void Clean_ImageWithJavascriptScheme_SrcIsRemoved()
    {
        var result = HtmlCleanup.Clean("<img src=\"javascript:alert(1)\">");

        Assert.DoesNotContain("javascript", result);
    }

    // ---- event-handler / disallowed attributes are stripped ----

    [Fact]
    public void Clean_InlineEventHandler_IsRemoved()
    {
        var result = HtmlCleanup.Clean("<p onclick=\"evil()\">text</p>");

        Assert.DoesNotContain("onclick", result);
        Assert.DoesNotContain("evil", result);
        Assert.Contains("text", result);
    }

    [Fact]
    public void Clean_DisallowedIdAttribute_IsRemoved()
    {
        var result = HtmlCleanup.Clean("<p id=\"main\">body</p>");

        Assert.DoesNotContain("main", result);
        Assert.Contains("<p", result);
        Assert.Contains("body", result);
    }

    // ---- allowed attributes are kept ----

    [Fact]
    public void Clean_ClassAttribute_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<p class=\"lead\">x</p>");

        Assert.Contains("class", result);
        Assert.Contains("lead", result);
    }

    [Fact]
    public void Clean_ContenteditableAttribute_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<div contenteditable=\"true\">x</div>");

        Assert.Contains("contenteditable", result);
    }

    [Fact]
    public void Clean_DataAttribute_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<div data-w=\"5\">x</div>");

        Assert.Contains("data-w", result);
    }

    [Fact]
    public void Clean_Checklist_KeepsDataChecked()
    {
        // quill puts data-checked on the ul, not on the li (see the vendored list blot)
        var result = HtmlCleanup.Clean("<ul data-checked=\"true\"><li>done</li></ul><ul data-checked=\"false\"><li>open</li></ul>");

        Assert.Contains("data-checked=\"true\"", result);
        Assert.Contains("data-checked=\"false\"", result);
    }

    [Fact]
    public void Clean_UnknownDataAttribute_IsDropped()
    {
        var result = HtmlCleanup.Clean("<p data-foo=\"x\">text</p>");

        Assert.DoesNotContain("data-foo", result);
        Assert.Contains("text", result);
    }

    [Fact]
    public void Clean_IndentAndAlignClasses_ArePreserved()
    {
        var result = HtmlCleanup.Clean("<p class=\"ql-align-center ql-indent-1\">x</p>");

        Assert.Contains("ql-align-center", result);
        Assert.Contains("ql-indent-1", result);
    }

    [Fact]
    public void Clean_TableColspanAttribute_IsPreserved()
    {
        var result = HtmlCleanup.Clean(
            "<table><tbody><tr><td colspan=\"2\">c</td></tr></tbody></table>");

        Assert.Contains("colspan", result);
    }

    // ---- link schemes ----

    [Fact]
    public void Clean_HttpsLink_KeepsHref()
    {
        var result = HtmlCleanup.Clean("<a href=\"https://example.com\">link</a>");

        Assert.Contains("href", result);
        Assert.Contains("https://example.com", result);
    }

    [Fact]
    public void Clean_HttpLink_KeepsHref()
    {
        var result = HtmlCleanup.Clean("<a href=\"http://example.com\">link</a>");

        Assert.Contains("http://example.com", result);
    }

    [Fact]
    public void Clean_MailtoLink_KeepsHref()
    {
        var result = HtmlCleanup.Clean("<a href=\"mailto:agent@fibhq.com\">mail</a>");

        Assert.Contains("mailto:", result);
    }

    [Fact]
    public void Clean_LinkTargetAndRel_ArePreserved()
    {
        var result = HtmlCleanup.Clean(
            "<a href=\"https://x.com\" target=\"_blank\" rel=\"noopener\">y</a>");

        Assert.Contains("target", result);
        Assert.Contains("rel", result);
    }

    [Fact]
    public void Clean_JavascriptScheme_HrefIsRemoved()
    {
        var result = HtmlCleanup.Clean("<a href=\"javascript:alert(1)\">click</a>");

        Assert.DoesNotContain("javascript", result);
        Assert.Contains("click", result);
    }

    [Theory]
    [InlineData("ftp://host/file")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("vbscript:msgbox(1)")]
    public void Clean_DisallowedScheme_HrefIsRemoved(string href)
    {
        var result = HtmlCleanup.Clean($"<a href=\"{href}\">link</a>");

        Assert.DoesNotContain("href", result);
        Assert.Contains("link", result);
    }

    // ---- CSS property filtering ----

    [Fact]
    public void Clean_AllowedCssColor_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<span style=\"color: red\">x</span>");

        Assert.Contains("color", result);
    }

    [Fact]
    public void Clean_AllowedCssTextAlign_IsPreserved()
    {
        var result = HtmlCleanup.Clean("<p style=\"text-align: center\">x</p>");

        Assert.Contains("text-align", result);
    }

    [Theory]
    [InlineData("position: absolute", "absolute")]
    [InlineData("display: none", "none")]
    [InlineData("float: left", "left")]
    public void Clean_DisallowedCssProperty_IsRemoved(string style, string marker)
    {
        var result = HtmlCleanup.Clean($"<span style=\"{style}\">x</span>");

        Assert.DoesNotContain(marker, result);
        Assert.Contains("<span", result);
    }

    // ---- general robustness ----

    [Fact]
    public void Clean_MixedAllowedAndDangerousContent_KeepsSafeDropsRest()
    {
        var result = HtmlCleanup.Clean(
            "<p>Safe <b>bold</b></p><script>steal()</script><iframe src=\"http://evil\"></iframe>");

        Assert.Contains("<p", result);
        Assert.Contains("<b", result);
        Assert.Contains("bold", result);
        Assert.DoesNotContain("<script", result);
        Assert.DoesNotContain("steal", result);
        Assert.DoesNotContain("<iframe", result);
    }

    [Fact]
    public void Clean_AnyInput_NeverReturnsNull()
    {
        Assert.NotNull(HtmlCleanup.Clean("<b>x</b>"));
        Assert.NotNull(HtmlCleanup.Clean("<script>x</script>"));
        Assert.NotNull(HtmlCleanup.Clean("plain"));
    }

    // ---- FromPlain: typed text in, paragraphs out ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t \r\n  ")]
    public void FromPlain_NullEmptyOrWhitespace_ReturnsEmptyString(string? input)
    {
        Assert.Equal(string.Empty, HtmlCleanup.FromPlain(input));
    }

    [Fact]
    public void FromPlain_Null_NeverReturnsNull()
    {
        Assert.NotNull(HtmlCleanup.FromPlain(null));
    }

    [Fact]
    public void FromPlain_SingleLine_IsExactlyOneParagraph()
    {
        Assert.Equal("<p>Hallo Welt</p>", HtmlCleanup.FromPlain("Hallo Welt"));
    }

    [Fact]
    public void FromPlain_SingleLine_IsTrimmed()
    {
        Assert.Equal("<p>Hallo Welt</p>", HtmlCleanup.FromPlain("  Hallo Welt  "));
    }

    [Fact]
    public void FromPlain_SeveralLines_AreSeveralParagraphs()
    {
        Assert.Equal("<p>eins</p><p>zwei</p><p>drei</p>", HtmlCleanup.FromPlain("eins\nzwei\ndrei"));
    }

    [Fact]
    public void FromPlain_BlankLinesBetween_ProduceNoEmptyParagraph()
    {
        // an empty <p> is a visible gap in the record; two paragraphs already read as a break
        Assert.Equal("<p>eins</p><p>zwei</p>", HtmlCleanup.FromPlain("eins\n\n\nzwei"));
        Assert.Equal("<p>eins</p><p>zwei</p>", HtmlCleanup.FromPlain("eins\n   \nzwei"));
    }

    [Fact]
    public void FromPlain_LeadingAndTrailingNewlines_AreDropped()
    {
        Assert.Equal("<p>eins</p>", HtmlCleanup.FromPlain("\n\neins\n\n"));
    }

    [Fact]
    public void FromPlain_CrlfAndLf_AreTreatedAlike()
    {
        // a browser textarea sends CRLF, seeded and generated text arrives with LF
        Assert.Equal(HtmlCleanup.FromPlain("eins\nzwei"), HtmlCleanup.FromPlain("eins\r\nzwei"));
        Assert.Equal("<p>eins</p><p>zwei</p>", HtmlCleanup.FromPlain("eins\r\nzwei"));
    }

    [Fact]
    public void FromPlain_HtmlSpecialCharacters_AreEncoded()
    {
        // without encoding a typed angle bracket pair reaches the sanitizer as a tag and is deleted:
        // the text would lose a character it should never lose
        Assert.Equal("<p>&lt;b&gt;</p>", HtmlCleanup.FromPlain("<b>"));
        Assert.Equal("<p>a &amp; b</p>", HtmlCleanup.FromPlain("a & b"));
        Assert.Equal("<p>3 &lt; 5</p>", HtmlCleanup.FromPlain("3 < 5"));
    }

    [Fact]
    public void FromPlain_Umlauts_ArePassedThroughVerbatim()
    {
        // German is the whole content language here; a numeric entity would be a needless detour
        Assert.Equal("<p>Prüffall geöffnet, Maß größer</p>", HtmlCleanup.FromPlain("Prüffall geöffnet, Maß größer"));
    }

    [Fact]
    public void FromPlain_MentionToken_SurvivesUntouched()
    {
        // braces and the at sign are not encoded; the renderer resolves the token later
        const string token = "@{Person:11111111-1111-1111-1111-111111111111}";

        Assert.Equal($"<p>Siehe {token}</p>", HtmlCleanup.FromPlain($"Siehe {token}"));
    }

    [Fact]
    public void FromPlain_Result_SurvivesCleanUnchanged()
    {
        // the output is stored through the sanitizer, so this round trip has to be lossless
        var html = HtmlCleanup.FromPlain("Beobachtung am Hafen\n\nZwei Fahrzeuge, <b> getippt, a & b");

        Assert.Equal(html, HtmlCleanup.Clean(html));
    }
}
