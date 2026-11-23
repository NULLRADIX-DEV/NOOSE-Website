using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The situation level has one save path and one cache key; a file scan keeps it that way.</summary>
/// <remarks>
/// The sibling of <see cref="PublicWantedCacheDisciplineTests"/>, <see cref="PublicFactionProfileCacheDisciplineTests"/>,
/// <see cref="PressCacheDisciplineTests"/>, <see cref="PublicWarningCacheDisciplineTests"/> and
/// <see cref="PublicReportCacheDisciplineTests"/> — named rather than counted, because an ordinal goes stale the next
/// time a phase adds one. This one guards a shared table rather than a private one: <c>SystemSettings</c> holds every
/// config row in the house, so the rule is not "who writes the table" but "who touches these four keys".
/// </remarks>
public partial class PublicSituationCacheDisciplineTests
{
    private const string CacheKeyLiteral = "\"OeffentlicheGefahrenlage\"";
    private const string ServiceName = "PublicSituationService.cs";
    private const string KeysDeclaration = "SystemConfiguration.cs";
    // names the keys only to link their audit rows to the section; it reads and writes nothing
    private const string AuditDisplay = "AuditEntityDisplay.cs";

    private static readonly string[] KeyLiterals =
    [
        "\"GefahrenlageStufe\"", "\"GefahrenlageEinschaetzung\"",
        "\"GefahrenlageSeit\"", "\"GefahrenlageZuvor\"",
    ];

    private static string ProjectRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website"));

    private static string ServiceRoot() => Path.Combine(ProjectRoot(), "Services", "Public");

    private static string ServiceFile()
    {
        var file = Path.Combine(ServiceRoot(), ServiceName);
        Assert.True(File.Exists(file), $"Dienst nicht gefunden: {file}");
        return file;
    }

    private static string[] ProductionFiles()
        => Directory.EnumerateFiles(ProjectRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("Data", "Migrations"), StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void ThePublicSituationService_SavesThroughExactlyOneChokePoint()
    {
        var text = File.ReadAllText(ServiceFile());
        Assert.Single(SaveChanges().Matches(text));
    }

    [Fact]
    public void TheSnapshot_IsDroppedOnlyInThatChokePoint()
    {
        var text = File.ReadAllText(ServiceFile());
        Assert.Single(CacheRemove().Matches(text));
        Assert.Single(CacheSet().Matches(text));
    }

    [Fact]
    public void NoOtherPublicServiceKnowsTheCacheKey()
    {
        var offenders = Directory.EnumerateFiles(ServiceRoot(), "*.cs")
            .Where(f => Path.GetFileName(f) != ServiceName)
            .Where(f => File.ReadAllText(f).Contains(CacheKeyLiteral, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Nur PublicSituationService kennt den Snapshot-Schlüssel: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSettingKeys_AreDeclaredInExactlyOnePlace()
    {
        var offenders = ProductionFiles()
            .Where(f => Path.GetFileName(f) != KeysDeclaration)
            .Where(f => File.ReadAllText(f) is var t && KeyLiterals.Any(k => t.Contains(k, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Die Schlüssel stehen nur in SystemSettingKeys: " + string.Join(", ", offenders));
    }

    [Fact]
    public void OnlyThatOneService_TouchesTheSituationSettings()
    {
        // SystemSettings is everybody's table, so the rule cannot be "who writes it". A second reader would go stale
        // behind this cache; a second writer would leave the snapshot standing.
        var offenders = ProductionFiles()
            .Where(f => Path.GetFileName(f) is not (ServiceName or KeysDeclaration or AuditDisplay))
            .Where(f => File.ReadAllText(f).Contains("SystemSettingKeys.PublicSituation", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Nur PublicSituationService liest und schreibt die Gefahrenlage-Zeilen: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\bSaveChangesAsync\(")]
    private static partial Regex SaveChanges();

    [GeneratedRegex(@"cache\.Remove\(")]
    private static partial Regex CacheRemove();

    [GeneratedRegex(@"cache\.Set\(")]
    private static partial Regex CacheSet();
}
