using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Authorization;

public class AgentPrincipalExtensionsTests
{
    // ---------- GetAgentId ----------

    [Fact]
    public void GetAgentId_defaultAgent_returnsNameIdentifier()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Equal("agent-1", user.GetAgentId());
    }

    [Fact]
    public void GetAgentId_customId_returnsThatId()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent("agent-77").Build();
        Assert.Equal("agent-77", user.GetAgentId());
    }

    [Fact]
    public void GetAgentId_anonymous_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Anonymous();
        Assert.Null(user.GetAgentId());
    }

    // ---------- GetCodename ----------

    [Fact]
    public void GetCodename_whenSet_returnsCodename()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithCodename("Ghost").Build();
        Assert.Equal("Ghost", user.GetCodename());
    }

    [Fact]
    public void GetCodename_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Null(user.GetCodename());
    }

    // ---------- GetBadgeNumber ----------

    [Fact]
    public void GetBadgeNumber_whenSet_returnsBadge()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithBadge("A-12").Build();
        Assert.Equal("A-12", user.GetBadgeNumber());
    }

    [Fact]
    public void GetBadgeNumber_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Null(user.GetBadgeNumber());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void GetBadgeNumber_whenBlank_returnsNull(string value)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.BadgeNumber, value).Build();
        Assert.Null(user.GetBadgeNumber());
    }

    // ---------- GetRank ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void GetRank_definedRankClaim_returnsRank(int rankValue)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Rank, rankValue.ToString()).Build();
        Assert.Equal((Rank)rankValue, user.GetRank());
    }

    [Fact]
    public void GetRank_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Null(user.GetRank());
    }

    [Theory]
    [InlineData("0")]      // below first defined member (JuniorAgent = 1)
    [InlineData("7")]      // one above Director (6)
    [InlineData("99")]     // far out of range
    [InlineData("-1")]     // negative
    [InlineData("abc")]    // non-numeric
    [InlineData("")]       // empty
    [InlineData("3.5")]    // not an int
    public void GetRank_undefinedOrMalformed_returnsNull(string raw)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Rank, raw).Build();
        Assert.Null(user.GetRank());
    }

    // ---------- GetStatus ----------

    [Fact]
    public void GetStatus_defaultAgent_returnsActive()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Equal(AgentStatus.Active, user.GetStatus());
    }

    [Theory]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Applicant)]
    public void GetStatus_validName_returnsStatus(AgentStatus status)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithStatus(status).Build();
        Assert.Equal(status, user.GetStatus());
    }

    [Fact]
    public void GetStatus_numericString_parsesToStatus()
    {
        // Enum.TryParse also accepts the numeric backing value.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Status, "3").Build();
        Assert.Equal(AgentStatus.Applicant, user.GetStatus());
    }

    [Fact]
    public void GetStatus_wrongCaseName_returnsNull()
    {
        // The two-argument Enum.TryParse overload is case-sensitive.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Status, "active").Build();
        Assert.Null(user.GetStatus());
    }

    [Fact]
    public void GetStatus_unparseable_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Status, "notastatus").Build();
        Assert.Null(user.GetStatus());
    }

    [Fact]
    public void GetStatus_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Anonymous();
        Assert.Null(user.GetStatus());
    }

    // ---------- IsAdmin (string "true" predicate, representative) ----------

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("tRuE", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(" true ", false)] // no trimming; Ordinal comparison
    public void IsAdmin_claimValue_matchesTrueOrdinalIgnoreCase(string value, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.IsAdmin, value).Build();
        Assert.Equal(expected, user.IsAdmin());
    }

    [Fact]
    public void IsAdmin_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsAdmin());
    }

    [Fact]
    public void IsAdmin_asAdmin_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.IsAdmin());
    }

    // ---------- IsBootstrapAdmin ----------

    [Fact]
    public void IsBootstrapAdmin_asBootstrap_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsBootstrap().Build();
        Assert.True(user.IsBootstrapAdmin());
    }

    [Fact]
    public void IsBootstrapAdmin_caseInsensitiveTrue_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.IsBootstrap, "TRUE").Build();
        Assert.True(user.IsBootstrapAdmin());
    }

    [Fact]
    public void IsBootstrapAdmin_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsBootstrapAdmin());
    }

    // ---------- IsTRU ----------

    [Fact]
    public void IsTRU_asTru_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTru().Build();
        Assert.True(user.IsTRU());
    }

    [Fact]
    public void IsTRU_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsTRU());
    }

    // ---------- IsHRB ----------

    [Fact]
    public void IsHRB_asHrb_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsHrb().Build();
        Assert.True(user.IsHRB());
    }

    [Fact]
    public void IsHRB_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsHRB());
    }

    // ---------- IsTeamLead ----------

    [Fact]
    public void IsTeamLead_asTeamLead_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.True(user.IsTeamLead());
    }

    [Fact]
    public void IsTeamLead_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsTeamLead());
    }

    // ---------- IsDemo ----------

    [Fact]
    public void IsDemo_asDemo_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsDemo().Build();
        Assert.True(user.IsDemo());
    }

    [Fact]
    public void IsDemo_whenMissing_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsDemo());
    }

    // ---------- IsOnlyReader (teamlead && !admin) ----------

    [Theory]
    [InlineData(false, false, false)] // neither
    [InlineData(true, false, true)]   // teamlead only => reader
    [InlineData(false, true, false)]  // admin only
    [InlineData(true, true, false)]   // teamlead + admin => not reader
    public void IsOnlyReader_teamleadAndNotAdmin(bool teamLead, bool admin, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead(teamLead).AsAdmin(admin).Build();
        Assert.Equal(expected, user.IsOnlyReader());
    }

    // ---------- GetPartnerAgency ----------

    [Theory]
    [InlineData(1, PartnerAgency.DoJ)]
    [InlineData(2, PartnerAgency.LSPD)]
    [InlineData(3, PartnerAgency.LSMD)]
    public void GetPartnerAgency_definedClaim_returnsAgency(int value, PartnerAgency expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.PartnerAgency, value.ToString()).Build();
        Assert.Equal(expected, user.GetPartnerAgency());
    }

    [Fact]
    public void GetPartnerAgency_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Null(user.GetPartnerAgency());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("99")]
    [InlineData("xyz")]
    [InlineData("")]
    public void GetPartnerAgency_undefinedOrMalformed_returnsNull(string raw)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.PartnerAgency, raw).Build();
        Assert.Null(user.GetPartnerAgency());
    }

    // ---------- GetPartnerRank ----------

    [Theory]
    [InlineData(1, PartnerRank.Member)]
    [InlineData(2, PartnerRank.Special)]
    [InlineData(3, PartnerRank.Chief)]
    public void GetPartnerRank_definedClaim_returnsRank(int value, PartnerRank expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.PartnerRank, value.ToString()).Build();
        Assert.Equal(expected, user.GetPartnerRank());
    }

    [Fact]
    public void GetPartnerRank_whenMissing_returnsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.Null(user.GetPartnerRank());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("nope")]
    public void GetPartnerRank_undefinedOrMalformed_returnsNull(string raw)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.PartnerRank, raw).Build();
        Assert.Null(user.GetPartnerRank());
    }

    // ---------- IsPartner ----------

    [Fact]
    public void IsPartner_withPartnerAgency_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();
        Assert.True(user.IsPartner());
    }

    [Fact]
    public void IsPartner_internalAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsPartner());
    }

    // ---------- HasPartnerRank ----------

    [Fact]
    public void HasPartnerRank_sameAgencyRankAboveMinimum_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();
        Assert.True(user.HasPartnerRank(PartnerAgency.LSPD, PartnerRank.Member));
    }

    [Fact]
    public void HasPartnerRank_sameAgencyRankEqualsMinimum_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();
        Assert.True(user.HasPartnerRank(PartnerAgency.LSPD, PartnerRank.Special));
    }

    [Fact]
    public void HasPartnerRank_sameAgencyRankBelowMinimum_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();
        Assert.False(user.HasPartnerRank(PartnerAgency.LSPD, PartnerRank.Chief));
    }

    [Fact]
    public void HasPartnerRank_differentAgency_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();
        Assert.False(user.HasPartnerRank(PartnerAgency.DoJ, PartnerRank.Member));
    }

    [Fact]
    public void HasPartnerRank_nonPartner_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.HasPartnerRank(PartnerAgency.LSPD, PartnerRank.Member));
    }

    // ---------- MayWrite (!OnlyReader && !Partner && !Demo) ----------

    [Fact]
    public void MayWrite_plainAgent_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).Build();
        Assert.True(user.MayWrite());
    }

    [Fact]
    public void MayWrite_adminEvenIfTeamLead_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().AsAdmin().Build();
        Assert.True(user.MayWrite());
    }

    [Fact]
    public void MayWrite_onlyReader_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.False(user.MayWrite());
    }

    [Fact]
    public void MayWrite_partner_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();
        Assert.False(user.MayWrite());
    }

    [Fact]
    public void MayWrite_demo_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsDemo().Build();
        Assert.False(user.MayWrite());
    }

    // ---------- MayContribute (MayWrite || IsPartner) ----------

    [Fact]
    public void MayContribute_plainAgent_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.True(user.MayContribute());
    }

    [Fact]
    public void MayContribute_partner_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSMD, PartnerRank.Member).Build();
        Assert.True(user.MayContribute());
    }

    [Fact]
    public void MayContribute_onlyReader_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.False(user.MayContribute());
    }

    [Fact]
    public void MayContribute_demo_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsDemo().Build();
        Assert.False(user.MayContribute());
    }

    // ---------- IsLeadership (admin || rank >= SupervisorySpecialAgent(4)) ----------

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void IsLeadership_byRank(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();
        Assert.Equal(expected, user.IsLeadership());
    }

    [Fact]
    public void IsLeadership_adminWithoutRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.IsLeadership());
    }

    [Fact]
    public void IsLeadership_noRankNoAdmin_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsLeadership());
    }

    // ---------- MayClassifiedRead (leadership || onlyReader) ----------

    [Fact]
    public void MayClassifiedRead_leadership_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.True(user.MayClassifiedRead());
    }

    [Fact]
    public void MayClassifiedRead_onlyReader_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.True(user.MayClassifiedRead());
    }

    [Fact]
    public void MayClassifiedRead_juniorAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).Build();
        Assert.False(user.MayClassifiedRead());
    }

    // ---------- MayAllTaskforcesSee (leadership || onlyReader) ----------

    [Fact]
    public void MayAllTaskforcesSee_leadership_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.MayAllTaskforcesSee());
    }

    [Fact]
    public void MayAllTaskforcesSee_onlyReader_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.True(user.MayAllTaskforcesSee());
    }

    [Fact]
    public void MayAllTaskforcesSee_juniorAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).Build();
        Assert.False(user.MayAllTaskforcesSee());
    }

    // ---------- MayRealNameSee (leadership && !onlyReader) ----------

    [Fact]
    public void MayRealNameSee_leadershipByRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.True(user.MayRealNameSee());
    }

    [Fact]
    public void MayRealNameSee_adminTeamLead_returnsTrue()
    {
        // admin => leadership, and admin means not an only-reader.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().AsAdmin().Build();
        Assert.True(user.MayRealNameSee());
    }

    [Fact]
    public void MayRealNameSee_onlyReaderWithLeadershipRank_returnsFalse()
    {
        // team lead (no admin) is an only-reader even at leadership rank => never sees real names.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.False(user.MayRealNameSee());
    }

    [Fact]
    public void MayRealNameSee_juniorAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).Build();
        Assert.False(user.MayRealNameSee());
    }

    // ---------- MayCounterIntel (leadership && !onlyReader) ----------

    [Fact]
    public void MayCounterIntel_leadershipByRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.True(user.MayCounterIntel());
    }

    [Fact]
    public void MayCounterIntel_adminTeamLead_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().AsAdmin().Build();
        Assert.True(user.MayCounterIntel());
    }

    [Fact]
    public void MayCounterIntel_onlyReaderWithLeadershipRank_returnsFalse()
    {
        // this is the case Policies.Leadership would have let through while the service throws.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.False(user.MayCounterIntel());
    }

    [Fact]
    public void MayCounterIntel_juniorAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).Build();
        Assert.False(user.MayCounterIntel());
    }

    // ---------- MayHighestClassification (admin || rank >= SeniorSpecialAgent(3)) ----------

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, true)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void MayHighestClassification_byRank(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();
        Assert.Equal(expected, user.MayHighestClassification());
    }

    [Fact]
    public void MayHighestClassification_adminWithoutRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.MayHighestClassification());
    }

    [Fact]
    public void MayHighestClassification_noRank_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.MayHighestClassification());
    }

    // ---------- MayEvidenceWithdraw (admin || rank >= SeniorSpecialAgent(3)) ----------

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, true)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void MayEvidenceWithdraw_byRank(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();
        Assert.Equal(expected, user.MayEvidenceWithdraw());
    }

    [Fact]
    public void MayEvidenceWithdraw_adminWithoutRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.MayEvidenceWithdraw());
    }

    [Fact]
    public void MayEvidenceWithdraw_noRank_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.MayEvidenceWithdraw());
    }

    // ---------- MayMeetingRead (MayHighestClassification || onlyReader) ----------

    [Fact]
    public void MayMeetingRead_highestClassificationRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent).Build();
        Assert.True(user.MayMeetingRead());
    }

    [Fact]
    public void MayMeetingRead_onlyReader_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().Build();
        Assert.True(user.MayMeetingRead());
    }

    [Fact]
    public void MayMeetingRead_specialAgent_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).Build();
        Assert.False(user.MayMeetingRead());
    }

    // ---------- MayPromotionDecide (admin || rank >= DeputyDirector(5)) ----------

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SupervisorySpecialAgent, false)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void MayPromotionDecide_byRank(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();
        Assert.Equal(expected, user.MayPromotionDecide());
    }

    [Fact]
    public void MayPromotionDecide_adminWithoutRank_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();
        Assert.True(user.MayPromotionDecide());
    }

    [Fact]
    public void MayPromotionDecide_noRank_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.MayPromotionDecide());
    }

    // ---------- IsApplicant ----------

    [Fact]
    public void IsApplicant_statusApplicant_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Applicant).Build();
        Assert.True(user.IsApplicant());
    }

    [Fact]
    public void IsApplicant_statusActive_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();
        Assert.False(user.IsApplicant());
    }

    [Fact]
    public void IsApplicant_missingStatus_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Anonymous();
        Assert.False(user.IsApplicant());
    }

    // ---------- IsCitizen ----------

    [Fact]
    public void IsCitizen_statusCivilian_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian).Build();
        Assert.True(user.IsCitizen());
    }

    [Fact]
    public void IsCitizen_neverTrueForAgentApplicantOrAnonymous()
    {
        Assert.False(ClaimsPrincipalBuilder.Agent().Build().IsCitizen());
        Assert.False(ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Applicant).Build().IsCitizen());
        Assert.False(ClaimsPrincipalBuilder.Anonymous().IsCitizen());
    }

    [Fact]
    public void IsCitizen_andIsApplicant_areMutuallyExclusive()
    {
        ClaimsPrincipal citizen = ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian).Build();
        Assert.True(citizen.IsCitizen());
        Assert.False(citizen.IsApplicant());
    }

    [Fact]
    public void IsCitizen_grantsNoAgencyRights()
    {
        // the admin flag is a separate axis, but a citizen account never carries one; assert the plain shape
        ClaimsPrincipal citizen = ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian).Build();
        Assert.False(citizen.IsAdmin());
        Assert.False(citizen.IsLeadership());
        Assert.False(citizen.MayClassifiedRead());
        Assert.False(citizen.MayRealNameSee());
    }

    // ---------- MayUseCitizenPortal ----------

    [Theory]
    [InlineData(AgentStatus.Civilian)]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Applicant)]
    public void MayUseCitizenPortal_anySignedInAccount_returnsTrue(AgentStatus status)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithStatus(status).Build();
        Assert.True(user.MayUseCitizenPortal());
    }

    [Fact]
    public void MayUseCitizenPortal_partnerAndReadOnlySupervision_returnTrue()
    {
        // the area is readable for both; what separates them is MayCitizenSubmit below
        Assert.True(ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()
            .MayUseCitizenPortal());
        Assert.True(ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.Director).Build()
            .MayUseCitizenPortal());
    }

    [Fact]
    public void MayUseCitizenPortal_anonymous_returnsFalse()
        => Assert.False(ClaimsPrincipalBuilder.Anonymous().MayUseCitizenPortal());

    // ---------- MayCitizenSubmit (signed in, but no supervision and no demo) ----------

    [Theory]
    [InlineData(AgentStatus.Civilian)]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Applicant)]
    public void MayCitizenSubmit_anySignedInAccount_returnsTrue(AgentStatus status)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithStatus(status).Build();
        Assert.True(user.MayCitizenSubmit());
    }

    [Fact]
    public void MayCitizenSubmit_partner_returnsTrue()
    {
        // the one write an external account gets in the citizen area: its own identity and its own ticket
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent()
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();
        Assert.True(user.MayCitizenSubmit());
        Assert.False(user.MayWrite());
    }

    [Fact]
    public void MayCitizenSubmit_onlyReader_returnsTrue()
    {
        // the supervision writes nothing in the house, but the person behind it holds a civilian identity
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.Director).Build();
        Assert.True(user.MayCitizenSubmit());
        Assert.False(user.MayWrite());
    }

    [Fact]
    public void MayCitizenSubmit_demo_returnsFalse()
        => Assert.False(ClaimsPrincipalBuilder.Agent().AsDemo().Build().MayCitizenSubmit());

    [Fact]
    public void MayCitizenSubmit_anonymous_returnsFalse()
        => Assert.False(ClaimsPrincipalBuilder.Anonymous().MayCitizenSubmit());

    // ---------- IsHrbOrLeadership (IsHRB || IsLeadership) ----------

    [Fact]
    public void IsHrbOrLeadership_hrbWithoutLeadership_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsHrb().WithRank(Rank.JuniorAgent).Build();
        Assert.True(user.IsHrbOrLeadership());
    }

    [Fact]
    public void IsHrbOrLeadership_leadershipWithoutHrb_returnsTrue()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent).Build();
        Assert.True(user.IsHrbOrLeadership());
    }

    [Fact]
    public void IsHrbOrLeadership_neither_returnsFalse()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).Build();
        Assert.False(user.IsHrbOrLeadership());
    }
}
