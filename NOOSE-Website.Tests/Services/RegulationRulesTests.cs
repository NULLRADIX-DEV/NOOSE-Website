using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Labels, validity, audience and permissions of the service regulation; pure rules, no database.</summary>
public sealed class RegulationRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static ClaimsPrincipal Agent(Rank rank) => ClaimsPrincipalBuilder.Agent().WithRank(rank).Build();

    private static RegulationStanding Standing(Rank? rank, Division? home = null, bool hrb = false, bool admin = false)
        => new(admin, rank, home, hrb);

    // ---------- labels ----------

    [Fact]
    public void Section_and_paragraph_labels_read_as_the_regulation_prints_them()
    {
        Assert.Equal("Vorwort", RegulationRules.SectionLabel(null));
        Assert.Equal("Abschnitt 3", RegulationRules.SectionLabel(3));
        Assert.Equal("Handlungsvorschriften", RegulationRules.SectionCaption(null, "Handlungsvorschriften"));
        Assert.Equal("Abschnitt 3: Handlungsvorschriften", RegulationRules.SectionCaption(3, "Handlungsvorschriften"));
        Assert.Equal("DVO Abschnitt 3 · Funkdisziplin", RegulationRules.ParagraphLabel(3, "Funkdisziplin"));
        Assert.Equal("DVO Vorwort · Leitbild", RegulationRules.ParagraphLabel(null, "Leitbild"));
    }

    [Fact]
    public void Audience_labels_name_the_unit_and_map_to_the_division()
    {
        Assert.Equal("Ganzes FIB", RegulationAudienceDisplay.Label(RegulationAudience.Bureau));
        Assert.Equal("Administration Division", RegulationAudienceDisplay.Label(RegulationAudience.Administration));
        Assert.Equal(DivisionDisplay.Name(Division.Hrt), RegulationAudienceDisplay.Label(RegulationAudience.Hrt));
        Assert.Equal("FIB", RegulationAudienceDisplay.Short(RegulationAudience.Bureau));
        Assert.Equal(DivisionDisplay.Short(Division.Ctd), RegulationAudienceDisplay.Short(RegulationAudience.Ctd));
        Assert.Equal("—", RegulationAudienceDisplay.Label((RegulationAudience)99));

        Assert.Null(RegulationAudienceDisplay.DivisionOf(RegulationAudience.Bureau));
        Assert.Null(RegulationAudienceDisplay.DivisionOf(RegulationAudience.Administration));
        foreach (var division in Enum.GetValues<Division>())
        {
            Assert.Equal(division, RegulationAudienceDisplay.DivisionOf(RegulationAudienceDisplay.Of(division)));
        }
        // every value is offered once, and the stored numbers never move
        Assert.Equal(Enum.GetValues<RegulationAudience>().Order(), RegulationAudienceDisplay.All.Order());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<RegulationAudience>().Select(a => (int)a));
    }

    // ---------- validity ----------

    [Fact]
    public void ValidVersion_is_the_newest_that_has_started()
    {
        Assert.Equal(1, RegulationRules.ValidVersion([(1, Today.AddDays(-10)), (2, Today.AddDays(5))], Today));
        Assert.Null(RegulationRules.ValidVersion([(1, Today.AddDays(1))], Today));
        Assert.Equal(2, RegulationRules.ValidVersion([(1, Today.AddDays(-3)), (2, Today)], Today));
        // order is irrelevant
        Assert.Equal(2, RegulationRules.ValidVersion([(2, Today.AddDays(-3)), (1, Today.AddDays(-10))], Today));
        Assert.Null(RegulationRules.ValidVersion([], Today));
    }

    [Fact]
    public void IsValidFromAllowed_refuses_a_date_before_the_previous_version()
    {
        Assert.True(RegulationRules.IsValidFromAllowed(Today, null));
        Assert.True(RegulationRules.IsValidFromAllowed(Today, Today));
        Assert.True(RegulationRules.IsValidFromAllowed(Today.AddDays(1), Today));
        Assert.False(RegulationRules.IsValidFromAllowed(Today.AddDays(-1), Today));
    }

    [Fact]
    public void NextAcknowledgeFrom_moves_only_when_the_version_asks()
    {
        Assert.Equal(3, RegulationRules.NextAcknowledgeFrom(currentAcknowledgeFrom: 2, newVersion: 3, requiresAcknowledgment: true));
        Assert.Equal(2, RegulationRules.NextAcknowledgeFrom(currentAcknowledgeFrom: 2, newVersion: 3, requiresAcknowledgment: false));
    }

    // ---------- coarse gates ----------

    [Fact]
    public void MayManage_is_leadership_with_write_access_never_a_team_lead()
    {
        Assert.True(RegulationRules.MayManage(Agent(Rank.SectionChief)));
        Assert.True(RegulationRules.MayManage(Agent(Rank.Director)));
        Assert.True(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin().Build()));

        Assert.False(RegulationRules.MayManage(Agent(Rank.SpecialAgentInCharge)));
        Assert.False(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().Build()));
        // team lead plus admin
        Assert.False(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().AsAdmin().Build()));
        Assert.False(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
        Assert.False(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
        Assert.False(RegulationRules.MayManage(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .WithStatus(AgentStatus.Terminated).Build()));
    }

    [Fact]
    public void MaySeeQuota_is_leadership_and_supervision()
    {
        Assert.True(RegulationRules.MaySeeQuota(Agent(Rank.SectionChief)));
        Assert.True(RegulationRules.MaySeeQuota(ClaimsPrincipalBuilder.Agent().AsTeamLead().Build()));

        Assert.False(RegulationRules.MaySeeQuota(Agent(Rank.Trainee)));
        Assert.False(RegulationRules.MaySeeQuota(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
    }

    [Fact]
    public void MayAcknowledge_is_every_internal_writer()
    {
        Assert.True(RegulationRules.MayAcknowledge(Agent(Rank.Trainee)));

        Assert.False(RegulationRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().AsTeamLead().Build()));
        Assert.False(RegulationRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()));
        Assert.False(RegulationRules.MayAcknowledge(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
    }

    [Fact]
    public void MayRead_is_every_internal_agent_never_a_partner()
    {
        Assert.True(RegulationRules.MayRead(ViewerScope.From(Agent(Rank.Trainee))));
        Assert.True(RegulationRules.MayRead(ViewerScope.From(ClaimsPrincipalBuilder.Agent().AsTeamLead().Build())));

        Assert.False(RegulationRules.MayRead(ViewerScope.From(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build())));
        Assert.False(RegulationRules.MayRead(ViewerScope.From(ClaimsPrincipalBuilder.Anonymous())));
        // a hand-built scope is never internal
        Assert.False(RegulationRules.MayRead(new ViewerScope(true, true, "x", null)));
    }

    [Fact]
    public void RequireRegulationWrite_puts_the_write_guard_ahead_of_the_rank_guard()
    {
        Assert.Null(Record.Exception(() => Permission.RequireRegulationWrite(Agent(Rank.SectionChief))));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireRegulationWrite(Agent(Rank.SpecialAgentInCharge)));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireRegulationWrite(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo().Build()));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireRegulationWrite(
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().AsAdmin().Build()));
    }

    // ---------- fine gate ----------

    public static TheoryData<string, RegulationStanding, RegulationAudience[]> EditMatrix() => new()
    {
        { "admin trainee", Standing(Rank.Trainee, Division.Cid, admin: true), RegulationAudienceDisplay.All.ToArray() },
        { "admin without rank", Standing(null, admin: true), RegulationAudienceDisplay.All.ToArray() },
        { "assistant director", Standing(Rank.AssistantDirector, Division.Cid), RegulationAudienceDisplay.All.ToArray() },
        { "director", Standing(Rank.Director, Division.Hrt), RegulationAudienceDisplay.All.ToArray() },
        { "section chief cid", Standing(Rank.SectionChief, Division.Cid), [RegulationAudience.Cid] },
        { "section chief ctd", Standing(Rank.SectionChief, Division.Ctd), [RegulationAudience.Ctd] },
        { "section chief hrt", Standing(Rank.SectionChief, Division.Hrt), [RegulationAudience.Hrt] },
        { "section chief ctd hrb", Standing(Rank.SectionChief, Division.Ctd, hrb: true),
            [RegulationAudience.Administration, RegulationAudience.Ctd] },
        { "section chief without home", Standing(Rank.SectionChief), [] },
        { "section chief hrb without home", Standing(Rank.SectionChief, hrb: true), [RegulationAudience.Administration] },
        { "sac hrb", Standing(Rank.SpecialAgentInCharge, Division.Cid, hrb: true), [] },
        { "trainee", Standing(Rank.Trainee, Division.Cid), [] },
        { "no rank", Standing(null), [] },
    };

    [Theory]
    [MemberData(nameof(EditMatrix))]
    public void MayEditParagraph_follows_board_admin_and_the_section_chiefs_own_unit(string who, RegulationStanding actor,
        RegulationAudience[] editable)
    {
        foreach (var audience in RegulationAudienceDisplay.All)
        {
            Assert.True(editable.Contains(audience) == RegulationRules.MayEditParagraph(actor, audience), $"{who} / {audience}");
        }
        Assert.Equal(editable.Order(), RegulationRules.EditableAudiences(actor).Order());
        // sections are the Board's and the admins' alone
        Assert.Equal(editable.Length == RegulationAudienceDisplay.All.Count, RegulationRules.MayManageSections(actor));
    }

    [Fact]
    public void A_refusal_names_who_may_edit_instead()
    {
        Assert.Contains("Board", RegulationRules.EditRefusal(RegulationAudience.Bureau), StringComparison.Ordinal);
        Assert.Contains(DivisionDisplay.Name(Division.Ctd), RegulationRules.EditRefusal(RegulationAudience.Ctd), StringComparison.Ordinal);
        Assert.Contains("Section Chief", RegulationRules.EditRefusal(RegulationAudience.Administration), StringComparison.Ordinal);
    }

    // ---------- standing ----------

    [Fact]
    public void Standing_from_the_claims_derives_the_home_like_the_division_rules()
    {
        var ctd = RegulationStanding.Of(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).AsDivision(Division.Ctd).AsHrb().Build());
        Assert.Equal(new RegulationStanding(false, Rank.SectionChief, Division.Ctd, true), ctd);
        Assert.False(ctd.IsBoard);
        Assert.False(ctd.SeesAll);

        // no division claim: the legacy TRU flag means HRT, otherwise CID
        Assert.Equal(Division.Hrt, RegulationStanding.Of(ClaimsPrincipalBuilder.Agent().WithRank(Rank.SectionChief).AsTru().Build()).Home);
        Assert.Equal(Division.Cid, RegulationStanding.Of(Agent(Rank.SectionChief)).Home);

        var board = RegulationStanding.Of(Agent(Rank.AssistantDirector));
        Assert.True(board.IsBoard);
        Assert.True(board.SeesAll);
        // the admin flag makes no Board member
        var admin = RegulationStanding.Of(ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin().Build());
        Assert.False(admin.IsBoard);
        Assert.True(admin.SeesAll);
    }

    [Fact]
    public void Standing_from_the_row_ignores_the_claims_rank_and_division()
    {
        // the session still says Section Chief of the CID
        var claims = ClaimsPrincipalBuilder.Agent("sc").WithRank(Rank.SectionChief).AsDivision(Division.Cid).Build();

        var moved = RegulationStanding.Of(claims, Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Ctd));
        Assert.Equal(Division.Ctd, moved.Home);
        Assert.False(RegulationRules.MayEditParagraph(moved, RegulationAudience.Cid));
        Assert.True(RegulationRules.MayEditParagraph(moved, RegulationAudience.Ctd));

        var demoted = RegulationStanding.Of(claims, Seed.Agent("sc", Rank.SpecialAgentInCharge));
        Assert.Equal(Rank.SpecialAgentInCharge, demoted.ActorRank);
        Assert.Empty(RegulationRules.EditableAudiences(demoted));

        // gone, inactive or external: no rank at all
        Agent[] noStanding =
        [
            Seed.Agent("sc", Rank.SectionChief, AgentStatus.Terminated),
            Seed.Agent("sc", Rank.SectionChief, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Seed.Agent("sc", Rank.SectionChief, configure: a => a.Rank = null),
        ];
        foreach (var row in noStanding)
        {
            Assert.Equal(new RegulationStanding(false, null, null, false), RegulationStanding.Of(claims, row));
        }
        Assert.Equal(new RegulationStanding(false, null, null, false), RegulationStanding.Of(claims, null));
    }

    // ---------- audience ----------

    /// <summary>The spec written out independently: who owes a paragraph of that audience.</summary>
    internal static bool Owes(RegulationAudience audience, Rank? rank, Division? stored, bool isTru, bool isHrb)
    {
        if (audience == RegulationAudience.Bureau)
        {
            return true;
        }
        if (audience == RegulationAudience.Administration)
        {
            return isHrb;
        }
        if (rank is null || rank >= Rank.AssistantDirector)
        {
            // the Board is its own seat
            return false;
        }
        var home = stored ?? (isTru ? Division.Hrt : Division.Cid);
        if (home == Division.Hrt && rank < Rank.SpecialAgent)
        {
            home = Division.Cid;
        }
        return RegulationAudienceDisplay.Of(home) == audience;
    }

    public static IEnumerable<object?[]> Combinations()
    {
        var ranks = Enum.GetValues<Rank>().Select(r => (Rank?)r).Append(null);
        var divisions = Enum.GetValues<Division>().Select(d => (Division?)d).Append(null);
        foreach (var rank in ranks)
        {
            foreach (var division in divisions)
            {
                foreach (var tru in new[] { false, true })
                {
                    foreach (var hrb in new[] { false, true })
                    {
                        yield return [rank, division, tru, hrb];
                    }
                }
            }
        }
    }

    [Fact]
    public void InAudience_agrees_with_the_division_rules_for_every_rank_division_and_flag()
    {
        var count = 0;
        foreach (var row in Combinations())
        {
            var (rank, division, tru, hrb) = ((Rank?)row[0], (Division?)row[1], (bool)row[2]!, (bool)row[3]!);
            var unit = DivisionRules.Of(rank, division, tru);
            foreach (var audience in RegulationAudienceDisplay.All)
            {
                Assert.True(Owes(audience, rank, division, tru, hrb) == RegulationRules.InAudience(audience, unit, hrb),
                    $"{audience} / {rank?.ToString() ?? "-"} / {division?.ToString() ?? "-"} / tru {tru} / hrb {hrb}");
                count++;
            }
        }
        // 9 ranks x 4 divisions x 2 x 2 x 5 audiences
        Assert.Equal(9 * 4 * 2 * 2 * 5, count);
    }

    [Fact]
    public void The_board_owes_no_division_paragraph_but_the_bureau_and_administration_ones()
    {
        var board = DivisionRules.Of(Rank.AssistantDirector, Division.Ctd, false);

        Assert.False(RegulationRules.InAudience(RegulationAudience.Ctd, board, isHrb: true));
        Assert.False(RegulationRules.InAudience(RegulationAudience.Cid, board, isHrb: true));
        Assert.True(RegulationRules.InAudience(RegulationAudience.Bureau, board, isHrb: false));
        Assert.True(RegulationRules.InAudience(RegulationAudience.Administration, board, isHrb: true));
        // HRT below Special Agent sits in the CID
        var trainee = DivisionRules.Of(Rank.Trainee, Division.Hrt, true);
        Assert.True(RegulationRules.InAudience(RegulationAudience.Cid, trainee, false));
        Assert.False(RegulationRules.InAudience(RegulationAudience.Hrt, trainee, false));
    }
}
