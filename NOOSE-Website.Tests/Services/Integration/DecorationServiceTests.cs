using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Decorations;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Decorations;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Decorations: leadership awards, never to itself; the award stands even when an announcement fails.</summary>
public sealed class DecorationServiceTests : IDisposable
{
    private const string Gold = "deko-gold";
    private const string Off = "deko-aus";
    private const string Silver = "deko-silber";

    private readonly SqliteTestContext _ctx = new();
    private readonly IAnnouncementService _announcements = Substitute.For<IAnnouncementService>();
    private readonly IDiscordWebhookService _discord = Substitute.For<IDiscordWebhookService>();
    private readonly IDiscordWebhookService _bellChannel = Substitute.For<IDiscordWebhookService>();
    private readonly NavPreferencesService _prefs;
    private readonly DecorationService _service;

    public DecorationServiceTests()
    {
        _announcements.CreateAsync(Arg.Any<AnnouncementInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => new Announcement { Id = "ann-1" });
        var cache = new MemoryCache(new MemoryCacheOptions());
        _prefs = new NavPreferencesService(_ctx.Factory, cache);
        var notifications = new NotificationService(_ctx.Factory, new NotificationBroadcaster(), _bellChannel,
            Substitute.For<IDiscordDmService>(), Substitute.For<IDiscordDmQueue>());
        _service = new DecorationService(_ctx.Factory, _announcements, notifications, _discord,
            NullLogger<DecorationService>.Instance);
        SeedRoster();
    }

    public void Dispose() => _ctx.Dispose();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Adler").Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).WithCodename("Falke").Build();

    private static ClaimsPrincipal Colleague()
        => ClaimsPrincipalBuilder.Agent("other").WithRank(Rank.SeniorSpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Demo()
        => ClaimsPrincipalBuilder.Agent(DemoIdentity.AgentId).WithRank(Rank.Director).AsDemo().AsHrb().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static DecorationAwardInput Input(string agentId = "junior", string decorationId = Gold,
        DateTime? on = null, string citation = "Für Mut im Einsatz.") => new()
    {
        AgentId = agentId,
        DecorationId = decorationId,
        AwardedOn = on,
        Citation = citation,
    };

    // ---- who may award ----

    [Fact]
    public async Task A_plain_agent_may_not_award()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.AwardAsync(Input("other"), Junior()));

    [Fact]
    public async Task The_supervision_and_the_demo_visitor_stop_at_the_write_guard()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.AwardAsync(Input(), Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.AwardAsync(Input(), Demo()));
        Assert.Empty(Awards());
    }

    [Fact]
    public async Task Nobody_decorates_himself()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AwardAsync(Input("lead"), Leader()));
        Assert.Empty(Awards());
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("tl")]
    [InlineData("gone")]
    [InlineData("nobody")]
    public async Task Only_current_in_house_staff_can_be_decorated(string target)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AwardAsync(Input(target), Leader()));
        Assert.Empty(Awards());
    }

    [Fact]
    public async Task A_switched_off_decoration_a_future_day_and_an_empty_citation_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AwardAsync(Input(decorationId: Off), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AwardAsync(Input(on: DateTime.Today.AddDays(1)), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AwardAsync(Input(citation: "  "), Leader()));
        Assert.Empty(Awards());
    }

    // ---- announcements ----

    [Fact]
    public async Task An_award_posts_to_the_board_without_a_broadcast_and_rings_only_the_recipient()
    {
        var id = await _service.AwardAsync(Input(on: new DateTime(2026, 10, 1)), Leader());

        await _announcements.Received(1).CreateAsync(
            Arg.Is<AnnouncementInput>(a => !a.AsBroadcast && !a.AcknowledgmentRequired
                && a.Audience == AnnouncementAudience.AllActive
                && a.Title == "Ehrung: Verdienstmedaille für Falke"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        var award = Awards().Single();
        Assert.Equal(id, award.Id);
        Assert.Equal("ann-1", award.AnnouncementId);
        Assert.Equal(new DateTime(2026, 10, 1), award.AwardedOn);
        Assert.Equal(Rank.Trainee, award.RecipientRank);
        Assert.Equal("Adler", award.AwardedByName);

        var bell = Assert.Single(Notices());
        Assert.Equal("junior", bell.RecipientId);
        Assert.Equal(NotificationType.DecorationAwarded, bell.Type);
        Assert.Equal("/personal/junior?tab=auszeichnungen", bell.Href);
        Assert.Null(bell.ReadAt);

        await _discord.Received(1).PushDecorationAsync("junior", "Falke", "Verdienstmedaille", new DateTime(2026, 10, 1),
            "Für Mut im Einsatz.", "Adler", "/personal/junior?tab=auszeichnungen", Arg.Any<CancellationToken>());
        await _bellChannel.DidNotReceiveWithAnyArgs().PushAsync(default, default, default, default, default);
    }

    [Theory]
    [InlineData("board")]
    [InlineData("discord")]
    [InlineData("bell")]
    public async Task Each_switch_silences_its_own_announcement_only(string off)
    {
        var input = Input();
        input.PostToBoard = off != "board";
        input.PostToDiscord = off != "discord";
        input.NotifyAgent = off != "bell";

        await _service.AwardAsync(input, Leader());

        Assert.Single(Awards());
        await _announcements.Received(off == "board" ? 0 : 1)
            .CreateAsync(Arg.Any<AnnouncementInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        Assert.Equal(off == "board" ? null : "ann-1", Awards().Single().AnnouncementId);
        await _discord.Received(off == "discord" ? 0 : 1).PushDecorationAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(off == "bell" ? 0 : 1, Notices().Count);
    }

    [Fact]
    public async Task A_muted_bell_arrives_read()
    {
        await _prefs.SetNotificationMutedAsync("junior", NotificationType.DecorationAwarded, true);

        await _service.AwardAsync(Input(), Leader());

        Assert.NotNull(Assert.Single(Notices()).ReadAt);
    }

    [Fact]
    public async Task A_failing_board_costs_neither_the_award_nor_the_bell()
    {
        _announcements.CreateAsync(Arg.Any<AnnouncementInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Brett kaputt"));

        await _service.AwardAsync(Input(), Leader());

        Assert.Null(Assert.Single(Awards()).AnnouncementId);
        Assert.Single(Notices());
    }

    [Fact]
    public async Task The_same_decoration_may_be_awarded_twice()
    {
        await _service.AwardAsync(Input(), Leader());
        await _service.AwardAsync(Input(), Leader());

        var plaque = Assert.Single((await _service.GetPlaquesAsync(["junior"], Colleague()))["junior"]);
        Assert.Equal(2, plaque.Count);
    }

    // ---- signatory ----

    [Fact]
    public async Task The_signatory_is_frozen_on_the_award()
    {
        await _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", "Los Santos"), Leader());
        var first = await _service.AwardAsync(Input(), Leader());

        Terminate("dd");
        var second = await _service.AwardAsync(Input(), Leader());

        Assert.Equal("Codename-dd", Award(first).SignatoryName);
        Assert.Equal(Rank.DeputyDirector, Award(first).SignatoryRank);
        Assert.Null(Award(second).SignatoryName);
        Assert.Equal("Codename-dd", (await _service.GetCertificateAsync(first, Leader()))!.SignatoryName);
    }

    [Fact]
    public async Task Only_staff_from_assistant_director_up_can_be_made_signatory_but_a_stored_one_may_stay()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SaveCertificateSettingsAsync(new CertificateSettings("lead2", ""), Leader()));

        await _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", ""), Leader());
        Terminate("dd");
        await _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", "Sandy Shores"), Leader());

        var settings = await _service.GetCertificateSettingsAsync();
        Assert.Equal("dd", settings.SignatoryId);
        Assert.Equal("Sandy Shores", settings.Place);
        var options = await _service.GetSignatoryOptionsAsync(Leader());
        Assert.False(options.Single(o => o.Id == "dd").Eligible);
        Assert.DoesNotContain(options, o => o.Id == "lead2");
    }

    private static Dictionary<Division, string> Picks(params (Division Division, string Id)[] picks)
        => picks.ToDictionary(p => p.Division, p => p.Id);

    [Fact]
    public async Task The_signatory_follows_the_division_of_the_recipient_and_is_frozen_on_the_award()
    {
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "", Picks((Division.Hrt, "sc-hrt"))), Leader());

        var hrt = await _service.AwardAsync(Input("hrt-agent"), Leader());
        var cid = await _service.AwardAsync(Input("junior"), Leader());
        Terminate("sc-hrt");

        Assert.Equal("Codename-sc-hrt", Award(hrt).SignatoryName);
        Assert.Equal(Rank.SectionChief, Award(hrt).SignatoryRank);
        // the CID names nobody of its own, so the global signatory signs
        Assert.Equal("Codename-dd", Award(cid).SignatoryName);
        Assert.Equal(Rank.DeputyDirector, Award(cid).SignatoryRank);
        // the snapshot outlives the Section Chief
        Assert.Equal("Codename-sc-hrt", (await _service.GetCertificateAsync(hrt, Leader()))!.SignatoryName);
    }


    [Fact]
    public async Task A_board_recipient_gets_the_global_signatory_never_a_division_one()
    {
        using (var db = _ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("board-hrt", Rank.AssistantDirector, configure: a => a.Division = Division.Hrt));
            db.SaveChanges();
        }
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "", Picks((Division.Hrt, "sc-hrt"))), Leader());
        SeedHistory("h-board", Rank.SectionChief, Rank.AssistantDirector, "board-hrt");

        var award = await _service.AwardAsync(Input("board-hrt"), Leader());
        var certificate = await _service.GetRankCertificateAsync("h-board", Leader());

        Assert.Equal("Codename-dd", Award(award).SignatoryName);
        Assert.Equal("Codename-dd", certificate!.SignatoryName);
    }
    [Fact]
    public async Task A_division_signatory_who_left_or_changed_division_falls_back_to_the_global_one()
    {
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "", Picks((Division.Hrt, "sc-hrt"))), Leader());
        Terminate("sc-hrt");
        var left = await _service.AwardAsync(Input("hrt-agent"), Leader());

        Assert.Equal("Codename-dd", Award(left).SignatoryName);

        Unterminate("sc-hrt");
        MoveTo("sc-hrt", Division.Cid);
        var moved = await _service.AwardAsync(Input("hrt-agent"), Leader());

        Assert.Equal("Codename-dd", Award(moved).SignatoryName);
    }

    [Fact]
    public async Task Without_any_signatory_the_award_names_nobody()
    {
        var id = await _service.AwardAsync(Input("hrt-agent"), Leader());

        Assert.Null(Award(id).SignatoryName);
        Assert.Null(Award(id).SignatoryRank);
    }

    [Fact]
    public async Task The_rank_certificate_carries_the_signatory_of_the_recipient_division()
    {
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "", Picks((Division.Hrt, "sc-hrt"))), Leader());
        SeedHistory("h-hrt", Rank.Trainee, Rank.SpecialAgent, "hrt-agent");
        SeedHistory("h-cid", Rank.Trainee, Rank.SpecialAgent);

        var hrt = await _service.GetRankCertificateAsync("h-hrt", Leader());
        var cid = await _service.GetRankCertificateAsync("h-cid", Leader());

        Assert.Equal("Codename-sc-hrt", hrt!.SignatoryName);
        Assert.Equal(Rank.SectionChief, hrt.SignatoryRank);
        Assert.Equal("Codename-dd", cid!.SignatoryName);
        Assert.Equal(Rank.DeputyDirector, cid.SignatoryRank);
    }

    [Fact]
    public async Task A_section_chief_can_be_picked_for_the_own_division_only_and_never_as_the_global_signatory()
    {
        // the global one is the office head: Board or admin
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SaveCertificateSettingsAsync(new CertificateSettings("sc-cid", ""), Leader()));
        // the Section Chief of another division, a Special Agent in Charge and a stranger sign for no division
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveCertificateSettingsAsync(
            new CertificateSettings(null, "", Picks((Division.Hrt, "sc-cid"))), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveCertificateSettingsAsync(
            new CertificateSettings(null, "", Picks((Division.Cid, "lead2"))), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveCertificateSettingsAsync(
            new CertificateSettings(null, "", Picks((Division.Cid, "nobody"))), Leader()));
        Assert.Null((await _service.GetCertificateSettingsAsync()).DivisionSignatoryId(Division.Hrt));

        await _service.SaveCertificateSettingsAsync(new CertificateSettings(null, "",
            Picks((Division.Cid, "sc-cid"), (Division.Hrt, "sc-hrt"), (Division.Ctd, "dd"))), Leader());

        var settings = await _service.GetCertificateSettingsAsync();
        Assert.Equal("sc-cid", settings.DivisionSignatoryId(Division.Cid));
        Assert.Equal("sc-hrt", settings.DivisionSignatoryId(Division.Hrt));
        Assert.Equal("dd", settings.DivisionSignatoryId(Division.Ctd));
        Assert.Null(settings.SignatoryId);
    }

    [Fact]
    public async Task Saving_without_a_division_pick_clears_it_and_a_kept_one_that_dropped_out_may_stay()
    {
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "", Picks((Division.Hrt, "sc-hrt"))), Leader());
        MoveTo("sc-hrt", Division.Cid);

        // unchanged, although the Section Chief no longer qualifies: the form must not lock
        await _service.SaveCertificateSettingsAsync(
            new CertificateSettings("dd", "Sandy Shores", Picks((Division.Hrt, "sc-hrt"))), Leader());
        Assert.Equal("sc-hrt", (await _service.GetCertificateSettingsAsync()).DivisionSignatoryId(Division.Hrt));
        var options = await _service.GetSignatoryOptionsAsync(Leader(), Division.Hrt);
        Assert.False(options.Single(o => o.Id == "sc-hrt").Eligible);

        await _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", "Sandy Shores"), Leader());
        Assert.Null((await _service.GetCertificateSettingsAsync()).DivisionSignatoryId(Division.Hrt));
    }

    [Fact]
    public async Task The_signatory_options_of_a_division_are_the_board_and_the_section_chiefs_of_that_division()
    {
        var hrt = (await _service.GetSignatoryOptionsAsync(Leader(), Division.Hrt)).Select(o => o.Id).ToList();
        var global = (await _service.GetSignatoryOptionsAsync(Leader())).Select(o => o.Id).ToList();

        Assert.Contains("lead", hrt);
        Assert.Contains("dd", hrt);
        Assert.Contains("sc-hrt", hrt);
        Assert.DoesNotContain("sc-cid", hrt);
        Assert.DoesNotContain("lead2", hrt);
        Assert.Contains("lead", global);
        Assert.DoesNotContain("sc-hrt", global);
        Assert.DoesNotContain("sc-cid", global);
        Assert.Empty(await _service.GetSignatoryOptionsAsync(Partner(), Division.Hrt));
    }

    [Fact]
    public async Task Without_a_stored_place_the_certificate_names_the_observatory()
        => Assert.Equal(DecorationRules.DefaultPlace, (await _service.GetCertificateSettingsAsync()).Place);

    // ---- correcting and revoking ----

    [Fact]
    public async Task A_correction_rewrites_the_board_entry_but_announces_nothing_new()
    {
        var id = await _service.AwardAsync(Input(), Leader());
        _announcements.ClearReceivedCalls();
        _discord.ClearReceivedCalls();

        await _service.UpdateAsync(id, Input(decorationId: Silver, on: new DateTime(2026, 9, 30), citation: "Neuer Text."),
            Leader());

        Assert.Equal("Neuer Text.", Award(id).Citation);
        Assert.Equal(new DateTime(2026, 9, 30), Award(id).AwardedOn);
        Assert.Equal(Silver, Award(id).DecorationId);
        await _announcements.Received(1).RefreshAsync("ann-1",
            Arg.Is<AnnouncementInput>(a => a.Title == "Ehrung: Tapferkeitsmedaille für Falke" && a.Content!.Contains("Neuer Text.")),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        await _announcements.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
        await _discord.DidNotReceiveWithAnyArgs()
            .PushDecorationAsync(default!, default!, default!, default, default!, default, default, default);
        Assert.Single(Notices());
    }

    [Fact]
    public async Task Switching_the_decoration_is_logged_by_name()
    {
        var id = await _service.AwardAsync(Input(), Leader());

        await _service.UpdateAsync(id, Input(decorationId: Silver), Leader());

        using var db = _ctx.NewContext();
        var row = Assert.Single(db.AuditLogs.AsNoTracking()
            .Where(l => l.EntityType == nameof(DecorationAward) && l.EntityId == id && l.ChangesJson!.Contains("Ehrenzeichen"))
            .ToList());
        Assert.Contains("Verdienstmedaille", row.ChangesJson);
        Assert.Contains("Tapferkeitsmedaille", row.ChangesJson);
    }

    [Fact]
    public async Task A_correction_cannot_move_the_day_into_the_future()
    {
        var id = await _service.AwardAsync(Input(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateAsync(id, Input(on: DateTime.Today.AddDays(1)), Leader()));
    }

    [Fact]
    public async Task A_switched_off_decoration_stays_on_its_award_but_cannot_be_newly_chosen()
    {
        var id = await _service.AwardAsync(Input(), Leader());
        SwitchOff(Gold);

        await _service.UpdateAsync(id, Input(citation: "Korrigiert."), Leader());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateAsync(id, Input(decorationId: Off), Leader()));
        Assert.Equal(Gold, Award(id).DecorationId);
    }

    [Fact]
    public async Task Revoking_needs_a_reason_and_keeps_the_row_struck_through()
    {
        var id = await _service.AwardAsync(Input(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RevokeAsync(id, " ", Leader()));
        await _service.RevokeAsync(id, "Verfehlung im Dienst", Leader());

        var view = Assert.Single(await _service.GetForAgentAsync("junior", Colleague()));
        Assert.True(view.IsRevoked);
        Assert.Equal("Verfehlung im Dienst", view.RevocationReason);
        Assert.Equal("Adler", view.RevokedByName);
        await _announcements.Received(1).DeleteAsync("ann-1", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_revoked_award_leaves_plaques_dashboard_and_certificate()
    {
        var id = await _service.AwardAsync(Input(), Leader());
        await _service.RevokeAsync(id, "Irrtum", Leader());

        Assert.Empty(await _service.GetPlaquesAsync(["junior"], Colleague()));
        Assert.Empty(await _service.GetRecentAsync(Colleague()));
        Assert.Null(await _service.GetCertificateAsync(id, Leader()));
        Assert.Null(await _service.GetCertificateAsync(id, Junior()));
    }

    [Fact]
    public async Task A_revoked_award_can_be_neither_corrected_nor_revoked_again()
    {
        var id = await _service.AwardAsync(Input(), Leader());
        await _service.RevokeAsync(id, "Irrtum", Leader());
        var revokedAt = Award(id).RevokedAt;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(id, Input(), Leader()));
        await _service.RevokeAsync(id, "Nochmal", Leader());

        Assert.Equal(revokedAt, Award(id).RevokedAt);
        Assert.Equal("Irrtum", Award(id).RevocationReason);
    }

    [Fact]
    public async Task Nobody_corrects_or_revokes_his_own_decoration()
    {
        var id = await _service.AwardAsync(Input("lead2"), Leader());
        var lead2 = ClaimsPrincipalBuilder.Agent("lead2").WithRank(Rank.SectionChief).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(id, Input("lead2"), lead2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RevokeAsync(id, "weg damit", lead2));
        Assert.Null(Award(id).RevokedAt);
    }

    // ---- reading ----

    [Fact]
    public async Task Partners_read_nothing()
    {
        await _service.AwardAsync(Input(), Leader());

        Assert.Empty(await _service.GetForAgentAsync("junior", Partner()));
        Assert.Empty(await _service.GetPlaquesAsync(["junior"], Partner()));
        Assert.Empty(await _service.GetRecentAsync(Partner()));
        Assert.Empty(await _service.GetCatalogAsync(Partner()));
    }

    [Fact]
    public async Task The_other_write_paths_stop_plain_agents_supervision_and_demo()
    {
        var id = await _service.AwardAsync(Input(), Leader());

        foreach (var actor in new[] { Colleague(), Supervision(), Demo() })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UpdateAsync(id, Input(), actor));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RevokeAsync(id, "weg", actor));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.DeleteDecorationAsync(Off, actor));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", ""), actor));
        }
        Assert.Null(Award(id).RevokedAt);
        Assert.Equal(3, (await _service.GetCatalogAsync(Colleague())).Count);
    }

    [Fact]
    public async Task The_dashboard_drops_an_agent_who_has_since_become_a_team_lead()
    {
        await _service.AwardAsync(Input(), Leader());
        using (var db = _ctx.NewContext())
        {
            db.Users.Single(u => u.Id == "junior").IsTeamLead = true;
            db.SaveChanges();
        }

        Assert.Empty(await _service.GetRecentAsync(Colleague()));
    }

    [Fact]
    public async Task The_dashboard_shows_at_most_five()
    {
        for (var i = 0; i < DecorationRules.RecentMax + 2; i++)
        {
            await _service.AwardAsync(Input(on: DateTime.Today.AddDays(-i)), Leader());
        }

        Assert.Equal(DecorationRules.RecentMax, (await _service.GetRecentAsync(Colleague())).Count);
    }

    [Fact]
    public async Task The_dashboard_lists_the_newest_awards_of_the_last_weeks_only()
    {
        await _service.AwardAsync(Input(on: DateTime.Today.AddDays(-DecorationRules.RecentDays - 1)), Leader());
        var fresh = await _service.AwardAsync(Input(on: DateTime.Today.AddDays(-2)), Leader());

        var recent = Assert.Single(await _service.GetRecentAsync(Colleague()));
        Assert.Equal(fresh, recent.AwardId);
        Assert.Equal("Falke", recent.Codename);
    }

    // ---- certificates ----

    [Fact]
    public async Task Leadership_and_the_recipient_get_the_certificate_with_the_real_name_others_get_nothing()
    {
        var id = await _service.AwardAsync(Input(), Leader());

        var forLeader = await _service.GetCertificateAsync(id, Leader());
        var forRecipient = await _service.GetCertificateAsync(id, Junior());
        var forSupervision = await _service.GetCertificateAsync(id, Supervision());

        Assert.Equal("Max Falk", forLeader!.RealName);
        Assert.Equal("Max Falk", forRecipient!.RealName);
        Assert.NotNull(forSupervision);
        Assert.Null(forSupervision!.RealName);
        Assert.Null(await _service.GetCertificateAsync(id, Colleague()));
        Assert.Equal(CertificateKind.Decoration, forLeader.Kind);
        Assert.Equal("Verdienstmedaille", forLeader.DecorationName);
        Assert.Equal(Rank.Trainee, forLeader.Rank);
    }

    [Fact]
    public async Task The_rank_history_prints_appointments_and_promotions_never_demotions()
    {
        SeedHistory("h-start", null, Rank.Trainee);
        SeedHistory("h-up", Rank.Trainee, Rank.SpecialAgent);
        SeedHistory("h-down", Rank.SpecialAgent, Rank.Trainee);
        await _service.SaveCertificateSettingsAsync(new CertificateSettings("dd", ""), Leader());

        var appointment = await _service.GetRankCertificateAsync("h-start", Junior());
        var promotion = await _service.GetRankCertificateAsync("h-up", Leader());

        Assert.Equal(CertificateKind.Appointment, appointment!.Kind);
        Assert.Equal(Rank.Trainee, appointment.Rank);
        Assert.Equal(CertificateKind.Promotion, promotion!.Kind);
        Assert.Equal(Rank.SpecialAgent, promotion.Rank);
        Assert.Equal("Codename-dd", promotion.SignatoryName);
        Assert.Null(await _service.GetRankCertificateAsync("h-down", Leader()));
        Assert.Null(await _service.GetRankCertificateAsync("h-up", Colleague()));
    }

    // ---- catalog ----

    [Fact]
    public async Task An_awarded_decoration_stays_in_the_catalog_an_unused_one_can_go()
    {
        await _service.AwardAsync(Input(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteDecorationAsync(Gold, Leader()));
        await _service.DeleteDecorationAsync(Off, Leader());

        Assert.Equal([Gold, Silver], (await _service.GetCatalogAsync(Colleague())).Select(d => d.Id).ToList());
    }

    [Fact]
    public async Task The_catalog_refuses_a_twin_name_and_an_unknown_symbol()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveDecorationAsync(
            new DecorationInput { Name = "verdienstmedaille", Symbol = "stern" }, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveDecorationAsync(
            new DecorationInput { Name = "Neu", Symbol = "kaputt" }, Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SaveDecorationAsync(
            new DecorationInput { Name = "Neu", Symbol = "stern" }, Junior()));

        var id = await _service.SaveDecorationAsync(
            new DecorationInput { Name = "Lebensretter", Symbol = "herz", Tier = DecorationTier.Red }, Leader());
        Assert.Equal(4, (await _service.GetCatalogAsync(Colleague())).Count);
        Assert.Contains(await _service.GetCatalogAsync(Colleague()), d => d.Id == id && d.Tier == DecorationTier.Red);
    }

    // ---- helpers ----

    private void SeedRoster()
    {
        using var db = _ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead", Rank.Director, configure: a => a.Codename = "Adler"),
            Seed.Agent("lead2", Rank.SpecialAgentInCharge),
            Seed.Agent("dd", Rank.DeputyDirector),
            Seed.Agent("sc-cid", Rank.SectionChief, configure: a => a.Division = Division.Cid),
            Seed.Agent("sc-hrt", Rank.SectionChief, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; }),
            Seed.Agent("hrt-agent", Rank.SpecialAgent, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; }),
            Seed.Agent("junior", Rank.Trainee, configure: a =>
            {
                a.Codename = "Falke";
                a.RealName = "Max Falk";
            }),
            Seed.Agent("other", Rank.SeniorSpecialAgent),
            Seed.Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Terminated),
            Seed.Agent(DemoIdentity.AgentId, Rank.Director));
        db.Decorations.AddRange(
            new Decoration { Id = Gold, Name = "Verdienstmedaille", Symbol = "medaille", Tier = DecorationTier.Gold, Order = 10 },
            new Decoration { Id = Silver, Name = "Tapferkeitsmedaille", Symbol = "stern", Tier = DecorationTier.Silver, Order = 15 },
            new Decoration { Id = Off, Name = "Altes Abzeichen", Symbol = "stern", Tier = DecorationTier.Blue, Order = 20, IsActive = false });
        db.SaveChanges();
    }

    private void SeedHistory(string id, Rank? previous, Rank conferred, string agentId = "junior")
    {
        using var db = _ctx.NewContext();
        db.AgentRankHistories.Add(new AgentRankHistory
        {
            Id = id,
            AgentId = agentId,
            Alt = previous,
            New = conferred,
            Timestamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            ActorName = "Adler",
        });
        db.SaveChanges();
    }

    private void Terminate(string agentId)
    {
        using var db = _ctx.NewContext();
        db.Users.Single(u => u.Id == agentId).Status = AgentStatus.Terminated;
        db.SaveChanges();
    }

    private void Unterminate(string agentId)
    {
        using var db = _ctx.NewContext();
        db.Users.Single(u => u.Id == agentId).Status = AgentStatus.Active;
        db.SaveChanges();
    }

    private void MoveTo(string agentId, Division division)
    {
        using var db = _ctx.NewContext();
        var agent = db.Users.Single(u => u.Id == agentId);
        agent.Division = division;
        agent.IsTRU = division == Division.Hrt;
        db.SaveChanges();
    }

    private void SwitchOff(string decorationId)
    {
        using var db = _ctx.NewContext();
        db.Decorations.Single(d => d.Id == decorationId).IsActive = false;
        db.SaveChanges();
    }

    private List<DecorationAward> Awards()
    {
        using var db = _ctx.NewContext();
        return db.DecorationAwards.AsNoTracking().ToList();
    }

    private DecorationAward Award(string id) => Awards().Single(a => a.Id == id);

    private List<NOOSE_Website.Data.Entities.Notifications.Notification> Notices()
    {
        using var db = _ctx.NewContext();
        return db.Notifications.AsNoTracking().ToList();
    }
}
