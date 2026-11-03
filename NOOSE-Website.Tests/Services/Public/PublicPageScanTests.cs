using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>Structural guard over the pages an anonymous visitor actually renders.</summary>
/// <remarks>
/// Deliberately limited to <c>Components/Pages/Public</c> plus the two public shell components. There is no marker
/// scan over <c>Services/Public</c>: <c>PublicPageService</c> legitimately projects a codename four lines below a
/// permission guard, and "the marker is fine because a guard runs upstream" is not decidable from the text.
/// </remarks>
public partial class PublicPageScanTests
{
    /// <summary>Anything that names an agent.</summary>
    private static readonly string[] IdentityMarkers =
    [
        "Codename", "RealName", "Klarname", "Dienstgrad", "BadgeNumber", "Dienstnummer",
        "IsAdmin", "IsTeamLead", "IsTRU", "IsHRB", "GetDivision", "GetAgentId",
    ];

    /// <summary>Anything that reaches past the publication snapshot into the record surface.</summary>
    private static readonly string[] InternalMarkers =
    [
        "<RichHtml", "MentionDisplay", "MentionInput", "MentionText", "MentionPicker", "IgnoreQueryFilters",
        "ThreatScore", "IsClassified", "AuditLog", "AccessLog", "db.Users",
        // a public page reads through a service, never through the context itself
        "IDbContextFactory", "AppDbContext",
        // PrintFrame prints through JS interop (dead without a circuit) and renders "von {PrintedBy}"
        "PrintFrame",
        // the frozen monthly snapshot: it counts classified records and names people with their internal file
        // numbers, so a released report is written text and never a projection of these
        "SnapshotJson", "StatisticsReport", "DashboardMetrics", "StatisticsTopEntry",
        "SituationReportDisplay", "ISituationReportService",
    ];

    /// <summary>Pages that legitimately sit in another shell — the exemption covers the layout line only.</summary>
    /// <remarks>
    /// A set rather than one constant so the next exemption is a decision with a reason, not an overwritten string.
    /// Moving a page out of this folder to escape a failure would drop it from all four scans at once.
    /// </remarks>
    private static readonly Dictionary<string, string> LayoutExempt = new(StringComparer.Ordinal)
    {
        ["Invite.razor"] = "applicant invite: uses the narrow applicant shell",
        ["WantedPoster.razor"] = "print poster: PrintLayout is the document shell, and it pulls its own module gate",
    };

    /// <summary>Pages that must run a circuit — forms, not reading surfaces.</summary>
    /// <remarks>
    /// Static rendering is the rule because an anonymous reader must not open a SignalR circuit. A form is the
    /// documented exception (PublicPlan, Leitsatz 5): it needs binding, a file picker and error feedback. Every other
    /// fact in this file still applies to them, and the exemption is per file with a reason.
    /// </remarks>
    private static readonly Dictionary<string, string> InteractiveExempt = new(StringComparer.Ordinal)
    {
        ["TipForm.razor"] = "tip form: input, image upload and validation feedback need a circuit",
    };

    /// <summary>Pages that deliberately send no preview card.</summary>
    /// <remarks>
    /// The default is the other way round: a public page is meant to be shared, and a link without a card reads as
    /// spam in a Discord channel - which is the whole point of the wanted notices.
    /// </remarks>
    private static readonly Dictionary<string, string> PreviewExempt = new(StringComparer.Ordinal)
    {
        ["Invite.razor"] = "one-time invite: a card in a channel is the opposite of what the link is for",
        ["WantedPoster.razor"] = "print sheet: always noindex, so a preview would never be delivered anyway",
    };

    /// <summary>The two files that may write into the head without going through LinkPreview.</summary>
    private static readonly Dictionary<string, string> HeadExempt = new(StringComparer.Ordinal)
    {
        ["PublicModuleGate.razor"] = "the gate answers 404 for a switched-off module and says noindex itself",
        ["WantedPoster.razor"] = "print sheet: noindex unconditionally, and it carries no preview to merge it with",
    };

    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website", "Components"));

    /// <summary>The shell every public page renders inside; named, so moving one out of the scan is a red test.</summary>
    private static readonly string[][] Shell =
    [
        ["Layout", "PublicSiteLayout.razor"],
        ["Layout", "PublicNav.razor"],
        ["Common", "Shared", "PublicModuleGate.razor"],
    ];

    private static string[] PublicPages(string root)
    {
        var shell = Shell.Select(parts => Path.Combine(new[] { root }.Concat(parts).ToArray())).ToArray();
        foreach (var file in shell)
        {
            // not a Where(File.Exists): a renamed shell component would silently leave the scan set
            Assert.True(File.Exists(file), $"Teil der öffentlichen Hülle nicht gefunden: {file}");
        }
        return Directory.EnumerateFiles(Path.Combine(root, "Pages", "Public"), "*.razor", SearchOption.AllDirectories)
            .Concat(shell)
            .Order()
            .ToArray();
    }

    /// <summary>Razor and HTML comments are stripped first: a comment that explains why a file avoids something
    /// contains the very word the scan forbids.</summary>
    private static string Code(string file)
    {
        var text = File.ReadAllText(file);
        text = RazorComment().Replace(text, " ");
        return HtmlComment().Replace(text, " ");
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    private static string[] Files()
    {
        var root = Root();
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");
        var files = PublicPages(root);
        // a wrong path would otherwise leave every fact below green forever
        Assert.NotEmpty(files);
        return files;
    }

    [Fact]
    public void NoPublicPageMentionsAnAgentIdentity()
    {
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => IdentityMarkers.Where(m => x.Text.Contains(m, StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Agenten bleiben nach außen anonym: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoPublicPageReachesForTheInternalRecordSurface()
    {
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => InternalMarkers.Where(m => x.Text.Contains(m, StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Öffentliche Seiten lesen nur den Publikations-Snapshot: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryPublicPageIsAnonymousAndStatic()
    {
        var offenders = Files()
            .Where(f => Code(f).Contains("@page", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = Code(f);
                // the exemption covers the layout line only; anonymous and static apply to every public page
                var layoutOk = text.Contains("@layout PublicSiteLayout", StringComparison.Ordinal)
                    || LayoutExempt.ContainsKey(Path.GetFileName(f));
                var staticOk = text.Contains("[ExcludeFromInteractiveRouting]", StringComparison.Ordinal)
                    || InteractiveExempt.ContainsKey(Path.GetFileName(f));
                return !text.Contains("[AllowAnonymous]", StringComparison.Ordinal)
                    || !staticOk
                    || !layoutOk;
            })
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede öffentliche Seite ist anonym, statisch gerendert und liegt im öffentlichen Layout: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryPublicQueryParameterIsBoundAsAPublicString()
    {
        // Blazor answers a query value it cannot parse with HTTP 500, and anyone can append a query to a public URL.
        // The declaration is matched whatever its accessibility and then required to be public - the old pattern
        // only matched "public", so a private declaration escaped the type check without a word.
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => QueryParameter().Matches(x.Text)
                .Where(m => !m.Groups["decl"].Value.Contains("public", StringComparison.Ordinal)
                    || !m.Groups["type"].Value.StartsWith("string", StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m.Groups["decl"].Value.Trim()} {m.Groups["type"].Value}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Query-Parameter öffentlicher Routen werden als public string gebunden: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\[SupplyParameterFromQuery[^\]]*\]\s*(?<decl>(?:public|private|internal|protected)?\s*)"
        + @"(?<type>\S+)\s", RegexOptions.Singleline)]
    private static partial Regex QueryParameter();

    [Fact]
    public void EveryPublicPageDeclaresItsLinkPreview()
    {
        var offenders = Files()
            .Where(f => Code(f).Contains("@page", StringComparison.Ordinal))
            .Where(f => !PreviewExempt.ContainsKey(Path.GetFileName(f)))
            .Where(f => !Code(f).Contains("<LinkPreview", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede öffentliche Seite sagt, wie ihr Link außerhalb aussieht: " + string.Join(", ", offenders));
    }

    /// <summary>The preview rule follows the ROUTE, not the folder.</summary>
    /// <remarks>
    /// <see cref="PublicRoutes"/> decides what an anonymous visitor may open and a crawler may index, and that set
    /// is not the same as <c>Pages/Public</c>: the legal pages live elsewhere, are linked from the footer of every
    /// public page, and had no preview at all — a shared link to them unfurled as a bare address. Scanning by
    /// folder could never have seen them.
    /// </remarks>
    [Fact]
    public void EveryPubliclyRoutedPageDeclaresItsLinkPreview()
    {
        var root = Root();
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories).Order())
        {
            var name = Path.GetFileName(file);
            if (PreviewExempt.ContainsKey(name))
            {
                continue;
            }
            var code = Code(file);
            if (code.Contains("<LinkPreview", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match treffer in PageDirective().Matches(code))
            {
                var route = treffer.Groups[1].Value;
                // a parameter segment is not part of the prefix PublicRoutes matches on
                var klammer = route.IndexOf('{');
                if (klammer >= 0)
                {
                    route = route[..klammer].TrimEnd('/');
                }
                if (route.Length > 1 && NOOSE_Website.Services.Public.PublicRoutes.IsPublic(route))
                {
                    offenders.Add(name);
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Öffentlich erreichbar, aber ohne Link-Vorschau: " + string.Join(", ", offenders.Distinct().Order()));
    }

    [GeneratedRegex("""@page\s+"([^"]+)"\s*""")]
    private static partial Regex PageDirective();

    [Fact]
    public void TheHeadOfAPublicPageIsWrittenInOnePlace()
    {
        // Two HeadContent blocks on one page are not added together: the outlet keeps the last one registered and
        // drops the other. A page that put its own <meta robots> next to a LinkPreview would therefore publish
        // whichever of the two happened to render later - and losing that one is a soft-404 in a search index.
        var offenders = Files()
            .Where(f => Code(f).Contains("<HeadContent", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !HeadExempt.ContainsKey(name!))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der Kopf einer öffentlichen Seite wird über LinkPreview geschrieben, nicht von Hand: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheTwoWantedBoardsDoNotShareARoute()
    {
        var root = Root();
        var publicPages = PublicPages(root).Select(File.ReadAllText).ToArray();
        Assert.DoesNotContain(publicPages, t => t.Contains("@page \"/fahndung", StringComparison.Ordinal));

        var internalBoard = File.ReadAllText(Path.Combine(root, "Pages", "Wanted", "WantedBoard.razor"));
        Assert.Contains("@page \"/fahndung\"", internalBoard, StringComparison.Ordinal);
        Assert.DoesNotContain("@page \"/gesucht", internalBoard, StringComparison.Ordinal);
    }
}
