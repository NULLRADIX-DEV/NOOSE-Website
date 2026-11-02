using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Authorization;

/// <summary>The coarse promotion gate: policy, write twin and guard open at Section Chief and agree with each other.</summary>
public class PromotionPolicyTests
{
    private static async Task<bool> AllowsAsync(string policy, ClaimsPrincipal user)
    {
        await using var provider = new ServiceCollection().AddLogging().AddNooseAuthorization().BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        return result.Succeeded;
    }

    private static bool GuardAllows(ClaimsPrincipal user)
        => Record.Exception(() => Permission.RequirePromotionDecide(user)) is null;

    [Theory]
    [InlineData(Rank.Trainee, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SpecialAgentInCharge, false)]
    [InlineData(Rank.SectionChief, true)]
    [InlineData(Rank.AssistantDirector, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public async Task PromotionDecide_OpensAtSectionChief_ForPolicyWriteTwinAndGuardAlike(Rank rank, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();

        Assert.Equal(expected, await AllowsAsync(Policies.PromotionDecide, user));
        Assert.Equal(expected, await AllowsAsync(Policies.PromotionDecideWrite, user));
        Assert.Equal(expected, GuardAllows(user));
    }

    [Fact]
    public async Task PromotionDecide_AdminWithoutRankPasses()
    {
        ClaimsPrincipal admin = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();

        Assert.True(await AllowsAsync(Policies.PromotionDecide, admin));
        Assert.True(await AllowsAsync(Policies.PromotionDecideWrite, admin));
        Assert.True(GuardAllows(admin));
    }

    /// <summary>Both carry a Board rank; only the write twin and the guard keep them off a button that would fail after the click.</summary>
    [Fact]
    public async Task PromotionDecideWrite_ReadOnlySupervisionAndTheDemoVisitorFail()
    {
        foreach (var user in new[]
        {
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build(),
        })
        {
            Assert.False(await AllowsAsync(Policies.PromotionDecideWrite, user));
            Assert.False(GuardAllows(user));
        }
    }

    [Fact]
    public async Task PromotionDecide_AnonymousFails()
    {
        Assert.False(await AllowsAsync(Policies.PromotionDecide, ClaimsPrincipalBuilder.Anonymous()));
        Assert.False(await AllowsAsync(Policies.PromotionDecideWrite, ClaimsPrincipalBuilder.Anonymous()));
    }
}
