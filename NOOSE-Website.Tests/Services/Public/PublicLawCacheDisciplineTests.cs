using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The public law snapshot, and the one thing that makes it different from its siblings.</summary>
/// <remarks>
/// Its table is not the public service's own: <c>Gesetze</c> is an internal, widely read table, and
/// <c>ILawService</c> curates it. So the rule is not "one writer" but "every writer drops the snapshot" — a corrected
/// or deleted paragraph would otherwise stand outside for a whole cache window. Readers are numerous and harmless,
/// which is why they are listed by name with a reason rather than pattern-matched away.
/// </remarks>
public partial class PublicLawCacheDisciplineTests
{
    private const string CacheKeyLiteral = "\"OeffentlichesRecht\"";
    private const string ServiceName = "PublicLawService.cs";

    /// <summary>The files allowed to write <c>Gesetze</c>; both must drop the snapshot.</summary>
    private static readonly string[] Writers = ["PublicLawService.cs", "LawService.cs"];

    /// <summary>Files that read the law table next to a save of their own, and why that is not a write.</summary>
    /// <remarks>
    /// A new name here is a decision, not a formality: either the file only reads paragraphs, then it belongs in this
    /// list with its reason, or it writes them, then it belongs in <see cref="Writers"/> and drops the snapshot.
    /// </remarks>
    private static readonly Dictionary<string, string> Readers = new(StringComparer.Ordinal)
    {
        ["SearchIndexBackfillWorker.cs"] = "Liest Paragrafen, um den Suchindex zu füllen; geschrieben wird der Index.",
        ["LinkService.cs"] = "Löst einen Paragrafen als Verknüpfungsziel auf; geschrieben wird die Verknüpfung.",
        ["PartnerShareService.cs"] = "Liest Paragrafen für die Partnerfreigabe; geschrieben wird die Freigabe.",
        ["WarrantService.cs"] = "Liest Paragrafen als Rechtsgrundlage einer Anordnung; geschrieben wird die Anordnung.",
    };

    private static string ProjectRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website"));

    private static string ServiceRoot() => Path.Combine(ProjectRoot(), "Services", "Public");

    [Fact]
    public void ThePublicLawService_SavesThroughExactlyOneChokePoint()
    {
        var text = File.ReadAllText(Path.Combine(ServiceRoot(), ServiceName));
        Assert.Single(SaveChanges().Matches(text));
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
            "Nur PublicLawService kennt den Snapshot-Schlüssel: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryWriterOfTheLawTable_DropsThePublicSnapshot()
    {
        foreach (var name in Writers)
        {
            var file = Directory.EnumerateFiles(ProjectRoot(), name, SearchOption.AllDirectories).Single();
            Assert.Contains("InvalidatePublicViewAsync", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryFileTouchingTheLawTable_IsAKnownWriterOrAListedReader()
    {
        var known = Writers.Concat(Readers.Keys).ToHashSet(StringComparer.Ordinal);

        var unknown = Directory.EnumerateFiles(ProjectRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("Data", "Migrations"), StringComparison.Ordinal))
            .Where(f => !known.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadAllText(f) is var t
                && t.Contains("db.Laws", StringComparison.Ordinal)
                && SaveChanges().IsMatch(t))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(unknown.Length == 0,
            "Wer die Gesetzestabelle anfasst und speichert, ist Schreiber (dann Snapshot verwerfen) oder Leser "
            + "(dann mit Begründung eintragen): " + string.Join(", ", unknown));
    }

    [GeneratedRegex(@"\bSaveChangesAsync\(")]
    private static partial Regex SaveChanges();

    [GeneratedRegex(@"cache\.Remove\(")]
    private static partial Regex CacheRemove();

    [GeneratedRegex(@"cache\.Set\(")]
    private static partial Regex CacheSet();
}
