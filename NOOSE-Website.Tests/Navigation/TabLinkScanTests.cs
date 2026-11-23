using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Every literal link into a section of a page in <see cref="MergedPageSections.ByRoute"/> names an existing section.</summary>
/// <remarks>
/// A stale slug is no error anywhere: the rail falls back to its first section, and a search hit on a counter-intel
/// rule opened the chronicle that way for weeks. The slug lists in <see cref="MergedPageSections"/> are the truth.
/// </remarks>
public sealed class TabLinkScanTests
{
    // longest route first
    private static readonly Regex Link = new(
        @"(?<![\w/.\-])(" + string.Join("|", MergedPageSections.ByRoute.Keys
            .OrderByDescending(r => r.Length).Select(Regex.Escape)) + @")\?tab=([a-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            .SelectMany(f => StaleLinks(File.ReadAllText(f)).Select(link => $"{Path.GetRelativePath(root, f)}: {link}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("/tickets?tab=gibtsnicht")]
    [InlineData("/dienstverordnung?tab=aufgehobn")]
    [InlineData("/tests?tab=korrektor")]
    public void A_stale_link_to_a_listed_rail_page_is_found(string link)
        => Assert.Equal(link, Assert.Single(StaleLinks($"Href=\"{link}\"")));

    [Theory]
    [InlineData("/tickets?tab=beteiligt")]
    [InlineData("/tickets?tab={slug}")]
    [InlineData("/portal/tickets?tab=gibtsnicht")]
    public void A_valid_dynamic_or_foreign_link_is_left_alone(string link)
        => Assert.Empty(StaleLinks($"Href=\"{link}\""));

    private static List<string> StaleLinks(string text)
        => Link.Matches(text)
            .Where(m => !MergedPageSections.ByRoute[m.Groups[1].Value].Contains(m.Groups[2].Value))
            .Select(m => m.Value)
            .ToList();
}
