using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

public sealed class UsageRulesTests
{
    private static ClaimsPrincipalBuilder Counted() => ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.SpecialAgent);

    [Fact]
    public void An_internal_agent_with_rank_and_write_access_is_counted()
        => Assert.True(UsageRules.IsCounted(Counted()));

    [Fact]
    public void An_admin_is_counted_like_anyone_else()
        => Assert.True(UsageRules.IsCounted(Counted().AsAdmin()));

    [Fact]
    public void Nobody_else_is_counted()
    {
        Assert.False(UsageRules.IsCounted(Counted().AsDemo()));
        Assert.False(UsageRules.IsCounted(Counted().AsTeamLead()));
        Assert.False(UsageRules.IsCounted(Counted().AsPartner(PartnerAgency.DoJ, PartnerRank.Member)));
        Assert.False(UsageRules.IsCounted(ClaimsPrincipalBuilder.Agent("a1")));
        Assert.False(UsageRules.IsCounted(ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.Trainee).WithStatus(AgentStatus.Civilian)));
        Assert.False(UsageRules.IsCounted(ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.Trainee).WithStatus(AgentStatus.Pending)));
        Assert.False(UsageRules.IsCounted(ClaimsPrincipalBuilder.Anonymous()));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "< 3")]
    [InlineData(2, "< 3")]
    [InlineData(3, "3")]
    [InlineData(17, "17")]
    public void Small_agent_numbers_are_hidden(int agentDays, string shown)
        => Assert.Equal(shown, UsageRules.AgentDays(agentDays));
}
