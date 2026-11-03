using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Models;

/// <summary>The rank titles: the neutral one for what leaves the house, the HRT's own for the house itself.</summary>
[Collection("EnumLabels")]
public sealed class RankDisplayTests : IDisposable
{
    public RankDisplayTests() => EnumLabelText.ReplaceAll([]);

    public void Dispose() => EnumLabelText.ReplaceAll([]);

    private static readonly Division?[] Elsewhere = [null, Division.Cid, Division.Ctd];

    // ---- the HRT ladder ----

    [Theory]
    [InlineData(Rank.SpecialAgent, "Operator")]
    [InlineData(Rank.SeniorSpecialAgent, "Senior Operator")]
    [InlineData(Rank.SpecialAgentInCharge, "Leading Operator")]
    [InlineData(Rank.SectionChief, "Commanding Operator")]
    public void The_hrt_has_its_own_title_from_special_agent_to_section_chief(Rank rank, string title)
    {
        Assert.Equal(title, RankDisplay.HrtTitle(rank));
        Assert.Equal(title, RankDisplay.Name(rank, Division.Hrt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.AssistantDirector)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public void Outside_the_four_hrt_levels_the_hrt_has_no_title_of_its_own(Rank? rank)
    {
        Assert.Null(RankDisplay.HrtTitle(rank));
        Assert.Equal(RankDisplay.Name(rank), RankDisplay.Name(rank, Division.Hrt));
    }

    [Fact]
    public void Another_division_or_none_gets_the_neutral_title_at_every_level()
    {
        foreach (var rank in RankDisplay.All)
        {
            foreach (var division in Elsewhere)
            {
                Assert.Equal(RankDisplay.Name(rank), RankDisplay.Name(rank, division));
            }
        }
        Assert.Equal("— (kein Rang)", RankDisplay.Name(null, Division.Hrt));
    }

    // ---- the neutral title never carries the division ----

    [Fact]
    public void The_neutral_title_is_never_an_hrt_title()
    {
        var hrtTitles = RankDisplay.All.Select(r => RankDisplay.HrtTitle(r)).OfType<string>().ToList();

        Assert.Equal(4, hrtTitles.Count);
        foreach (var rank in RankDisplay.All)
        {
            Assert.DoesNotContain(RankDisplay.Name(rank), hrtTitles);
            Assert.DoesNotContain(RankDisplay.DefaultName(rank), hrtTitles);
        }
    }

    [Fact]
    public void The_hrt_titles_are_distinct_and_never_a_neutral_name()
    {
        var hrtTitles = RankDisplay.All.Select(r => RankDisplay.HrtTitle(r)).OfType<string>().ToList();
        var neutral = RankDisplay.All.Select(r => RankDisplay.DefaultName(r)).ToList();

        Assert.Equal(hrtTitles.Count, hrtTitles.Distinct().Count());
        Assert.Empty(hrtTitles.Intersect(neutral));
    }

    [Fact]
    public void A_label_override_changes_the_neutral_title_and_the_non_hrt_division_but_not_the_hrt_title()
    {
        EnumLabelText.ReplaceAll([("Rank", "SectionChief", "Abteilungsleiter")]);

        Assert.Equal("Abteilungsleiter", RankDisplay.Name(Rank.SectionChief));
        Assert.Equal("Abteilungsleiter", RankDisplay.Name(Rank.SectionChief, Division.Cid));
        Assert.Equal("Commanding Operator", RankDisplay.Name(Rank.SectionChief, Division.Hrt));
        Assert.Equal("Section Chief", RankDisplay.DefaultName(Rank.SectionChief));
    }

    [Fact]
    public void The_override_key_is_the_member_name_of_the_eight_ranks()
    {
        foreach (var rank in RankDisplay.All)
        {
            EnumLabelText.ReplaceAll([("Rank", rank.ToString(), "X")]);

            Assert.Equal("X", RankDisplay.Name(rank));
            Assert.All(RankDisplay.All.Where(other => other != rank), other => Assert.NotEqual("X", RankDisplay.Name(other)));
        }
    }

    // ---- as the house sees an agent ----

    [Theory]
    [InlineData(Division.Hrt, Rank.SpecialAgent, "Operator")]
    [InlineData(Division.Hrt, Rank.SectionChief, "Commanding Operator")]
    [InlineData(Division.Hrt, Rank.Director, "Director")]
    [InlineData(Division.Hrt, Rank.Trainee, "Trainee")]
    [InlineData(Division.Cid, Rank.SpecialAgent, "Special Agent")]
    [InlineData(Division.Ctd, Rank.SpecialAgentInCharge, "Special Agent in Charge")]
    public void An_agent_row_shows_the_title_of_its_division(Division home, Rank rank, string expected)
    {
        var agent = Seed.Agent("a", rank, configure: a => a.Division = home);

        Assert.Equal(expected, DivisionRules.RankName(agent));
    }

    [Fact]
    public void A_row_of_an_older_stand_derives_the_hrt_from_the_tru_flag_and_a_partner_has_no_title()
    {
        Assert.Equal("Senior Operator", DivisionRules.RankName(Seed.Agent("a", Rank.SeniorSpecialAgent, configure: a => a.IsTRU = true)));
        Assert.Equal("Senior Special Agent", DivisionRules.RankName(Seed.Agent("a", Rank.SeniorSpecialAgent)));
        Assert.Equal("— (kein Rang)", DivisionRules.RankName(Seed.Agent("p", Rank.Trainee, configure: a =>
        {
            a.Rank = null;
            a.PartnerAgency = PartnerAgency.LSPD;
        })));
    }

    [Fact]
    public void A_board_member_keeps_the_home_division_but_never_an_hrt_title()
    {
        var board = Seed.Agent("b", Rank.AssistantDirector, configure: a => a.Division = Division.Hrt);

        Assert.Equal("Assistant Director", DivisionRules.RankName(board));
    }
}
