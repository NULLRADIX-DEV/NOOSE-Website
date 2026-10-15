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

    /// <summary>Characters of HTML one import converts; a long Google document stays far below.</summary>
    public const int MaxHtmlChars = 10_000_000;

    /// <summary>Style rules and elements one import folds. The cascade is a lookup per rule now, so what these bound
    /// is mostly the sanitizer, at roughly a tenth of a millisecond per styled element and pass.</summary>
    public const int MaxRules = 10_000;
    public const int MaxElements = 100_000;

    /// <summary>Rules whose selector the indexed matcher cannot read; each costs a walk over the whole document.</summary>
    private const int MaxComplexRules = 100;

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
        if (html.Length > MaxHtmlChars)
        {
            throw new GoogleExportException("Das Dokument ist zu groß für den Import.");
        }
        var document = new HtmlParser().ParseDocument(html);
        var title = TrimToNull(document.Title);

        var rules = ParseRules(string.Concat(document.QuerySelectorAll("style").Select(s => s.TextContent)));
        if (rules.Count > MaxRules)
        {
            throw new GoogleExportException("Das Dokument hat zu viele Formatvorlagen für den Import.");
        }
        var listTypes = ListStyleTypes(rules);
        TagLists(document);
        var bodyStyle = InlineCascade(document, rules);

        RemoveComments(document);
        RewriteLinksAndIds(document);
        MapLists(document, listTypes, RestartBases(rules));
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
        // a block at-rule (@media print, @supports) only applies conditionally; folding its rules would apply them always
        css = StripBlockAtRules(css);
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
        var all = document.All.ToList();
        if (all.Count > MaxElements)
        {
            throw new GoogleExportException("Das Dokument ist zu groß für den Import.");
        }
        // indexed once: a selector is then a lookup over the elements it can match, not a walk over the whole
        // document per rule, which made a crafted export with many rules and elements run for hours
        var index = new SelectorIndex(all);
        var applied = new Dictionary<IElement, OrderedDictionary<string, string>>();
        var complex = 0;
        foreach (var rule in rules
                     .Where(r => !r.Selector.Contains(':'))
                     .OrderBy(r => r.Specificity.Ids).ThenBy(r => r.Specificity.Classes)
                     .ThenBy(r => r.Specificity.Elements).ThenBy(r => r.Order))
        {
            IEnumerable<IElement> matched;
            if (index.TryMatch(rule.Selector, out var fast))
            {
                matched = fast;
            }
            else
            {
                // Google writes plain class, tag and child selectors; anything else is rare and capped
                if (++complex > MaxComplexRules)
                {
                    continue;
                }
                try
                {
                    matched = document.QuerySelectorAll(rule.Selector).ToList();
                }
                catch
                {
                    continue; /* ignore */
                }
            }
            foreach (var element in matched)
            {
                if (!applied.TryGetValue(element, out var style))
                {
                    style = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    applied[element] = style;
                }
                foreach (var (name, value) in rule.Declarations)
                {
                    Declare(style, name, value);
                }
            }
        }

        string bodyStyle = string.Empty;
        foreach (var element in all)
        {
            applied.TryGetValue(element, out var style);
            style ??= new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.GetAttribute("style") is { } own)
            {
                foreach (var (name, value) in ParseDeclarations(own))
                {
                    Declare(style, name, value);
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

    /// <summary>Sets a declaration as the last one: a later shorthand must override an earlier longhand and the
    /// other way round, and a browser reads them in the order they stand.</summary>
    private static void Declare(OrderedDictionary<string, string> style, string name, string value)
    {
        style.Remove(name);
        style.Add(name, value);
    }

    /// <summary>Matches the selectors Google writes - compounds of tag, classes and id, joined by child or
    /// descendant combinators - through lookups by class, tag and id.</summary>
    private sealed partial class SelectorIndex
    {
        private readonly List<IElement> _all;
        private readonly Dictionary<string, List<IElement>> _byClass = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<IElement>> _byTag = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<IElement>> _byId = new(StringComparer.Ordinal);

        private sealed record Compound(string? Tag, IReadOnlyList<string> Classes, string? Id);

        public SelectorIndex(List<IElement> all)
        {
            _all = all;
            foreach (var element in all)
            {
                Add(_byTag, element.LocalName, element);
                foreach (var name in element.ClassList)
                {
                    Add(_byClass, name, element);
                }
                if (!string.IsNullOrEmpty(element.Id))
                {
                    Add(_byId, element.Id, element);
                }
            }
        }

        private static void Add(Dictionary<string, List<IElement>> map, string key, IElement element)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            list.Add(element);
        }

        /// <summary>False when the selector is outside the simple form; the caller falls back to the engine.</summary>
        public bool TryMatch(string selector, out List<IElement> matched)
        {
            matched = [];
            var tokens = Token().Matches(selector.Trim()).Select(m => m.Value.Trim()).ToList();
            if (tokens.Count == 0)
            {
                return false;
            }
            var compounds = new List<Compound>();
            var combinators = new List<char>();
            var expectCompound = true;
            foreach (var token in tokens)
            {
                if (token is ">" or "")
                {
                    if (expectCompound)
                    {
                        return false;
                    }
                    combinators.Add(token == ">" ? '>' : ' ');
                    expectCompound = true;
                    continue;
                }
                var parsed = CompoundPattern().Match(token);
                if (!parsed.Success || !expectCompound)
                {
                    return false;
                }
                var tag = parsed.Groups["tag"].Success && parsed.Groups["tag"].Value != "*" ? parsed.Groups["tag"].Value : null;
                var classes = parsed.Groups["part"].Captures.Where(c => c.Value[0] == '.').Select(c => c.Value[1..]).ToList();
                var ids = parsed.Groups["part"].Captures.Where(c => c.Value[0] == '#').Select(c => c.Value[1..]).ToList();
                if (ids.Count > 1)
                {
                    return false;
                }
                compounds.Add(new Compound(tag, classes, ids.FirstOrDefault()));
                expectCompound = false;
            }
            if (expectCompound)
            {
                return false;
            }

            var last = compounds[^1];
            IEnumerable<IElement> candidates = last.Id is { } id ? _byId.GetValueOrDefault(id) ?? []
                : last.Classes.Count > 0 ? _byClass.GetValueOrDefault(last.Classes[0]) ?? []
                : last.Tag is { } t ? _byTag.GetValueOrDefault(t) ?? []
                : _all;
            foreach (var element in candidates)
            {
                if (Is(element, last) && Ancestry(element, compounds, combinators, compounds.Count - 2))
                {
                    matched.Add(element);
                }
            }
            return true;
        }

        private static bool Ancestry(IElement element, List<Compound> compounds, List<char> combinators, int at)
        {
            if (at < 0)
            {
                return true;
            }
            var compound = compounds[at];
            if (combinators[at] == '>')
            {
                return element.ParentElement is { } parent && Is(parent, compound)
                    && Ancestry(parent, compounds, combinators, at - 1);
            }
            for (var up = element.ParentElement; up is not null; up = up.ParentElement)
            {
                if (Is(up, compound) && Ancestry(up, compounds, combinators, at - 1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Is(IElement element, Compound compound)
            => (compound.Tag is null || string.Equals(element.LocalName, compound.Tag, StringComparison.OrdinalIgnoreCase))
                && (compound.Id is null || element.Id == compound.Id)
                && compound.Classes.All(element.ClassList.Contains);

        [GeneratedRegex(@"\s*>\s*|\s+|[^\s>]+")]
        private static partial Regex Token();

        [GeneratedRegex(@"^(?<tag>[a-zA-Z][\w-]*|\*)?(?<part>[.#][\w-]+)*$")]
        private static partial Regex CompoundPattern();
    }

    /// <summary>Removes at-rules with a block (<c>@media</c>, <c>@supports</c>, <c>@font-face</c>), nested braces
    /// included, and statement at-rules up to their semicolon.</summary>
    private static string StripBlockAtRules(string css)
    {
        var result = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            var at = css.IndexOf('@', i);
            if (at < 0)
            {
                result.Append(css, i, css.Length - i);
                break;
            }
            result.Append(css, i, at - i);
            var j = at;
            while (j < css.Length && css[j] != '{' && css[j] != ';')
            {
                j++;
            }
            if (j >= css.Length)
            {
                break;
            }
            if (css[j] == ';')
            {
                i = j + 1;
                continue;
            }
            var depth = 0;
            var k = j;
            for (; k < css.Length; k++)
            {
                if (css[k] == '{')
                {
                    depth++;
                }
                else if (css[k] == '}' && --depth == 0)
                {
                    break;
                }
            }
            i = k + 1;
        }
        return result.ToString();
    }

    // ---------- lists ----------

    /// <summary>The number a restarted Google list counts on from, read from its <c>.start</c> rule's counter-reset.</summary>
    public static Dictionary<string, int> RestartBases(IEnumerable<CssRule> rules)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Selector.Contains(".start", StringComparison.Ordinal)))
        {
            var listClass = ListClass().Match(rule.Selector);
            var reset = rule.Declarations.LastOrDefault(d => d.Name == "counter-reset").Value;
            if (listClass.Success && reset is not null
                && int.TryParse(reset.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out var start) && start > 0)
            {
                map[listClass.Value] = start;
            }
        }
        return map;
    }

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
    private static void MapLists(IHtmlDocument document, Dictionary<string, string> listTypes,
        Dictionary<string, int> restartBases)
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
                // Google's own start attribute is the truth; otherwise a restart counts from its counter-reset
                // and a continuation from where the same list stopped
                var done = int.TryParse(list.GetAttribute("start"), out var given) && given > 0 ? given - 1
                    : restart ? restartBases.GetValueOrDefault(key)
                    : counters.GetValueOrDefault(key);
                if (done > 0)
                {
                    list.SetAttribute("start", (done + 1).ToString());
                }
                else
                {
                    list.RemoveAttribute("start");
                }
                counters[key] = done + list.Children.Count(c => c.LocalName == "li");
            }
            // for the editable copy, which nests by indent; the import profile drops both attributes
            var level = ListLevel().Match(key);
            list.SetAttribute("data-gdoc-group", level.Groups["group"].Value);
            list.SetAttribute("data-gdoc-level", level.Groups["level"].Value);
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
        // Google writes each level and each run of a list as its own sibling list; the editor stores one flat list
        // whose items carry ql-indent-N, like the Word paste. Adjacent lists of one Google list and one kind merge
        // into it, so a sub-item is no longer a new list numbered from 1.
        foreach (var list in document.QuerySelectorAll("ul[data-gdoc-group], ol[data-gdoc-group]").ToList())
        {
            var group = list.GetAttribute("data-gdoc-group");
            _ = int.TryParse(list.GetAttribute("data-gdoc-level"), out var level);
            foreach (var item in list.Children.Where(c => c.LocalName == "li"))
            {
                if (level > 0)
                {
                    item.ClassList.Add($"ql-indent-{Math.Min(level, 8)}");
                }
            }
            if (list.PreviousElementSibling is { } previous && previous.LocalName == list.LocalName
                && previous.GetAttribute("data-gdoc-group") == group)
            {
                while (list.FirstChild is { } child)
                {
                    previous.AppendChild(child);
                }
                list.Remove();
            }
        }
        foreach (var list in document.QuerySelectorAll("[data-gdoc-group]"))
        {
            list.RemoveAttribute("data-gdoc-group");
            list.RemoveAttribute("data-gdoc-level");
        }
        // the editor has no sup/sub; unwrapped, a footnote marker stays as text instead of vanishing with its tag
        foreach (var mark in document.QuerySelectorAll("sup, sub").ToList())
        {
            while (mark.FirstChild is { } child)
            {
                mark.Parent!.InsertBefore(child, mark);
            }
            mark.Remove();
        }
        // a Google page break is a hidden rule, and the copy would show it as a line
        foreach (var rule in document.QuerySelectorAll("hr[style]").ToList())
        {
            if (ParseDeclarations(rule.GetAttribute("style")!).Any(d => d.Name == "display" && d.Value.Contains("none")))
            {
                rule.Remove();
            }
        }
        // the app is dark: Google's dark text and white fills would be unreadable there, and sizes in pt fight the
        // editor's own scale. A light fill the author chose (highlight, shaded cell) stays, with dark text on it.
        foreach (var element in document.QuerySelectorAll("[style]").ToList())
        {
            var declarations = ParseDeclarations(element.GetAttribute("style")!);
            var fill = declarations.LastOrDefault(d => d.Name == "background-color").Value;
            var ink = declarations.LastOrDefault(d => d.Name == "color").Value;
            var whiteFill = fill is not null && (IsWhite(fill) || (TryRgb(fill, out var w) && IsGrey(w) && Luminance(w) > 0.9));
            var lightFill = !whiteFill && fill is not null && TryRgb(fill, out var f) && Luminance(f) > 0.5;
            var kept = declarations
                .Where(d => d.Name is not ("font-size" or "vertical-align" or "font-family" or "color")
                    && !(d.Name == "background-color" && whiteFill))
                .ToList();
            if (lightFill)
            {
                kept.Add(("color", ink is not null && TryRgb(ink, out var dark) && Luminance(dark) < 0.45 ? ink : "#1f2328"));
            }
            else if (ink is not null && !IsUnreadableInk(ink))
            {
                kept.Add(("color", ink));
            }
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

    /// <summary>Text colour that disappears on the dark app: black, dark greys, and any very dark hue.</summary>
    private static bool IsUnreadableInk(string value)
        => TryRgb(value, out var rgb) && ((IsGrey(rgb) && Luminance(rgb) < 0.45) || Luminance(rgb) < 0.15);

    /// <summary>Reads #rgb, #rrggbb(aa), rgb() and rgba() as channels between 0 and 1.</summary>
    private static bool TryRgb(string value, out (double R, double G, double B) rgb)
    {
        rgb = default;
        var compact = Compact(value);
        if (compact is "black")
        {
            return true;
        }
        if (compact is "white")
        {
            rgb = (1, 1, 1);
            return true;
        }
        if (compact.StartsWith('#'))
        {
            var hex = compact[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Take(3).Select(c => $"{c}{c}"));
            }
            if (hex.Length is not (6 or 8) || !int.TryParse(hex[..6], System.Globalization.NumberStyles.HexNumber, null, out var n))
            {
                return false;
            }
            rgb = (((n >> 16) & 255) / 255d, ((n >> 8) & 255) / 255d, (n & 255) / 255d);
            return true;
        }
        var channels = RgbFunction().Match(compact);
        if (!channels.Success)
        {
            return false;
        }
        double Channel(int i) => Math.Clamp(double.Parse(channels.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) / 255d, 0, 1);
        rgb = (Channel(1), Channel(2), Channel(3));
        return true;
    }

    /// <summary>Relative luminance after WCAG.</summary>
    private static double Luminance((double R, double G, double B) rgb)
    {
        static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(rgb.R) + 0.7152 * Linear(rgb.G) + 0.0722 * Linear(rgb.B);
    }

    private static bool IsGrey((double R, double G, double B) rgb)
        => Math.Max(rgb.R, Math.Max(rgb.G, rgb.B)) - Math.Min(rgb.R, Math.Min(rgb.G, rgb.B)) < 0.12;

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

    [GeneratedRegex(@"^(?<group>lst-kix_[\w]+)-(?<level>\d+)$")]
    private static partial Regex ListLevel();

    [GeneratedRegex(@"^rgba?\((\d+(?:\.\d+)?),(\d+(?:\.\d+)?),(\d+(?:\.\d+)?)")]
    private static partial Regex RgbFunction();

    [GeneratedRegex(@"counter\(\s*[\w-]+\s*,\s*(?<style>[\w-]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CounterStyle();

    [GeneratedRegex(@"^cmnt\d+$")]
    private static partial Regex CommentId();

    [GeneratedRegex(@"font-family\s*:\s*(?<name>[^;,""]+|""[^""]+""|'[^']+')", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamily();

    [GeneratedRegex(@"^[A-Za-z0-9 ]{2,40}$")]
    private static partial Regex SafeFontName();
}
