using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Whether a viewer may open an address from <see cref="AuditEntityDisplay.Route"/>.</summary>
public class RouteAccessServiceTests
{
    private static readonly IRouteAccessService Access = RouteAccessTestHost.Create();

    private static ClaimsPrincipal Admin() => ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build();

    // leadership without the admin flag
    private static ClaimsPrincipal Director() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // read-only supervision without counter-intelligence
    private static ClaimsPrincipal Supervision() => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Trainee() => ClaimsPrincipalBuilder.Agent("trainee").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static async Task<bool> MayOpenAsync(ClaimsPrincipal user, string route)
        => (await Access.OpenableAsync(user, [route])).Contains(route);

    [Fact]
    public async Task AnAdminOnlySectionOpensForAnAdminAndNotForLeadershipWithoutTheFlag()
    {
        Assert.True(await MayOpenAsync(Admin(), "/einstellungen?tab=system"));
        Assert.False(await MayOpenAsync(Director(), "/einstellungen?tab=system"));
    }

    [Theory]
    [InlineData("/einstellungen?tab=system")]
    [InlineData("/einstellungen?tab=status")]
    [InlineData("/einstellungen?tab=partner")]
    [InlineData("/einstellungen?tab=oeffentliche-module")]
    public async Task EveryAdminSectionOfTheSettingsPageStaysClosedToLeadershipWithoutTheFlag(string route)
    {
        Assert.False(await MayOpenAsync(Director(), route));
        Assert.True(await MayOpenAsync(Admin(), route));
    }

    [Theory]
    [InlineData("/einstellungen?tab=discord")]
    [InlineData("/einstellungen?tab=einsatzalarm")]
    [InlineData("/einstellungen?tab=basisdaten")]
    public async Task AnUnprotectedSectionOfTheSamePageOpensForLeadership(string route)
    {
        Assert.True(await MayOpenAsync(Director(), route));
        Assert.True(await MayOpenAsync(Admin(), route));
    }

    [Theory]
    [InlineData("/einstellungen?tab=discord")]
    [InlineData("/einstellungen?tab=system")]
    [InlineData("/einstellungen")]
    public async Task ThePagePolicyDecidesBeforeTheSection(string route)
    {
        // an agent below leadership may not open the settings page
        Assert.False(await MayOpenAsync(Trainee(), route));
    }

    [Fact]
    public async Task ACounterIntelligenceSectionClosesForSupervisionThatOpensTheAdminSections()
    {
        Assert.False(await MayOpenAsync(Supervision(), "/einstellungen?tab=oeffentliche-fuehrung"));
        Assert.True(await MayOpenAsync(Director(), "/einstellungen?tab=oeffentliche-fuehrung"));
        Assert.True(await MayOpenAsync(Supervision(), "/einstellungen?tab=system"));
    }

    [Theory]
    [InlineData("/nachweis?tab=aenderungen", false)]
    [InlineData("/nachweis?tab=chronik", true)]
    [InlineData("/finanzierungen?tab=budgets", false)]
    [InlineData("/finanzierungen?tab=katalog", true)]
    [InlineData("/kasse?tab=vorlagen", false)]
    [InlineData("/kasse?tab=kopfgeld", true)]
    [InlineData("/personen/abc", true)]
    [InlineData("/vorgaenge/abc", true)]
    public async Task ASectionOfAnotherPageFollowsItsOwnPolicy(string route, bool trainee)
    {
        Assert.Equal(trainee, await MayOpenAsync(Trainee(), route));
        Assert.True(await MayOpenAsync(Director(), route));
    }

    [Fact]
    public async Task APartnerMayNotOpenAnInternalPageOrAnInternalSectionOfASharedPage()
    {
        Assert.False(await MayOpenAsync(Partner(), "/nachweis?tab=chronik"));
        Assert.False(await MayOpenAsync(Partner(), "/fahndung?tab=oeffentlich"));
        Assert.True(await MayOpenAsync(Partner(), "/fahndung?tab=observationen"));
        Assert.True(await MayOpenAsync(Trainee(), "/fahndung?tab=oeffentlich"));
    }

    [Fact]
    public async Task AnUnknownSectionFallsBackToThePagePolicyAlone()
    {
        Assert.True(await MayOpenAsync(Director(), "/einstellungen?tab=gibt-es-nicht"));
        Assert.False(await MayOpenAsync(Trainee(), "/einstellungen?tab=gibt-es-nicht"));
    }

    [Fact]
    public async Task AnAddressNoPageAnswersIsNeverOpen()
    {
        // the catch-all page must not count as a match
        Assert.False(await MayOpenAsync(Admin(), "/gibt/es/nicht"));
    }

    [Fact]
    public async Task OnlyTheOpenableAddressesComeBackAndNullIsSkipped()
    {
        var open = await Access.OpenableAsync(Director(),
            ["/einstellungen?tab=system", "/einstellungen?tab=discord", null, "/personen/abc", "/personen/abc"]);

        Assert.Equal(new[] { "/einstellungen?tab=discord", "/personen/abc" }, open.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARemovedAddressIsJudgedByThePageItRedirectsTo()
    {
        Assert.False(await MayOpenAsync(Trainee(), "/abmeldungen/uebersicht"));
        Assert.False(await MayOpenAsync(Partner(), "/abmeldungen/uebersicht"));
        Assert.True(await MayOpenAsync(Director(), "/abmeldungen/uebersicht"));
        Assert.False(await MayOpenAsync(Director(), "/admin/system"));
        Assert.True(await MayOpenAsync(Admin(), "/admin/system"));
    }

    [Fact]
    public async Task AnonymousViewersOpenNothingBehindALoginPolicy()
    {
        Assert.False(await MayOpenAsync(ClaimsPrincipalBuilder.Anonymous(), "/personen/abc"));
        Assert.False(await MayOpenAsync(ClaimsPrincipalBuilder.Anonymous(), "/einstellungen?tab=discord"));
    }

    /// <summary>Every arm of Route must resolve to a page.</summary>
    [Fact]
    public void EveryRouteTheAuditLinksHandOutResolvesToAPage()
    {
        var offenders = RouteTypes()
            .Select(t => (Type: t, Route: AuditEntityDisplay.Route(t, "id-1")))
            .Where(x => x.Route is not null)
            .Where(x => PageRouteTable.Find(PathOf(LegacyRoutes.Target(x.Route) ?? x.Route!)) is null)
            .Select(x => $"{x.Type}: {x.Route}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string PathOf(string route) => route.Split('?')[0];

    // every quoted arm key of the display switches
    private static IEnumerable<string> RouteTypes()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Services", "AuditEntityDisplay.cs"));
        return Regex.Matches(source, "\"([A-Z][A-Za-z]+)\"(?= (?:=>|or ))")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(t => t != "SystemSetting");
    }

    [Fact]
    public void EverySystemSettingKeyWithASectionResolvesToAPage()
    {
        var keys = typeof(SystemSettingKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        var offenders = keys
            .Select(k => (Key: k, Route: AuditEntityDisplay.Route("SystemSetting", k)))
            .Where(x => x.Route is not null)
            .Where(x => PageRouteTable.Find(PathOf(x.Route!)) is null)
            .Select(x => $"{x.Key}: {x.Route}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));
}
