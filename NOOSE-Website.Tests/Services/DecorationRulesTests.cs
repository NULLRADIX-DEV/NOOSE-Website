using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Decorations;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>The pure rules behind decorations and certificates.</summary>
public sealed class DecorationRulesTests
{
    // ---- rank certificates ----

    [Fact]
    public void A_first_rank_is_an_appointment()
        => Assert.Equal(CertificateKind.Appointment, DecorationRules.RankCertificateKind(null, Rank.JuniorAgent));

    [Fact]
    public void A_higher_rank_is_a_promotion()
        => Assert.Equal(CertificateKind.Promotion, DecorationRules.RankCertificateKind(Rank.JuniorAgent, Rank.SpecialAgent));

    [Theory]
    [InlineData(Rank.SeniorSpecialAgent, Rank.SpecialAgent)]
    [InlineData(Rank.SpecialAgent, Rank.SpecialAgent)]
    public void A_demotion_or_an_unchanged_rank_earns_no_certificate(Rank previous, Rank conferred)
        => Assert.Null(DecorationRules.RankCertificateKind(previous, conferred));

    // ---- who prints and who names ----

    [Fact]
    public void Leadership_and_the_recipient_may_print_nobody_else()
    {
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SupervisorySpecialAgent).Build();
        var recipient = ClaimsPrincipalBuilder.Agent("held").WithRank(Rank.JuniorAgent).Build();
        var colleague = ClaimsPrincipalBuilder.Agent("other").WithRank(Rank.SeniorSpecialAgent).Build();
        var partner = ClaimsPrincipalBuilder.Agent("lspd").WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

        Assert.True(DecorationRules.MayPrint(leader, "held", revoked: false));
        Assert.True(DecorationRules.MayPrint(recipient, "held", revoked: false));
        Assert.False(DecorationRules.MayPrint(colleague, "held", revoked: false));
        Assert.False(DecorationRules.MayPrint(partner, "held", revoked: false));
    }

    [Fact]
    public void A_revoked_award_prints_for_nobody()
    {
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
        var recipient = ClaimsPrincipalBuilder.Agent("held").WithRank(Rank.JuniorAgent).Build();

        Assert.False(DecorationRules.MayPrint(leader, "held", revoked: true));
        Assert.False(DecorationRules.MayPrint(recipient, "held", revoked: true));
    }

    [Fact]
    public void The_real_name_is_offered_to_leadership_and_to_the_recipient_but_not_to_the_supervision()
    {
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();
        var recipient = ClaimsPrincipalBuilder.Agent("held").WithRank(Rank.JuniorAgent).Build();
        var colleague = ClaimsPrincipalBuilder.Agent("other").WithRank(Rank.JuniorAgent).Build();

        Assert.True(DecorationRules.MayChooseRealName(leader, "held"));
        Assert.False(DecorationRules.MayChooseRealName(supervision, "held"));
        Assert.True(DecorationRules.MayChooseRealName(recipient, "held"));
        Assert.False(DecorationRules.MayChooseRealName(colleague, "held"));
    }

    [Fact]
    public void Only_current_staff_from_deputy_director_up_may_sign()
    {
        Assert.True(DecorationRules.MaySign(Seed.Agent("d", Rank.Director)));
        Assert.True(DecorationRules.MaySign(Seed.Agent("dd", Rank.DeputyDirector)));
        Assert.False(DecorationRules.MaySign(Seed.Agent("ssa", Rank.SupervisorySpecialAgent)));
        Assert.False(DecorationRules.MaySign(Seed.Agent("gone", Rank.Director, AgentStatus.Terminated)));
        Assert.False(DecorationRules.MaySign(Seed.Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true)));
        Assert.False(DecorationRules.MaySign(
            Seed.Agent("p", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD)));
    }

    // ---- ceremony day and text ----

    [Fact]
    public void The_ceremony_day_defaults_to_today_drops_the_time_and_never_lies_ahead()
    {
        var today = new DateTime(2026, 10, 14, 18, 30, 0);

        Assert.Equal(today.Date, DecorationRules.CeremonyDay(null, today));
        Assert.Equal(new DateTime(2026, 10, 1), DecorationRules.CeremonyDay(new DateTime(2026, 10, 1, 9, 0, 0), today));
        Assert.Equal(today.Date, DecorationRules.CeremonyDay(today.Date.AddHours(23), today));
        Assert.Null(DecorationRules.CeremonyDay(today.Date.AddDays(1), today));
    }

    [Fact]
    public void The_citation_is_trimmed_and_keeps_one_kind_of_line_break()
        => Assert.Equal("Zeile eins\nZeile zwei\nZeile drei",
            DecorationRules.NormalizeText("  Zeile eins\r\nZeile zwei\rZeile drei \n"));

    // ---- plaques ----

    [Fact]
    public void Plaques_fold_repeats_skip_revoked_and_put_the_best_grade_first()
    {
        var plaques = DecorationRules.Plaques(
        [
            new DecorationRules.PlaqueRow("bronze", "Ehrenkreuz", "orden", DecorationTier.Bronze, 10, new DateTime(2026, 9, 1), false),
            new DecorationRules.PlaqueRow("gold", "Verdienstmedaille", "medaille", DecorationTier.Gold, 30, new DateTime(2026, 8, 1), false),
            new DecorationRules.PlaqueRow("gold", "Verdienstmedaille", "medaille", DecorationTier.Gold, 30, new DateTime(2026, 10, 1), false),
            new DecorationRules.PlaqueRow("silver", "Tapferkeitsmedaille", "stern", DecorationTier.Silver, 20, new DateTime(2026, 10, 2), true),
        ]);

        Assert.Equal(["gold", "bronze"], plaques.Select(p => p.DecorationId).ToList());
        Assert.Equal(2, plaques[0].Count);
        Assert.Equal(new DateTime(2026, 10, 1), plaques[0].LatestOn);
    }

    [Fact]
    public void Within_one_grade_the_catalog_order_decides()
    {
        var plaques = DecorationRules.Plaques(
        [
            new DecorationRules.PlaqueRow("a", "A", "stern", DecorationTier.Gold, 20, new DateTime(2026, 1, 1), false),
            new DecorationRules.PlaqueRow("z", "Z", "stern", DecorationTier.Gold, 10, new DateTime(2026, 1, 1), false),
        ]);

        Assert.Equal(["z", "a"], plaques.Select(p => p.DecorationId).ToList());
    }

    // ---- board and bell ----

    [Fact]
    public void The_board_entry_encodes_every_value_and_keeps_the_line_breaks()
    {
        var html = DecorationRules.AnnouncementHtml("Medaille <b>", "Falke&Co", new DateTime(2026, 10, 14),
            "Erste Zeile\n<script>alert(1)</script>", "Adler");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Falke&amp;Co", html);
        Assert.Contains("Medaille &lt;b&gt;", html);
        Assert.Contains("Erste Zeile<br>", html);
        Assert.Contains("14. Oktober 2026", html);
        Assert.Contains("durch Adler", html);
    }

    [Fact]
    public void The_board_title_names_decoration_and_codename_and_fits_the_column()
    {
        Assert.Equal("Ehrung: Verdienstmedaille für Falke", DecorationRules.AnnouncementTitle("Verdienstmedaille", "Falke"));
        Assert.Equal(200, DecorationRules.AnnouncementTitle(new string('x', 80), new string('y', 180)).Length);
    }

    [Fact]
    public void An_unknown_symbol_falls_back_to_the_medal()
    {
        Assert.False(DecorationSymbols.IsKnown("kaputt"));
        Assert.Equal(DecorationSymbols.Default, DecorationSymbols.Find("kaputt").Key);
        Assert.Equal(DecorationSymbols.Default, DecorationSymbols.Find(null).Key);
    }
}
