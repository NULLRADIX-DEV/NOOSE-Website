using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>
/// Structural guard over the pages an ACCOUNT renders outside the internal shell: the citizen portal, the applicant
/// portal and the legal pages.
/// </summary>
/// <remarks>
/// <para>
/// <c>PublicPageScanTests</c> deliberately covers only <c>Components/Pages/Public</c> plus the three components of
/// the anonymous shell. Everything else an unauthenticated or non-agent visitor can reach was therefore unscanned -
/// all ten portal pages, both legal pages (which ARE public routes) and four of the five shells - and that is
/// exactly where the citizen-area defects sat: an inert wordmark, account controls shown to anonymous visitors, a
/// receipt opening the print dialog unasked, click-only rows, and a load failure rendered as "you have none".
/// </para>
/// <para>
/// The rules are split on purpose. Structure - anonymous attribute, string-bound query parameters, a way back,
/// bounded field widths - applies to every file here. Identity and internal markers apply to the PORTAL only:
/// the legal pages explain in German prose what a codename is, and a marker scan cannot tell prose from a
/// projection.
/// </para>
/// </remarks>
public partial class CitizenSurfaceScanTests
{
    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..",
            "NOOSE-Website", "Components"));

    /// <summary>The shells these pages render inside; named, so moving one out of the scan is a red test.</summary>
    private static readonly string[][] Shells =
    [
        ["Layout", "BuergerLayout.razor"],
        ["Layout", "ApplicantPortalLayout.razor"],
        ["Layout", "PublicLayout.razor"],
        ["Layout", "LegalLayout.razor"],
    ];

    /// <summary>Anything that names an agent; portal only, see the class remark.</summary>
    private static readonly string[] IdentityMarkers =
    [
        "Codename", "RealName", "Klarname", "Dienstgrad", "BadgeNumber", "Dienstnummer",
        "IsAdmin", "IsTeamLead", "IsTRU", "IsHRB", "GetDivision",
    ];

    /// <summary>Anything that reaches past the service layer into the record surface.</summary>
    private static readonly string[] InternalMarkers =
    [
        // no "IsClassified": PrintFrame takes it as a parameter, and the record layer is already covered by the
        // context markers above it
        "IDbContextFactory", "AppDbContext", "IgnoreQueryFilters", "ThreatScore",
        "AuditLog", "AccessLog", "db.Users", "<RichHtml", "MentionInput", "MentionPicker",
        "SnapshotJson", "StatisticsReport", "DashboardMetrics", "ISituationReportService",
    ];

    /// <summary>Pages whose way back comes from somewhere other than their own markup.</summary>
    /// <remarks>A dictionary rather than a list, so the next exemption is a decision with a reason.</remarks>
    private static readonly Dictionary<string, string> BackExempt = new(StringComparer.Ordinal)
    {
        ["Privacy.razor"] = "LegalLayout carries the back button for both legal pages",
        ["Nutzungsbedingungen.razor"] = "LegalLayout carries the back button for both legal pages",
        ["MeinEinspruch.razor"] = "BuergerLayout carries the citizen section nav",
        ["MeineBewerbung.razor"] = "ApplicantPortalLayout carries the wordmark link and the career-page link",
        ["BuergerPortal.razor"] = "the citizen hub itself; its tiles are the navigation",
    };

    private static string[] Files()
    {
        var root = Root();
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");

        var shells = Shells.Select(parts => Path.Combine(new[] { root }.Concat(parts).ToArray())).ToArray();
        foreach (var file in shells)
        {
            // not a Where(File.Exists): a renamed shell would silently leave the scan set
            Assert.True(File.Exists(file), $"Hülle nicht gefunden: {file}");
        }

        var files = Directory.EnumerateFiles(Path.Combine(root, "Pages", "Portal"), "*.razor",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Pages", "Legal"), "*.razor",
                SearchOption.AllDirectories))
            .Concat(shells)
            .Order()
            .ToArray();
        // a wrong path would otherwise leave every fact below green forever
        Assert.NotEmpty(files);
        return files;
    }

    private static string[] PortalFiles()
        => Directory.EnumerateFiles(Path.Combine(Root(), "Pages", "Portal"), "*.razor", SearchOption.AllDirectories)
            .Order()
            .ToArray();

    /// <summary>Comments are stripped first: a remark explaining why a file avoids something names the very thing.</summary>
    private static string Code(string file)
    {
        var text = File.ReadAllText(file);
        text = RazorComment().Replace(text, " ");
        text = HtmlComment().Replace(text, " ");
        // the C# comments as well: an XML remark inside @code that explains a rule quotes the attribute it is
        // about, which is what made this scan report its own documentation
        text = BlockComment().Replace(text, " ");
        return LineComment().Replace(text, " ");
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//.*")]
    private static partial Regex LineComment();

    [Fact]
    public void NoPortalPageMentionsAnAgentIdentity()
    {
        var offenders = PortalFiles()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => IdentityMarkers.Where(m => x.Text.Contains(m, StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der Bürger- und Bewerberbereich nennt keinen Agenten: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoCitizenSurfaceReachesForTheRecordLayer()
    {
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => InternalMarkers.Where(m => x.Text.Contains(m, StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Seiten lesen durch die Dienste, nicht durch den Kontext: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryCitizenPageIsAnonymousAndSelfGated()
    {
        // all of them carry [AllowAnonymous] and decide for themselves who may see what: the layout cannot, because
        // /buerger/einspruch is deliberately reachable while signed out
        var offenders = Files()
            .Where(f => Code(f).Contains("@page", StringComparison.Ordinal))
            .Where(f => !Code(f).Contains("[AllowAnonymous]", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede Seite dieser Flächen ist [AllowAnonymous] und gated sich selbst: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryQueryParameterIsBoundAsAPublicString()
    {
        // Blazor answers a value it cannot parse with HTTP 500, and anyone can append a query to these routes.
        // The declaration has to be public as well - not because a private one fails to bind (it binds), but
        // because a scan cannot see it, which is how two private ones on the login page went unchecked for good.
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => QueryParameter().Matches(x.Text)
                .Where(m => !m.Groups["decl"].Value.Contains("public", StringComparison.Ordinal)
                    || !m.Groups["type"].Value.StartsWith("string", StringComparison.Ordinal))
                .Select(m => $"{x.File}: {m.Groups["decl"].Value.Trim()} {m.Groups["type"].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Query-Parameter werden als public string gebunden: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\[SupplyParameterFromQuery[^\]]*\]\s*(?<decl>(?:public|private|internal|protected)?\s*)"
        + @"(?<type>\S+)\s", RegexOptions.Singleline)]
    private static partial Regex QueryParameter();

    [Fact]
    public void EveryCitizenPageOffersAWayBack()
    {
        // the defect this pins: a citizen had no route off /buerger/profil, /buerger/hinweise or /buerger/tickets,
        // because the shell's return button was set for NON-citizens only
        var offenders = PortalFiles()
            .Concat(Directory.EnumerateFiles(Path.Combine(Root(), "Pages", "Legal"), "*.razor"))
            .Where(f => Code(f).Contains("@page", StringComparison.Ordinal))
            .Where(f => !BackExempt.ContainsKey(Path.GetFileName(f)))
            .Where(f => !BackAffordance().IsMatch(Code(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede Seite bietet einen Weg zurück oder steht mit Begründung in BackExempt: "
            + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"ArrowBack|BackHref|Href=""/buerger""|Href=""/portal""|Href=""/karriere""|Href=""/""",
        RegexOptions.Singleline)]
    private static partial Regex BackAffordance();

    [Fact]
    public void NoInputFieldIsWiderThanItsContent()
    {
        // the reported defect: two name fields in a flexing row grew to half the viewport each. A hard floor over
        // 400px also overflows a phone, which is what put a dialog's submit button off-screen.
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .SelectMany(x => WideFloor().Matches(x.Text)
                .Where(m => int.TryParse(m.Groups["px"].Value, out var px) && px > 400)
                .Select(m => $"{x.File}: min-width:{m.Groups["px"].Value}px"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Ein harter Mindestbreiten-Boden über 400px sprengt ein Telefon: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"min-width:\s*(?<px>\d+)px")]
    private static partial Regex WideFloor();

    [Fact]
    public void NoCitizenPagePrintsWithoutBeingAsked()
    {
        // PrintFrame defaults AutomaticPrint to true, and a citizen's receipt opening the OS print dialog unasked
        // is the presumption the wanted poster deliberately avoids
        var offenders = Files()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .Where(x => x.Text.Contains("<PrintFrame", StringComparison.Ordinal)
                && !x.Text.Contains("AutomaticPrint=\"false\"", StringComparison.Ordinal))
            .Select(x => x.File)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "PrintFrame auf einer Bürgerseite nur mit AutomaticPrint=\"false\": " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoRowIsClickableWithoutBeingReachableByKeyboard()
    {
        // these rows are the ONLY way into a detail view, so a click-only div locked out every keyboard and
        // screen-reader user. StatTile has carried the correct shape all along.
        var offenders = PortalFiles()
            .Select(f => (File: Path.GetFileName(f), Text: Code(f)))
            .Where(x => x.Text.Contains("cursor:pointer", StringComparison.Ordinal)
                && !x.Text.Contains("@onkeydown", StringComparison.Ordinal))
            .Select(x => x.File)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Was klickbar aussieht, muss mit der Tastatur erreichbar sein: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryBackExemptionNamesAFileThatExists()
    {
        var known = PortalFiles()
            .Concat(Directory.EnumerateFiles(Path.Combine(Root(), "Pages", "Legal"), "*.razor"))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var stale = BackExempt.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(stale.Length == 0,
            "Diese Datei gibt es nicht mehr; Eintrag aus BackExempt entfernen: " + string.Join(", ", stale));
    }
}
