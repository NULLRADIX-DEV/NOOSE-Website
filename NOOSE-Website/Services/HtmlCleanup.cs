using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace NOOSE_Website.Services;

/// <summary>Server-side HTML sanitizer for WYSIWYG content.</summary>
public static partial class HtmlCleanup
{
    /// <summary>Marker the editor leaves behind where an image was, while NOOSEI works on the text.</summary>
    public const string AiImagePlaceholderAttribute = "data-noosei-bild";

    /// <summary>Sanitizes HTML; never returns null.</summary>
    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        return Generate().Sanitize(html);
    }

    /// <summary>Sanitizes rich text that has just been given heading anchors, keeping them.</summary>
    /// <remarks>
    /// For <see cref="Infrastructure.RichTextHtmlInterceptor"/> and nothing else. It runs
    /// <see cref="RichTextAnchors.ToStored"/> on the carriers in <see cref="RichTextAnchorFields"/> and then
    /// cleans the result; the ordinary <see cref="Clean"/> would strip the ids it had just written.
    /// </remarks>
    public static string CleanWithAnchors(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        return Generate(allowHeadingIds: true).Sanitize(html);
    }

    /// <summary>Sanitizes NOOSEI diff markup: the same allowlist plus the ins/del marks the diff renderer adds.</summary>
    public static string CleanDiff(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        return Generate(allowDiffMarks: true).Sanitize(html);
    }

    /// <summary>Sanitizes editor HTML on its way to NOOSEI, keeping the image placeholder.</summary>
    /// <remarks>
    /// The editor swaps every base64 image for <c>data-noosei-bild="n"</c> before marshalling and puts the
    /// picture back on apply. A placeholder in <c>src</c> cannot work: it is a URI attribute, and any scheme
    /// outside the list below is dropped here — which silently deleted the image from the corrected document.
    /// </remarks>
    public static string CleanAiPayload(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        return Generate(allowImagePlaceholder: true).Sanitize(html);
    }

    /// <summary>Sanitizes an imported Google original with the wider import allowlist; never returns null.</summary>
    /// <remarks>
    /// Only for <c>Document.ImportedHtml</c>, which is rendered read-only and never loaded into the editor. The
    /// profile keeps what a Google export needs to look like itself - fonts, spacing, borders, cropped images,
    /// positioned pictures - but no classes, no scripts, no data: or remote URLs in styles, ids only with the
    /// <c>gdoc-</c> prefix the converter writes, and no position that could leave the document's box.
    /// </remarks>
    public static string CleanImported(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        return ImportSanitizer().Sanitize(html);
    }

    private static readonly string[] ImportTagNames =
    [
        "p", "br", "span", "b", "strong", "i", "em", "u", "s", "sub", "sup",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre", "code", "a", "img",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col", "div", "hr",
        "figure", "figcaption",
    ];

    private static readonly string[] ImportAttributeNames =
    [
        "href", "target", "rel", "style", "src", "alt", "title", "colspan", "rowspan", "width", "height",
        "start", "id",
    ];

    private static readonly string[] ImportCssPropertyNames =
    [
        "color", "background-color", "text-align", "font-size", "font-family", "font-weight", "font-style",
        "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style",
        "vertical-align", "line-height", "letter-spacing", "word-spacing", "text-indent", "text-transform",
        "white-space", "direction", "list-style-type", "list-style-position",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-color", "border-style", "border-width", "border-collapse", "border-spacing",
        "border-top", "border-right", "border-bottom", "border-left",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "overflow", "display", "transform", "position", "left", "top", "right", "bottom",
        "float", "clear", "table-layout",
    ];

    private static readonly string[] ImportSchemeNames = ["http", "https", "mailto"];

    private static HtmlSanitizer ImportSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in ImportTagNames)
        {
            s.AllowedTags.Add(tag);
        }
        s.AllowedAttributes.Clear();
        foreach (var attr in ImportAttributeNames)
        {
            s.AllowedAttributes.Add(attr);
        }
        s.AllowedCssProperties.Clear();
        foreach (var prop in ImportCssPropertyNames)
        {
            s.AllowedCssProperties.Add(prop);
        }
        s.AllowedSchemes.Clear();
        foreach (var scheme in ImportSchemeNames)
        {
            s.AllowedSchemes.Add(scheme);
        }
        s.AllowedAtRules.Clear();
        s.AllowedClasses.Clear();
        s.AllowedAttributes.Remove("class");

        // a style that leaves the box (fixed/sticky) or loads something (url) has no business in a document
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
            {
                return;
            }
            if (element.GetAttribute("id") is { } id && !id.StartsWith("gdoc-", StringComparison.Ordinal))
            {
                element.RemoveAttribute("id");
            }
            if (element.GetAttribute("style") is { } style
                && (PinnedPosition().IsMatch(style) || UrlDeclaration().IsMatch(style)))
            {
                var kept = UrlDeclaration().Replace(PinnedPosition().Replace(style, string.Empty), string.Empty).Trim();
                if (kept.Length == 0)
                {
                    element.RemoveAttribute("style");
                }
                else
                {
                    element.SetAttribute("style", kept);
                }
            }
            if (element is { NodeName: "A" } anchor
                && anchor.GetAttribute("href")?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
            {
                anchor.RemoveAttribute("href");
            }
        };
        return s;
    }

    [GeneratedRegex(@"position\s*:\s*(fixed|sticky)\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex PinnedPosition();

    [GeneratedRegex(@"[\w-]+\s*:[^;]*url\([^)]*\)[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlDeclaration();

    /// <summary>Tags out, entities decoded, whitespace collapsed to single spaces. For search snippets, Discord
    /// embeds, LLM context and emptiness probes.</summary>
    /// <remarks>
    /// NOT a sanitizer — the result is meant to be rendered as TEXT; use <see cref="Clean"/> for anything that
    /// stays markup. Regex rather than a <see cref="Clean"/>-then-strip round trip because that is a full AngleSharp
    /// parse, and the search path runs this over dozens of rows per category.
    /// Block structure is deliberately lost: a caller that needs paragraph breaks (the applicant letter) keeps its
    /// own converter, because collapsing its newlines would run the whole letter together.
    /// </remarks>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        // space, not empty: stripping <b>a</b><b>b</b> to "ab" glues neighbouring words together
        var text = WebUtility.HtmlDecode(TagStrip().Replace(html, " "));
        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>Plain text to paragraphs: one <c>&lt;p&gt;</c> per line, escaped.</summary>
    /// <remarks>
    /// The counterpart of <see cref="PlainText"/>, for the few places that take typed text into a field the rest
    /// of the house fills with a WYSIWYG editor - the quick-capture activity is the first. Encoding matters more
    /// than the markup: without it a typed angle bracket would reach the sanitizer as a tag and be dropped, so
    /// the text would lose a character it was never meant to lose.
    /// </remarks>
    public static string FromPlain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        // blank lines carry no meaning here; two paragraphs already read as a break
        var lines = text.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Concat(lines.Select(l => $"<p>{Escape(l)}</p>"));
    }

    /// <summary>The three characters that must not reach the parser as markup, and nothing else.</summary>
    /// <remarks>
    /// Deliberately not <c>WebUtility.HtmlEncode</c>: that turns every character above 159 into a numeric
    /// entity, so a German sentence would be stored as <c>Pr&amp;#252;ffall</c> - unreadable in the column and in
    /// the audit log, and no longer its own output, because the sanitizer decodes the entity straight back.
    /// Inside a text node these three are the whole job; quotes only matter in an attribute.
    /// </remarks>
    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagStrip();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Allowlist of the sanitizer and of the editor's paste cleaner; one table, two consumers.</summary>
    public sealed record ContentProfile(
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<string> CssProperties,
        IReadOnlyList<string> Schemes);

    private static readonly string[] AllowedTagNames =
    [
        "p", "br", "span", "b", "strong", "i", "em", "u", "s",
        "h1", "h2", "h3", "ul", "ol", "li", "blockquote", "pre", "code", "a", "img",
        "table", "thead", "tbody", "tr", "td", "th", "caption", "colgroup", "col", "div", "contain",
        "figure", "figcaption", "hr",
    ];

    private static readonly string[] AllowedAttributeNames =
    [
        // "id" is deliberately NOT here: it is allowed on headings only, see the RemovingAttribute hook below
        "href", "target", "rel", "class", "style", "src", "alt",
        "colspan", "rowspan", "width", "cellpadding", "cellspacing", "contenteditable",
        "data-table-id", "data-row-id", "data-col-id", "data-rowspan", "data-colspan",
        "data-row", "data-col", "data-w", "data-full", "data-checked",
    ];

    private static readonly string[] AllowedCssPropertyNames =
    [
        "color", "background-color", "text-align", "font-size",
        "width", "height", "vertical-align",
        "border", "border-color", "border-style", "border-width",
    ];

    private static readonly string[] AllowedSchemeNames =
    [
        "http", "https", "mailto",
        "data", // pasted images arrive as data uris
    ];

    /// <summary>What the sanitizer keeps; the editor cleans a paste against the same lists.</summary>
    /// <remarks>
    /// "id" is not here either. A pasted anchor is never worth keeping: <see cref="RichTextAnchors"/> assigns the
    /// ids on save, from the heading text, for the carriers registered in <see cref="RichTextAnchorFields"/> — a
    /// name carried in from somewhere else would only collide with one of those or with the page's own.
    /// </remarks>
    public static ContentProfile Profile { get; } = new(
        AllowedTagNames, AllowedAttributeNames, AllowedCssPropertyNames, AllowedSchemeNames);

    private static HtmlSanitizer Generate(
        bool allowDiffMarks = false, bool allowImagePlaceholder = false, bool allowHeadingIds = false)
    {
        var s = new HtmlSanitizer();

        s.AllowedTags.Clear();
        foreach (var tag in AllowedTagNames)
        {
            s.AllowedTags.Add(tag);
        }
        if (allowDiffMarks)
        {
            s.AllowedTags.Add("ins");
            s.AllowedTags.Add("del");
        }

        s.AllowedAttributes.Clear();
        foreach (var attr in AllowedAttributeNames)
        {
            s.AllowedAttributes.Add(attr);
        }
        if (allowImagePlaceholder)
        {
            s.AllowedAttributes.Add(AiImagePlaceholderAttribute);
        }

        s.AllowedCssProperties.Clear();
        foreach (var prop in AllowedCssPropertyNames)
        {
            s.AllowedCssProperties.Add(prop);
        }

        s.AllowedSchemes.Clear();
        foreach (var scheme in AllowedSchemeNames)
        {
            s.AllowedSchemes.Add(scheme);
        }

        // id survives on headings, and only in the one pass that writes them: RichTextAnchors has just assigned
        // them inside the interceptor and this call cleans its output. Everywhere else it goes, because an id is
        // a document-wide name - in a comment, a ticket or a public page an author could otherwise shadow an id
        // the page itself uses, redirect an in-page link, or break a label/aria reference, from any field that
        // accepts rich text. H4-H6 are not listed because they are not allowed tags to begin with.
        if (allowHeadingIds)
        {
            s.RemovingAttribute += (_, e) =>
            {
                if (e.Reason == RemoveReason.NotAllowedAttribute
                    && string.Equals(e.Attribute.Name, "id", StringComparison.OrdinalIgnoreCase)
                    && e.Tag.NodeName is "H1" or "H2" or "H3")
                {
                    e.Cancel = true;
                }
            };
        }

        // data: stays image-only; a data: href is a phishing vector
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement { NodeName: "A" } anchor
                && anchor.GetAttribute("href")?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
            {
                anchor.RemoveAttribute("href");
            }
        };

        return s;
    }
}
