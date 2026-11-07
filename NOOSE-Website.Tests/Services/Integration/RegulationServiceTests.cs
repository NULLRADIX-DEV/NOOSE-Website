using System.Data.Common;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Regulation;
using NOOSE_Website.Services;
using NSubstitute;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="RegulationService"/> over in-memory SQLite.</summary>
public sealed class RegulationServiceTests
{
    /// <summary>Boss (Director, Board) and a trainee, plus one live section.</summary>
    private static async Task<string> SeedBasicsAsync(SqliteTestContext ctx, params NOOSE_Website.Data.Entities.Agent[] more)
    {
        await SeedAgentsAsync(ctx, [Seed.Agent("boss", Rank.Director), .. more]);
        return await SeedSectionAsync(ctx);
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_by_the_board_writes_version_one_and_acknowledges_it_for_the_publisher()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);

        var id = await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), Boss());

        await using var db = ctx.NewContext();
        var head = await db.RegulationParagraphs.SingleAsync();
        Assert.Equal(id, head.Id);
        Assert.Equal(section, head.SectionId);
        Assert.Equal(1, head.CurrentVersion);
        Assert.Equal(1, head.AcknowledgeFromVersion);
        Assert.Equal("Funkdisziplin", head.Heading);
        Assert.Equal(RegulationAudience.Ctd, head.Audience);
        var version = await db.RegulationVersions.SingleAsync();
        Assert.Equal((id, 1), (version.ParagraphId, version.Version));
        Assert.True(version.RequiresAcknowledgment);
        Assert.Equal(RegulationAudience.Ctd, version.Audience);
        Assert.Equal("Funkdisziplin", version.Heading);
        Assert.Equal(("boss", "Falke"), (version.PublishedById, version.PublishedByName));
        Assert.Equal(Today.AddDays(-1), version.ValidFrom);
        Assert.Contains("Eins zwei drei.", version.ContentHtml);
        Assert.Equal("Erstfassung", version.ChangeNote);
        var ack = await db.RegulationAcknowledgments.SingleAsync();
        Assert.Equal((id, 1, "boss"), (ack.ParagraphId, ack.Version, ack.AgentId));
    }

    [Fact]
    public async Task New_paragraphs_go_to_the_end_of_their_section()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);

        await svc.CreateParagraphAsync(section, Input("Eins"), Boss());
        await svc.CreateParagraphAsync(section, Input("Zwei"), Boss());
        await svc.CreateParagraphAsync(section, Input("Drei"), Boss());

        var document = await svc.GetDocumentAsync(Boss());
        Assert.Equal(["Eins", "Zwei", "Drei"], Assert.Single(document.Sections).Paragraphs.Select(p => p.Heading));
    }

    [Fact]
    public async Task Create_below_leadership_or_by_supervision_demo_or_partner_is_refused_before_anything_is_written()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("sac", Rank.SpecialAgentInCharge),
            Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true));
        var (svc, _, _) = Build(ctx);
        ClaimsPrincipal[] refused =
        [
            ClaimsPrincipalBuilder.Agent("sac").WithRank(Rank.SpecialAgentInCharge).Build(),
            Supervision(),
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().AsAdmin().Build(),
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build(),
            Partner(),
        ];

        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateParagraphAsync(section, Input(), actor));
        }

        await using var db = ctx.NewContext();
        Assert.Empty(await db.RegulationParagraphs.ToListAsync());
        Assert.Empty(await db.RegulationVersions.ToListAsync());
        Assert.Empty(await db.RegulationAcknowledgments.ToListAsync());
    }

    [Theory]
    [InlineData("<p><br></p>")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_with_an_empty_text_is_refused(string html)
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateParagraphAsync(section, Input(html: html), Boss()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.RegulationParagraphs.ToListAsync());
    }

    [Fact]
    public async Task Create_refuses_bad_limits_an_unknown_audience_and_a_missing_section()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);

        async Task RefusedAsync(RegulationParagraphInput input, string sectionId = "s1")
            => await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateParagraphAsync(sectionId, input, Boss()));

        await RefusedAsync(Input() with { Heading = new string('T', RegulationRules.MaxHeading + 1) });
        await RefusedAsync(Input() with { Heading = "  " });
        await RefusedAsync(Input() with { ChangeNote = "" });
        await RefusedAsync(Input() with { ChangeNote = new string('N', RegulationRules.MaxChangeNote + 1) });
        await RefusedAsync(Input() with { Audience = (RegulationAudience)42 });
        await RefusedAsync(Input(), "gibt-es-nicht");
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(await db.RegulationParagraphs.ToListAsync());
        }

        // the limits themselves are fine
        await svc.CreateParagraphAsync(section, Input() with
        {
            Heading = new string('T', RegulationRules.MaxHeading),
            ChangeNote = new string('N', RegulationRules.MaxChangeNote),
        }, Boss());
        await using var check = ctx.NewContext();
        Assert.Single(await check.RegulationParagraphs.ToListAsync());
    }

    // ---------- fine gate ----------

    [Fact]
    public async Task A_section_chief_writes_only_paragraphs_of_his_own_division()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Cid));
        var (svc, _, _) = Build(ctx);
        var chief = Chief("sc", Division.Cid);

        var own = await svc.CreateParagraphAsync(section, Input("CID-Regel", RegulationAudience.Cid), chief);
        foreach (var audience in new[] { RegulationAudience.Bureau, RegulationAudience.Administration, RegulationAudience.Ctd, RegulationAudience.Hrt })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateParagraphAsync(section, Input(audience: audience), chief));
        }

        // a paragraph of another unit stays out of reach in every write
        var other = await svc.CreateParagraphAsync(section, Input("CTD-Regel", RegulationAudience.Ctd), Boss());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PublishVersionAsync(other, Next(1, RegulationAudience.Ctd), chief));
        // old audience foreign, new audience his own: still refused
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PublishVersionAsync(other, Next(1, RegulationAudience.Cid), chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.MoveParagraphAsync(other, -1, chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepealParagraphAsync(other, "überholt", chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteParagraphAsync(other, chief));

        // his own paragraph may not leave his unit either
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PublishVersionAsync(own, Next(1, RegulationAudience.Bureau), chief));
        await svc.PublishVersionAsync(own, Next(1, RegulationAudience.Cid), chief);

        await using var db = ctx.NewContext();
        Assert.Equal(2, (await db.RegulationParagraphs.SingleAsync(p => p.Id == own)).CurrentVersion);
        var untouched = await db.RegulationParagraphs.SingleAsync(p => p.Id == other);
        Assert.Equal((1, RegulationAudience.Ctd), (untouched.CurrentVersion, untouched.Audience));
        Assert.Null(untouched.RepealedAt);
        // nor do sections
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateSectionAsync("Neu", false, chief));
    }

    [Fact]
    public async Task A_section_chief_with_the_administration_flag_writes_administration_paragraphs()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("hrb", Rank.SectionChief, configure: a => { a.Division = Division.Ctd; a.IsHRB = true; }),
            Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Ctd));
        var (svc, _, _) = Build(ctx);

        var id = await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Administration), Chief("hrb", Division.Ctd, hrb: true));
        // from Administration to his own division is fine, both are his
        await svc.PublishVersionAsync(id, Next(1, RegulationAudience.Ctd), Chief("hrb", Division.Ctd, hrb: true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Administration), Chief("sc", Division.Ctd)));
    }

    [Fact]
    public async Task The_section_chief_guard_reads_the_standing_fresh_from_the_database()
    {
        using var ctx = new SqliteTestContext();
        // moved to the CTD a moment ago; the session still says CID
        var section = await SeedBasicsAsync(ctx, Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Ctd));
        var (svc, _, _) = Build(ctx);
        var stale = Chief("sc", Division.Cid);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Cid), stale));
        Assert.Equal(RegulationRules.EditRefusal(RegulationAudience.Cid), ex.Message);
        await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), stale);

        // demoted meanwhile: nothing at all, although the claims still pass the coarse gate
        await SetAgentAsync(ctx, "sc", a => a.Rank = Rank.SpecialAgentInCharge);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), stale));
        // and a terminated account has no standing either
        await SetAgentAsync(ctx, "sc", a => { a.Rank = Rank.SectionChief; a.Status = AgentStatus.Terminated; });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), stale));

        await using var db = ctx.NewContext();
        Assert.Equal(RegulationAudience.Ctd, (await db.RegulationParagraphs.SingleAsync()).Audience);
    }

    [Fact]
    public async Task An_admin_of_any_rank_writes_every_audience_and_the_sections()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("adm", Rank.Trainee, configure: a => a.IsAdmin = true));
        var (svc, _, _) = Build(ctx);
        var admin = ClaimsPrincipalBuilder.Agent("adm").WithRank(Rank.Trainee).AsAdmin().Build();

        foreach (var audience in RegulationAudienceDisplay.All)
        {
            await svc.CreateParagraphAsync(section, Input(audience: audience), admin);
        }
        await svc.CreateSectionAsync("Neu", false, admin);

        await using var db = ctx.NewContext();
        Assert.Equal(5, await db.RegulationParagraphs.CountAsync());
        Assert.Equal(2, await db.RegulationSections.CountAsync());
    }

    // ---------- bell and live badge ----------

    [Fact]
    public async Task Publishing_rings_every_owing_agent_but_the_publisher()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.SpecialAgent),
            Seed.Agent("chief", Rank.SectionChief));
        var (svc, notifications, reported) = Build(ctx);

        var id = await svc.CreateParagraphAsync(section, Input(), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "a", "b", "chief" })),
            NotificationType.RegulationChanged, "Neuer Absatz in der Dienstverordnung: DVO Abschnitt 1 · Funkdisziplin",
            $"/dienstverordnung/absatz/{id}", "boss", Arg.Any<CancellationToken>());
        Assert.Equal(["a", "b", "chief"], reported.Order().ToList());

        // a new version that asks again rings the same agents
        notifications.ClearReceivedCalls();
        await svc.PublishVersionAsync(id, Next(1), Boss());
        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "a", "b", "chief" })),
            NotificationType.RegulationChanged, "Dienstverordnung geändert: DVO Abschnitt 1 · Funkdisziplin",
            $"/dienstverordnung/absatz/{id}", "boss", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_division_paragraph_rings_only_its_division()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("cid", Rank.Trainee),
            Seed.Agent("ctd", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd),
            Seed.Agent("ad", Rank.AssistantDirector, configure: a => a.Division = Division.Ctd));
        var (svc, notifications, _) = Build(ctx);

        await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { "ctd" })),
            NotificationType.RegulationChanged, Arg.Any<string>(), Arg.Any<string?>(), "boss", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_bell_title_names_the_heading_never_the_wording()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, notifications, _) = Build(ctx);

        var id = await svc.CreateParagraphAsync(section, Input(html: "<p>Geheimer Wortlaut</p>"), Boss());
        await svc.PublishVersionAsync(id, Next(1, html: "<p>Noch geheimerer Wortlaut</p>"), Boss());

        await notifications.Received(2).NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.RegulationChanged, Arg.Is<string>(t => t.Contains("Funkdisziplin")), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<NotificationType>(), Arg.Is<string>(t => t.Contains("Wortlaut") || t.Contains('<')), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_bell_does_not_undo_the_paragraph_and_still_reports_the_badge()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, notifications, reported) = Build(ctx);
        notifications.NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Glocke defekt")));

        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        Assert.Equal(["a"], reported);
        reported.Clear();
        await svc.PublishVersionAsync(id, Next(1), Boss());

        await using var db = ctx.NewContext();
        Assert.Equal(2, (await db.RegulationParagraphs.SingleAsync(p => p.Id == id)).CurrentVersion);
        Assert.Equal(["a"], reported);
    }

    // ---------- publish ----------

    [Fact]
    public async Task A_new_version_with_acknowledgment_reopens_the_debt_for_everyone()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee), Seed.Agent("b", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));
        Assert.Equal(["b"], await OwingAsync(ctx, id));

        await svc.PublishVersionAsync(id, Next(1, heading: "Funkdisziplin neu"), Boss());

        Assert.Equal(["a", "b"], await OwingAsync(ctx, id));
        var open = Assert.Single(await svc.GetOpenForMeAsync(Plain("a")));
        Assert.Equal(2, open.Version);
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin neu", open.Label);
        await using var db = ctx.NewContext();
        var head = await db.RegulationParagraphs.SingleAsync();
        Assert.Equal((2, 2, "Funkdisziplin neu"), (head.CurrentVersion, head.AcknowledgeFromVersion, head.Heading));
        // the first version keeps its own heading
        Assert.Equal("Funkdisziplin", (await db.RegulationVersions.SingleAsync(v => v.Version == 1)).Heading);
    }

    [Fact]
    public async Task A_correction_keeps_every_earlier_acknowledgment_valid_and_rings_nobody()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee), Seed.Agent("b", Rank.Trainee));
        var (svc, notifications, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));
        notifications.ClearReceivedCalls();

        await svc.PublishVersionAsync(id, Next(1, requires: false), Boss());

        await RingsNobodyAsync(notifications);
        await using var db = ctx.NewContext();
        var head = await db.RegulationParagraphs.SingleAsync();
        Assert.Equal((2, 1), (head.CurrentVersion, head.AcknowledgeFromVersion));
        Assert.False((await db.RegulationVersions.SingleAsync(v => v.Version == 2)).RequiresAcknowledgment);
        // a is still square, b still owes the first version but reads the current one
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(1, Assert.Single(await svc.GetOpenForMeAsync(Plain("b"))).Version);
        var shown = await svc.GetParagraphAsync(id, null, Plain("b"));
        Assert.True(shown!.MustAcknowledge);
        // b may acknowledge the current version, which covers the first
        await svc.AcknowledgeAsync(id, 2, Plain("b"));
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("b")));
    }

    [Fact]
    public async Task A_correction_that_changes_the_audience_rings_only_the_newcomers()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("cid-done", Rank.Trainee),
            Seed.Agent("cid-open", Rank.Trainee),
            Seed.Agent("ctd", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd),
            Seed.Agent("hrt", Rank.SpecialAgent, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; }));
        var (svc, notifications, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Cid), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("cid-done"));
        Assert.Equal(["cid-open"], await OwingAsync(ctx, id));
        notifications.ClearReceivedCalls();
        reported.Clear();

        await svc.PublishVersionAsync(id, Next(1, RegulationAudience.Bureau, requires: false), Boss());

        // the CID agent still owing was rung before; the publisher never
        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "ctd", "hrt" })),
            NotificationType.RegulationChanged, "Dienstverordnung geändert: DVO Abschnitt 1 · Funkdisziplin",
            $"/dienstverordnung/absatz/{id}", "boss", Arg.Any<CancellationToken>());
        // the old and the new debtors recount
        Assert.Equal(["cid-open", "ctd", "hrt"], reported.Order().ToList());
        Assert.Equal(["cid-open", "ctd", "hrt"], await OwingAsync(ctx, id));

        // narrowing again rings nobody and ends the debt of the ones left out
        notifications.ClearReceivedCalls();
        reported.Clear();
        await svc.PublishVersionAsync(id, Next(2, RegulationAudience.Hrt, requires: false), Boss());
        await RingsNobodyAsync(notifications);
        Assert.Equal(["hrt"], await OwingAsync(ctx, id));
        Assert.Equal(["cid-open", "ctd", "hrt"], reported.Order().ToList());
        await using var db = ctx.NewContext();
        Assert.Equal([RegulationAudience.Cid, RegulationAudience.Bureau, RegulationAudience.Hrt],
            await db.RegulationVersions.OrderBy(v => v.Version).Select(v => v.Audience).ToListAsync());
    }

    [Fact]
    public async Task Publishing_on_a_stale_version_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("lead-1", Rank.Director), Seed.Agent("lead-2", Rank.Director));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss("lead-1"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, html: "<p>Anderer Text.</p>"), Boss("lead-2")));

        Assert.Equal(StaleText, ex.Message);
        await using var db = ctx.NewContext();
        Assert.Equal([1, 2], await db.RegulationVersions.OrderBy(v => v.Version).Select(v => v.Version).ToListAsync());
        Assert.Equal(2, (await db.RegulationParagraphs.SingleAsync()).CurrentVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeal_or_trash_between_load_and_publish_makes_the_publish_match_no_row(bool repeal)
    {
        // the publisher's row was loaded before the other leader committed
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.Add(SectionRow("s1", 10));
            seed.RegulationParagraphs.Add(ParagraphRow("p1", "s1"));
            await seed.SaveChangesAsync();
        }
        await using var publisher = ctx.NewContext();
        var stale = await publisher.RegulationParagraphs.SingleAsync(p => p.Id == "p1");

        await using (var other = ctx.NewContext())
        {
            var head = await other.RegulationParagraphs.SingleAsync(p => p.Id == "p1");
            if (repeal)
                head.RepealedAt = Stamp;
            else
                head.IsDeleted = true;
            await other.SaveChangesAsync();
        }

        stale.CurrentVersion = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => publisher.SaveChangesAsync());
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("move")]
    [InlineData("repeal")]
    [InlineData("delete")]
    [InlineData("restore")]
    public async Task A_write_on_a_stale_row_reads_as_a_newer_version(string action)
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.Add(SectionRow("s1", 10));
            seed.RegulationParagraphs.AddRange(
                ParagraphRow("p1", "s1", configure: p => p.IsDeleted = action == "restore"),
                ParagraphRow("p2", "s1", configure: p => p.SortOrder = 20));
            seed.RegulationVersions.Add(VersionRow("p1", 1));
            await seed.SaveChangesAsync();
        }
        var rival = new RivalPublish();
        var (svc, _, _) = Build(ctx, rival);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            "publish" => svc.PublishVersionAsync("p1", Next(1), Boss()),
            "move" => svc.MoveParagraphAsync("p1", 1, Boss()),
            "repeal" => svc.RepealParagraphAsync("p1", "überholt", Boss()),
            "delete" => svc.DeleteParagraphAsync("p1", Boss()),
            _ => svc.RestoreAsync("p1", Boss()),
        });

        Assert.True(rival.Fired);
        // a restore touches the section, so its refusal names the section
        Assert.Equal(action == "restore" ? "Der Abschnitt wurde inzwischen geändert oder gelöscht. Lade die Seite neu." : StaleText, ex.Message);
        await using var db = ctx.NewContext();
        var head = await db.RegulationParagraphs.IgnoreQueryFilters().SingleAsync(p => p.Id == "p1");
        Assert.Null(head.RepealedAt);
        Assert.Equal(action == "restore", head.IsDeleted);
        // no version slipped in with the failed save
        Assert.Single(await db.RegulationVersions.ToListAsync());
    }

    [Fact]
    public async Task A_version_dated_before_its_predecessor_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, validFrom: Today.AddDays(-2)), Boss()));
        // the same day and later are fine
        await svc.PublishVersionAsync(id, Next(1, validFrom: Today.AddDays(-1)), Boss());
        await svc.PublishVersionAsync(id, Next(2, validFrom: Today.AddDays(30)), Boss());

        await using var db = ctx.NewContext();
        Assert.Equal(3, (await db.RegulationParagraphs.SingleAsync()).CurrentVersion);
    }

    [Fact]
    public async Task The_page_shows_heading_and_audience_of_the_version_it_shows()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));
        // a correction that renames and narrows, in force only next week
        await svc.PublishVersionAsync(id, Next(1, RegulationAudience.Hrt, requires: false,
            validFrom: Today.AddDays(7), heading: "Funkverkehr"), Boss());

        var row = (await svc.GetDocumentAsync(Plain("a"))).Sections.SelectMany(s => s.Paragraphs).Single();

        Assert.Equal(1, row.ShownVersion);
        Assert.Equal("Funkdisziplin", row.Heading);
        Assert.Equal(RegulationAudience.Bureau, row.Audience);
    }

    [Fact]
    public async Task A_repealed_paragraph_takes_no_new_version_no_move_and_no_acknowledgment()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.RepealParagraphAsync(id, "überholt", Boss());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishVersionAsync(id, Next(1), Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveParagraphAsync(id, 1, Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 1, Plain("a")));
        // repealed is gone, not changed: nobody owes it any more
        Assert.Equal(new RegulationAcknowledgeResult(0, 0),
            await svc.AcknowledgeAllAsync([new RegulationSeen(id, 1)], Plain("a")));

        await using var db = ctx.NewContext();
        Assert.Single(await db.RegulationVersions.ToListAsync());
        // the publisher's own row only
        Assert.Equal("boss", (await db.RegulationAcknowledgments.SingleAsync()).AgentId);
    }

    [Fact]
    public async Task Writing_a_missing_paragraph_reads_as_not_found()
    {
        using var ctx = new SqliteTestContext();
        await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishVersionAsync("gibt-es-nicht", Next(1), Boss()));

        Assert.Equal("Absatz nicht gefunden.", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync("gibt-es-nicht", Boss()));
    }

    // ---------- acknowledge ----------

    [Fact]
    public async Task Acknowledge_records_the_current_version_and_reports_the_agent()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        reported.Clear();

        await svc.AcknowledgeAsync(id, 1, Plain("me"));

        Assert.Equal(["me"], reported);
        await using var db = ctx.NewContext();
        var mine = await db.RegulationAcknowledgments.SingleAsync(a => a.AgentId == "me");
        Assert.Equal((id, 1), (mine.ParagraphId, mine.Version));
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("me")));
    }

    [Fact]
    public async Task Acknowledging_a_version_that_is_not_current_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss());
        reported.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 1, Plain("me")));

        Assert.Equal("Inzwischen gibt es eine neuere Fassung. Lies sie und nimm sie dann zur Kenntnis.", ex.Message);
        Assert.Empty(reported);
        await using var db = ctx.NewContext();
        Assert.False(await db.RegulationAcknowledgments.AnyAsync(a => a.AgentId == "me"));
        // a version that does not exist yet is no better
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 3, Plain("me")));
    }

    [Fact]
    public async Task Acknowledge_twice_is_harmless()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        reported.Clear();

        await svc.AcknowledgeAsync(id, 1, Plain("me"));
        await svc.AcknowledgeAsync(id, 1, Plain("me"));
        Assert.Equal(new RegulationAcknowledgeResult(1, 0), await svc.AcknowledgeAllAsync([new RegulationSeen(id, 1)], Plain("me")));

        await using var db = ctx.NewContext();
        Assert.Single(await db.RegulationAcknowledgments.Where(a => a.AgentId == "me").ToListAsync());
        Assert.Equal(["me"], reported);
    }

    [Fact]
    public async Task A_double_click_that_lands_on_the_unique_index_is_retried_quietly()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var id = await Build(ctx).Svc.CreateParagraphAsync(section, Input(), Boss());
        // the second click commits right before the first one saves
        var rival = new RivalAcknowledgment(ctx, id, "me");
        var (svc, _, _) = Build(ctx, rival);

        await svc.AcknowledgeAsync(id, 1, Plain("me"));

        Assert.True(rival.Fired);
        await using var db = ctx.NewContext();
        Assert.Single(await db.RegulationAcknowledgments.Where(a => a.AgentId == "me").ToListAsync());
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("me")));
    }

    [Fact]
    public async Task Acknowledge_by_supervision_partner_or_demo_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.AcknowledgeAsync(id, 1, ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Trainee).AsDemo().Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAllAsync([new RegulationSeen(id, 1)], Partner()));

        await using var db = ctx.NewContext();
        // the publisher's own row only
        Assert.Single(await db.RegulationAcknowledgments.ToListAsync());
    }

    [Fact]
    public async Task Acknowledge_checks_the_account_in_the_database()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("gone", Rank.Trainee, AgentStatus.Terminated),
            // the flag came after the login
            Seed.Agent("tl", Rank.Trainee, configure: a => a.IsTeamLead = true));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());

        // the session still carries the old claims
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Plain("gone")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Plain("tl")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAllAsync([new RegulationSeen(id, 1)], Plain("gone")));
        // no row at all
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Plain("ghost")));

        await using var db = ctx.NewContext();
        Assert.Equal(["boss"], await db.RegulationAcknowledgments.Select(a => a.AgentId).ToListAsync());
    }

    [Fact]
    public async Task Acknowledge_all_takes_exactly_the_versions_seen_and_leaves_a_changed_one_open()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var first = await svc.CreateParagraphAsync(section, Input("Eins"), Boss());
        var second = await svc.CreateParagraphAsync(section, Input("Zwei"), Boss());
        var third = await svc.CreateParagraphAsync(section, Input("Drei"), Boss());

        // the page as the agent loaded it
        var seen = (await svc.GetDocumentAsync(Plain("me"))).OpenSeen;
        Assert.Equal(3, seen.Count);
        // meanwhile the second one changes, the third is corrected
        await svc.PublishVersionAsync(second, Next(1, heading: "Zwei"), Boss());
        await svc.PublishVersionAsync(third, Next(1, requires: false, heading: "Drei"), Boss());
        reported.Clear();

        // a duplicate in the list counts once
        var result = await svc.AcknowledgeAllAsync([.. seen, seen[0]], Plain("me"));

        Assert.Equal(new RegulationAcknowledgeResult(1, 2), result);
        Assert.Equal(["me"], reported);
        var open = (await svc.GetOpenForMeAsync(Plain("me"))).Select(o => o.ParagraphId).Order();
        Assert.Equal(new[] { second, third }.Order(), open);
        await using var db = ctx.NewContext();
        Assert.Equal([(first, 1)], (await db.RegulationAcknowledgments.Where(a => a.AgentId == "me")
            .Select(a => new { a.ParagraphId, a.Version }).ToListAsync()).Select(a => (a.ParagraphId, a.Version)));
        // nothing seen, nothing done
        Assert.Equal(new RegulationAcknowledgeResult(0, 0), await svc.AcknowledgeAllAsync([], Plain("me")));
    }

    [Fact]
    public async Task A_future_version_shows_its_predecessor_as_valid_but_takes_the_acknowledgment()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("me", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("me"));
        await svc.PublishVersionAsync(id, Next(1, html: "<p>Neuer Text ab nächster Woche.</p>", validFrom: Today.AddDays(7)), Boss());

        var shown = await svc.GetParagraphAsync(id, null, Plain("me"));

        Assert.NotNull(shown);
        Assert.Equal(1, shown.ShownVersion);
        Assert.Contains("Eins zwei drei.", shown.ShownHtml);
        Assert.Equal(1, shown.Row.ValidVersion);
        Assert.Equal(2, shown.Row.CurrentVersion);
        Assert.Equal([2, 1], shown.Versions.Select(v => v.Version));
        Assert.True(shown.MustAcknowledge);
        Assert.Equal(1, shown.MyAcknowledgedVersion);
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", shown.Label);
        // the page shows an owing reader the version to acknowledge, dated in the future
        var row = Assert.Single(Assert.Single((await svc.GetDocumentAsync(Plain("me"))).Sections).Paragraphs);
        Assert.Equal(2, row.ShownVersion);
        Assert.Equal(Today.AddDays(7), row.ShownValidFrom);
        Assert.Contains("Neuer Text", row.ShownHtml);

        // the newest version is the one to acknowledge, before its date
        await svc.AcknowledgeAsync(id, 2, Plain("me"));

        var after = await svc.GetParagraphAsync(id, 2, Plain("me"));
        Assert.NotNull(after);
        Assert.Equal(2, after.ShownVersion);
        Assert.Contains("Neuer Text", after.ShownHtml);
        Assert.False(after.MustAcknowledge);
        Assert.Equal(2, after.MyAcknowledgedVersion);
        Assert.NotNull(after.MyAcknowledgedAt);
        Assert.Null(await svc.GetParagraphAsync(id, 9, Plain("me")));
        // square again, the page falls back to the valid version
        Assert.Equal(1, Assert.Single(Assert.Single((await svc.GetDocumentAsync(Plain("me"))).Sections).Paragraphs).ShownVersion);
    }

    // ---------- repeal / delete / restore ----------

    [Fact]
    public async Task Repeal_needs_a_reason_ends_every_debt_and_stays_readable()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        reported.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealParagraphAsync(id, "", Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealParagraphAsync(id, "   ", Boss()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepealParagraphAsync(id, "überholt", Plain("a")));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));

        await svc.RepealParagraphAsync(id, "  Durch Absatz 2 ersetzt  ", Boss());

        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        await using var db = ctx.NewContext();
        var head = await db.RegulationParagraphs.SingleAsync();
        Assert.NotNull(head.RepealedAt);
        Assert.Equal("Durch Absatz 2 ersetzt", head.RepealReason);
        Assert.Equal(("boss", "Falke"), (head.RepealedById, head.RepealedByName));
        var document = await svc.GetDocumentAsync(Plain("a"));
        Assert.Empty(Assert.Single(document.Sections).Paragraphs);
        var repealed = Assert.Single(document.Repealed);
        Assert.True(repealed.IsRepealed);
        Assert.False(repealed.MustAcknowledge);
        Assert.Equal(head.RepealedAt, document.LastChangedAt);
        var detail = await svc.GetParagraphAsync(id, null, Plain("a"));
        Assert.Equal("Durch Absatz 2 ersetzt", detail?.Row.RepealReason);
        Assert.Equal("Falke", detail?.RepealedByName);
        // once is enough
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealParagraphAsync(id, "nochmal", Boss()));
    }

    [Fact]
    public async Task Delete_and_restore_move_the_debt_away_and_back()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteParagraphAsync(id, Plain("a")));
        reported.Clear();

        await svc.DeleteParagraphAsync(id, Boss());

        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        Assert.Empty(Assert.Single((await svc.GetDocumentAsync(Boss())).Sections).Paragraphs);
        var trash = Assert.Single(await svc.GetTrashAsync());
        Assert.Equal((id, false, "Funkdisziplin", "Allgemeines"), (trash.Id, trash.IsSection, trash.Title, trash.Detail));
        reported.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(id, Plain("a")));
        await svc.RestoreAsync(id, Boss());

        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        Assert.Empty(await svc.GetTrashAsync());
        // nothing to restore any more
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(id, Boss()));
    }

    [Fact]
    public async Task Restoring_a_paragraph_needs_its_section_back_first()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.DeleteParagraphAsync(id, Boss());
        // the section is empty now and may go too
        await svc.DeleteSectionAsync(section, Boss());
        Assert.Equal(2, (await svc.GetTrashAsync()).Count);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(id, Boss()));
        Assert.Equal("Stelle zuerst den Abschnitt des Absatzes wieder her.", ex.Message);
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));

        await svc.RestoreAsync(section, Boss());
        await svc.RestoreAsync(id, Boss());

        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Single(Assert.Single((await svc.GetDocumentAsync(Boss())).Sections).Paragraphs);
    }

    // ---------- sections ----------

    [Fact]
    public async Task Sections_are_created_renamed_and_moved_with_the_foreword_staying_first()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _, _) = Build(ctx);

        var first = await svc.CreateSectionAsync("Allgemeines", false, Boss());
        var second = await svc.CreateSectionAsync("Handlungsvorschriften", false, Boss());
        // created last, still shown first and never numbered
        var preamble = await svc.CreateSectionAsync("  Vorwort  ", true, Boss());

        async Task<string[]> CaptionsAsync()
            => (await svc.GetDocumentAsync(Boss())).Sections.Select(s => s.Caption).ToArray();

        Assert.Equal(["Vorwort", "Abschnitt 1: Allgemeines", "Abschnitt 2: Handlungsvorschriften"], await CaptionsAsync());

        await svc.MoveSectionAsync(second, -1, Boss());
        Assert.Equal(["Vorwort", "Abschnitt 1: Handlungsvorschriften", "Abschnitt 2: Allgemeines"], await CaptionsAsync());
        // at either end nothing moves, and the foreword is no numbered section
        await svc.MoveSectionAsync(second, -1, Boss());
        await svc.MoveSectionAsync(first, 1, Boss());
        Assert.Equal(["Vorwort", "Abschnitt 1: Handlungsvorschriften", "Abschnitt 2: Allgemeines"], await CaptionsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveSectionAsync(preamble, 1, Boss()));

        await svc.RenameSectionAsync(first, "Grundsätze", Boss());
        Assert.Equal("Abschnitt 2: Grundsätze", (await CaptionsAsync())[2]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RenameSectionAsync(first, "  ", Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RenameSectionAsync(first, new string('T', RegulationRules.MaxSectionTitle + 1), Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RenameSectionAsync("gibt-es-nicht", "X", Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateSectionAsync("", false, Boss()));
    }

    [Fact]
    public async Task Sections_are_the_boards_and_the_admins_alone()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Cid));
        var (svc, _, _) = Build(ctx);
        var chief = Chief("sc", Division.Cid);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateSectionAsync("Neu", false, chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RenameSectionAsync(section, "Neu", chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.MoveSectionAsync(section, 1, chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteSectionAsync(section, chief));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateSectionAsync("Neu", false, Plain("a")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateSectionAsync("Neu", false, Supervision()));

        await using var db = ctx.NewContext();
        Assert.Equal("Allgemeines", (await db.RegulationSections.SingleAsync()).Title);
    }

    [Fact]
    public async Task A_section_with_paragraphs_is_not_deleted_repealed_ones_included()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.RepealParagraphAsync(id, "überholt", Boss());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteSectionAsync(section, Boss()));
        Assert.Contains("Absätze", ex.Message, StringComparison.Ordinal);

        var empty = await svc.CreateSectionAsync("Leer", false, Boss());
        await svc.DeleteSectionAsync(empty, Boss());
        await using var db = ctx.NewContext();
        // soft delete
        Assert.True((await db.RegulationSections.IgnoreQueryFilters().SingleAsync(s => s.Id == empty)).IsDeleted);
        Assert.Equal([section], await db.RegulationSections.Select(s => s.Id).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteSectionAsync(empty, Boss()));
    }

    [Fact]
    public async Task A_paragraph_moves_within_its_section()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var other = await svc.CreateSectionAsync("Anderer", false, Boss());
        var a = await svc.CreateParagraphAsync(section, Input("A"), Boss());
        await svc.CreateParagraphAsync(section, Input("B"), Boss());
        var c = await svc.CreateParagraphAsync(section, Input("C"), Boss());
        await svc.CreateParagraphAsync(other, Input("X"), Boss());

        async Task<string[]> HeadingsAsync()
            => (await svc.GetDocumentAsync(Boss())).Sections.First(s => s.Id == section).Paragraphs.Select(p => p.Heading).ToArray();

        await svc.MoveParagraphAsync(c, -1, Boss());
        Assert.Equal(["A", "C", "B"], await HeadingsAsync());
        // at the top nothing moves, and the other section is untouched
        await svc.MoveParagraphAsync(a, -1, Boss());
        Assert.Equal(["A", "C", "B"], await HeadingsAsync());
        await svc.MoveParagraphAsync(a, 1, Boss());
        Assert.Equal(["C", "A", "B"], await HeadingsAsync());
        Assert.Equal(["X"], (await svc.GetDocumentAsync(Boss())).Sections.First(s => s.Id == other).Paragraphs.Select(p => p.Heading));
    }

    // ---------- reads ----------

    [Fact]
    public async Task The_document_mirrors_the_rights_of_the_reader()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("a", Rank.Trainee),
            Seed.Agent("sc", Rank.SectionChief, configure: a => a.Division = Division.Ctd));
        var (svc, _, _) = Build(ctx);
        await svc.CreateParagraphAsync(section, Input("Für alle"), Boss());
        await svc.CreateParagraphAsync(section, Input("CTD", RegulationAudience.Ctd), Boss());

        var asBoss = await svc.GetDocumentAsync(Boss());
        var asChief = await svc.GetDocumentAsync(Chief("sc", Division.Ctd));
        var asAgent = await svc.GetDocumentAsync(Plain("a"));
        var asSupervision = await svc.GetDocumentAsync(Supervision());

        Assert.True(asBoss.MayManageSections);
        Assert.True(asBoss.MayAddParagraphs);
        Assert.All(asBoss.Sections[0].Paragraphs, p => Assert.True(p.MayEdit));
        Assert.Equal(0, asBoss.OpenCount);
        Assert.NotNull(asBoss.LastChangedAt);

        Assert.False(asChief.MayManageSections);
        Assert.True(asChief.MayAddParagraphs);
        Assert.Equal([false, true], asChief.Sections[0].Paragraphs.Select(p => p.MayEdit));
        Assert.Equal(2, asChief.OpenCount);

        Assert.False(asAgent.MayManageSections);
        Assert.False(asAgent.MayAddParagraphs);
        Assert.All(asAgent.Sections[0].Paragraphs, p => Assert.False(p.MayEdit));
        // every internal agent reads every paragraph; the CTD one is not his to acknowledge
        Assert.Equal(["Für alle", "CTD"], asAgent.Sections[0].Paragraphs.Select(p => p.Heading));
        Assert.Equal([true, false], asAgent.Sections[0].Paragraphs.Select(p => p.MustAcknowledge));
        Assert.Equal("Abschnitt 1", asAgent.Sections[0].Label);

        Assert.Equal(2, asSupervision.Sections[0].Paragraphs.Count);
        Assert.False(asSupervision.MayAddParagraphs);
        Assert.Equal(0, asSupervision.OpenCount);
    }

    [Fact]
    public async Task The_detail_reads_the_debt_and_the_rights_of_the_viewer()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());

        var asBoss = await svc.GetParagraphAsync(id, null, Boss());
        var asAgent = await svc.GetParagraphAsync(id, null, Plain("a"));
        var asSupervision = await svc.GetParagraphAsync(id, null, Supervision());

        Assert.NotNull(asBoss);
        Assert.True(asBoss.Row.MayEdit);
        Assert.True(asBoss.MaySeeQuota);
        Assert.False(asBoss.MustAcknowledge);
        Assert.Equal(1, asBoss.MyAcknowledgedVersion);
        Assert.Equal("Allgemeines", asBoss.SectionTitle);
        Assert.Equal("Abschnitt 1", asBoss.SectionLabel);
        Assert.NotNull(asAgent);
        Assert.False(asAgent.Row.MayEdit);
        Assert.False(asAgent.MaySeeQuota);
        Assert.True(asAgent.MustAcknowledge);
        Assert.True(asAgent.Row.MustAcknowledge);
        Assert.Null(asAgent.MyAcknowledgedVersion);
        Assert.Equal(1, asAgent.Row.ValidVersion);
        Assert.NotNull(asSupervision);
        Assert.False(asSupervision.Row.MayEdit);
        Assert.True(asSupervision.MaySeeQuota);
        Assert.False(asSupervision.MustAcknowledge);
    }

    [Fact]
    public async Task Quota_is_hidden_from_plain_agents_and_shown_to_supervision()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.Trainee),
            Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));

        Assert.Null(await svc.GetQuotaAsync(id, Plain("a")));
        var quota = await svc.GetQuotaAsync(id, Supervision());

        Assert.NotNull(quota);
        // supervision is never in the audience
        Assert.Equal(3, quota.Total);
        Assert.Equal(2, quota.Acknowledged);
        Assert.Equal(1, quota.AcknowledgeFromVersion);
        Assert.Equal("b", Assert.Single(quota.Missing).AgentId);
        Assert.Equal("Codename-b", quota.Missing[0].Codename);
        Assert.Null(quota.Missing[0].AcknowledgedVersion);
        Assert.Equal(["a", "boss"], quota.Done.Select(d => d.AgentId).Order());
        Assert.All(quota.Done, d => Assert.Equal(1, d.AcknowledgedVersion));
        Assert.All(quota.Done, d => Assert.NotNull(d.AcknowledgedAt));

        // a new version that asks again: the old acknowledgment shows as the highest own version
        await svc.PublishVersionAsync(id, Next(1), Boss());
        var after = await svc.GetQuotaAsync(id, Boss());
        Assert.NotNull(after);
        Assert.Equal(2, after.AcknowledgeFromVersion);
        Assert.Equal(["a", "b"], after.Missing.Select(m => m.AgentId));
        Assert.Equal(1, after.Missing.Single(m => m.AgentId == "a").AcknowledgedVersion);
        Assert.Equal(["boss"], after.Done.Select(d => d.AgentId));
        Assert.Null(await svc.GetQuotaAsync("gibt-es-nicht", Boss()));
    }

    [Fact]
    public async Task Quota_of_a_division_paragraph_lists_only_that_division()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("ctd", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd),
            Seed.Agent("cid", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(audience: RegulationAudience.Ctd), Boss());

        var quota = await svc.GetQuotaAsync(id, Supervision());

        Assert.NotNull(quota);
        Assert.Equal(["ctd"], quota.Missing.Select(m => m.AgentId));
        // the Board publisher is no CTD member, so his own acknowledgment does not count here
        Assert.Equal(1, quota.Total);
        Assert.Empty(quota.Done);
    }

    [Fact]
    public async Task Compare_marks_the_changed_words()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss());

        // either order reads old to new
        var comparison = await svc.CompareAsync(id, 2, 1, Plain("a"));

        Assert.NotNull(comparison);
        Assert.Equal((1, 2), (comparison.FromVersion, comparison.ToVersion));
        Assert.True(comparison.Diff.Added > 0);
        Assert.Contains("noosei-neu", comparison.Diff.Html);
        Assert.Null(await svc.CompareAsync(id, 1, 5, Plain("a")));
        Assert.Null(await svc.CompareAsync("gibt-es-nicht", 1, 2, Plain("a")));
    }

    [Fact]
    public async Task A_partner_sees_nothing_of_the_regulation()
    {
        using var ctx = new SqliteTestContext();
        var section = await SeedBasicsAsync(ctx,
            Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss());

        var document = await svc.GetDocumentAsync(Partner());
        Assert.Empty(document.Sections);
        Assert.Empty(document.Repealed);
        Assert.Null(document.LastChangedAt);
        Assert.False(document.MayAddParagraphs);
        Assert.Null(await svc.GetParagraphAsync(id, null, Partner()));
        Assert.Null(await svc.GetParagraphAsync(id, 1, Partner()));
        Assert.Null(await svc.GetQuotaAsync(id, Partner()));
        Assert.Null(await svc.CompareAsync(id, 1, 2, Partner()));
        Assert.Empty(await svc.SearchAsync(null, Partner()));
        Assert.Empty(await svc.SearchAsync("Funk", Partner()));
        Assert.Equal(0, await svc.GetOpenCountAsync(Partner()));
        Assert.Empty(await svc.GetOpenForMeAsync(Partner()));
    }

    [Fact]
    public async Task Search_finds_by_heading_section_title_and_current_wording_only()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _, _) = Build(ctx);
        var general = await svc.CreateSectionAsync("Allgemeines", false, Boss());
        var radio = await svc.CreateSectionAsync("Funkverkehr", false, Boss());
        var weapons = await svc.CreateParagraphAsync(general, Input("Waffenmitführung", html: "<p>Zebra alt</p>"), Boss());
        await svc.CreateParagraphAsync(radio, Input("Rufnamen", html: "<p>Kennung</p>"), Boss());
        await svc.PublishVersionAsync(weapons, Next(1, html: "<p>Giraffe neu</p>", heading: "Waffenmitführung"), Boss());

        var byHeading = Assert.Single(await svc.SearchAsync("Waffen", Plain("a")));
        Assert.Equal((weapons, "Waffenmitführung", "DVO Abschnitt 1 · Waffenmitführung"),
            (byHeading.Id, byHeading.Heading, byHeading.Label));
        Assert.Equal("Rufnamen", Assert.Single(await svc.SearchAsync("Funkverk", Plain("a"))).Heading);
        Assert.Equal(weapons, Assert.Single(await svc.SearchAsync("Giraffe", Plain("a"))).Id);
        // the old wording is history, not a match
        Assert.Empty(await svc.SearchAsync("Zebra", Plain("a")));
        Assert.Equal(["Rufnamen", "Waffenmitführung"], (await svc.SearchAsync(null, Plain("a"))).Select(p => p.Heading));
        Assert.Single(await svc.SearchAsync(null, Boss(), take: 1));
    }

    // ---------- doubles ----------

    /// <summary>Commits the same acknowledgment in another context right before the service's first save, as a second click would.</summary>
    private sealed class RivalAcknowledgment(SqliteTestContext ctx, string paragraphId, string agentId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<RegulationAcknowledgment>().Any(e => e.State == EntityState.Added))
            {
                Fired = true;
                using var rival = ctx.NewContext();
                rival.RegulationAcknowledgments.Add(AckRow(paragraphId, agentId, 1));
                rival.SaveChanges();
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Bumps the paragraph's version right before the service's own update runs, as a parallel publisher would.</summary>
    private sealed class RivalPublish : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Bump(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Bump(command);
            return ValueTask.FromResult(result);
        }

        private void Bump(DbCommand command)
        {
            if (Fired || !command.CommandText.Contains("UPDATE \"DvoAbsaetze\"", StringComparison.Ordinal))
            {
                return;
            }
            Fired = true;
            // same connection, rolls back with the save
            using var rival = command.Connection!.CreateCommand();
            rival.Transaction = command.Transaction;
            rival.CommandText = "UPDATE \"DvoAbsaetze\" SET \"AktuelleFassung\" = \"AktuelleFassung\" + 1";
            rival.ExecuteNonQuery();
        }
    }
}
