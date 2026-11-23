using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Authorization;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Every section a page guards with an AuthorizeView is in the policy map.</summary>
public sealed class SectionPoliciesTests
{
    private static readonly Regex Wrapper = new(@"<AuthorizeView\b[^>]*?\bPolicy=""@Policies\.(\w+)""[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Section = new(@"<RecordSection\b[^>]*?\bSlug=""([^""@]+)""", RegexOptions.Compiled);
    private static readonly Regex PageDirective = new(@"^@page ""([^""]+)""", RegexOptions.Compiled | RegexOptions.Multiline);

    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    private static IEnumerable<string> PageFiles() => Directory
        .EnumerateFiles(Path.Combine(SourceRoot(), "Components"), "*.razor", SearchOption.AllDirectories);

    // pages with a rail
    private static Dictionary<string, string> RailPages()
    {
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in PageFiles())
        {
            var match = PageDirective.Match(File.ReadAllText(file));
            if (match.Success && MergedPageSections.ByRoute.ContainsKey(match.Groups[1].Value))
            {
                pages[match.Groups[1].Value] = file;
            }
        }
        return pages;
    }

    /// <summary>Slug and policy name of every section inside an AuthorizeView.</summary>
    private static IEnumerable<(string Slug, string PolicyName)> ProtectedSections(string markup)
    {
        foreach (Match wrapper in Wrapper.Matches(markup))
        {
            var depth = 1;
            var at = wrapper.Index + wrapper.Length;
            var end = at;
            while (depth > 0 && end < markup.Length)
            {
                var open = markup.IndexOf("<AuthorizeView", end, StringComparison.Ordinal);
                var close = markup.IndexOf("</AuthorizeView>", end, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }
                if (open >= 0 && open < close)
                {
                    depth++;
                    end = open + 1;
                }
                else
                {
                    depth--;
                    end = close + 1;
                }
            }
            foreach (Match section in Section.Matches(markup[at..end]))
            {
                yield return (section.Groups[1].Value, wrapper.Groups[1].Value);
            }
        }
    }

    private static string PolicyValue(string constName)
        => (string)typeof(Policies).GetField(constName, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    [Fact]
    public void Every_rail_page_with_a_protected_section_is_found()
    {
        var pages = RailPages();

        // no empty scan
        foreach (var route in new[] { "/einstellungen", "/nachweis", "/finanzierungen", "/verpflegung", "/kasse", "/fahndung" })
        {
            Assert.True(pages.ContainsKey(route), $"Seite für {route} nicht gefunden");
        }
    }

    [Fact]
    public void Every_section_a_page_wraps_in_an_AuthorizeView_is_mapped_to_that_policy()
    {
        var problems = new List<string>();
        foreach (var (route, file) in RailPages())
        {
            foreach (var (slug, policyName) in ProtectedSections(File.ReadAllText(file)))
            {
                var mapped = SectionPolicies.For(route, slug);
                if (mapped is null)
                {
                    problems.Add($"{route}?tab={slug}: fehlt in SectionPolicies ({policyName})");
                }
                else if (mapped != PolicyValue(policyName))
                {
                    problems.Add($"{route}?tab={slug}: Seite {policyName}, SectionPolicies {mapped}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Every_mapped_section_exists_and_is_wrapped_by_its_page()
    {
        var pages = RailPages();
        var problems = new List<string>();
        foreach (var (route, sections) in SectionPolicies.ByRoute)
        {
            if (!pages.TryGetValue(route, out var file))
            {
                problems.Add($"{route}: keine Seite gefunden");
                continue;
            }
            var wrapped = ProtectedSections(File.ReadAllText(file)).Select(s => s.Slug).ToHashSet();
            foreach (var slug in sections.Keys)
            {
                if (!MergedPageSections.ByRoute[route].Contains(slug))
                {
                    problems.Add($"{route}?tab={slug}: Abschnitt fehlt in MergedPageSections");
                }
                else if (!wrapped.Contains(slug))
                {
                    problems.Add($"{route}?tab={slug}: Seite schützt den Abschnitt nicht per AuthorizeView");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void An_admin_section_of_the_settings_page_is_mapped_to_the_admin_page_policy()
    {
        Assert.Equal(Policies.AdminPage, SectionPolicies.For("/einstellungen", "system"));
        Assert.Null(SectionPolicies.For("/einstellungen", "discord"));
        Assert.Null(SectionPolicies.For("/unbekannt", "system"));
    }
}
