using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
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

    private static UsageRow Row(long events, int agentDays, int peakAgents)
        => new("search:run", "Suche", "Suche", events, agentDays, peakAgents);

    [Fact]
    public void A_feature_nobody_used_shows_its_zeros()
    {
        var row = Row(0, 0, 0);

        Assert.True(UsageRules.IsShown(row));
        Assert.Equal(("0", "0"), (UsageRules.Events(row), UsageRules.AgentDays(row)));
    }

    [Fact]
    public void One_agent_on_many_days_stays_hidden_however_many_agent_days_add_up()
    {
        // 30 agent-days, 80 events, but never more than one agent a day: may be one person
        var row = Row(80, 30, 1);

        Assert.False(UsageRules.IsShown(row));
        Assert.Equal((UsageRules.Hidden, UsageRules.Hidden), (UsageRules.Events(row), UsageRules.AgentDays(row)));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, true)]
    public void Numbers_show_once_one_day_had_enough_different_agents(int peakAgents, bool shown)
        => Assert.Equal(shown, UsageRules.IsShown(Row(12, 9, peakAgents)));
}
