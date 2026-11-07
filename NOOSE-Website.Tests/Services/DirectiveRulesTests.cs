using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>Numbering, validity and permissions of service directives; pure rules, no database.</summary>
public sealed class DirectiveRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static ClaimsPrincipal Agent(Rank rank) => ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();

    [Fact]
    public void Label_pads_to_two_digits()
    {
        Assert.Equal("DA-07", DirectiveRules.Label(7));
        Assert.Equal("DA-12", DirectiveRules.Label(12));
        Assert.Equal("DA-123", DirectiveRules.Label(123));
    }

    [Theory]
    [InlineData("DA-07", 7)]
    [InlineData("da 7", 7)]
    [InlineData("DA07", 7)]
    [InlineData("da-7", 7)]
    [InlineData("  DA-123  ", 123)]
    public void TryParseNumber_reads_the_spellings_agents_type(string text, int expected)
    {
        Assert.True(DirectiveRules.TryParseNumber(text, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("DA-")]
    [InlineData("DA-0")]
    [InlineData("DA-00")]
    [InlineData("XY-7")]
    [InlineData("DA-7x")]
    [InlineData("DA-12345678901")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParseNumber_refuses_what_is_no_directive_number(string? text)
        => Assert.False(DirectiveRules.TryParseNumber(text, out _));

    [Fact]
    public void ValidVersion_is_the_newest_that_has_started()
    {
        Assert.Equal(1, DirectiveRules.ValidVersion([(1, Today.AddDays(-10)), (2, Today.AddDays(5))], Today));
        Assert.Null(DirectiveRules.ValidVersion([(1, Today.AddDays(1))], Today));
        Assert.Equal(2, DirectiveRules.ValidVersion([(1, Today.AddDays(-3)), (2, Today)], Today));
        // order is irrelevant
        Assert.Equal(2, DirectiveRules.ValidVersion([(2, Today.AddDays(-3)), (1, Today.AddDays(-10))], Today));
        Assert.Null(DirectiveRules.ValidVersion([], Today));
    }

    [Fact]
    public void IsValidFromAllowed_refuses_a_date_before_the_previous_version()
    {
        Assert.True(DirectiveRules.IsValidFromAllowed(Today, null));
        Assert.True(DirectiveRules.IsValidFromAllowed(Today, Today));
        Assert.True(DirectiveRules.IsValidFromAllowed(Today.AddDays(1), Today));
        Assert.False(DirectiveRules.IsValidFromAllowed(Today.AddDays(-1), Today));
    }

    [Fact]
    public void NextAcknowledgeFrom_moves_only_when_the_version_asks()
    {
        Assert.Equal(3, DirectiveRules.NextAcknowledgeFrom(currentAcknowledgeFrom: 2, newVersion: 3, requiresAcknowledgment: true));
        Assert.Equal(2, DirectiveRules.NextAcknowledgeFrom(currentAcknowledgeFrom: 2, newVersion: 3, requiresAcknowledgment: false));
    }

    [Fact]
    public void MayManage_is_leadership_with_write_access()
    {
        Assert.True(DirectiveRules.MayManage(Agent(Rank.SectionChief)));
        Assert.True(DirectiveRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin().Build()));

        Assert.False(DirectiveRules.MayManage(Agent(Rank.SpecialAgentInCharge)));
        Assert.False(DirectiveRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().Build()));
        // team lead plus admin
        Assert.False(DirectiveRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().AsAdmin().Build()));
        Assert.False(DirectiveRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
        Assert.False(DirectiveRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
    }

    [Fact]
    public void MaySeeQuota_is_leadership_and_supervision()
    {
        Assert.True(DirectiveRules.MaySeeQuota(Agent(Rank.SectionChief)));
        Assert.True(DirectiveRules.MaySeeQuota(ClaimsPrincipalBuilder.Agent().AsTeamLead().Build()));

        Assert.False(DirectiveRules.MaySeeQuota(Agent(Rank.Trainee)));
        Assert.False(DirectiveRules.MaySeeQuota(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
    }

    [Fact]
    public void MayAcknowledge_is_every_internal_writer()
    {
        Assert.True(DirectiveRules.MayAcknowledge(Agent(Rank.Trainee)));

        Assert.False(DirectiveRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().AsTeamLead().Build()));
        Assert.False(DirectiveRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
        Assert.False(DirectiveRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
    }

    [Fact]
    public void RequireDirectiveWrite_puts_the_write_guard_ahead_of_the_rank_guard()
    {
        Assert.Null(Record.Exception(() => Permission.RequireDirectiveWrite(Agent(Rank.SectionChief))));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireDirectiveWrite(Agent(Rank.SpecialAgentInCharge)));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireDirectiveWrite(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireDirectiveWrite(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().AsAdmin().Build()));
    }
}
