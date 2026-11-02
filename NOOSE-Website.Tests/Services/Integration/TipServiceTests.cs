using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="TipService"/>: who may submit, what a handler sees, and what stays hidden.</summary>
public sealed class TipServiceTests
{
    private const string PersonId = "p1";
    private const string CitizenUserId = "buerger1";
    private const string ProfileId = "profil1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal Citizen(string id = CitizenUserId)
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Civilian).Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    /// <summary>Read-only supervision: leadership rank but no admin flag plus the team-lead marker.</summary>
    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).WithCodename("Owl").AsTeamLead().Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        TipService Service,
        TipPriorityService Priority,
        PublicWantedService Wanted,
        PublicModuleService Modules,
        ITipAttachmentStorageService Storage,
        INotificationService Notifications,
        IDiscordWebhookService Discord,
        IMemoryCache Cache,
        TestDbContextFactory Factory);

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>
    /// The interceptor rewrites <c>Remove</c> into a soft delete, which the quota and trash facts depend on.
    /// <see cref="ICaseNumberService"/> is stubbed — the real one issues MySQL-only raw SQL — but it counts up, or the
    /// unique index on the case number would fail on the second submission.
    /// </remarks>
    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        var seq = 0;
        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{++seq:0000}");

        var tipPriority = new TipPriorityService(factory);
        var wanted = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            Substitute.For<INotificationService>(), tipPriority, Substitute.For<IDiscordWebhookService>(),
            Substitute.For<IPressReleaseService>(), cache);

        var storage = Substitute.For<ITipAttachmentStorageService>();
        storage.IsAllowedType(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0).StartsWith("image/"));
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("gespeichert.jpg");

        var notifications = Substitute.For<INotificationService>();
        var discord = Substitute.For<IDiscordWebhookService>();
        var service = new TipService(factory, modules, new BuergerService(factory), wanted, caseNumbers,
            storage, notifications, tipPriority, new PublicTemplateService(factory), discord,
            new TipsBroadcaster());
        return new Host(service, tipPriority, wanted, modules, storage, notifications, discord, cache, factory);
    }

    /// <summary>Seeds module switches, one person file and one complete citizen profile.</summary>
    private static async Task<SqliteTestContext> SeededAsync(
        bool tipsOn = true, Action<BuergerProfil>? profile = null, bool capturesOn = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        foreach (var key in new[] { PublicModules.Wanted, PublicModules.Tips, PublicModules.CaptureReports })
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == key);
            row.IsEnabled = key switch
            {
                PublicModules.Tips => tipsOn,
                PublicModules.CaptureReports => capturesOn,
                _ => true,
            };
        }

        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
        }));

        var row2 = new BuergerProfil
        {
            Id = ProfileId,
            UserId = CitizenUserId,
            FirstName = "Erika",
            LastName = "Musterfrau",
        };
        profile?.Invoke(row2);
        db.BuergerProfile.Add(row2);
        await db.SaveChangesAsync();
        return ctx;
    }

    /// <summary>Gives one more account a complete civilian identity; the seed only carries the one citizen.</summary>
    private static async Task ProfileAsync(SqliteTestContext ctx, string userId, string profileId)
    {
        await using var db = ctx.NewContext();
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = profileId,
            UserId = userId,
            FirstName = "Trevor",
            LastName = "Ward",
        });
        await db.SaveChangesAsync();
    }

    private static Task<string> SubmitAsync(Host host, string? text = null, bool anonymous = false,
        string? reference = null, ClaimsPrincipal? actor = null)
        => host.Service.SubmitAsync(
            new TipInput
            {
                Text = text ?? "Ich habe die gesuchte Person gestern Abend am Hafen gesehen, sie stieg in einen Van.",
                WantsAnonymity = anonymous,
                WantedCaseNumber = reference,
            },
            null, null, null, actor ?? Citizen());

    private static Task<string> ReportCaptureAsync(Host host, string? reference, string? location = "Tankstelle Sandy Shores",
        TipHandover? handover = TipHandover.Festgehalten, ClaimsPrincipal? actor = null, bool anonymous = false)
        => host.Service.SubmitAsync(
            new TipInput
            {
                Text = "Ich habe die gesuchte Person an der Tankstelle gestellt und halte sie hier fest.",
                Kind = TipKind.Ergreifung,
                Handover = handover,
                HandoverLocation = location,
                WantsAnonymity = anonymous,
                WantedCaseNumber = reference,
            },
            null, null, null, actor ?? Citizen());

    /// <summary>Publishes a notice for a person file of its own; one notice per file is the rule.</summary>
    private static async Task<string> ExtraNoticeAsync(Host host, int index)
    {
        var personId = $"extra-person-{index}";
        await using (var db = host.Factory.CreateDbContext())
        {
            db.People.Add(Seed.Person(personId, $"Gesuchter {index}", p =>
            {
                p.CaseNumber = $"FIB-P-2026-90{index:00}";
                p.WantedReason = "Verdacht auf Raub";
            }));
            await db.SaveChangesAsync();
        }
        var id = await host.Wanted.CreateDraftFromPersonAsync(personId, Leader());
        await host.Wanted.PublishAsync(id, null, Leader());
        await using var read = host.Factory.CreateDbContext();
        return await read.OeffentlicheFahndungen.Where(f => f.Id == id).Select(f => f.CaseNumber!).SingleAsync();
    }

    /// <summary>Publishes a notice for the seeded person and returns its public case number.</summary>
    private static async Task<string> PublishedNoticeAsync(Host host)
    {
        var id = await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());
        await host.Wanted.PublishAsync(id, null, Leader());
        await using var db = host.Factory.CreateDbContext();
        return await db.OeffentlicheFahndungen.Where(f => f.Id == id).Select(f => f.CaseNumber!).SingleAsync();
    }

    private static async Task<string> TipIdAsync(Host host, string caseNumber)
    {
        await using var db = host.Factory.CreateDbContext();
        return await db.Hinweise.Where(h => h.CaseNumber == caseNumber).Select(h => h.Id).SingleAsync();
    }

    // ---- desk badge and manual priority ----

    [Fact]
    public async Task TheDeskBadgeCountsAnUnreadTip()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);

        Assert.Equal(1, await host.Service.GetOpenCountAsync());

        await host.Service.MarkAgentReadAsync(await TipIdAsync(host, caseNumber), Agent());

        // the reported defect: looking at everything left the number where it was, because it counted Status == Neu
        Assert.Equal(0, await host.Service.GetOpenCountAsync());
    }

    [Fact]
    public async Task ReadingATipTwiceKeepsTheFirstStamp()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await host.Service.MarkAgentReadAsync(id, Agent());
        await using (var db = host.Factory.CreateDbContext())
        {
            var first = (await db.Hinweise.SingleAsync(h => h.Id == id)).AgentLastReadAt;
            await host.Service.MarkAgentReadAsync(id, Agent());
            await using var again = host.Factory.CreateDbContext();
            Assert.Equal(first, (await again.Hinweise.SingleAsync(h => h.Id == id)).AgentLastReadAt);
        }
    }

    [Fact]
    public async Task ReadingATipDoesNotStampItAsChanged()
    {
        // it goes through ExecuteUpdate for the same reason the citizen's read mark does: opening a tip is not a
        // change to it, and a tracked write would push it onto the record timeline every visit
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await host.Service.MarkAgentReadAsync(id, Agent());

        await using var db = host.Factory.CreateDbContext();
        Assert.Null((await db.Hinweise.SingleAsync(h => h.Id == id)).ModifiedAt);
    }

    [Fact]
    public async Task AHandlerMayNotPinThePriority()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetPriorityAsync(id, 20, "dringend", Agent()));
    }

    [Fact]
    public async Task APinnedPriorityWinsAndSurvivesARecomputation()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await PublishedNoticeAsync(host);
        var id = await TipIdAsync(host, await SubmitAsync(host, reference: caseNumber));

        await host.Service.SetPriorityAsync(id, 20, "Chef will das zuerst", Leader());
        // the automatic path runs on every bounty or hazard change; it must leave a pinned row alone
        await host.Priority.StampAsync(id);

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.NotNull(detail);
        Assert.Equal(20, detail!.Priority);
        Assert.Equal(20, detail.PriorityOverride);
        Assert.Equal("Chef will das zuerst", detail.PriorityOverrideReason);
    }

    [Fact]
    public async Task HandingThePriorityBackRecomputesItAtOnce()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.SetPriorityAsync(id, 25, null, Leader());

        await host.Service.SetPriorityAsync(id, null, null, Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.NotNull(detail);
        Assert.Null(detail!.PriorityOverride);
        // a tip without a notice scores its trust tier alone, never the pinned 25
        Assert.NotEqual(25, detail.Priority);
    }

    [Fact]
    public async Task APinnedPriorityStaysInsideTheScale()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await host.Service.SetPriorityAsync(id, 9999, null, Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TipPriority.Max, detail!.Priority);
    }

    // ---- submitting ----

    [Fact]
    public async Task Submitting_mints_a_case_number_and_stores_the_text()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseNumber = await SubmitAsync(host);

        Assert.StartsWith("FIB-H-", caseNumber, StringComparison.Ordinal);
        await using var db = ctx.NewContext();
        var row = await db.Hinweise.SingleAsync();
        Assert.Equal(ProfileId, row.CitizenProfileId);
        Assert.Equal(TipStatus.Neu, row.Status);
        Assert.Null(row.WantedId);
    }

    [Fact]
    public async Task A_legacy_NOOSE_number_still_opens_the_own_tip_and_the_notice_it_names()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        // a poster from before the rename names the notice, a saved link names the tip
        var caseNumber = await SubmitAsync(host, reference: notice.Replace("FIB-", "NOOSE-", StringComparison.Ordinal));
        var legacy = caseNumber.Replace("FIB-", "NOOSE-", StringComparison.Ordinal);

        var detail = await host.Service.GetOwnDetailAsync(legacy, Citizen());

        Assert.Equal(caseNumber, detail!.CaseNumber);
        Assert.Equal(notice, detail.WantedCaseNumber);
        Assert.Null(await host.Service.GetOwnDetailAsync("NOOSE-H-2026-9999", Citizen()));
    }

    [Fact]
    public async Task Submitting_pings_the_configured_role_on_Discord_without_naming_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);

        // no headline and no recipients: the channel gets the generic notice plus the login-gated link, never
        // the text, the subject or the case number
        await host.Discord.Received(1).PushAsync(NotificationType.PublicTipCreated, $"/hinweise/{id}",
            null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_citizen_reply_pings_nobody_on_Discord()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Können Sie das Kennzeichen nennen?", Agent());
        host.Discord.ClearReceivedCalls();

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Es war ein weißer Van, HB 42 XY.", Citizen());

        // only the submission is an event for the channel; a running thread would turn the ping into noise
        await host.Discord.DidNotReceive().PushAsync(Arg.Any<NotificationType>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_tip_below_the_minimum_length_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host, "Da war wer."));
    }

    [Fact]
    public async Task The_module_switch_blocks_a_submission_even_when_the_page_is_reachable()
    {
        using var ctx = await SeededAsync(tipsOn: false);
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
    }

    [Fact]
    public async Task The_module_switch_does_not_strand_a_running_conversation()
    {
        // same call as with the citizen registration: the switch stops new tips, it does not lock people out of a
        // conversation the agency itself started
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Können Sie das Kennzeichen nennen?", Agent());

        await using (var db = ctx.NewContext())
        {
            var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Tips);
            row.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        host.Cache.Remove("OeffentlicheModule");

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Es war ein weißer Van, HB 42 XY.", Citizen());
        Assert.Equal(2, (await host.Service.GetOwnDetailAsync(caseNumber, Citizen()))!.Messages.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
    }

    [Fact]
    public async Task A_blocked_account_may_not_submit()
    {
        using var ctx = await SeededAsync(profile: p =>
        {
            p.IsBlocked = true;
            p.BlockedReason = "Falschmeldungen";
        });
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SubmitAsync(host));
    }

    [Fact]
    public async Task An_incomplete_profile_may_not_submit()
    {
        using var ctx = await SeededAsync(profile: p => p.LastName = string.Empty);
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
    }

    [Fact]
    public async Task A_partner_files_and_answers_a_tip_like_a_citizen()
    {
        // an external agency observes the same city; what it files out of a civilian identity is a tip like any
        // other, and the guard here is RequireCitizenSubmission rather than the write guard
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "partner", "partner-profil");
        var host = NewHost(ctx);

        var caseNumber = await SubmitAsync(host, actor: Partner());
        await host.Service.ReplyAsCitizenAsync(caseNumber, "Der Van hatte ein Kennzeichen aus Blaine County.",
            Partner());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Partner());
        Assert.NotNull(detail);
        await using var db = ctx.NewContext();
        Assert.Equal("partner-profil", (await db.Hinweise.SingleAsync()).CitizenProfileId);
        Assert.Equal(1, await db.HinweisNachrichten.CountAsync(m => m.AuthorIsCitizen));
    }

    [Fact]
    public async Task A_partner_reading_their_own_tip_moves_the_read_mark()
    {
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "partner", "partner-profil");
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host, actor: Partner());

        await host.Service.MarkCitizenReadAsync(caseNumber, Partner());

        await using var db = ctx.NewContext();
        Assert.NotNull((await db.Hinweise.SingleAsync()).CitizenLastReadAt);
    }

    [Fact]
    public async Task The_read_only_supervision_files_a_tip_of_its_own()
    {
        // the civilian behind the supervision account observes the same city; working the tip still needs MayWrite
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "aufsicht", "aufsicht-profil");
        var host = NewHost(ctx);

        var caseNumber = await SubmitAsync(host, actor: Supervision());

        await using var db = ctx.NewContext();
        Assert.Equal("aufsicht-profil", (await db.Hinweise.SingleAsync(h => h.CaseNumber == caseNumber)).CitizenProfileId);
    }

    // ---- the quota ----

    [Fact]
    public async Task The_daily_quota_blocks_the_next_tip()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        for (var i = 0; i < TipRules.PerDay; i++)
        {
            await SubmitAsync(host);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
    }

    [Fact]
    public async Task A_deleted_tip_does_not_refill_the_quota()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        for (var i = 0; i < TipRules.PerDay; i++)
        {
            await SubmitAsync(host);
        }

        // the whole point of counting with IgnoreQueryFilters: deleting must not buy another slot
        await using (var db = ctx.NewContext())
        {
            var ids = await db.Hinweise.Select(h => h.Id).ToListAsync();
            foreach (var id in ids)
            {
                await host.Service.DeleteAsync(id, Leader());
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
    }

    // ---- the reference ----

    [Fact]
    public async Task A_reference_to_a_published_notice_is_kept()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await SubmitAsync(host, reference: notice);

        await using var db = ctx.NewContext();
        var row = await db.Hinweise.SingleAsync();
        Assert.NotNull(row.WantedId);
    }

    [Fact]
    public async Task A_reference_to_a_notice_that_is_not_published_is_refused()
    {
        // an unpublished draft must not be confirmable from outside; accepting it would make the form an oracle
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SubmitAsync(host, reference: "FIB-FA-2026-0001"));
    }

    // ---- anonymity ----

    [Fact]
    public async Task An_anonymous_tip_shows_the_handler_no_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host, anonymous: true);
        var id = await TipIdAsync(host, caseNumber);

        var detail = await host.Service.GetAsync(id, Agent());

        Assert.NotNull(detail);
        Assert.True(detail!.WantsAnonymity);
        Assert.Null(detail.CitizenName);
        Assert.Null(detail.CitizenConfirmedTips);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());
        Assert.True(rows.Single().IsAnonymous);
        Assert.Null(rows.Single().CitizenName);
    }

    [Fact]
    public async Task A_named_tip_shows_the_handler_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        var detail = await host.Service.GetAsync(id, Agent());

        Assert.Equal("Erika Musterfrau", detail!.CitizenName);
    }

    [Fact]
    public async Task Resolving_anonymity_is_leadership_only()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host, anonymous: true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.ResolveAnonymityAsync(id, "Belohnung", Agent()));
    }

    [Fact]
    public async Task Resolving_anonymity_needs_a_reason_and_writes_an_audit_row()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host, anonymous: true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ResolveAnonymityAsync(id, "  ", Leader()));

        await host.Service.ResolveAnonymityAsync(id, "Auszahlung einer Belohnung", Leader());

        var detail = await host.Service.GetAsync(id, Agent());
        Assert.True(detail!.AnonymityResolved);
        Assert.Equal("Erika Musterfrau", detail.CitizenName);

        await using var db = ctx.NewContext();
        var log = await db.AuditLogs.SingleAsync(a => a.EntityType == "Hinweis" && a.EntityId == id
            && a.ChangesJson != null && a.ChangesJson.Contains("Begr"));
        Assert.Contains("Auszahlung einer Belohnung", log.ChangesJson!, StringComparison.Ordinal);
    }

    // ---- the conversation ----

    [Fact]
    public async Task The_citizen_view_of_an_agency_message_carries_no_author()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);

        await host.Service.AskCitizenAsync(id, "Können Sie das Kennzeichen nennen?", Agent());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var message = Assert.Single(detail!.Messages);
        Assert.False(message.FromCitizen);
        // structural, not filtered: the record has no author property at all
        Assert.DoesNotContain(typeof(NOOSE_Website.Models.Public.CitizenTipMessage).GetProperties(),
            p => p.Name.Contains("Author", StringComparison.Ordinal));

        await using var db = ctx.NewContext();
        Assert.Null((await db.HinweisNachrichten.SingleAsync()).AuthorAgentId);
    }

    [Fact]
    public async Task An_internal_note_never_reaches_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);

        await host.Service.PostInternalNoteAsync(id, "Abgleich mit der Akte läuft.", Agent());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Empty(detail!.Messages);
        Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Agent()));
    }

    [Fact]
    public async Task Only_the_owner_may_reply_to_a_tip()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ReplyAsCitizenAsync(caseNumber, "Ich war das auch", Citizen("fremder")));
    }

    [Fact]
    public async Task A_citizen_may_not_open_the_inbox()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Citizen()));
    }

    [Fact]
    public async Task The_unread_count_moves_with_the_read_mark()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Bitte um das Kennzeichen.", Agent());

        Assert.Equal(1, await host.Service.GetOwnUnreadCountAsync(Citizen()));
        await host.Service.MarkCitizenReadAsync(caseNumber, Citizen());
        Assert.Equal(0, await host.Service.GetOwnUnreadCountAsync(Citizen()));
    }

    // ---- editing a line afterwards ----

    /// <summary>The interceptor stamps whatever <see cref="FixedUser"/> reports, so every line the fixture writes
    /// belongs to "lead": <see cref="Leader"/> is its author and <see cref="Agent"/> the stranger.</summary>
    [Fact]
    public async Task A_handler_rewrites_his_own_internal_note()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Abgleich läuft.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));
        Assert.Null(message.EditedAt);
        Assert.True(message.Mine);

        await host.Service.EditMessageAsync(message.Id, "  Abgleich abgeschlossen.  ", Leader());

        var edited = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));
        Assert.Equal("Abgleich abgeschlossen.", edited.Text);
        Assert.NotNull(edited.EditedAt);
    }

    [Fact]
    public async Task A_rewritten_agency_line_reaches_the_citizen_marked_as_edited()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Nennen Sie das Kennzeichen.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Buerger, Leader()));

        await host.Service.EditMessageAsync(message.Id, "Nennen Sie bitte das Kennzeichen.", Leader());

        var outside = Assert.Single((await host.Service.GetOwnDetailAsync(caseNumber, Citizen()))!.Messages);
        Assert.Equal("Nennen Sie bitte das Kennzeichen.", outside.Text);
        Assert.NotNull(outside.EditedAt);
    }

    /// <summary>The text is the one field the change protocol never carries; /nachweis is open to every agent.</summary>
    [Fact]
    public async Task An_edit_is_audited_without_the_wording()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Alter Wortlaut.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));

        await host.Service.EditMessageAsync(message.Id, "Neuer Wortlaut.", Leader());

        await using var db = ctx.NewContext();
        var row = await db.AuditLogs.SingleAsync(
            a => a.EntityType == nameof(HinweisNachricht) && a.Action == AuditAction.Modified);
        Assert.Equal("lead", row.AgentId);
        // no ChangesJson at all: Text was the only changed field, and it is redacted
        Assert.Null(row.ChangesJson);
    }

    [Fact]
    public async Task A_foreign_line_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Abgleich läuft.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));

        Assert.False((await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Agent()))[0].Mine);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(message.Id, "Umformuliert", Agent()));
    }

    [Fact]
    public async Task The_line_the_citizen_wrote_is_never_editable_from_the_desk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Nennen Sie das Kennzeichen.", Leader());
        await host.Service.ReplyAsCitizenAsync(caseNumber, "Es war ein weißer Van.", Citizen());
        var citizenLine = (await host.Service.GetMessagesAsync(id, TipMessageAudience.Buerger, Leader()))
            .Single(m => m.FromCitizen);

        // the fixture stamps every row with the same account, so the author check alone would have let this through
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(citizenLine.Id, "Umgeschrieben", Leader()));
    }

    [Fact]
    public async Task An_empty_or_oversized_rewrite_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Abgleich läuft.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(message.Id, "   ", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(
                message.Id, new string('x', TipRules.MaxMessageLength + 1), Leader()));
    }

    [Fact]
    public async Task An_unchanged_rewrite_writes_nothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Abgleich läuft.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));

        await host.Service.EditMessageAsync(message.Id, "Abgleich läuft.", Leader());

        Assert.Null(Assert.Single(
            await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader())).EditedAt);
        await using var db = ctx.NewContext();
        Assert.Empty(await db.AuditLogs
            .Where(a => a.EntityType == nameof(HinweisNachricht) && a.Action == AuditAction.Modified)
            .ToListAsync());
    }

    /// <summary>A typo is usually noticed after the file is closed, so the correction must survive the closure.</summary>
    [Fact]
    public async Task A_closed_tip_still_takes_a_correction()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Abgleich läuft.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));
        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Leader());
        await host.Service.SetStatusAsync(id, TipStatus.Verworfen, Leader());

        await host.Service.EditMessageAsync(message.Id, "Abgleich ergebnislos.", Leader());

        Assert.Equal("Abgleich ergebnislos.", Assert.Single(
            await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader())).Text);
    }

    [Fact]
    public async Task Nobody_without_tip_handling_may_rewrite_a_line()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.AskCitizenAsync(id, "Nennen Sie das Kennzeichen.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TipMessageAudience.Buerger, Leader()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(message.Id, "Anders", Citizen()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(message.Id, "Anders", Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(message.Id, "Anders", Partner()));
    }

    // ---- status ----

    [Fact]
    public async Task The_status_whitelist_refuses_an_undefined_move()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        // Neu straight to "bestätigt" skips every check the phase exists for
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, TipStatus.Bestaetigt, Agent()));

        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());
        await host.Service.SetStatusAsync(id, TipStatus.Bestaetigt, Agent());
    }

    [Fact]
    public async Task The_capture_status_is_reserved_for_the_payout()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());

        // the transition table still opens this door, because the reward service walks through it; by hand the
        // status meant a bounty nobody could pay out any more
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, TipStatus.FuehrteZurErgreifung, Agent()));

        Assert.Contains("Auszahlung", error.Message, StringComparison.Ordinal);
        await using var db = ctx.NewContext();
        Assert.Equal(TipStatus.InPruefung,
            await db.Hinweise.Where(h => h.Id == id).Select(h => h.Status).SingleAsync());
    }

    [Fact]
    public async Task A_closed_tip_takes_no_further_message()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);
        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());
        await host.Service.SetStatusAsync(id, TipStatus.Verworfen, Agent());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ReplyAsCitizenAsync(caseNumber, "Doch noch etwas gesehen", Citizen()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.AskCitizenAsync(id, "Nachfrage", Agent()));
    }

    // ---- the attachment ----

    [Fact]
    public async Task The_attachment_is_readable_by_the_owner_and_a_handler_only()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("bild"));
        var caseNumber = await host.Service.SubmitAsync(
            new TipInput { Text = new string('x', TipRules.MinLength + 1) },
            // the length is stated: an attachment without one is refused, because a bound a caller can skip by
            // omitting it is no bound at all
            content, "image/jpeg", "handy.jpg", Citizen(), content.Length);
        var id = await TipIdAsync(host, caseNumber);

        Assert.NotNull(await host.Service.GetAttachmentAsync(id, Citizen()));
        Assert.NotNull(await host.Service.GetAttachmentAsync(id, Agent()));
        Assert.Null(await host.Service.GetAttachmentAsync(id, Citizen("fremder")));
    }

    [Fact]
    public async Task A_non_image_attachment_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.SubmitAsync(
            new TipInput { Text = new string('x', TipRules.MinLength + 1) },
            content, "application/pdf", "beweis.pdf", Citizen()));
    }

    [Fact]
    public void Attachments_are_stored_outside_wwwroot()
    {
        var path = new FileUploadOptions().TipsPath.Replace('\\', '/');

        Assert.StartsWith("App_Data/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("wwwroot", path, StringComparison.OrdinalIgnoreCase);
    }

    // ---- trash ----

    [Fact]
    public async Task Deleting_is_a_soft_delete_and_restoring_brings_the_tip_back()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await host.Service.DeleteAsync(id, Agent());
        Assert.Single(await host.Service.GetTrashAsync());
        Assert.Empty(await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent()));

        await host.Service.RestoreAsync(id, Agent());
        Assert.Empty(await host.Service.GetTrashAsync());
        Assert.Single(await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent()));
    }
    // ---- triage: duplicates, priority, trust ----

    private const string Sighting =
        "Der Gesuchte wurde heute Abend am Hafen von Los Santos gesehen, in einem blauen Wagen.";

    private const string Reworded =
        "Heute Abend war der Gesuchte am Hafen von Los Santos, er saß in einem blauen Wagen.";

    private const string OtherIncident =
        "Vor der Bank in Paleto Bay stand ein Motorrad ohne Kennzeichen, zwei Männer warteten dort.";

    private static async Task<string> NoticeIdAsync(Host host, string caseNumber)
    {
        await using var db = host.Factory.CreateDbContext();
        return await db.OeffentlicheFahndungen.Where(f => f.CaseNumber == caseNumber)
            .Select(f => f.Id).SingleAsync();
    }

    private static async Task PledgeAsync(Host host, string wantedId, decimal amount)
    {
        await using var db = host.Factory.CreateDbContext();
        db.FahndungKopfgeldAnteile.Add(new FahndungKopfgeldAnteil
        {
            WantedId = wantedId,
            Origin = BountyOrigin.NooseKasse,
            Amount = amount,
            Status = BountyShareStatus.Zugesagt,
            Timestamp = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task ScoreAsync(Host host, int score)
    {
        await using var db = host.Factory.CreateDbContext();
        var person = await db.People.SingleAsync(p => p.Id == PersonId);
        person.ThreatScore = score;
        await db.SaveChangesAsync();
    }

    private static async Task<int> ConfirmedTipsAsync(Host host)
    {
        await using var db = host.Factory.CreateDbContext();
        return await db.BuergerProfile.Where(p => p.Id == ProfileId).Select(p => p.ConfirmedTips).SingleAsync();
    }

    [Fact]
    public async Task Two_reports_of_one_incident_land_in_one_group()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host, Sighting);
        await SubmitAsync(host, Reworded);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.DuplicateGroupId));
        Assert.Single(rows.Select(r => r.DuplicateGroupId).Distinct());
        Assert.All(rows, r => Assert.Equal(2, r.DuplicateCount));
    }

    [Fact]
    public async Task Two_different_incidents_stay_ungrouped()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host, Sighting);
        await SubmitAsync(host, OtherIncident);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());

        Assert.All(rows, r => Assert.Null(r.DuplicateGroupId));
        Assert.All(rows, r => Assert.Equal(0, r.DuplicateCount));
    }

    [Fact]
    public async Task The_same_text_on_a_different_reference_is_not_a_duplicate()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);
        await SubmitAsync(host, Sighting, reference: notice);
        await SubmitAsync(host, Sighting);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());

        Assert.All(rows, r => Assert.Null(r.DuplicateGroupId));
    }

    [Fact]
    public async Task The_sibling_list_names_the_other_tips_of_the_group()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var first = await SubmitAsync(host, Sighting);
        var second = await SubmitAsync(host, Reworded);

        var siblings = await host.Service.GetDuplicatesAsync(await TipIdAsync(host, second), Agent());

        Assert.Equal(first, Assert.Single(siblings).CaseNumber);
    }

    [Fact]
    public async Task A_tip_on_a_dangerous_notice_with_bounty_sorts_above_a_plain_one()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await ScoreAsync(host, 90);
        var notice = await PublishedNoticeAsync(host);
        await PledgeAsync(host, await NoticeIdAsync(host, notice), 250_000m);

        await SubmitAsync(host, OtherIncident);
        var hot = await SubmitAsync(host, Sighting, reference: notice);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());

        Assert.Equal(hot, rows[0].CaseNumber);
        Assert.True(rows[0].Priority > rows[1].Priority);
    }

    [Fact]
    public async Task A_later_bounty_raises_the_priority_of_a_tip_already_filed()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);
        var wantedId = await NoticeIdAsync(host, notice);
        var caseNumber = await SubmitAsync(host, Sighting, reference: notice);
        var before = (await host.Service.GetAsync(await TipIdAsync(host, caseNumber), Agent()))!.Priority;

        await PledgeAsync(host, wantedId, 250_000m);
        await host.Priority.StampForNoticeAsync(wantedId);

        var after = (await host.Service.GetAsync(await TipIdAsync(host, caseNumber), Agent()))!.Priority;
        Assert.True(after > before);
    }

    [Fact]
    public async Task A_decided_tip_is_not_re_stamped()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);
        var wantedId = await NoticeIdAsync(host, notice);
        var id = await TipIdAsync(host, await SubmitAsync(host, Sighting, reference: notice));
        await host.Service.SetStatusAsync(id, TipStatus.Verworfen, Agent());
        var before = (await host.Service.GetAsync(id, Agent()))!.Priority;

        await PledgeAsync(host, wantedId, 250_000m);
        await host.Priority.StampForNoticeAsync(wantedId);

        Assert.Equal(before, (await host.Service.GetAsync(id, Agent()))!.Priority);
    }

    [Fact]
    public async Task A_trusted_tipster_may_submit_more_than_the_base_quota()
    {
        using var ctx = await SeededAsync(profile: p => p.ConfirmedTips = 5);
        var host = NewHost(ctx);

        for (var i = 0; i <= TipRules.PerDay; i++)
        {
            await SubmitAsync(host, $"Meldung Nummer {i} über einen Vorfall am Hafen von Los Santos heute Abend.");
        }

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent());
        Assert.Equal(TipRules.PerDay + 1, rows.Count);
    }

    [Fact]
    public async Task The_quota_message_names_the_personal_allowance()
    {
        using var ctx = await SeededAsync(profile: p => p.ConfirmedTips = 20);
        var host = NewHost(ctx);
        var quota = TipTrust.QuotaFor(20);
        for (var i = 0; i < quota; i++)
        {
            await SubmitAsync(host, $"Meldung Nummer {i} über einen Vorfall am Hafen von Los Santos heute Abend.");
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
        Assert.Contains(quota.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_a_tip_twice_leaves_the_trust_counter_at_one()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));

        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());
        await host.Service.SetStatusAsync(id, TipStatus.Bestaetigt, Agent());
        Assert.Equal(1, await ConfirmedTipsAsync(host));

        // the status whitelist allows a decided tip back into review; an increment would count it twice
        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());
        Assert.Equal(0, await ConfirmedTipsAsync(host));
        await host.Service.SetStatusAsync(id, TipStatus.Bestaetigt, Agent());
        Assert.Equal(1, await ConfirmedTipsAsync(host));
    }

    [Fact]
    public async Task A_deleted_confirmed_tip_stops_counting()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host));
        await host.Service.SetStatusAsync(id, TipStatus.InPruefung, Agent());
        await host.Service.SetStatusAsync(id, TipStatus.Bestaetigt, Agent());

        await host.Service.DeleteAsync(id, Agent());
        Assert.Equal(0, await ConfirmedTipsAsync(host));

        await host.Service.RestoreAsync(id, Agent());
        Assert.Equal(1, await ConfirmedTipsAsync(host));
    }

    [Fact]
    public async Task An_anonymous_tip_shows_the_trust_tier_but_neither_name_nor_count()
    {
        using var ctx = await SeededAsync(profile: p => p.ConfirmedTips = 5);
        var host = NewHost(ctx);
        var id = await TipIdAsync(host, await SubmitAsync(host, anonymous: true));

        var detail = await host.Service.GetAsync(id, Agent());
        var row = Assert.Single(await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Agent()));

        Assert.Null(detail!.CitizenName);
        Assert.Null(detail.CitizenConfirmedTips);
        Assert.Equal(TipTrust.Tier(5), detail.TrustTier);
        Assert.Null(row.CitizenName);
        Assert.Equal(TipTrust.Tier(5), row.TrustTier);
    }
    // ---- tipster history on the person file ----

    [Fact]
    public async Task The_person_file_lists_the_tips_of_its_linked_citizen()
    {
        using var ctx = await SeededAsync(profile: p => p.LinkedPersonId = PersonId);
        var host = NewHost(ctx);
        var named = await SubmitAsync(host, Sighting);

        var rows = await host.Service.GetForLinkedPersonAsync(PersonId, Agent());

        var row = Assert.Single(rows);
        Assert.Equal(named, row.CaseNumber);
        Assert.Equal("Erika Musterfrau", row.CitizenName);
    }

    [Fact]
    public async Task A_promised_tip_never_reaches_the_person_file()
    {
        using var ctx = await SeededAsync(profile: p => p.LinkedPersonId = PersonId);
        var host = NewHost(ctx);
        await SubmitAsync(host, Sighting, anonymous: true);
        await SubmitAsync(host, OtherIncident, anonymous: true);

        // not even as a count: the section is keyed on the citizen, so a number would name them by arithmetic
        Assert.Empty(await host.Service.GetForLinkedPersonAsync(PersonId, Agent()));
    }

    [Fact]
    public async Task A_resolved_anonymity_brings_the_tip_onto_the_person_file()
    {
        using var ctx = await SeededAsync(profile: p => p.LinkedPersonId = PersonId);
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host, Sighting, anonymous: true);
        Assert.Empty(await host.Service.GetForLinkedPersonAsync(PersonId, Agent()));

        await host.Service.ResolveAnonymityAsync(await TipIdAsync(host, caseNumber), "Belohnung", Leader());

        Assert.Single(await host.Service.GetForLinkedPersonAsync(PersonId, Agent()));
    }

    [Fact]
    public async Task An_unlinked_person_file_shows_no_tipster_history()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host, Sighting);

        Assert.Empty(await host.Service.GetForLinkedPersonAsync(PersonId, Agent()));
    }

    [Fact]
    public async Task A_citizen_may_not_read_a_tipster_history()
    {
        using var ctx = await SeededAsync(profile: p => p.LinkedPersonId = PersonId);
        var host = NewHost(ctx);
        await SubmitAsync(host, Sighting);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.GetForLinkedPersonAsync(PersonId, Citizen()));
    }

    // ---- the automatic confirmation ----

    private static async Task SeedConfirmationAsync(SqliteTestContext ctx, bool active = true)
    {
        await new PublicTemplateService(ctx.Factory).SaveAsync(
            new PublicTemplateInput(null, PublicTemplateKind.HinweisEingang, "Eingang",
                "Guten Tag BUERGER, Ihr Hinweis AKTENZEICHEN ist eingegangen. Mit Gruss NAME", active, 10),
            Leader());
    }

    [Fact]
    public async Task Submitting_with_an_active_template_confirms_without_naming_an_agent()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await SubmitAsync(host);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var confirmation = Assert.Single(detail!.Messages, m => !m.FromCitizen);
        Assert.Contains("Erika Musterfrau", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains(caseNumber, confirmation.Text, StringComparison.Ordinal);
        Assert.Contains(PublicTemplateRenderer.Redaction, confirmation.Text, StringComparison.Ordinal);

        await using var db = host.Factory.CreateDbContext();
        var row = await db.HinweisNachrichten.SingleAsync();
        Assert.Null(row.AuthorAgentId);
        Assert.False(row.AuthorIsCitizen);
        Assert.Equal(TipMessageAudience.Buerger, row.Audience);
    }

    [Fact]
    public async Task An_anonymous_tip_is_confirmed_without_the_name()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await SubmitAsync(host, anonymous: true);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var confirmation = Assert.Single(detail!.Messages, m => !m.FromCitizen);
        // the promise holds against the agency's own confirmation, not only against the desk projection
        Assert.Contains(PublicTemplateRenderer.CitizenFallback, confirmation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Erika", confirmation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Musterfrau", confirmation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_confirmation_leaves_the_status_at_new_and_writes_nothing_internally()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await SubmitAsync(host);
        var id = await TipIdAsync(host, caseNumber);

        var tip = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TipStatus.Neu, tip!.Status);
        Assert.Equal(1, await host.Service.GetOwnUnreadCountAsync(Citizen()));
        Assert.Empty(await host.Service.GetMessagesAsync(id, TipMessageAudience.Intern, Leader()));
    }

    [Fact]
    public async Task Without_an_active_template_no_confirmation_is_written()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx, active: false);

        var caseNumber = await SubmitAsync(host);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Empty(detail!.Messages);
    }

    [Fact]
    public async Task The_confirmation_does_not_shadow_a_real_citizen_message()
    {
        // unlike a ticket, a tip carries its own text on the record and starts with an empty thread, so a fresh
        // one never awaited an answer. What must hold is that the confirmation neither claims one nor hides the
        // citizen's later reply — the two would share a timestamp if they ever landed in one SaveChanges.
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await SubmitAsync(host);
        var fresh = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Leader());
        Assert.False(fresh[0].AwaitingAnswer);

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Ich habe noch ein Foto vom Fahrzeug.", Citizen());

        var after = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Leader());
        Assert.True(after[0].AwaitingAnswer);
    }

    // ---- capture reports ----

    [Fact]
    public async Task ACaptureReport_needsTheNotice()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, null));
        Assert.Equal(CaptureRules.NoticeRequired, error.Message);
    }

    [Fact]
    public async Task ACaptureReport_isNeverAnonymous()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        // the form does not offer the switch; this asserts the service overrules it anyway
        var caseNumber = await ReportCaptureAsync(host, notice, anonymous: true);

        await using var db = host.Factory.CreateDbContext();
        var row = await db.Hinweise.SingleAsync(h => h.CaseNumber == caseNumber);
        Assert.False(row.WantsAnonymity);
        Assert.Equal(TipKind.Ergreifung, row.Kind);
    }

    [Fact]
    public async Task ACaptureReport_doesNotUseTheTipDeskPing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await ReportCaptureAsync(host, notice);

        // it has its own routable category (PublicCaptureReported, pushed by the notification service), so
        // routing it through the tip desk as well would ping the role twice for one submission
        await host.Discord.DidNotReceive().PushAsync(NotificationType.PublicTipCreated, Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACaptureReport_needsAHandoverStateAndALocation()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice, handover: null));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice, location: "  "));
        Assert.Equal(CaptureRules.LocationRequired, missing.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice, location: new string('x', CaptureRules.MaxLocationLength + 1)));
    }

    [Fact]
    public async Task ACaptureReport_storesWhatWasReported()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        var caseNumber = await ReportCaptureAsync(host, notice, location: "Hinter der Wache",
            handover: TipHandover.Uebergeben);

        await using var db = host.Factory.CreateDbContext();
        var row = await db.Hinweise.SingleAsync(h => h.CaseNumber == caseNumber);
        Assert.Equal(TipHandover.Uebergeben, row.Handover);
        Assert.Equal("Hinter der Wache", row.HandoverLocation);
    }

    [Fact]
    public async Task ACaptureReport_outranksEveryObservationInTheInbox()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await SubmitAsync(host, reference: notice);
        var capture = await ReportCaptureAsync(host, notice);

        var rows = await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Leader());
        Assert.Equal(capture, rows[0].CaseNumber);
        Assert.Equal(TipKind.Ergreifung, rows[0].Kind);
        Assert.Equal(TipHandover.Festgehalten, rows[0].Handover);
    }

    [Fact]
    public async Task TheSecondOpenReportOnOneNotice_isRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await ReportCaptureAsync(host, notice);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice));
        Assert.Equal(CaptureRules.AlreadyOpen, again.Message);
    }

    [Fact]
    public async Task TheTwoQuotas_doNotSpendEachOther()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        // tier 1 allows TipRules.PerDay observations; spend every one of them
        for (var i = 0; i < TipRules.PerDay; i++)
        {
            await SubmitAsync(host, text: $"Beobachtung Nummer {i} am Hafen, die Person stieg in einen Van ein.");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));

        // and the capture path is still open
        var caseNumber = await ReportCaptureAsync(host, notice);
        Assert.False(string.IsNullOrWhiteSpace(caseNumber));
    }

    [Fact]
    public async Task TheCaptureQuota_isCountedOnItsOwn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        // one report per notice AND one notice per person file, so the quota needs its own people to spend on
        for (var i = 0; i < CaptureRules.PerDay; i++)
        {
            await ReportCaptureAsync(host, await ExtraNoticeAsync(host, i));
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice));
        Assert.Contains($"{CaptureRules.PerDay}", refused.Message);

        // an ordinary tip is unaffected
        Assert.False(string.IsNullOrWhiteSpace(await SubmitAsync(host)));
    }

    [Fact]
    public async Task TheCaptureModule_gatesOnlyTheCapturePath()
    {
        using var ctx = await SeededAsync(capturesOn: false);
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReportCaptureAsync(host, notice));
        // tips are a separate switch and stay open
        Assert.False(string.IsNullOrWhiteSpace(await SubmitAsync(host, reference: notice)));
    }

    [Fact]
    public async Task TheTipModule_gatesOnlyTheObservationPath()
    {
        using var ctx = await SeededAsync(tipsOn: false);
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(host));
        Assert.False(string.IsNullOrWhiteSpace(await ReportCaptureAsync(host, notice)));
    }

    [Fact]
    public async Task AnObservationAndACaptureReport_areNeverGroupedAsDuplicates()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);
        const string same = "Ich habe die gesuchte Person an der Tankstelle Sandy Shores angetroffen und festgehalten.";

        await SubmitAsync(host, text: same, reference: notice);
        var capture = await host.Service.SubmitAsync(
            new TipInput
            {
                Text = same,
                Kind = TipKind.Ergreifung,
                Handover = TipHandover.Festgehalten,
                HandoverLocation = "Tankstelle Sandy Shores",
                WantedCaseNumber = notice,
            },
            null, null, null, Citizen());

        await using var db = host.Factory.CreateDbContext();
        var rows = await db.Hinweise.ToListAsync();
        var captureRow = rows.Single(h => h.CaseNumber == capture);
        Assert.Null(captureRow.DuplicateGroupId);
        Assert.All(rows, r => Assert.Null(r.DuplicateGroupId));
    }

    [Fact]
    public async Task TheNamedPerson_cannotReportTheirOwnCapture()
    {
        // the profile carries the published display name of the notice
        using var ctx = await SeededAsync(profile: p =>
        {
            p.FirstName = "Max";
            p.LastName = "Mustermann";
        });
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReportCaptureAsync(host, notice));
        Assert.Equal(CaptureRules.SelfRefused, error.Message);
    }

    [Fact]
    public async Task TheCitizensOwnView_carriesTheKindAndTheHandover()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var notice = await PublishedNoticeAsync(host);
        var caseNumber = await ReportCaptureAsync(host, notice, location: "Am Pier");

        var rows = await host.Service.GetOwnAsync(Citizen());
        var row = rows.Single(r => r.CaseNumber == caseNumber);
        Assert.Equal(TipKind.Ergreifung, row.Kind);
        Assert.Equal(TipHandover.Festgehalten, row.Handover);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.NotNull(detail);
        Assert.Equal(TipKind.Ergreifung, detail!.Kind);
        Assert.Equal("Am Pier", detail.HandoverLocation);
    }

    // ---- the link picker ----

    [Fact]
    public async Task ThePicker_findsATip_byCaseNumberAndByText()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);

        var byNumber = await host.Service.SearchForLinkAsync(caseNumber, Agent());
        Assert.Equal(caseNumber, Assert.Single(byNumber).CaseNumber);

        var byText = await host.Service.SearchForLinkAsync("Hafen", Agent());
        Assert.Equal(caseNumber, Assert.Single(byText).CaseNumber);

        // an empty term is the freshly opened dropdown, not "match nothing"
        Assert.NotEmpty(await host.Service.SearchForLinkAsync(null, Agent()));
    }

    [Fact]
    public async Task ThePicker_offersATipOfEveryStatus()
    {
        // it is not the inbox: which tab a tip sits in has nothing to do with whether a record may point at it
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await SubmitAsync(host);
        var id = (await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Leader())).Single().Id;
        await host.Service.SetStatusAsync(id, TipStatus.Verworfen, Leader());

        var rows = await host.Service.SearchForLinkAsync(null, Agent());

        Assert.Equal(caseNumber, Assert.Single(rows).CaseNumber);
        Assert.Equal(TipStatus.Verworfen, rows[0].Status);
    }

    [Fact]
    public async Task ThePicker_isClosedToAPartnerAndToACitizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SearchForLinkAsync(null, Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SearchForLinkAsync(null, Citizen()));
        // the read-only supervision reads along here, as everywhere else
        Assert.NotEmpty(await host.Service.SearchForLinkAsync(null, Supervision()));
    }

    [Fact]
    public async Task ThePickerRow_carriesNoCitizenField()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host);

        var row = Assert.Single(await host.Service.SearchForLinkAsync(null, Agent()));

        // structural, not a branch: the type has no property that could name the tipster
        Assert.DoesNotContain(typeof(TipPickRow).GetProperties(),
            p => p.Name.Contains("Citizen", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Buerger", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(row.CaseNumber));
    }

    [Fact]
    public async Task ThePicker_leavesADeletedTipOut()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SubmitAsync(host);
        var id = (await host.Service.GetInboxAsync(TipInboxScope.Eingang, null, false, Leader())).Single().Id;

        await host.Service.DeleteAsync(id, Leader());

        Assert.Empty(await host.Service.SearchForLinkAsync(null, Agent()));
    }
}
