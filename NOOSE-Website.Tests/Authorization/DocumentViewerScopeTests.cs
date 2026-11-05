using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;
using Xunit;

namespace NOOSE_Website.Tests.Authorization;

public class DocumentViewerScopeTests
{
    // ---- CanSee: direct struct construction, every switch arm ----

    [Theory]
    // None => always visible, independent of any field
    [InlineData(false, false, false, DocumentClassification.None, true)]
    [InlineData(true, true, true, DocumentClassification.None, true)]
    // Leadership => MayClassified only (Tru/Hrb flags never help)
    [InlineData(false, false, false, DocumentClassification.Leadership, false)]
    [InlineData(false, true, true, DocumentClassification.Leadership, false)]
    [InlineData(true, false, false, DocumentClassification.Leadership, true)]
    [InlineData(true, true, true, DocumentClassification.Leadership, true)]
    // Tru => MayClassified || IsTru
    [InlineData(false, false, false, DocumentClassification.Tru, false)]
    [InlineData(false, true, false, DocumentClassification.Tru, true)]
    [InlineData(true, false, false, DocumentClassification.Tru, true)]
    [InlineData(false, false, true, DocumentClassification.Tru, false)] // Hrb flag does not open Tru
    // Hrb => MayClassified || IsHrb
    [InlineData(false, false, false, DocumentClassification.Hrb, false)]
    [InlineData(false, false, true, DocumentClassification.Hrb, true)]
    [InlineData(true, false, false, DocumentClassification.Hrb, true)]
    [InlineData(false, true, false, DocumentClassification.Hrb, false)] // Tru flag does not open Hrb
    public void CanSee_directScope_matchesSwitchArm(
        bool mayClassified, bool isTru, bool isHrb, DocumentClassification classification, bool expected)
    {
        var scope = new DocumentViewerScope(
            MayClassified: mayClassified,
            IsTru: isTru,
            IsHrb: isHrb,
            IsLeadership: false,
            IsAdmin: false,
            MeId: null);

        Assert.Equal(expected, scope.CanSee(classification));
    }

    [Fact]
    public void CanSee_undefinedClassification_fallsToDefaultFalse_evenWithFullScope()
    {
        var scope = new DocumentViewerScope(
            MayClassified: true,
            IsTru: true,
            IsHrb: true,
            IsLeadership: true,
            IsAdmin: true,
            MeId: "agent-1");

        Assert.False(scope.CanSee((DocumentClassification)99));
    }

    [Fact]
    public void CanSee_leadershipAndAdminFields_doNotAffectVisibility()
    {
        // IsLeadership/IsAdmin are carried on the struct but unused by CanSee.
        var scope = new DocumentViewerScope(
            MayClassified: false,
            IsTru: false,
            IsHrb: false,
            IsLeadership: true,
            IsAdmin: true,
            MeId: "agent-1");

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
        Assert.False(scope.CanSee(DocumentClassification.Tru));
        Assert.False(scope.CanSee(DocumentClassification.Hrb));
    }

    // ---- CanSee: scope derived via From(principal) ----

    [Fact]
    public void CanSee_plainAgent_seesOnlyNone()
    {
        var scope = DocumentViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee));

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
        Assert.False(scope.CanSee(DocumentClassification.Tru));
        Assert.False(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_leadershipByRank_seesEveryDefinedLevel()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.True(scope.CanSee(DocumentClassification.Leadership));
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_admin_seesEveryDefinedLevel()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.True(scope.CanSee(DocumentClassification.Leadership));
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_onlyReader_readsAllClassifiedViaMayClassifiedRead()
    {
        // TeamLead without admin => OnlyReader => MayClassifiedRead true.
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTeamLead());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.True(scope.CanSee(DocumentClassification.Leadership));
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_truOnlyAgent_seesNoneAndTruOnly()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTru());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.False(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_hrbOnlyAgent_seesNoneAndHrbOnly()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsHrb());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
        Assert.False(scope.CanSee(DocumentClassification.Tru));
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_teamLeadWithAdmin_seesAllViaLeadership()
    {
        // Admin cancels OnlyReader but still grants leadership => MayClassified.
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTeamLead().AsAdmin());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.True(scope.CanSee(DocumentClassification.Leadership));
        Assert.True(scope.CanSee(DocumentClassification.Tru));
        Assert.True(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_anonymous_seesOnlyNone()
    {
        var scope = DocumentViewerScope.From(ClaimsPrincipalBuilder.Anonymous());

        Assert.True(scope.CanSee(DocumentClassification.None));
        Assert.False(scope.CanSee(DocumentClassification.Leadership));
        Assert.False(scope.CanSee(DocumentClassification.Tru));
        Assert.False(scope.CanSee(DocumentClassification.Hrb));
    }

    [Fact]
    public void CanSee_rankJustBelowLeadership_cannotSeeLeadership()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent));

        Assert.False(scope.CanSee(DocumentClassification.Leadership));
    }

    [Fact]
    public void CanSee_rankExactlyLeadershipThreshold_canSeeLeadership()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.True(scope.CanSee(DocumentClassification.Leadership));
    }

    // ---- From: field mapping (order + derivation) ----

    [Fact]
    public void From_admin_mapsMayClassifiedLeadershipAndAdmin()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin());

        Assert.True(scope.MayClassified);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.True(scope.IsLeadership);
        Assert.True(scope.IsAdmin);
    }

    [Fact]
    public void From_leadershipByRank_isLeadershipButNotAdmin()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.True(scope.MayClassified);
        Assert.True(scope.IsLeadership);
        Assert.False(scope.IsAdmin);
    }

    [Fact]
    public void From_truOnly_mapsTruFlagWithoutMayClassified()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTru());

        Assert.False(scope.MayClassified);
        Assert.True(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.IsAdmin);
    }

    [Fact]
    public void From_hrbOnly_mapsHrbFlagWithoutMayClassified()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsHrb());

        Assert.False(scope.MayClassified);
        Assert.False(scope.IsTru);
        Assert.True(scope.IsHrb);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.IsAdmin);
    }

    [Fact]
    public void From_onlyReader_hasMayClassifiedButNotLeadershipNorAdmin()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTeamLead());

        Assert.True(scope.MayClassified); // via read-only supervision
        Assert.False(scope.IsLeadership);
        Assert.False(scope.IsAdmin);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
    }

    [Fact]
    public void From_plainAgent_allDerivedFlagsFalse()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee));

        Assert.False(scope.MayClassified);
        Assert.False(scope.IsTru);
        Assert.False(scope.IsHrb);
        Assert.False(scope.IsLeadership);
        Assert.False(scope.IsAdmin);
    }

    [Fact]
    public void From_malformedRankClaim_isNotLeadership()
    {
        var scope = DocumentViewerScope.From(
            ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Rank, "not-a-number"));

        Assert.False(scope.IsLeadership);
        Assert.False(scope.MayClassified);
    }

    [Fact]
    public void From_capturesAgentIdAsMeId()
    {
        var scope = DocumentViewerScope.From(ClaimsPrincipalBuilder.Agent("agent-42"));

        Assert.Equal("agent-42", scope.MeId);
    }

    [Fact]
    public void From_anonymous_meIdIsNull()
    {
        var scope = DocumentViewerScope.From(ClaimsPrincipalBuilder.Anonymous());

        Assert.Null(scope.MeId);
    }

    // ---- AssignableOptions ----

    [Fact]
    public void AssignableOptions_plainAgent_onlyNone()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee));

        Assert.Equal(new[] { DocumentClassification.None }, options);
    }

    [Fact]
    public void AssignableOptions_leadership_allLevelsInOrder()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.Equal(
            new[]
            {
                DocumentClassification.None,
                DocumentClassification.Leadership,
                DocumentClassification.Tru,
                DocumentClassification.Hrb,
            },
            options);
    }

    [Fact]
    public void AssignableOptions_admin_allLevelsInOrder()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin());

        Assert.Equal(
            new[]
            {
                DocumentClassification.None,
                DocumentClassification.Leadership,
                DocumentClassification.Tru,
                DocumentClassification.Hrb,
            },
            options);
    }

    [Fact]
    public void AssignableOptions_truOnly_noneAndTru()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTru());

        Assert.Equal(new[] { DocumentClassification.None, DocumentClassification.Tru }, options);
    }

    [Fact]
    public void AssignableOptions_hrbOnly_noneAndHrb()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsHrb());

        Assert.Equal(new[] { DocumentClassification.None, DocumentClassification.Hrb }, options);
    }

    [Fact]
    public void AssignableOptions_truAndHrb_noneTruHrbNoLeadership()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTru().AsHrb());

        Assert.Equal(
            new[]
            {
                DocumentClassification.None,
                DocumentClassification.Tru,
                DocumentClassification.Hrb,
            },
            options);
    }

    [Fact]
    public void AssignableOptions_leadershipWithTruFlag_noDuplicateTru()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).AsTru());

        Assert.Equal(
            new[]
            {
                DocumentClassification.None,
                DocumentClassification.Leadership,
                DocumentClassification.Tru,
                DocumentClassification.Hrb,
            },
            options);
        Assert.Single(options, o => o == DocumentClassification.Tru);
    }

    [Fact]
    public void AssignableOptions_onlyReader_onlyNone()
    {
        // Read-only supervision may read classified but may assign only None.
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsTeamLead());

        Assert.Equal(new[] { DocumentClassification.None }, options);
    }

    [Fact]
    public void AssignableOptions_anonymous_onlyNone()
    {
        var options = DocumentViewerScope.AssignableOptions(ClaimsPrincipalBuilder.Anonymous());

        Assert.Equal(new[] { DocumentClassification.None }, options);
    }

    [Fact]
    public void AssignableOptions_alwaysStartsWithNone()
    {
        var options = DocumentViewerScope.AssignableOptions(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief));

        Assert.NotEmpty(options);
        Assert.Equal(DocumentClassification.None, options[0]);
    }
}
