using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Who holds a service ID card and who may print it.</summary>
public class ServiceIdRulesTests
{
    private static Agent Holder(string id = "holder", Action<Agent>? configure = null)
        => Seed.Agent(id, Rank.SpecialAgent, configure: a =>
        {
            a.BadgeNumber = "IV";
            configure?.Invoke(a);
        });

    // ---- who may print ----

    [Fact]
    public void An_agent_prints_their_own_card()
    {
        var self = ClaimsPrincipalBuilder.Agent("holder").WithRank(Rank.SpecialAgent).Build();

        Assert.Equal(ServiceIdAccess.Allowed, ServiceIdRules.Check(self, Holder("holder")));
    }

    [Fact]
    public void An_agent_without_a_gate_cannot_print_somebody_elses_card()
    {
        var other = ClaimsPrincipalBuilder.Agent("other").WithRank(Rank.SeniorSpecialAgent).Build();

        Assert.Equal(ServiceIdAccess.Forbidden, ServiceIdRules.Check(other, Holder("holder")));
    }

    [Fact]
    public void Leadership_prints_any_card()
    {
        var lead = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SpecialAgentInCharge).Build();

        Assert.Equal(ServiceIdAccess.Allowed, ServiceIdRules.Check(lead, Holder("holder")));
    }

    [Fact]
    public void The_rank_just_below_leadership_does_not_print_for_others()
    {
        var senior = ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build();

        Assert.False(ServiceIdRules.MayPrint(senior, "holder"));
    }

    [Fact]
    public void The_administration_division_prints_any_card_without_a_leadership_rank()
    {
        var hrb = ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build();

        Assert.True(ServiceIdRules.MayPrint(hrb, "holder"));
    }

    [Fact]
    public void An_admin_prints_any_card()
    {
        var admin = ClaimsPrincipalBuilder.Agent("root").WithRank(Rank.Trainee).AsAdmin().Build();

        Assert.True(ServiceIdRules.MayPrint(admin, "holder"));
    }

    [Fact]
    public void Read_only_supervision_never_prints_not_even_with_the_admin_flag()
    {
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().AsAdmin().Build();

        Assert.False(ServiceIdRules.MayPrint(supervision, "holder"));
        Assert.False(ServiceIdRules.MayPrint(supervision, "tl"));
    }

    [Fact]
    public void A_partner_never_prints()
    {
        var partner = ClaimsPrincipalBuilder.Agent("p").WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

        Assert.False(ServiceIdRules.MayPrint(partner, "holder"));
        Assert.False(ServiceIdRules.MayPrint(partner, "p"));
    }

    [Fact]
    public void The_demo_visitor_never_prints()
    {
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build();

        Assert.False(ServiceIdRules.MayPrint(demo, "holder"));
    }

    [Fact]
    public void A_signed_out_visitor_never_prints()
        => Assert.False(ServiceIdRules.MayPrint(ClaimsPrincipalBuilder.Anonymous(), "holder"));

    [Fact]
    public void A_missing_agent_is_not_found_for_everyone()
    {
        var lead = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

        Assert.Equal(ServiceIdAccess.NotFound, ServiceIdRules.Check(lead, null));
    }

    [Fact]
    public void Leadership_gets_no_card_for_an_agent_who_holds_none()
    {
        var lead = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

        Assert.Equal(ServiceIdAccess.NotIssuable,
            ServiceIdRules.Check(lead, Holder("gone", a => a.Status = AgentStatus.Terminated)));
    }

    // ---- who holds one ----

    [Fact]
    public void An_active_agent_with_a_roman_numeral_holds_a_card()
        => Assert.True(ServiceIdRules.IsHolder(Holder()));

    [Fact]
    public void The_number_is_the_canonical_numeral()
    {
        Assert.Equal("XL", ServiceIdRules.Number(Holder(configure: a => a.BadgeNumber = " xl ")));
        Assert.True(ServiceIdRules.IsHolder(Holder(configure: a => a.BadgeNumber = "iv")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A-12")]
    [InlineData("XLI")]
    [InlineData("0")]
    public void No_valid_badge_number_means_no_card(string? number)
        => Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.BadgeNumber = number)));

    [Theory]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Civilian)]
    public void Only_an_active_account_holds_a_card(AgentStatus status)
        => Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.Status = status)));

    [Fact]
    public void Supervision_partners_and_nameless_or_rankless_accounts_hold_none()
    {
        Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.IsTeamLead = true)));
        Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.PartnerAgency = PartnerAgency.LSPD)));
        Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.Codename = " ")));
        Assert.False(ServiceIdRules.IsHolder(Holder(configure: a => a.Rank = null)));
    }
}
