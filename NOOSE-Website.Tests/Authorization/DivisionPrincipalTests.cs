using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Authorization;

/// <summary>The division claim, the Board by rank and the right to move agents between divisions.</summary>
public class DivisionPrincipalTests
{
    [Theory]
    [InlineData(Division.Cid)]
    [InlineData(Division.Ctd)]
    [InlineData(Division.Hrt)]
    public void GetDivision_ReadsTheClaim(Division division)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsDivision(division).Build();

        Assert.Equal(division, user.GetDivision());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("4")]
    public void GetDivision_MalformedOrUndefinedIsNull(string raw)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Division, raw).Build();

        Assert.Null(user.GetDivision());
    }

    [Fact]
    public void GetDivision_MissingClaimIsNull()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().Build();

        Assert.Null(user.GetDivision());
    }

    [Theory]
    [InlineData(Rank.SpecialAgentInCharge, false)]
    [InlineData(Rank.SectionChief, false)]
    [InlineData(Rank.AssistantDirector, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void IsBoard_FollowsTheRankAlone(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();

        Assert.Equal(expected, user.IsBoard());
    }

    [Fact]
    public void IsBoard_AdminFlagIsNoBoardSeat()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).AsAdmin().Build();

        Assert.False(user.IsBoard());
    }

    [Fact]
    public void IsBoard_WithoutRankIsFalse()
        => Assert.False(ClaimsPrincipalBuilder.Agent().Build().IsBoard());

    [Fact]
    public void MayDivisionSet_BoardAndAdminMaySpecialAgentInChargeMayNot()
    {
        Assert.True(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).Build().MayDivisionSet());
        Assert.True(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).AsAdmin().Build().MayDivisionSet());
        Assert.False(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build().MayDivisionSet());
        Assert.False(ClaimsPrincipalBuilder.Anonymous().MayDivisionSet());
    }

    /// <summary>A Section Chief hands out ranks in the own division but does not move agents between divisions: that is the Board's.</summary>
    [Fact]
    public void MayDivisionSet_StartsAtAssistantDirectorNotAtSectionChief()
    {
        Assert.False(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).Build().MayDivisionSet());
        Assert.True(ClaimsPrincipalBuilder.Agent().WithRank(Rank.AssistantDirector).Build().MayDivisionSet());
    }

    [Fact]
    public void IsTru_KeepsReadingItsOwnClaim()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsDivision(Division.Hrt).AsTru().Build();

        Assert.True(user.IsTRU());
        Assert.False(ClaimsPrincipalBuilder.Agent().AsDivision(Division.Hrt).Build().IsTRU());
    }

    // ---- the policy twin of Permission.RequireDivisionSet ----

    private static async Task<bool> PolicyAllowsAsync(ClaimsPrincipal user)
    {
        await using var provider = new ServiceCollection().AddLogging().AddNooseAuthorization().BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, Policies.DivisionSetWrite);
        return result.Succeeded;
    }

    [Fact]
    public async Task Policy_BoardAndAdminPass()
    {
        Assert.True(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).Build()));
        Assert.True(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).AsAdmin().Build()));
    }

    [Fact]
    public async Task Policy_SpecialAgentInChargeFails()
        => Assert.False(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build()));

    /// <summary>Both carry Director; a rank-only policy would show them the picker and refuse after the dialog.</summary>
    [Fact]
    public async Task Policy_ReadOnlySupervisionAndTheDemoVisitorFail()
    {
        Assert.False(await PolicyAllowsAsync(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().Build()));
        Assert.False(await PolicyAllowsAsync(DemoIdentity.BuildPrincipal()));
    }

    [Fact]
    public async Task Policy_AndGuardAgree()
    {
        var principals = new[]
        {
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).Build(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).AsAdmin().Build(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build(),
        };

        foreach (var user in principals)
        {
            var guardPasses = true;
            try { Permission.RequireDivisionSet(user); }
            catch (UnauthorizedAccessException) { guardPasses = false; }

            Assert.Equal(guardPasses, await PolicyAllowsAsync(user));
        }
    }

    // ---- demo ----

    [Fact]
    public void DemoPrincipal_IsABoardMemberWithAHomeAndNoTru()
    {
        var demo = DemoIdentity.BuildPrincipal();

        Assert.True(demo.IsBoard());
        Assert.Equal(Division.Cid, demo.GetDivision());
        Assert.False(demo.IsTRU());
    }
}
