using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Decorations;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>Who may hand out which rank to whom: the fine gate behind every rank write, and the predicate the buttons mirror.</summary>
public class PromotionRulesTests
{
    private static PromotionStanding Standing(Rank? rank, Division? home = null, bool admin = false)
        => new(admin, rank, home);

    private static readonly Division?[] AllHomes = [Division.Cid, Division.Ctd, Division.Hrt, null];

    private static readonly Rank?[] AllFrom = [null, .. RankDisplay.All.Select(r => (Rank?)r)];

    // ---- the thresholds ----

    [Fact]
    public void The_coarse_gate_opens_at_section_chief_and_the_board_starts_one_level_up()
    {
        Assert.Equal(Rank.SectionChief, PromotionRules.DeciderFloor);
        Assert.Equal(Rank.AssistantDirector, DivisionRules.BoardFloor);
        Assert.Equal(DivisionRules.BoardFloor, DecorationRules.SignatoryFloor);
        // leadership stays where it was; a Special Agent in Charge leads but does not hand out ranks
        Assert.Equal(Rank.SpecialAgentInCharge, LeadershipChart.RankFloor);
    }

    [Theory]
    [InlineData(Rank.Trainee, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SpecialAgentInCharge, false)]
    [InlineData(Rank.SectionChief, true)]
    [InlineData(Rank.AssistantDirector, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void The_standing_passes_the_coarse_gate_like_the_claims_do(Rank rank, bool expected)
    {
        Assert.Equal(expected, Standing(rank).MayDecideAny);
        Assert.Equal(expected, ClaimsPrincipalBuilder.Agent().WithRank(rank).Build().MayPromotionDecide());
    }

    [Fact]
    public void An_admin_and_nobody_else_without_a_rank_passes_the_coarse_gate()
    {
        Assert.True(Standing(null, admin: true).MayDecideAny);
        Assert.False(Standing(null).MayDecideAny);
    }

    // ---- Board and admin: everything ----

    [Theory]
    [InlineData(Rank.AssistantDirector)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public void The_board_assigns_every_rank_in_every_division(Rank actorRank)
    {
        var actor = Standing(actorRank, Division.Cid);

        foreach (var home in AllHomes)
        {
            foreach (var from in AllFrom)
            {
                foreach (var to in RankDisplay.All)
                {
                    Assert.True(PromotionRules.MayAssign(actor, home, from, to), $"{home} {from} -> {to}");
                    Assert.Null(PromotionRules.Refusal(actor, home, from, to));
                }
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SectionChief)]
    public void An_admin_assigns_every_rank_whatever_their_own_rank(Rank? actorRank)
    {
        var actor = Standing(actorRank, null, admin: true);

        Assert.True(PromotionRules.MayAssign(actor, Division.Hrt, null, Rank.Director));
        Assert.True(PromotionRules.MayAssign(actor, Division.Ctd, Rank.Director, Rank.Trainee));
        Assert.True(actor.SeesAll);
    }

    // ---- Section Chief: the own division, below Section Chief ----

    [Theory]
    // an agent of the own division, moved anywhere below Section Chief
    [InlineData(Division.Cid, Rank.SpecialAgent, Rank.SeniorSpecialAgent, true)]
    [InlineData(Division.Cid, Rank.Trainee, Rank.SpecialAgent, true)]
    [InlineData(Division.Cid, Rank.SeniorSpecialAgent, Rank.SpecialAgentInCharge, true)]
    [InlineData(Division.Cid, Rank.SpecialAgentInCharge, Rank.SeniorSpecialAgent, true)]
    [InlineData(Division.Cid, Rank.SpecialAgent, Rank.Trainee, true)]
    // never up to Section Chief or beyond
    [InlineData(Division.Cid, Rank.SpecialAgentInCharge, Rank.SectionChief, false)]
    [InlineData(Division.Cid, Rank.SpecialAgent, Rank.AssistantDirector, false)]
    [InlineData(Division.Cid, Rank.SpecialAgent, Rank.Director, false)]
    // never a peer or a Board member, even down
    [InlineData(Division.Cid, Rank.SectionChief, Rank.SpecialAgentInCharge, false)]
    [InlineData(Division.Cid, Rank.AssistantDirector, Rank.SpecialAgent, false)]
    [InlineData(Division.Cid, Rank.Director, Rank.Trainee, false)]
    // a foreign or unknown division
    [InlineData(Division.Ctd, Rank.SpecialAgent, Rank.SeniorSpecialAgent, false)]
    [InlineData(Division.Hrt, Rank.SpecialAgent, Rank.SeniorSpecialAgent, false)]
    [InlineData(null, Rank.SpecialAgent, Rank.SeniorSpecialAgent, false)]
    // a first release: into the own division only, below Section Chief
    [InlineData(Division.Cid, null, Rank.Trainee, true)]
    [InlineData(Division.Cid, null, Rank.SpecialAgentInCharge, true)]
    [InlineData(Division.Cid, null, Rank.SectionChief, false)]
    [InlineData(Division.Ctd, null, Rank.Trainee, false)]
    [InlineData(Division.Hrt, null, Rank.SpecialAgent, false)]
    public void A_section_chief_of_the_cid_assigns_only_inside_the_cid_and_below_section_chief(
        Division? target, Rank? from, Rank to, bool expected)
    {
        var actor = Standing(Rank.SectionChief, Division.Cid);

        Assert.Equal(expected, PromotionRules.MayAssign(actor, target, from, to));
        Assert.Equal(expected, PromotionRules.Refusal(actor, target, from, to) is null);
    }

    [Theory]
    [InlineData(Division.Hrt, true)]
    [InlineData(Division.Cid, false)]
    [InlineData(Division.Ctd, false)]
    public void A_section_chief_of_the_hrt_assigns_only_inside_the_hrt(Division target, bool expected)
    {
        var actor = Standing(Rank.SectionChief, Division.Hrt);

        Assert.Equal(expected, PromotionRules.MayAssign(actor, target, Rank.SpecialAgent, Rank.SeniorSpecialAgent));
        Assert.Equal(expected, PromotionRules.MayAssign(actor, target, null, Rank.SpecialAgent));
    }

    [Fact]
    public void A_section_chief_without_a_known_home_assigns_nothing()
    {
        var actor = Standing(Rank.SectionChief, null);

        foreach (var home in AllHomes)
        {
            Assert.False(PromotionRules.MayAssign(actor, home, Rank.SpecialAgent, Rank.SeniorSpecialAgent));
            Assert.False(PromotionRules.MayAssign(actor, home, null, Rank.Trainee));
        }
    }

    // ---- Special Agent in Charge: one first release, nothing else ----

    [Theory]
    [InlineData(Division.Cid, null, Rank.Trainee, true)]
    [InlineData(Division.Cid, null, Rank.SpecialAgent, false)]
    [InlineData(Division.Cid, null, Rank.SpecialAgentInCharge, false)]
    [InlineData(Division.Ctd, null, Rank.Trainee, false)]
    [InlineData(Division.Hrt, null, Rank.Trainee, false)]
    [InlineData(null, null, Rank.Trainee, false)]
    // not a first release: the account has a rank already
    [InlineData(Division.Cid, Rank.Trainee, Rank.Trainee, false)]
    [InlineData(Division.Cid, Rank.Trainee, Rank.SpecialAgent, false)]
    [InlineData(Division.Cid, Rank.SpecialAgent, Rank.Trainee, false)]
    public void A_special_agent_in_charge_may_only_release_a_new_account_as_trainee_into_the_cid(
        Division? target, Rank? from, Rank to, bool expected)
    {
        var actor = Standing(Rank.SpecialAgentInCharge, Division.Cid);

        Assert.Equal(expected, PromotionRules.MayAssign(actor, target, from, to));
    }

    [Theory]
    [InlineData(Division.Ctd)]
    [InlineData(Division.Hrt)]
    public void A_special_agent_in_charge_of_another_division_still_releases_only_into_the_cid(Division home)
    {
        var actor = Standing(Rank.SpecialAgentInCharge, home);

        Assert.True(PromotionRules.MayAssign(actor, Division.Cid, null, Rank.Trainee));
        Assert.False(PromotionRules.MayAssign(actor, home, null, Rank.Trainee));
    }

    // ---- everybody else, and an actor nobody can place ----

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.SeniorSpecialAgent)]
    public void Below_special_agent_in_charge_nothing_is_assigned(Rank? actorRank)
    {
        var actor = Standing(actorRank, Division.Cid);

        foreach (var home in AllHomes)
        {
            foreach (var from in AllFrom)
            {
                foreach (var to in RankDisplay.All)
                {
                    Assert.False(PromotionRules.MayAssign(actor, home, from, to), $"{home} {from} -> {to}");
                }
            }
        }
    }

    [Fact]
    public void The_refusal_says_what_the_actor_may_do_instead()
    {
        var sectionChief = Standing(Rank.SectionChief, Division.Cid);
        var special = Standing(Rank.SpecialAgentInCharge, Division.Cid);

        Assert.Contains("eigenen Abteilung",
            PromotionRules.Refusal(sectionChief, Division.Ctd, Rank.SpecialAgent, Rank.SeniorSpecialAgent));
        Assert.Contains("ab Section Chief",
            PromotionRules.Refusal(sectionChief, Division.Cid, Rank.SpecialAgent, Rank.SectionChief));
        var first = PromotionRules.Refusal(special, Division.Cid, Rank.SpecialAgent, Rank.SeniorSpecialAgent);
        Assert.Contains("Trainee", first);
        Assert.Contains("Criminal Investigation Division", first);
        Assert.Contains("Board", PromotionRules.Refusal(Standing(Rank.SeniorSpecialAgent), Division.Cid, null, Rank.Trainee));
    }

    [Fact]
    public void RequireMayAssign_throws_unauthorized_with_that_message_and_otherwise_stays_silent()
    {
        var sectionChief = Standing(Rank.SectionChief, Division.Cid);

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            PromotionRules.RequireMayAssign(sectionChief, Division.Ctd, Rank.SpecialAgent, Rank.SeniorSpecialAgent));
        Assert.Equal(PromotionRules.Refusal(sectionChief, Division.Ctd, Rank.SpecialAgent, Rank.SeniorSpecialAgent), ex.Message);
        Assert.Null(Record.Exception(() =>
            PromotionRules.RequireMayAssign(sectionChief, Division.Cid, Rank.SpecialAgent, Rank.SeniorSpecialAgent)));
    }

    // ---- what a picker offers ----

    [Theory]
    [InlineData(Rank.SectionChief, Division.Hrt, Division.Hrt)]
    [InlineData(Rank.SectionChief, Division.Ctd, Division.Ctd)]
    [InlineData(Rank.SectionChief, Division.Cid, Division.Cid)]
    // a Special Agent in Charge of the HRT still releases into the CID only
    [InlineData(Rank.SpecialAgentInCharge, Division.Hrt, Division.Cid)]
    [InlineData(Rank.SpecialAgentInCharge, Division.Ctd, Division.Cid)]
    public void Without_a_division_picker_the_release_goes_into_the_division_the_actor_may_assign(
        Rank rank, Division home, Division expected)
    {
        var actor = Standing(rank, home);

        var division = PromotionRules.ReleaseDivision(actor);

        Assert.Equal(expected, division);
        Assert.NotEmpty(PromotionRules.AssignableRanks(actor, division, null));
    }

    [Fact]
    public void The_picker_offers_the_board_every_rank_and_a_section_chief_the_four_below_theirs()
    {
        Assert.Equal(RankDisplay.All,
            PromotionRules.AssignableRanks(Standing(Rank.AssistantDirector), Division.Cid, Rank.SpecialAgent));
        Assert.Equal(RankDisplay.All,
            PromotionRules.AssignableRanks(Standing(null, admin: true), Division.Hrt, null));

        var sectionChief = Standing(Rank.SectionChief, Division.Cid);
        Assert.Equal(
            [Rank.Trainee, Rank.SpecialAgent, Rank.SeniorSpecialAgent, Rank.SpecialAgentInCharge],
            PromotionRules.AssignableRanks(sectionChief, Division.Cid, Rank.SpecialAgent));
        Assert.Empty(PromotionRules.AssignableRanks(sectionChief, Division.Ctd, Rank.SpecialAgent));
        Assert.Empty(PromotionRules.AssignableRanks(sectionChief, Division.Cid, Rank.SectionChief));
    }

    [Fact]
    public void The_picker_offers_a_special_agent_in_charge_trainee_for_a_new_account_and_nothing_for_anyone_else()
    {
        var special = Standing(Rank.SpecialAgentInCharge, Division.Cid);

        Assert.Equal([Rank.Trainee], PromotionRules.AssignableRanks(special, Division.Cid, null));
        Assert.Empty(PromotionRules.AssignableRanks(special, Division.Ctd, null));
        Assert.Empty(PromotionRules.AssignableRanks(special, Division.Cid, Rank.Trainee));
        Assert.Empty(PromotionRules.AssignableRanks(Standing(Rank.SeniorSpecialAgent), Division.Cid, null));
    }

    // ---- one request, one row: buttons and the list ----

    private static Agent Target(string id, Rank? rank, Division? home, bool isTru = false)
        => Seed.Agent(id, rank ?? Rank.Trainee, configure: a =>
        {
            a.Rank = rank;
            a.Division = home;
            a.IsTRU = isTru;
        });

    [Fact]
    public void A_section_chief_decides_a_request_of_their_own_division_up_to_special_agent_in_charge()
    {
        var actor = Standing(Rank.SectionChief, Division.Cid);

        Assert.True(PromotionRules.MayDecide(actor, Target("own", Rank.SpecialAgent, Division.Cid), Rank.SeniorSpecialAgent));
        Assert.True(PromotionRules.MayDecide(actor, Target("derived", Rank.Trainee, null), Rank.SpecialAgent));
        Assert.False(PromotionRules.MayDecide(actor, Target("own", Rank.SpecialAgent, Division.Cid), Rank.SectionChief));
        Assert.False(PromotionRules.MayDecide(actor, Target("foreign", Rank.SpecialAgent, Division.Ctd), Rank.SeniorSpecialAgent));
        Assert.False(PromotionRules.MayDecide(actor, Target("hrt", Rank.SpecialAgent, null, isTru: true), Rank.SeniorSpecialAgent));
        Assert.False(PromotionRules.MayDecide(actor, Target("peer", Rank.SectionChief, Division.Cid), Rank.SpecialAgentInCharge));
        Assert.False(PromotionRules.MayDecide(actor, null, Rank.SpecialAgent));
    }

    [Fact]
    public void The_board_and_admins_decide_every_request_and_nobody_below_section_chief_any()
    {
        var target = Target("t", Rank.SpecialAgent, Division.Hrt, isTru: true);

        Assert.True(PromotionRules.MayDecide(Standing(Rank.AssistantDirector), target, Rank.Director));
        Assert.True(PromotionRules.MayDecide(Standing(null, admin: true), target, Rank.SectionChief));
        Assert.False(PromotionRules.MayDecide(Standing(Rank.SpecialAgentInCharge, Division.Hrt), target, Rank.SeniorSpecialAgent));
        // a first release would pass the fine gate for this actor, but a request never is one
        Assert.False(PromotionRules.MayDecide(Standing(Rank.SpecialAgentInCharge, Division.Cid), Target("new", null, null), Rank.Trainee));
    }

    [Fact]
    public void The_list_shows_the_board_and_admins_everything_a_section_chief_only_what_they_may_decide()
    {
        var cid = Target("cid", Rank.SpecialAgent, Division.Cid);
        var ctd = Target("ctd", Rank.SpecialAgent, Division.Ctd);
        var hrt = Target("hrt", Rank.SpecialAgent, Division.Hrt, isTru: true);
        var peer = Target("peer", Rank.SectionChief, Division.Cid);
        var all = new[] { cid, ctd, hrt, peer };

        Assert.All(all, t => Assert.True(PromotionRules.MayDecide(Standing(Rank.Director), t, Rank.Director)));
        Assert.All(all, t => Assert.True(PromotionRules.MayDecide(Standing(null, admin: true), t, Rank.SectionChief)));
        var sectionChief = Standing(Rank.SectionChief, Division.Cid);
        Assert.Equal([cid], all.Where(t => PromotionRules.MayDecide(sectionChief, t, Rank.SeniorSpecialAgent)).ToList());
        Assert.Empty(all.Where(t => PromotionRules.MayDecide(sectionChief, t, Rank.SectionChief)));
        Assert.All(all, t => Assert.False(PromotionRules.MayDecide(Standing(Rank.SpecialAgentInCharge, Division.Cid), t, Rank.SeniorSpecialAgent)));
    }

    [Theory]
    [InlineData(Rank.Director, Division.Ctd, Rank.Trainee, true)]
    [InlineData(Rank.AssistantDirector, Division.Cid, Rank.Director, true)]
    [InlineData(Rank.SectionChief, Division.Cid, Rank.SpecialAgent, true)]
    [InlineData(Rank.SectionChief, Division.Cid, Rank.SpecialAgentInCharge, true)]
    [InlineData(Rank.SectionChief, Division.Ctd, Rank.SpecialAgent, false)]
    [InlineData(Rank.SectionChief, Division.Cid, Rank.SectionChief, false)]
    [InlineData(Rank.SectionChief, Division.Cid, Rank.Director, false)]
    [InlineData(Rank.SpecialAgentInCharge, Division.Cid, Rank.Trainee, false)]
    [InlineData(Rank.SeniorSpecialAgent, Division.Cid, Rank.Trainee, false)]
    public void Turning_an_agent_into_a_partner_is_a_rank_write(Rank actorRank, Division target, Rank from, bool expected)
    {
        var actor = Standing(actorRank, Division.Cid);

        Assert.Equal(expected, PromotionRules.MayConvertToPartner(actor, target, from));
        if (expected)
        {
            PromotionRules.RequireMayConvertToPartner(actor, target, from);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => PromotionRules.RequireMayConvertToPartner(actor, target, from));
        }
    }

    [Fact]
    public void An_admin_may_turn_anybody_into_a_partner()
        => Assert.True(PromotionRules.MayConvertToPartner(Standing(null, admin: true), Division.Hrt, Rank.Director));

    // ---- the standing itself ----

    [Fact]
    public void The_claims_standing_carries_rank_admin_flag_and_home()
    {
        var hrtChief = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).AsDivision(Division.Hrt).AsTru().Build();
        var derived = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).AsTru().Build();
        var board = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDivision(Division.Ctd).Build();
        var admin = ClaimsPrincipalBuilder.Agent().AsAdmin().Build();

        Assert.Equal(new PromotionStanding(false, Rank.SectionChief, Division.Hrt), PromotionStanding.Of(hrtChief));
        // an older cookie without the division claim derives the home from the TRU claim
        Assert.Equal(Division.Hrt, PromotionStanding.Of(derived).Home);
        Assert.True(PromotionStanding.Of(board).IsBoard);
        Assert.Equal(Division.Ctd, PromotionStanding.Of(board).Home);
        Assert.Equal(new PromotionStanding(true, null, null), PromotionStanding.Of(admin));
    }

    [Fact]
    public void The_row_standing_is_the_row_not_the_claims()
    {
        var claims = ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.Director).AsDivision(Division.Hrt).Build();
        var row = Seed.Agent("a", Rank.SectionChief, configure: a => a.Division = Division.Ctd);

        var standing = PromotionStanding.Of(claims, row);

        Assert.Equal(Rank.SectionChief, standing.ActorRank);
        Assert.Equal(Division.Ctd, standing.Home);
        Assert.False(standing.IsAdmin);
        // the admin flag is still the one of the session
        Assert.True(PromotionStanding.Of(ClaimsPrincipalBuilder.Agent("a").AsAdmin().Build(), row).IsAdmin);
    }

    [Fact]
    public void A_row_that_is_gone_inactive_external_or_without_rank_stands_for_no_rank()
    {
        var claims = ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.Director).Build();
        var nobody = new PromotionStanding(false, null, null);

        Assert.Equal(nobody, PromotionStanding.Of(claims, null));
        Assert.Equal(nobody, PromotionStanding.Of(claims, Seed.Agent("a", Rank.Director, AgentStatus.Terminated)));
        Assert.Equal(nobody, PromotionStanding.Of(claims, Seed.Agent("a", Rank.Director, AgentStatus.Blocked)));
        Assert.Equal(nobody, PromotionStanding.Of(claims, Seed.Agent("a", Rank.Director,
            configure: a => a.PartnerAgency = PartnerAgency.LSPD)));
        Assert.Equal(nobody, PromotionStanding.Of(claims, Seed.Agent("a", Rank.Director, configure: a => a.Rank = null)));
    }

    [Fact]
    public void The_standing_of_a_bare_row_takes_the_admin_flag_from_an_active_row_only()
    {
        Assert.True(PromotionStanding.Of(Seed.Agent("a", Rank.Trainee, configure: a => a.IsAdmin = true)).IsAdmin);
        Assert.False(PromotionStanding.Of(Seed.Agent("a", Rank.Director, AgentStatus.Terminated, a => a.IsAdmin = true)).IsAdmin);
        Assert.Equal(new PromotionStanding(false, null, null), PromotionStanding.Of((Agent?)null));
    }
}
