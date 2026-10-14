using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Every literal link into a section of /nachweis or /einstellungen names a section that exists.</summary>
/// <remarks>
/// A stale slug is no error anywhere: the rail falls back to its first section, and a search hit on a counter-intel
/// rule opened the chronicle that way for weeks. The slug lists in <see cref="MergedPageSections"/> are the truth.
/// </remarks>
public sealed class TabLinkScanTests
{
    private static readonly Regex Link = new(@"/(nachweis|einstellungen)\?tab=([a-z0-9-]+)", RegexOptions.Compiled);

    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    [Fact]
    public void Every_tab_link_names_an_existing_section()
    {
        var root = SourceRoot();
        Assert.True(Directory.Exists(root), $"Quellordner nicht gefunden: {root}");

        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Link.Matches(File.ReadAllText(f))
                .Where(m => !(m.Groups[1].Value == "nachweis" ? MergedPageSections.Monitoring : MergedPageSections.Settings)
                    .Contains(m.Groups[2].Value))
                .Select(m => $"{Path.GetRelativePath(root, f)}: {m.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }
}
