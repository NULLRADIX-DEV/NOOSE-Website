using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Who may report, read, delete and hear about a firearm-use entry, and the policy twin of the report guard.</summary>
public class FirearmUseRulesTests
{
    private static ClaimsPrincipalBuilder Agent(string id, Rank rank) => ClaimsPrincipalBuilder.Agent(id).WithRank(rank);

    // ---- the reason ----

    [Fact]
    public void CleanReason_TrimsAndKeepsTheText()
        => Assert.Equal("Warnschuss", FirearmUseRules.CleanReason("  Warnschuss \n"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public void CleanReason_RefusesAnEmptyText(string? reason)
        => Assert.Throws<InvalidOperationException>(() => FirearmUseRules.CleanReason(reason));

    [Fact]
    public void CleanReason_TheLimitIsTheColumnWidth()
    {
        Assert.Equal(1000, FirearmUseRules.MaxReasonLength);
        Assert.Equal(1000, FirearmUseRules.CleanReason(new string('x', 1000)).Length);
        Assert.Throws<InvalidOperationException>(() => FirearmUseRules.CleanReason(new string('x', 1001)));
    }

    // ---- reading ----

    [Fact]
    public void MayRead_OwnFileForAnyInHouseAgent()
        => Assert.True(FirearmUseRules.MayRead(Agent("a1", Rank.Trainee).Build(), "a1"));

    [Fact]
    public void MayRead_AnotherFileOnlyWithTheLeadershipReadOrSupervision()
    {
        Assert.False(FirearmUseRules.MayRead(Agent("a2", Rank.SeniorSpecialAgent).Build(), "a1"));
        Assert.True(FirearmUseRules.MayRead(Agent("a2", Rank.SectionChief).Build(), "a1"));
        Assert.True(FirearmUseRules.MayRead(Agent("a2", Rank.Trainee).AsAdmin().Build(), "a1"));
        Assert.True(FirearmUseRules.MayRead(ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build(), "a1"));
    }

    [Fact]
    public void MayRead_NeverAPartnerACitizenOrAnApplicant_EvenForTheirOwnId()
    {
        Assert.False(FirearmUseRules.MayRead(
            Agent("p1", Rank.Director).AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build(), "p1"));
        Assert.False(FirearmUseRules.MayRead(
            ClaimsPrincipalBuilder.Agent("c1").WithStatus(AgentStatus.Civilian).Build(), "c1"));
        Assert.False(FirearmUseRules.MayRead(
            ClaimsPrincipalBuilder.Agent("b1").WithStatus(AgentStatus.Applicant).Build(), "b1"));
        Assert.False(FirearmUseRules.MayRead(ClaimsPrincipalBuilder.Anonymous(), "a1"));
    }

    // ---- deleting ----

    [Fact]
    public void MayDelete_LeadershipWithWriteAccess_ForSomeoneElsesReport()
    {
        Assert.True(FirearmUseRules.MayDelete(Agent("l1", Rank.SectionChief).Build(), "a1"));
        Assert.True(FirearmUseRules.MayDelete(Agent("l1", Rank.Director).Build(), "a1"));
        Assert.True(FirearmUseRules.MayDelete(Agent("l1", Rank.Trainee).AsAdmin().Build(), "a1"));
    }

    [Fact]
    public void MayDelete_NeverTheOwnReport_AdminIncluded()
    {
        Assert.False(FirearmUseRules.MayDelete(Agent("l1", Rank.Director).Build(), "l1"));
        Assert.False(FirearmUseRules.MayDelete(Agent("l1", Rank.Trainee).AsAdmin().Build(), "l1"));
    }

    [Fact]
    public void MayDelete_NotBelowTheLeadership_NotForSupervisionDemoOrPartner()
    {
        Assert.False(FirearmUseRules.MayDelete(Agent("a2", Rank.SeniorSpecialAgent).Build(), "a1"));
        Assert.False(FirearmUseRules.MayDelete(ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build(), "a1"));
        Assert.False(FirearmUseRules.MayDelete(Agent("l1", Rank.Director).AsDemo().Build(), "a1"));
        Assert.False(FirearmUseRules.MayDelete(
            Agent("l1", Rank.Director).AsPartner(PartnerAgency.LSMD, PartnerRank.Chief).Build(), "a1"));
    }

    // ---- the bell ----

    [Fact]
    public void Notifies_TheBoardWhateverTheReportersDivision()
    {
        var board = new OrgUnit(Division.Cid, IsBoard: true);

        Assert.True(FirearmUseRules.Notifies(board, Division.Hrt));
        Assert.True(FirearmUseRules.Notifies(board, null));
    }

    [Fact]
    public void Notifies_ADivisionOnlyForItsOwnMembers()
    {
        Assert.True(FirearmUseRules.Notifies(new OrgUnit(Division.Ctd, IsBoard: false), Division.Ctd));
        Assert.False(FirearmUseRules.Notifies(new OrgUnit(Division.Cid, IsBoard: false), Division.Ctd));
        Assert.False(FirearmUseRules.Notifies(new OrgUnit(Division.Hrt, IsBoard: false), Division.Cid));
    }

    [Fact]
    public void Notifies_NobodyBelowTheBoardWhenTheReporterHasNoHome()
        => Assert.False(FirearmUseRules.Notifies(new OrgUnit(Division.Cid, IsBoard: false), null));

    // ---- the page: leadership section, never a partner ----

    [Fact]
    public void TheNavEntry_SitsInTheLeadershipSection_WithTheOverviewRoute()
    {
        var entry = NavCatalog.ByKey("schusswaffengebrauch");

        Assert.NotNull(entry);
        Assert.Equal("/schusswaffengebrauch", entry.Route);
        Assert.Equal(NavSection.VerwaltungFuehrung, entry.Section);
        Assert.Equal(Policies.LeadershipPage, NavSectionPolicy.For(entry.Section));
        Assert.Equal(entry.Key, NavCatalog.ByRoute("/schusswaffengebrauch")?.Key);
    }

    [Fact]
    public void ThePageAndItsFiltersAreBlockedForEveryPartnerFeature()
    {
        var everything = Enum.GetValues<PartnerFeature>().Aggregate(PartnerFeature.None, (all, f) => all | f);

        Assert.False(PartnerRoutes.IsAllowed("schusswaffengebrauch", everything));
        Assert.False(PartnerRoutes.IsAllowed("schusswaffengebrauch?ag=a1", everything));
    }

    // ---- the policy twin of Permission.RequireFirearmUseReport ----

    private static async Task<bool> PolicyAllowsAsync(ClaimsPrincipal user)
    {
        await using var provider = new ServiceCollection().AddLogging().AddNooseAuthorization().BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, Policies.FirearmUseReport);
        return result.Succeeded;
    }

    private static bool GuardAllows(ClaimsPrincipal user)
    {
        try
        {
            Permission.RequireFirearmUseReport(user);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Policy_AndGuardAgree_ForEveryKindOfAccount()
    {
        var principals = new Dictionary<string, ClaimsPrincipal>
        {
            ["trainee"] = Agent("a1", Rank.Trainee).Build(),
            ["director"] = Agent("a1", Rank.Director).Build(),
            ["admin"] = Agent("a1", Rank.SpecialAgent).AsAdmin().Build(),
            ["supervision"] = ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build(),
            ["demo"] = DemoIdentity.BuildPrincipal(),
            ["partner"] = Agent("p1", Rank.Trainee).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            ["citizen"] = ClaimsPrincipalBuilder.Agent("c1").WithStatus(AgentStatus.Civilian).Build(),
            ["pending"] = ClaimsPrincipalBuilder.Agent("w1").WithStatus(AgentStatus.Pending).Build(),
            ["anonymous"] = ClaimsPrincipalBuilder.Anonymous(),
        };

        foreach (var (name, user) in principals)
        {
            Assert.True(GuardAllows(user) == await PolicyAllowsAsync(user), $"Guard und Policy weichen ab bei: {name}");
        }
    }


    [Fact]
    public async Task A_hidden_team_lead_with_the_admin_flag_may_not_report()
    {
        var user = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().AsAdmin().Build();

        Assert.False(user.MayReportFirearmUse());
        Assert.False(GuardAllows(user));
        Assert.False(await PolicyAllowsAsync(user));
    }
    [Fact]
    public async Task Policy_AdmitsInHouseWritersOnly()
    {
        Assert.True(await PolicyAllowsAsync(Agent("a1", Rank.Trainee).Build()));
        Assert.True(await PolicyAllowsAsync(Agent("a1", Rank.Director).Build()));
        // WriteAccess alone would admit a citizen, who carries neither a rank nor an agent status
        Assert.False(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent("c1").WithStatus(AgentStatus.Civilian).Build()));
        Assert.False(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build()));
        Assert.False(await PolicyAllowsAsync(DemoIdentity.BuildPrincipal()));
    }
}
