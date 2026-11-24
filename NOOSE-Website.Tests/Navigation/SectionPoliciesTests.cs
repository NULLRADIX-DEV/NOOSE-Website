using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Every section a page guards with an AuthorizeView is in the policy map. The pages read are the rail
/// pages of <see cref="MergedPageSections.ByRoute"/> and every page an address of
/// <see cref="AuditEntityDisplay.Route"/> points at.</summary>
public sealed class SectionPoliciesTests
{
    private static readonly Regex Wrapper = new(@"<AuthorizeView\b[^>]*?\bPolicy=""@Policies\.(\w+)""[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Section = new(@"<RecordSection\b[^>]*?\bSlug=""([^""@]+)""", RegexOptions.Compiled);
    private static readonly Regex PageDirective = new(@"^@page ""([^""]+)""", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex RouteArm = new("\"([A-Z][A-Za-z]+)\"(?= (?:=>|or ))", RegexOptions.Compiled);

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

    // pages that an audit address opens, keyed by their @page template, plus the addresses that resolve to no file
    private static (Dictionary<string, string> Pages, List<string> Unresolved) RoutePages()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in PageFiles())
        {
            foreach (Match match in PageDirective.Matches(File.ReadAllText(file)))
            {
                files[match.Groups[1].Value] = file;
            }
        }

        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        foreach (var route in AuditRoutes())
        {
            // follow legacy redirects like RouteAccessService
            var path = (LegacyRoutes.Target(route) ?? route).Split('?')[0];
            var template = PageRouteTable.Find(path)?.GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>().Select(a => a.Template).FirstOrDefault(files.ContainsKey);
            if (template is null)
            {
                unresolved.Add(route);
            }
            else
            {
                pages[template] = files[template];
            }
        }
        return (pages, unresolved);
    }

    // every address AuditEntityDisplay.Route hands out: all arms, all settings keys
    private static IEnumerable<string> AuditRoutes()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Services", "AuditEntityDisplay.cs"));
        var arms = RouteArm.Matches(source).Select(m => m.Groups[1].Value).Distinct().Where(t => t != "SystemSetting");
        var keys = typeof(SystemSettingKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            // keys of the config services, and one key per webhook and role category
            .Concat([
                FinancingConfigService.SettingKey, FoodLimitConfigService.SettingKey, InactivityRadarService.SettingKey,
                ProbationRules.SettingKey, LlmQuotaConfigService.SettingKey, NooseiPrompts.AddendumKey,
                NooseiProviderService.SettingKey, CareerRequirementsService.SettingKey,
                RecruitingAutomationService.EnabledKey, RecruitingAutomationService.TemplateKey,
                SystemSettingKeys.DiscordWebhookPrefix + "x", SystemSettingKeys.DiscordRolePrefix + "x",
            ]);
        return arms.Select(t => AuditEntityDisplay.Route(t, "id-1"))
            .Concat(keys.Select(k => AuditEntityDisplay.Route("SystemSetting", k)))
            .OfType<string>()
            .Distinct();
    }

    // rail pages and the pages audit addresses open, keyed by route or @page template
    private static Dictionary<string, string> ScannedPages()
    {
        var pages = RailPages();
        foreach (var (route, file) in RoutePages().Pages)
        {
            pages.TryAdd(route, file);
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
    public void Every_page_an_audit_route_points_at_is_found()
    {
        var (pages, unresolved) = RoutePages();

        Assert.Empty(unresolved);
        // reached only through Route: no entry in MergedPageSections, but a guarded section of its own
        foreach (var route in new[] { "/dienstverordnung/absatz/{Id}", "/personen/{Id}", "/einstellungen", "/bewerbungen" })
        {
            Assert.True(pages.ContainsKey(route), $"Seite für {route} nicht gefunden");
        }
        Assert.Contains(("kenntnisnahme", nameof(Policies.LeadershipPage)),
            ProtectedSections(File.ReadAllText(pages["/dienstverordnung/absatz/{Id}"])));
        Assert.DoesNotContain("/dienstverordnung/absatz/{Id}", MergedPageSections.ByRoute.Keys);
    }

    [Fact]
    public void Every_section_a_page_wraps_in_an_AuthorizeView_is_mapped_to_that_policy()
    {
        var problems = new List<string>();
        foreach (var (route, file) in ScannedPages())
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
        var pages = ScannedPages();
        var problems = new List<string>();
        foreach (var (route, sections) in SectionPolicies.ByRoute)
        {
            if (!pages.TryGetValue(route, out var file))
            {
                problems.Add($"{route}: keine Seite gefunden");
                continue;
            }
            var markup = File.ReadAllText(file);
            var wrapped = ProtectedSections(markup).Select(s => s.Slug).ToHashSet();
            var declared = Section.Matches(markup).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (var slug in sections.Keys)
            {
                // a page without an entry in MergedPageSections declares its sections itself
                var known = MergedPageSections.ByRoute.TryGetValue(route, out var merged)
                    ? merged.Contains(slug)
                    : declared.Contains(slug);
                if (!known)
                {
                    problems.Add($"{route}?tab={slug}: Abschnitt fehlt in MergedPageSections und auf der Seite");
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

    [Fact]
    public void A_detail_page_section_is_found_by_the_concrete_address()
    {
        Assert.Equal(Policies.LeadershipPage, SectionPolicies.For("/dienstverordnung/absatz/abc-1", "kenntnisnahme"));
        Assert.Null(SectionPolicies.For("/dienstverordnung/absatz/abc-1", "absatz"));
        Assert.Null(SectionPolicies.For("/dienstverordnung/absatz", "kenntnisnahme"));
        Assert.Null(SectionPolicies.For("/dienstverordnung/absatz/abc-1/mehr", "kenntnisnahme"));
    }
}
