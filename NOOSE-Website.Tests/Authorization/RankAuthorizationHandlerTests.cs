using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Authorization;

public class RankAuthorizationHandlerTests
{
    private static async Task<bool> EvaluateAsync(RankRequirement requirement, ClaimsPrincipal principal)
    {
        var handler = new RankAuthorizationHandler();
        var context = new AuthorizationHandlerContext(new[] { requirement }, principal, resource: null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // --- Admin short-circuit: succeeds regardless of rank ---

    [Fact]
    public async Task HandleAsync_AdminWithNoRank_Succeeds()
    {
        var requirement = new RankRequirement(Rank.Director);
        var principal = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_AdminWithRankBelowMinimum_Succeeds()
    {
        var requirement = new RankRequirement(Rank.Director);
        var principal = ClaimsPrincipalBuilder.Agent().AsAdmin().WithRank(Rank.Trainee).Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SpecialAgentInCharge)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public async Task HandleAsync_AdminAgainstEveryMinimum_Succeeds(Rank minimum)
    {
        var requirement = new RankRequirement(minimum);
        var principal = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    // --- Rank >= Minimum: succeeds ---

    [Fact]
    public async Task HandleAsync_RankEqualsMinimum_Succeeds()
    {
        var requirement = new RankRequirement(Rank.SpecialAgent);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_RankOneAboveMinimum_Succeeds()
    {
        var requirement = new RankRequirement(Rank.SpecialAgent);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent).Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_TopRankAgainstLowestMinimum_Succeeds()
    {
        var requirement = new RankRequirement(Rank.Trainee);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).Build();

        Assert.True(await EvaluateAsync(requirement, principal));
    }

    // --- Rank < Minimum: fails ---

    [Fact]
    public async Task HandleAsync_RankOneBelowMinimum_Fails()
    {
        var requirement = new RankRequirement(Rank.SpecialAgent);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_LowestRankAgainstTopMinimum_Fails()
    {
        var requirement = new RankRequirement(Rank.Director);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Theory]
    // rank, minimum, expected success (rank >= minimum)
    [InlineData(Rank.Trainee, Rank.Trainee, true)]
    [InlineData(Rank.Trainee, Rank.SpecialAgent, false)]
    [InlineData(Rank.SpecialAgent, Rank.Trainee, true)]
    [InlineData(Rank.SeniorSpecialAgent, Rank.SpecialAgentInCharge, false)]
    [InlineData(Rank.SpecialAgentInCharge, Rank.SeniorSpecialAgent, true)]
    [InlineData(Rank.SpecialAgentInCharge, Rank.SpecialAgentInCharge, true)]
    [InlineData(Rank.DeputyDirector, Rank.Director, false)]
    [InlineData(Rank.Director, Rank.Director, true)]
    public async Task HandleAsync_RankLadder_MatchesThreshold(Rank rank, Rank minimum, bool expected)
    {
        var requirement = new RankRequirement(minimum);
        var principal = ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();

        Assert.Equal(expected, await EvaluateAsync(requirement, principal));
    }

    // --- No / malformed rank claim, non-admin: fails ---

    [Fact]
    public async Task HandleAsync_NoRankClaimNonAdmin_Fails()
    {
        var requirement = new RankRequirement(Rank.Trainee);
        var principal = ClaimsPrincipalBuilder.Agent().Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_AnonymousPrincipal_Fails()
    {
        var requirement = new RankRequirement(Rank.Trainee);
        var principal = ClaimsPrincipalBuilder.Anonymous();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_MalformedRankClaimNonAdmin_Fails()
    {
        var requirement = new RankRequirement(Rank.Trainee);
        var principal = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Rank, "not-a-number").Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_OutOfRangeRankClaimNonAdmin_Fails()
    {
        // 99 is not a defined Rank -> GetRank returns null -> no success
        var requirement = new RankRequirement(Rank.Trainee);
        var principal = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.Rank, "99").Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }

    [Fact]
    public async Task HandleAsync_AdminFlagFalseValueTreatedAsNonAdmin_UsesRank()
    {
        // Non-"true" admin value is not admin; falls through to rank check which fails without a rank.
        var requirement = new RankRequirement(Rank.SpecialAgent);
        var principal = ClaimsPrincipalBuilder.Agent().Raw(AgentClaimTypes.IsAdmin, "false").Build();

        Assert.False(await EvaluateAsync(requirement, principal));
    }
}
