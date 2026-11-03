using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services;

/// <summary>Structural guard: the HRT title is for the house only, so no outward or stored text may ask for it.</summary>
/// <remarks>
/// The titles of the HRT (Operator and up) come from <c>RankDisplay.Name(rank, division)</c> and
/// <c>DivisionRules.RankName(agent)</c>. Applicant letters, the public roster, certificates, partner-visible output,
/// the public and citizen pages and the placeholders that are expanded into stored documents use the neutral
/// <c>RankDisplay.Name(rank)</c>: the division never leaves the house, and a stored title stays as it was written.
/// </remarks>
public partial class RankTitleScanTests
{
    /// <summary>Files and folders whose text leaves the house or is stored; relative to the app project.</summary>
    private static readonly string[] Outward =
    [
        "Services/BewerbungTemplateRenderer.cs",
        "Services/PlaceholderService.cs",
        "Components/Pages/Recruiting/Shared/BewerbungMessagePanel.razor",
        "Services/Public",
        "Services/Decorations",
        "Components/Pages/Public",
        "Components/Pages/Portal",
        "Components/Pages/Partner",
        "Components/Pages/Decorations",
        "Components/Pages/Legal",
    ];

    [Fact]
    public void No_outward_or_stored_text_asks_for_an_hrt_title()
    {
        var offenders = new List<string>();
        foreach (var file in OutwardFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in new[] { InternalName(), HrtTitle(), TwoArgumentName() })
            {
                if (pattern.IsMatch(text))
                {
                    offenders.Add(Path.GetRelativePath(AppRoot(), file).Replace('\\', '/'));
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Diese Stellen geben Text nach außen oder speichern ihn und dürfen keinen HRT-Titel verlangen "
            + "(RankDisplay.Name(rank) ist der neutrale Titel): " + string.Join(", ", offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_scan_finds_the_files_it_is_meant_to_guard()
    {
        var files = OutwardFiles().Select(f => Path.GetRelativePath(AppRoot(), f).Replace('\\', '/')).ToList();

        Assert.Contains("Services/BewerbungTemplateRenderer.cs", files);
        Assert.Contains("Services/PlaceholderService.cs", files);
        Assert.Contains("Components/Pages/Recruiting/Shared/BewerbungMessagePanel.razor", files);
        Assert.Contains("Services/Public/PublicLeadershipService.cs", files);
        Assert.Contains("Components/Pages/Decorations/Shared/CertificateSheet.razor", files);
    }

    private static IEnumerable<string> OutwardFiles()
    {
        foreach (var entry in Outward)
        {
            var path = Path.Combine(AppRoot(), entry);
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }
            Assert.True(Directory.Exists(path), $"{entry} gibt es nicht mehr: die Liste der Außenstellen nachziehen.");
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal)))
            {
                yield return file;
            }
        }
    }

    /// <summary>The agent-row helper that picks the title of the agent's division.</summary>
    [GeneratedRegex(@"\bDivisionRules\s*\.\s*RankName\s*\(")]
    private static partial Regex InternalName();

    [GeneratedRegex(@"\bHrtTitle\s*\(")]
    private static partial Regex HrtTitle();

    /// <summary>The overload that takes a division: one level of nested parentheses in the first argument is enough.</summary>
    [GeneratedRegex(@"\bRankDisplay\s*\.\s*Name\s*\((?:[^()]|\([^()]*\))*,")]
    private static partial Regex TwoArgumentName();

    private static string AppRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));
}
