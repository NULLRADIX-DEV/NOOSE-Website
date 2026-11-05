using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using Xunit;

namespace NOOSE_Website.Tests.Services;

public sealed class ViewerScopeTests
{
    // Builds a ViewerScope directly to isolate CanSee/derived-property behaviour from the From() factory.
    private static ViewerScope Scope(
        bool mayClassifiedRead = false,
        bool mayAllTaskforces = false,
        string? meId = null,
        PartnerAgency? partnerAgency = null,
        bool isTru = false,
        bool isHrb = false,
        bool isLeadership = false,
        bool mayAgenda = false)
        => new(mayClassifiedRead, mayAllTaskforces, meId, partnerAgency, isTru, isHrb, isLeadership, mayAgenda);

    // ---- CanSee: None arm ----

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void CanSee_None_AlwaysTrue(bool mayClassifiedRead, bool isTru, bool isHrb)
    {
        var scope = Scope(mayClassifiedRead: mayClassifiedRead, isTru: isTru, isHrb: isHrb);
        Assert.True(scope.CanSee(DocumentClassification.None));
    }

    // ---- CanSee: Leadership arm (== MayClassifiedRead only) ----

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]   // TRU/HRB flags do not unlock leadership-level docs
    [InlineData(true, true, true, true)]
    public void CanSee_Leadership_ReturnsMayClassifiedRead(bool mayClassifiedRead, bool isTru, bool isHrb, bool expected)
    {
        var scope = Scope(mayClassifiedRead: mayClassifiedRead, isTru: isTru, isHrb: isHrb);
        Assert.Equal(expected, scope.CanSee(DocumentClassification.Leadership));
    }

    // ---- CanSee: Tru arm (MayClassifiedRead || IsTru) ----

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]  // HRB flag does not unlock TRU docs
    [InlineData(true, true, false, true)]
    public void CanSee_Tru_ReturnsClassifiedOrTru(bool mayClassifiedRead, bool isTru, bool isHrb, bool expected)
    {
        var scope = Scope(mayClassifiedRead: mayClassifiedRead, isTru: isTru, isHrb: isHrb);
        Assert.Equal(expected, scope.CanSee(DocumentClassification.Tru));
    }

    // ---- CanSee: Hrb arm (MayClassifiedRead || IsHrb) ----

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]  // TRU flag does not unlock HRB docs
    [InlineData(true, false, true, true)]
    public void CanSee_Hrb_ReturnsClassifiedOrHrb(bool mayClassifiedRead, bool isTru, bool isHrb, bool expected)
    {
        var scope = Scope(mayClassifiedRead: mayClassifiedRead, isTru: isTru, isHrb: isHrb);
        Assert.Equal(expected, scope.CanSee(DocumentClassification.Hrb));
    }

    // ---- CanSee: default arm (unknown / undefined level) ----

    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-1)]
    public void CanSee_UnknownLevel_ReturnsFalse(int raw)
    {
        // Even a fully-privileged viewer cannot see an undefined classification value.
        var scope = Scope(mayClassifiedRead: true, isTru: true, isHrb: true, isLeadership: true);
        Assert.False(scope.CanSee((DocumentClassification)raw));
    }

    // ---- IsPartner derived property ----

    [Fact]
    public void IsPartner_NullAgency_False()
    {
        var scope = Scope(partnerAgency: null);
        Assert.False(scope.IsPartner);
    }

    [Theory]
    [InlineData(PartnerAgency.DoJ)]
    [InlineData(PartnerAgency.LSPD)]
    [InlineData(PartnerAgency.LSMD)]
    public void IsPartner_WithAgency_True(PartnerAgency agency)
    {
        var scope = Scope(partnerAgency: agency);
        Assert.True(scope.IsPartner);
    }

    // ---- MayRecruiting derived property (IsLeadership || IsHrb) ----

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void MayRecruiting_LeadershipOrHrb(bool isLeadership, bool isHrb, bool expected)
    {
        var scope = Scope(isLeadership: isLeadership, isHrb: isHrb);
        Assert.Equal(expected, scope.MayRecruiting);
    }

    // ---- From(principal) factory ----

    [Fact]
    public void From_Anonymous_AllDefaults()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Anonymous());

        Assert.False(scope.MayClassifiedRead);
        Assert.False(scope.MayAllTaskforces);
        Assert.Null(scope.MeId);
        Assert.Null(scope.PartnerAgency);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.MayAgenda);
        Assert.False(scope.IsPartner);
        Assert.False(scope.MayRecruiting);
    }

    [Fact]
    public void From_ActiveAgentWithoutRank_SetsOnlyMeId()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("agent-7").WithRank(Rank.Trainee));

        Assert.Equal("agent-7", scope.MeId);
        Assert.False(scope.MayClassifiedRead);
        Assert.False(scope.MayAllTaskforces);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.MayAgenda);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.Null(scope.PartnerAgency);
    }

    [Fact]
    public void From_CustomId_MapsMeId()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("agent-99"));
        Assert.Equal("agent-99", scope.MeId);
    }

    [Fact]
    public void From_LeadershipRank_SetsClassifiedTaskforcesLeadershipAndAgenda()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.True(scope.MayClassifiedRead);
        Assert.True(scope.MayAllTaskforces);
        Assert.True(scope.IsLeadership);
        Assert.True(scope.MayAgenda);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.False(scope.IsPartner);
    }

    [Fact]
    public void From_Admin_SetsLeadershipAndClassified()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().AsAdmin());

        Assert.True(scope.MayClassifiedRead);
        Assert.True(scope.MayAllTaskforces);
        Assert.True(scope.IsLeadership);
        Assert.True(scope.MayAgenda);
    }

    [Fact]
    public void From_OnlyReader_ReadsClassifiedButNotLeadership()
    {
        // TeamLead without admin = read-only supervision: reads everything, is not leadership.
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().AsTeamLead());

        Assert.True(scope.MayClassifiedRead);
        Assert.True(scope.MayAllTaskforces);
        Assert.False(scope.IsLeadership);
        Assert.True(scope.MayAgenda);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
    }

    [Fact]
    public void From_SeniorSpecialAgent_MayAgendaWithoutClassifiedOrLeadership()
    {
        // Senior rank meets highest-classification (>=3) so MayAgenda is set, but not leadership (>=4).
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent));

        Assert.True(scope.MayAgenda);
        Assert.False(scope.MayClassifiedRead);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.MayAllTaskforces);
    }

    [Fact]
    public void From_Tru_SetsIsTruAndSeesTruDocs()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTru());

        Assert.True(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.False(scope.MayClassifiedRead);
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.False(scope.CanSee(DocumentClassification.Hrb));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
    }

    [Fact]
    public void From_Hrb_SetsIsHrbAndRecruiting()
    {
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsHrb());

        Assert.True(scope.IsHrb);
        Assert.False(scope.IsTru);
        Assert.True(scope.MayRecruiting);
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
        Assert.False(scope.CanSee(DocumentClassification.Tru));
    }

    [Fact]
    public void From_Partner_SetsPartnerAgencyAndIsPartner()
    {
        var scope = ViewerScope.From(
            ClaimsPrincipalBuilder.Agent("partner-1").AsPartner(PartnerAgency.LSPD, PartnerRank.Member));

        Assert.Equal(PartnerAgency.LSPD, scope.PartnerAgency);
        Assert.True(scope.IsPartner);
        Assert.Equal("partner-1", scope.MeId);
        Assert.False(scope.MayClassifiedRead);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.MayAgenda);
    }
}
