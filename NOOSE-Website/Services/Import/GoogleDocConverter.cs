using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace NOOSE_Website.Services.Import;

/// <summary>One picture out of a Google export, keyed by its path inside the zip.</summary>
public sealed record ImportedImage(string Path, string ContentType, byte[] Bytes);

/// <summary>What a Google export turns into: the 1:1 original, the editable copy and the fonts the original uses.</summary>
public sealed record GoogleDocConversion(
    string? Title,
    string OriginalHtml,
    string EditableHtml,
    IReadOnlyList<string> FontFamilies);

/// <summary>Turns Google's zipped HTML export into the two HTML fragments a library document stores.</summary>
/// <remarks>
/// Google styles its export through one <c>&lt;style&gt;</c> block with generated class names, and the sanitizer
/// keeps neither the block nor the classes. So the rules are folded into each element's own <c>style</c> first,
/// in cascade order, and only then sanitized - the original keeps its look without a single class.
/// Bullets and numbers are drawn by Google with <c>::before</c> rules, which no inline style can carry; they are
/// mapped onto native list markers and an <c>ol start</c> that continues a list across interruptions.
/// Pure apart from the picture callback, so every rule here is unit-testable on a hand-made export.
/// </remarks>
public static partial class GoogleDocConverter
{
    public const int MaxEntries = 500;
    public const long MaxEntryBytes = 25L * 1024 * 1024;
    public const long MaxTotalBytes = 60L * 1024 * 1024;

    /// <summary>Fonts every browser has; no stylesheet needs to be loaded for them.</summary>
    private static readonly HashSet<string> SystemFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Arial", "Helvetica", "Times New Roman", "Times", "Courier New", "Courier", "Georgia", "Verdana",
        "Trebuchet MS", "Comic Sans MS", "Impact", "Tahoma", "Calibri", "Cambria", "sans-serif", "serif",
        "monospace", "cursive", "fantasy", "system-ui", "inherit",
    };

    /// <summary>Declarations that only concern paged media or vendor prefixes.</summary>
    private static readonly string[] DroppedPrefixes = ["-webkit-", "-moz-", "-ms-", "orphans", "widows", "page-break", "break-"];

    /// <summary>Reads the export zip: exactly one HTML file at the root plus its pictures.</summary>
    /// <remarks>Everything stays in memory and is keyed by name, so a path like <c>../x</c> can only fail to match
    /// - nothing is ever written to disk. Entry count, entry size and the total are capped against a zip bomb, by
    /// the bytes actually read, not by what the headers claim.</remarks>
    public static (string Html, IReadOnlyDictionary<string, ImportedImage> Images) ReadZip(byte[] zip)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
            {
                throw new GoogleExportException("Die Datei enthält zu viele Einträge.");
            }
            string? html = null;
            var images = new Dictionary<string, ImportedImage>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var isHtml = HtmlEntry().IsMatch(name);
                var imageType = ImageType(name);
                if (!isHtml && imageType is null)
                {
                    continue;
                }
                using var stream = entry.Open();
                var bytes = ReadCapped(stream, MaxEntryBytes);
                total += bytes.Length;
                if (total > MaxTotalBytes)
                {
                    throw new GoogleExportException("Die Datei ist entpackt zu groß.");
                }
                if (isHtml)
                {
                    if (html is not null)
                    {
                        throw new GoogleExportException("Die Datei enthält mehr als ein Dokument.");
                    }
                    html = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    images[name] = new ImportedImage(name, imageType!, bytes);
                }
            }
            return (html ?? throw new GoogleExportException(
                "In der Datei steckt kein Google-Dokument. Lade in Google „Herunterladen → Webseite (.html, gezippt)“ herunter."),
                images);
        }
        catch (InvalidDataException)
        {
            throw new GoogleExportException("Die Datei ist kein gültiges Zip-Archiv.");
        }
    }

    /// <summary>Converts the export's HTML; <paramref name="storeImage"/> files a picture and returns its URL, or null to drop it.</summary>
    public static async Task<GoogleDocConversion> ConvertAsync(string html, IReadOnlyDictionary<string, ImportedImage> images,
        Func<ImportedImage, Task<string?>> storeImage)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = TrimToNull(document.Title);

        var rules = ParseRules(string.Concat(document.QuerySelectorAll("style").Select(s => s.TextContent)));
        var listTypes = ListStyleTypes(rules);
        TagLists(document);
        var bodyStyle = InlineCascade(document, rules);

        RemoveComments(document);
        RewriteLinksAndIds(document);
        MapLists(document, listTypes);
        await ResolveImagesAsync(document, images, storeImage);

        var body = document.Body?.InnerHtml ?? string.Empty;
        var page = $"<div style=\"{Attr(bodyStyle)}\">{body}</div>";
        var original = HtmlCleanup.CleanImported(page);

        var editable = HtmlCleanup.Clean(ToEditable(document));
        return new GoogleDocConversion(title, original, editable, FontFamilies(original));
    }

    // ---------- CSS ----------

    /// <summary>One style rule of the export: a single selector with its declarations and source order.</summary>
    public sealed record CssRule(string Selector, IReadOnlyList<(string Name, string Value)> Declarations, int Order)
    {
        public (int Ids, int Classes, int Elements) Specificity { get; } = SpecificityOf(Selector);
    }

    /// <summary>Flat rules out of Google's machine-written stylesheet; at-rules are skipped.</summary>
    public static List<CssRule> ParseRules(string css)
    {
        var rules = new List<CssRule>();
        css = CssComment().Replace(css, string.Empty);
        css = AtImport().Replace(css, string.Empty);
        var order = 0;
        foreach (Match block in RuleBlock().Matches(css))
        {
            var selectors = block.Groups["sel"].Value;
            if (selectors.TrimStart().StartsWith('@'))
            {
                continue;
            }
            var declarations = ParseDeclarations(block.Groups["body"].Value);
            foreach (var selector in selectors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                rules.Add(new CssRule(selector, declarations, order++));
            }
        }
        return rules;
    }

    /// <summary>Declarations of one block or style attribute; <c>!important</c> is dropped, the last one wins.</summary>
    public static List<(string Name, string Value)> ParseDeclarations(string body)
    {
        var result = new List<(string, string)>();
        foreach (var part in SplitDeclarations(body))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (name.Length > 0 && value.Length > 0)
            {
                result.Add((name, value));
            }
        }
        return result;
    }

    /// <summary>Splits on semicolons outside quotes and parentheses.</summary>
    private static IEnumerable<string> SplitDeclarations(string body)
    {
        var start = 0;
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote is not null)
            {
                if (c == quote && body[i - 1] != '\\')
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ';' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start < body.Length)
        {
            yield return body[start..];
        }
    }

    private static (int, int, int) SpecificityOf(string selector)
    {
        var ids = selector.Count(c => c == '#');
        var classes = ClassOrAttribute().Matches(selector).Count;
        var elements = ElementName().Matches(selector).Count;
        return (ids, classes, elements);
    }

    /// <summary>Folds the rules into each element's style attribute; returns the body's declarations for the page wrapper.</summary>
    /// <remarks>Rules ascend by specificity and then source order, and the element's own style attribute is laid
    /// on top, exactly the order a browser would resolve. Pseudo-element rules cannot be inlined and are left to
    /// <see cref="ListStyleTypes"/>.</remarks>
    private static string InlineCascade(IHtmlDocument document, List<CssRule> rules)
    {
        var applied = new Dictionary<IElement, Dictionary<string, string>>();
        foreach (var rule in rules
                     .Where(r => !r.Selector.Contains(':'))
                     .OrderBy(r => r.Specificity.Ids).ThenBy(r => r.Specificity.Classes)
                     .ThenBy(r => r.Specificity.Elements).ThenBy(r => r.Order))
        {
            IEnumerable<IElement> matched;
            try
            {
                matched = document.QuerySelectorAll(rule.Selector).ToList();
            }
            catch
            {
                continue; /* ignore */
            }
            foreach (var element in matched)
            {
                if (!applied.TryGetValue(element, out var style))
                {
                    style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    applied[element] = style;
                }
                foreach (var (name, value) in rule.Declarations)
                {
                    style[name] = value;
                }
            }
        }

        string bodyStyle = string.Empty;
        foreach (var element in document.All.ToList())
        {
            applied.TryGetValue(element, out var style);
            style ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.GetAttribute("style") is { } own)
            {
                foreach (var (name, value) in ParseDeclarations(own))
                {
                    style[name] = value;
                }
            }
            var kept = style.Where(d => !DroppedPrefixes.Any(p => d.Key.StartsWith(p, StringComparison.Ordinal)))
                .Select(d => $"{d.Key}:{d.Value}");
            var serialized = string.Join(";", kept);
            if (element is IHtmlBodyElement)
            {
                bodyStyle = serialized;
                element.RemoveAttribute("style");
            }
            else if (serialized.Length > 0)
            {
                element.SetAttribute("style", serialized);
            }
            element.RemoveAttribute("class");
        }
        return bodyStyle;
    }

    // ---------- lists ----------

    /// <summary>The marker each Google list class draws, read from its <c>::before</c> content.</summary>
    public static Dictionary<string, string> ListStyleTypes(IEnumerable<CssRule> rules)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Selector.Contains(":before", StringComparison.OrdinalIgnoreCase)))
        {
            var listClass = ListClass().Match(rule.Selector);
            var content = rule.Declarations.LastOrDefault(d => d.Name == "content").Value;
            if (!listClass.Success || content is null)
            {
                continue;
            }
            map[listClass.Value] = MarkerFor(content);
        }
        return map;
    }

    private static string MarkerFor(string content)
    {
        var counter = CounterStyle().Match(content);
        if (counter.Success)
        {
            return counter.Groups["style"].Value.ToLowerInvariant() switch
            {
                "lower-latin" or "lower-alpha" => "lower-latin",
                "upper-latin" or "upper-alpha" => "upper-latin",
                "lower-roman" => "lower-roman",
                "upper-roman" => "upper-roman",
                _ => "decimal",
            };
        }
        var lower = content.ToLowerInvariant();
        return lower.Contains("25cb") || lower.Contains('○') ? "circle"
            : lower.Contains("25a0") || lower.Contains('■') ? "square"
            : "disc";
    }

    /// <summary>Notes each list's Google class before the cascade removes the classes.</summary>
    private static void TagLists(IHtmlDocument document)
    {
        foreach (var list in document.QuerySelectorAll("ul, ol"))
        {
            var classes = list.ClassList.ToList();
            if (classes.FirstOrDefault(c => ListClass().IsMatch(c)) is { } key)
            {
                list.SetAttribute("data-gdoc-list", ListClass().Match(key).Value);
                if (classes.Contains("start"))
                {
                    list.SetAttribute("data-gdoc-start", string.Empty);
                }
            }
        }
    }

    /// <summary>Native markers instead of Google's ::before bullets, and numbering that survives an interruption.</summary>
    private static void MapLists(IHtmlDocument document, Dictionary<string, string> listTypes)
    {
        // a Google list without "start" continues the numbering of the list with the same class above it
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var list in document.QuerySelectorAll("ul[data-gdoc-list], ol[data-gdoc-list]"))
        {
            var key = list.GetAttribute("data-gdoc-list")!;
            var restart = list.HasAttribute("data-gdoc-start");
            list.RemoveAttribute("data-gdoc-list");
            list.RemoveAttribute("data-gdoc-start");
            var type = listTypes.GetValueOrDefault(key) ?? (list.LocalName == "ol" ? "decimal" : "disc");
            SetDeclaration(list, "list-style-type", type);
            foreach (var item in list.Children.Where(c => c.LocalName == "li"))
            {
                RemoveDeclaration(item, "list-style-type");
            }
            if (list.LocalName == "ol")
            {
                var done = restart ? 0 : counters.GetValueOrDefault(key);
                if (done > 0)
                {
                    list.SetAttribute("start", (done + 1).ToString());
                }
                counters[key] = done + list.Children.Count(c => c.LocalName == "li");
            }
        }
    }

    // ---------- links, ids, comments ----------

    private static void RemoveComments(IHtmlDocument document)
    {
        // the marker in the text, and the comment block Google appends at the end
        foreach (var anchor in document.QuerySelectorAll("a[id^='cmnt_ref']").ToList())
        {
            var parent = anchor.ParentElement;
            anchor.Remove();
            if (parent is { LocalName: "sup" } && string.IsNullOrWhiteSpace(parent.TextContent))
            {
                parent.Remove();
            }
        }
        foreach (var anchor in document.QuerySelectorAll("a[id]").Where(a => CommentId().IsMatch(a.Id ?? string.Empty)).ToList())
        {
            var block = anchor.Closest("div") ?? anchor.ParentElement;
            block?.Remove();
        }
    }

    private static void RewriteLinksAndIds(IHtmlDocument document)
    {
        foreach (var element in document.QuerySelectorAll("[id]"))
        {
            element.Id = $"gdoc-{element.Id}";
        }
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")!.Trim();
            if (href.StartsWith('#'))
            {
                anchor.SetAttribute("href", $"#gdoc-{href[1..]}");
                continue;
            }
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
                && uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/url"
                && HttpUtility.ParseQueryString(uri.Query)["q"] is { Length: > 0 } real)
            {
                href = real;
            }
            anchor.SetAttribute("href", href);
            anchor.SetAttribute("target", "_blank");
            anchor.SetAttribute("rel", "noopener noreferrer");
        }
    }

    // ---------- images ----------

    private static async Task ResolveImagesAsync(IHtmlDocument document, IReadOnlyDictionary<string, ImportedImage> images,
        Func<ImportedImage, Task<string?>> storeImage)
    {
        var stored = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in document.QuerySelectorAll("img").ToList())
        {
            var src = (image.GetAttribute("src") ?? string.Empty).Trim().Replace('\\', '/');
            if (src.StartsWith("./", StringComparison.Ordinal))
            {
                src = src[2..];
            }
            // only pictures inside the export: a remote src would make every reader's browser call out
            if (!images.TryGetValue(src, out var file))
            {
                image.Remove();
                continue;
            }
            if (!stored.TryGetValue(src, out var url))
            {
                url = await storeImage(file);
                stored[src] = url;
            }
            if (url is null)
            {
                image.Remove();
            }
            else
            {
                image.SetAttribute("src", url);
            }
        }
    }

    // ---------- editable copy ----------

    /// <summary>The body as the house editor understands it: emphasis as tags, headings at most h3, no page box.</summary>
    /// <remarks>The editor's own allowlist has no font-weight, so bold, italic, underline and strike-through become
    /// tags, the way the paste path translates Word. Everything else the editor cannot hold is dropped by the
    /// ordinary sanitizer afterwards - that is the price of an editable copy, and the dialog says so.</remarks>
    private static string ToEditable(IHtmlDocument source)
    {
        var document = new HtmlParser().ParseDocument(source.Body?.InnerHtml ?? string.Empty);
        foreach (var span in document.QuerySelectorAll("span[style]").ToList())
        {
            var declarations = ParseDeclarations(span.GetAttribute("style")!);
            var value = (string name) => declarations.LastOrDefault(d => d.Name == name).Value ?? string.Empty;
            var wrappers = new List<string>();
            if (value("font-weight") is "bold" or "bolder" || int.TryParse(value("font-weight"), out var weight) && weight >= 600)
            {
                wrappers.Add("strong");
            }
            if (value("font-style") == "italic")
            {
                wrappers.Add("em");
            }
            var decoration = value("text-decoration") + " " + value("text-decoration-line");
            if (decoration.Contains("underline", StringComparison.Ordinal))
            {
                wrappers.Add("u");
            }
            if (decoration.Contains("line-through", StringComparison.Ordinal))
            {
                wrappers.Add("s");
            }
            foreach (var tag in wrappers)
            {
                var wrapper = document.CreateElement(tag);
                while (span.FirstChild is { } child)
                {
                    wrapper.AppendChild(child);
                }
                span.AppendChild(wrapper);
            }
        }
        // the app is dark: Google's black text and white fills would be unreadable there, and sizes in pt fight the
        // editor's own scale; the copy keeps colour only where the author chose one
        foreach (var element in document.QuerySelectorAll("[style]").ToList())
        {
            var kept = ParseDeclarations(element.GetAttribute("style")!)
                .Where(d => !(d.Name == "color" && IsBlack(d.Value))
                    && !(d.Name == "background-color" && IsWhite(d.Value))
                    && d.Name is not ("font-size" or "vertical-align" or "font-family"))
                .ToList();
            if (kept.Count == 0)
            {
                element.RemoveAttribute("style");
            }
            else
            {
                element.SetAttribute("style", string.Join(";", kept.Select(d => $"{d.Name}:{d.Value}")));
            }
        }
        // jumps inside the original point at ids the copy does not carry; the editor assigns its own on save
        foreach (var jump in document.QuerySelectorAll("a[href^='#']").ToList())
        {
            while (jump.FirstChild is { } child)
            {
                jump.Parent!.InsertBefore(child, jump);
            }
            jump.Remove();
        }
        foreach (var heading in document.QuerySelectorAll("h4, h5, h6").ToList())
        {
            var h3 = document.CreateElement("h3");
            while (heading.FirstChild is { } child)
            {
                h3.AppendChild(child);
            }
            heading.Replace(h3);
        }
        return document.Body?.InnerHtml ?? string.Empty;
    }

    // ---------- fonts ----------

    /// <summary>Font families the original uses that a browser does not ship; the viewer loads them from Google Fonts.</summary>
    public static IReadOnlyList<string> FontFamilies(string html)
        // the sanitizer writes the quotes inside a style attribute as entities
        => FontFamily().Matches(System.Net.WebUtility.HtmlDecode(html))
            .Select(m => m.Groups["name"].Value.Trim().Trim('"', '\''))
            .Where(name => name.Length > 0 && !SystemFonts.Contains(name) && SafeFontName().IsMatch(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    // ---------- helpers ----------

    private static void SetDeclaration(IElement element, string name, string value)
    {
        var declarations = ParseDeclarations(element.GetAttribute("style") ?? string.Empty)
            .Where(d => d.Name != name).Append((name, value));
        element.SetAttribute("style", string.Join(";", declarations.Select(d => $"{d.Item1}:{d.Item2}")));
    }

    private static void RemoveDeclaration(IElement element, string name)
    {
        if (element.GetAttribute("style") is not { } style)
        {
            return;
        }
        var kept = ParseDeclarations(style).Where(d => d.Name != name).ToList();
        if (kept.Count == 0)
        {
            element.RemoveAttribute("style");
        }
        else
        {
            element.SetAttribute("style", string.Join(";", kept.Select(d => $"{d.Name}:{d.Value}")));
        }
    }

    private static string Attr(string value) => value.Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool IsBlack(string value)
        => Compact(value) is "#000" or "#000000" or "black" or "rgb(0,0,0)" or "rgba(0,0,0,1)";

    private static bool IsWhite(string value)
        => Compact(value) is "#fff" or "#ffffff" or "white" or "transparent" or "rgb(255,255,255)" or "rgba(255,255,255,1)";

    private static string Compact(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ImageType(string name)
    {
        if (!ImageEntry().IsMatch(name))
        {
            return null;
        }
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
    }

    private static byte[] ReadCapped(Stream stream, long cap)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new GoogleExportException("Eine Datei im Archiv ist zu groß.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    [GeneratedRegex(@"^[^/]+\.html?$", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlEntry();

    [GeneratedRegex(@"^images/[^/]+\.(png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageEntry();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"@import[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex AtImport();

    [GeneratedRegex(@"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex RuleBlock();

    [GeneratedRegex(@"\.[\w-]+|\[[^\]]*\]|(?<!:):[\w-]+")]
    private static partial Regex ClassOrAttribute();

    [GeneratedRegex(@"(?:^|[\s>+~])[a-zA-Z][\w-]*")]
    private static partial Regex ElementName();

    [GeneratedRegex(@"lst-kix_[\w]+-\d+")]
    private static partial Regex ListClass();

    [GeneratedRegex(@"counter\(\s*[\w-]+\s*,\s*(?<style>[\w-]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CounterStyle();

    [GeneratedRegex(@"^cmnt\d+$")]
    private static partial Regex CommentId();

    [GeneratedRegex(@"font-family\s*:\s*(?<name>[^;,""]+|""[^""]+""|'[^']+')", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamily();

    [GeneratedRegex(@"^[A-Za-z0-9 ]{2,40}$")]
    private static partial Regex SafeFontName();
}
