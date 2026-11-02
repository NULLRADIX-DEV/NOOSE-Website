using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="TicketService"/>: who may open, who may answer, and what stays inside.</summary>
public sealed class TicketServiceTests
{
    private const string CitizenUserId = "buerger1";
    private const string ProfileId = "profil1";
    private const string OtherUserId = "buerger2";
    private const string OtherProfileId = "profil2";

    // a mention token only carries a 36-char GUID, which is the shape Identity gives an account in production
    private const string NamedAgentId = "11111111-1111-1111-1111-111111111111";
    private const string NamedSupervisionId = "22222222-2222-2222-2222-222222222222";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Admin()
        => ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).WithCodename("Owl")
            .AsTeamLead().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ClaimsPrincipal Citizen(string id = CitizenUserId)
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Civilian).Build();

    /// <summary>Adds an account whose id has the shape a mention token can address.</summary>
    private static async Task NamedAgentAsync(SqliteTestContext ctx, string id, bool teamLead = false)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id, Rank.SpecialAgent, configure: a =>
        {
            a.Codename = teamLead ? "Owl" : "Kestrel";
            a.IsTeamLead = teamLead;
        }));
        await db.SaveChangesAsync();
    }

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        TicketService Service,
        PublicModuleService Modules,
        INotificationService Notifications,
        IDiscordWebhookService Discord,
        FakeAttachments Attachments,
        TestDbContextFactory Factory);

    /// <summary>Attachment store in memory; the facts here are about the columns and the gates, not the disk.</summary>
    private sealed class FakeAttachments : ITicketAttachmentStorageService
    {
        public long MaxBytes { get; set; } = 1024;

        public Dictionary<string, byte[]> Saved { get; } = new(StringComparer.Ordinal);

        public bool IsAllowedType(string contentType)
            => contentType is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

        public async Task<string> SaveAsync(Stream content, string contentType,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var name = $"{Guid.NewGuid():N}.png";
            Saved[name] = buffer.ToArray();
            return name;
        }

        public Stream OpenRead(string fileNameSaved) => new MemoryStream(Saved[fileNameSaved]);

        public void Delete(string fileNameSaved) => Saved.Remove(fileNameSaved);
    }

    /// <summary>A tiny image upload; the store is a fake, so the bytes only have to be non-empty.</summary>
    private static TicketAttachmentUpload Upload(string name = "beweis.png", long size = 4,
        string contentType = "image/png")
        => new(new MemoryStream(new byte[] { 1, 2, 3, 4 }), contentType, name, size);

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>
    /// The interceptor rewrites <c>Remove</c> into a soft delete, which the quota and trash facts depend on.
    /// <see cref="ICaseNumberService"/> is stubbed — the real one issues MySQL-only raw SQL — but it counts up, or
    /// the unique index on the case number would fail on the second ticket.
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

        var notifications = Substitute.For<INotificationService>();
        var discord = Substitute.For<IDiscordWebhookService>();
        var attachments = new FakeAttachments();
        var service = new TicketService(factory, modules, new BuergerService(factory), caseNumbers,
            notifications, new PublicTemplateService(factory), discord, attachments, new TicketBroadcaster());
        return new Host(service, modules, notifications, discord, attachments, factory);
    }

    /// <summary>Seeds the module switch and two complete citizen profiles.</summary>
    private static async Task<SqliteTestContext> SeededAsync(
        bool ticketsOn = true, Action<BuergerProfil>? profile = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        var row = await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Tickets);
        row.IsEnabled = ticketsOn;

        db.Users.Add(Seed.Agent("lead", Rank.Director, configure: a => a.Codename = "Falcon"));
        // an ordinary agent, so the participant picker predicate has someone to find
        db.Users.Add(Seed.Agent("junior", Rank.SpecialAgent, configure: a => a.Codename = "Wren"));

        var mine = new BuergerProfil
        {
            Id = ProfileId,
            UserId = CitizenUserId,
            FirstName = "Erika",
            LastName = "Musterfrau",
        };
        profile?.Invoke(mine);
        db.BuergerProfile.Add(mine);
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = OtherProfileId,
            UserId = OtherUserId,
            FirstName = "Klaus",
            LastName = "Kleber",
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static Task<string> OpenAsync(Host host, string? subject = null, string? text = null,
        ClaimsPrincipal? actor = null, TicketKategorie category = TicketKategorie.Anzeige)
        => host.Service.OpenAsync(
            new TicketInput
            {
                Subject = subject ?? "Frage zu meinem Fahrzeug",
                Text = text ?? "Mein Kennzeichen steht auf der Fahndung, obwohl das Fahrzeug verkauft wurde.",
                Category = category,
            },
            actor ?? Citizen());

    /// <summary>Gives one more account a complete civilian identity; the seed only carries the two citizens.</summary>
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

    private static async Task<string> IdAsync(Host host, string caseNumber)
    {
        await using var db = host.Factory.CreateDbContext();
        return await db.Tickets.Where(t => t.CaseNumber == caseNumber).Select(t => t.Id).SingleAsync();
    }

    // ---- opening ----

    [Fact]
    public async Task Opening_mints_a_case_number_and_stores_the_first_message()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseNumber = await OpenAsync(host);

        Assert.StartsWith("FIB-T-", caseNumber, StringComparison.Ordinal);
        await using var db = ctx.NewContext();
        var row = await db.Tickets.SingleAsync();
        Assert.Equal(ProfileId, row.CitizenProfileId);
        Assert.Equal(TicketStatus.Offen, row.Status);
        Assert.Equal(TicketArt.Fuehrungsebene, row.Kind);
        var message = await db.TicketNachrichten.SingleAsync();
        Assert.Equal(TicketMessageAudience.Buerger, message.Audience);
        Assert.True(message.AuthorIsCitizen);
        Assert.Null(message.AuthorAgentId);
    }

    [Fact]
    public async Task Opening_pings_the_leadership_role_on_Discord_without_naming_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        // no headline argument: the channel gets the generic notice plus the link, never subject or name
        await host.Discord.Received(1).PushAsync(NotificationType.PublicTicketCreated, $"/tickets/{id}",
            null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_citizen_reply_pings_nobody_on_Discord()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        host.Discord.ClearReceivedCalls();

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Ich habe den Kaufvertrag gefunden.", Citizen());

        // only the opening is an event for the channel; a running thread would turn the ping into noise
        await host.Discord.DidNotReceive().PushAsync(Arg.Any<NotificationType>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_short_subject_or_text_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host, subject: "Hi"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host, text: "zu kurz"));
    }

    [Fact]
    public async Task A_closed_module_refuses_the_opening()
    {
        using var ctx = await SeededAsync(ticketsOn: false);
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host));
    }

    [Fact]
    public async Task A_closed_module_leaves_a_running_ticket_readable_and_answerable()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);

        // the switch stops new concerns; it does not strand a conversation the agency itself started
        await host.Modules.SaveAsync(
            [new PublicModuleInput { Key = PublicModules.Tickets, IsEnabled = false }], Admin());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.NotNull(detail);
        await host.Service.ReplyAsCitizenAsync(caseNumber, "Ich habe den Kaufvertrag gefunden.", Citizen());
        Assert.Equal(2, (await host.Service.GetOwnDetailAsync(caseNumber, Citizen()))!.Messages.Count);
    }

    [Fact]
    public async Task An_account_without_a_profile_cannot_open_one()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host, actor: Citizen("fremd")));
    }

    [Fact]
    public async Task A_partner_opens_and_answers_a_ticket_like_a_citizen()
    {
        // an external agency reaches the desk the same way a citizen does: their own correspondence is not a write
        // into the record stock, and the guard here is RequireCitizenSubmission rather than the write guard
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "partner", "partner-profil");
        var host = NewHost(ctx);

        var caseNumber = await OpenAsync(host, actor: Partner());
        await host.Service.ReplyAsCitizenAsync(caseNumber, "Wir hängen den Vorgang an unsere Akte an.", Partner());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Partner());
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Messages.Count(m => m.FromCitizen));
        await using var db = ctx.NewContext();
        Assert.Equal("partner-profil", (await db.Tickets.SingleAsync()).CitizenProfileId);
    }

    [Fact]
    public async Task The_read_only_supervision_opens_a_citizen_ticket_of_its_own()
    {
        // it writes nothing in the house, but the person behind the account plays a civilian and reaches the desk
        // like any other. Its desk role is untouched: answering, closing and noting still need MayWrite
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "aufsicht", "aufsicht-profil");
        var host = NewHost(ctx);

        var caseNumber = await OpenAsync(host, actor: Supervision());

        Assert.NotNull(await host.Service.GetOwnDetailAsync(caseNumber, Supervision()));
        await using var db = ctx.NewContext();
        Assert.Equal("aufsicht-profil", (await db.Tickets.SingleAsync()).CitizenProfileId);
    }

    [Fact]
    public async Task A_partner_reading_their_own_ticket_moves_the_read_mark()
    {
        // the mark is a write too, and it used to be gated on MayWrite: without this the partner's unread badge
        // would never clear. The stamp itself is asserted, not the derived count — SQLite rounds its own clock
        using var ctx = await SeededAsync();
        await ProfileAsync(ctx, "partner", "partner-profil");
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host, actor: Partner());
        var id = await IdAsync(host, caseNumber);
        await host.Service.ReplyToCitizenAsync(id, "Wir prüfen das.", Leader());

        await host.Service.MarkCitizenReadAsync(caseNumber, Partner());

        await using var db = ctx.NewContext();
        Assert.NotNull((await db.Tickets.SingleAsync()).CitizenLastReadAt);
    }

    [Fact]
    public async Task A_blocked_account_cannot_open_one()
    {
        using var ctx = await SeededAsync(profile: p =>
        {
            p.IsBlocked = true;
            p.BlockedReason = "Missbrauch";
        });
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OpenAsync(host));
    }

    [Fact]
    public async Task The_open_cap_refuses_the_next_ticket_and_closing_one_frees_the_slot()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var first = await OpenAsync(host);
        await OpenAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host));

        await host.Service.SetStatusAsync(await IdAsync(host, first), TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
        // the daily cap is 3, so exactly one more fits through
        var third = await OpenAsync(host);
        Assert.StartsWith("FIB-T-", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_daily_cap_counts_deleted_tickets_too()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        for (var i = 0; i < TicketRules.PerDay; i++)
        {
            var caseNumber = await OpenAsync(host);
            var id = await IdAsync(host, caseNumber);
            // closing frees the open cap, deleting must not free the daily one
            await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
            await host.Service.DeleteAsync(id, Leader());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(host));
    }

    // ---- who may look ----

    [Fact]
    public async Task A_junior_agent_a_citizen_and_a_partner_are_refused_at_the_desk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Citizen()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Partner()));

        // not a refusal any more: an agent who is not attached gets "does not exist", because a refusal would
        // confirm that this ticket is there
        Assert.Null(await host.Service.GetAsync(id, Junior()));
        Assert.Empty(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GetAsync(id, Citizen()));
    }

    // ---- participants and the internal thread ----

    [Fact]
    public async Task AnAttachedAgentReadsTheTicketAndItsInternalThread()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Interner Vermerk zur Lage.", Leader());

        Assert.Null(await host.Service.GetAsync(id, Junior()));

        await host.Service.AddParticipantAsync(id, "junior", Leader());

        Assert.NotNull(await host.Service.GetAsync(id, Junior()));
        Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Junior()));
    }

    [Fact]
    public async Task RemovingAnAgentTakesTheInternalThreadWithIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Interner Vermerk zur Lage.", Leader());
        await host.Service.AddParticipantAsync(id, "junior", Leader());
        var row = Assert.Single(await host.Service.GetParticipantsAsync(id, Leader()));

        await host.Service.RemoveParticipantAsync(row.Id, Leader());

        Assert.Null(await host.Service.GetAsync(id, Junior()));
        Assert.Empty(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Junior()));
    }

    [Fact]
    public async Task LeadershipReadsTheInternalThreadWithoutBeingAttached()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Interner Vermerk zur Lage.", Leader());

        Assert.Empty(await host.Service.GetParticipantsAsync(id, Leader()));
        Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));
    }

    [Fact]
    public async Task AnUnattachedAgentCannotWriteAnInternalNote()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.PostInternalNoteAsync(id, "Ich schreibe hier einfach mit.", Junior()));
    }

    [Fact]
    public async Task OnlyTheDeskMayAttachAnAgent()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.AddParticipantAsync(id, "junior", Junior()));
    }

    /// <summary>Two more ordinary agents, so a whole direction has members to hand to the batch.</summary>
    private static async Task SeedDirectionAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("tru1", Rank.SpecialAgent, configure: a =>
        {
            a.Codename = "Hawk";
            a.IsTRU = true;
        }));
        db.Users.Add(Seed.Agent("tru2", Rank.SpecialAgent, configure: a =>
        {
            a.Codename = "Kite";
            a.IsTRU = true;
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TheDeskAttachesSeveralAgentsInOneGo()
    {
        using var ctx = await SeededAsync();
        await SeedDirectionAsync(ctx);
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        var added = await host.Service.AddParticipantsAsync(id, ["junior", "tru1", "tru2"], Leader());

        Assert.Equal(3, added);
        Assert.Equal(3, (await host.Service.GetParticipantsAsync(id, Leader())).Count);
    }

    [Fact]
    public async Task AttachingADirectionSkipsWhoIsAlreadyOnTheTicket()
    {
        // the direction buttons overlap with the single picks by design; the second pass must not throw or double
        using var ctx = await SeededAsync();
        await SeedDirectionAsync(ctx);
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.AddParticipantAsync(id, "tru1", Leader());

        var added = await host.Service.AddParticipantsAsync(id, ["tru1", "tru2"], Leader());

        Assert.Equal(1, added);
        Assert.Equal(2, (await host.Service.GetParticipantsAsync(id, Leader())).Count);
    }

    [Fact]
    public async Task OneUnselectableAgentRefusesTheWholeBatch()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            // read-only supervision is out of every picker, so it must be out of the batch too
            db.Users.Add(Seed.Agent("aufsicht", Rank.Director, configure: a =>
            {
                a.Codename = "Owl";
                a.IsTeamLead = true;
            }));
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.AddParticipantsAsync(id, ["junior", "aufsicht"], Leader()));

        // nothing half-attached: the good id in the batch stays off the ticket as well
        Assert.Empty(await host.Service.GetParticipantsAsync(id, Leader()));
    }

    [Fact]
    public async Task OnlyTheDeskMayAttachSeveralAgents()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.AddParticipantsAsync(id, ["junior"], Junior()));
    }

    [Fact]
    public async Task TheDeskPutsAnotherAgentOnTheTicketAsHandler()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.AssignAsync(id, "junior", Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal("junior", detail!.HandlerId);
        // an open ticket that now has a handler is in handling, exactly as self-assignment leaves it
        Assert.Equal(TicketStatus.InBearbeitung, detail.Status);
        // the new handler learns of it; whoever moved the ticket already knows
        await host.Notifications.Received(1).NotifyOnceAsync("junior",
            NotificationType.PublicTicketInternal, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssigningTheHandlerAttachesNoParticipant()
    {
        // two axes: one handler drives "only mine" and the reply bell, participants carry their own read marks
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.AssignAsync(id, "junior", Leader());

        Assert.Empty(await host.Service.GetParticipantsAsync(id, Leader()));
    }

    [Fact]
    public async Task AnUnselectableAgentCannotBecomeHandler()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            // read-only supervision is out of every picker, so the raw id must not get past the write path either
            db.Users.Add(Seed.Agent("aufsicht", Rank.Director, configure: a =>
            {
                a.Codename = "Owl";
                a.IsTeamLead = true;
            }));
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.AssignAsync(id, "aufsicht", Leader()));

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Null(detail!.HandlerId);
        Assert.Equal(TicketStatus.Offen, detail.Status);
    }

    [Fact]
    public async Task OnlyTheDeskMayAssignAHandler()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.AssignAsync(id, "junior", Junior()));
    }

    [Fact]
    public async Task AnAttachedAgentMayMarkTheTicketRead()
    {
        // the regression: the detail page calls this in OnParametersSetAsync, and the leadership-only guard threw
        // before the page could render — the whole participation feature was unreachable for its own audience
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.AddParticipantAsync(id, "junior", Leader());

        await host.Service.MarkAgentReadAsync(id, Junior());

        await using var db = host.Factory.CreateDbContext();
        // the participant stamps their OWN row; the desk mark is one for the whole house and stays where it was
        Assert.NotNull((await db.TicketBeteiligte.SingleAsync(p => p.AgentId == "junior")).LastReadAt);
        Assert.Null((await db.Tickets.SingleAsync(t => t.Id == id)).AgentLastReadAt);
    }

    [Fact]
    public async Task TheDeskMarkMovesForTheDesk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.MarkAgentReadAsync(id, Leader());

        await using var db = host.Factory.CreateDbContext();
        Assert.NotNull((await db.Tickets.SingleAsync(t => t.Id == id)).AgentLastReadAt);
    }

    [Fact]
    public async Task AnUnattachedAgentMovesNoMark()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.MarkAgentReadAsync(id, Junior());

        await using var db = host.Factory.CreateDbContext();
        Assert.Null((await db.Tickets.SingleAsync(t => t.Id == id)).AgentLastReadAt);
    }

    [Fact]
    public async Task ReadingClearsTheParticipationBadge()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());
        var id = await IdAsync(host, caseNumber);
        await host.Service.PostInternalNoteAsync(id, "Antwort der Führung dazu.", Leader());
        Assert.Equal(1, Assert.Single(await host.Service.GetMyParticipationsAsync(Junior())).UnreadInternal);

        await host.Service.MarkAgentReadAsync(id, Junior());

        Assert.Equal(0, Assert.Single(await host.Service.GetMyParticipationsAsync(Junior())).UnreadInternal);
    }

    [Fact]
    public async Task AnInternalTicketHasNoCitizenThreadToAnswerInto()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());
        var id = await IdAsync(host, caseNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ReplyToCitizenAsync(id, "Sehr geehrte Frau Musterfrau,", Leader()));
    }

    [Fact]
    public async Task RestoringAnInternalTicketIgnoresTheCitizenQuota()
    {
        // the citizen quota compared a null column against a null parameter, so it counted every open internal
        // ticket in the house as one account's allowance
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        for (var i = 0; i < TicketRules.MaxOpen; i++)
        {
            await host.Service.OpenAsAgentAsync(
                new TicketInput { Subject = $"Laufendes Anliegen {i}", Text = "Ich brauche eine Entscheidung dazu." },
                [], Junior());
        }
        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Versehentlich gelöscht", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());
        var id = await IdAsync(host, caseNumber);
        await host.Service.DeleteAsync(id, Leader());

        await host.Service.RestoreAsync(id, Leader());

        await using var db = host.Factory.CreateDbContext();
        Assert.False((await db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == id)).IsDeleted);
    }

    [Fact]
    public async Task TheDeskListNamesTheOpenerOfAnInternalTicket()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());

        var row = Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));

        Assert.Equal(TicketArt.Intern, row.Kind);
        Assert.Equal("Wren", row.CitizenName);
    }

    [Fact]
    public async Task TheDeskListCarriesTheStampTheReactionLightReads()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        var waiting = Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));
        Assert.True(waiting.AwaitingAnswer);
        Assert.NotNull(waiting.WaitingSince);

        await host.Service.ReplyToCitizenAsync(id, "Wir prüfen das und melden uns.", Leader());

        // answered: nobody waits any more, so the light goes out with the stamp
        var answered = Assert.Single(
            await host.Service.GetInboxAsync(TicketInboxScope.Wartet, null, false, Leader()));
        Assert.False(answered.AwaitingAnswer);
        Assert.Null(answered.WaitingSince);
    }

    [Fact]
    public async Task AnInternalTicketNeverWaitsOnACitizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());

        var row = Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));

        Assert.Null(row.WaitingSince);
    }

    // ---- attachments ----

    [Fact]
    public async Task TheCitizensFileTravelsWithTheOpeningMessage()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsync(
            new TicketInput
            {
                Subject = "Beschädigtes Fahrzeug",
                Text = "Ich lege ein Foto des Schadens bei, damit es nachvollziehbar ist.",
            },
            Citizen(), Upload());

        var own = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var line = own!.Messages.First(m => m.FromCitizen);
        Assert.True(line.HasAttachment);
        Assert.Equal("beweis.png", line.AttachmentName);
        Assert.Single(host.Attachments.Saved);
    }

    [Fact]
    public async Task TheCitizenReachesTheirOwnFileByPositionAndNeverByRowId()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsync(
            new TicketInput
            {
                Subject = "Beschädigtes Fahrzeug",
                Text = "Ich lege ein Foto des Schadens bei, damit es nachvollziehbar ist.",
            },
            Citizen(), Upload());

        var access = await host.Service.GetOwnAttachmentAsync(caseNumber, 0, Citizen());
        Assert.Equal("beweis.png", access!.OriginalName);

        // a position without a file, and a stranger's ticket, answer the same way
        Assert.Null(await host.Service.GetOwnAttachmentAsync(caseNumber, 5, Citizen()));
        Assert.Null(await host.Service.GetOwnAttachmentAsync(caseNumber, 0, Citizen(OtherUserId)));
    }

    [Fact]
    public async Task ADeskFileIsReadableByTheDeskAndNotByABystander()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Screenshot der Meldung dazu.", Leader(), Upload("intern.png"));
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        Assert.True(note.HasAttachment);
        Assert.Equal("intern.png", (await host.Service.GetAttachmentAsync(note.Id, Leader()))!.OriginalName);

        // an agent who is not on the ticket gets the same null as for a file that is gone
        Assert.Null(await host.Service.GetAttachmentAsync(note.Id, Junior()));
        // and so does a citizen, who has no business on the message-id route at all
        Assert.Null(await host.Service.GetAttachmentAsync(note.Id, Citizen()));
    }

    [Fact]
    public async Task AnAttachedAgentReachesTheFileOnTheirOwnTicket()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.AddParticipantAsync(id, "junior", Leader());
        await host.Service.PostInternalNoteAsync(id, "Screenshot der Meldung dazu.", Leader(), Upload("intern.png"));
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        Assert.NotNull(await host.Service.GetAttachmentAsync(note.Id, Junior()));
    }

    [Fact]
    public async Task AForeignFileTypeAndAnUnstatedSizeAreBothRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PostInternalNoteAsync(
            id, "Anhang dazu.", Leader(), Upload("liste.pdf", contentType: "application/pdf")));

        // fail closed: a size a caller can omit is no bound at all
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PostInternalNoteAsync(
            id, "Anhang dazu.", Leader(), Upload(size: 0)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.PostInternalNoteAsync(
            id, "Anhang dazu.", Leader(), Upload(size: host.Attachments.MaxBytes + 1)));

        Assert.Empty(host.Attachments.Saved);
    }

    [Fact]
    public async Task OneTicketCarriesOnlySoManyFiles()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        for (var i = 0; i < TicketRules.MaxAttachments; i++)
        {
            await host.Service.PostInternalNoteAsync(id, $"Anhang Nummer {i} dazu.", Leader(), Upload());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.PostInternalNoteAsync(id, "Einer zu viel dazu.", Leader(), Upload()));

        Assert.Equal(TicketRules.MaxAttachments, host.Attachments.Saved.Count);
    }

    [Fact]
    public async Task ThePositionOfAnAttachmentSurvivesASharedTimestamp()
    {
        // the regression: the opening message and the automatic confirmation are written in one SaveChanges, so
        // the interceptor stamps them identically. Ordered by the timestamp alone, the position the citizen sees
        // and the one the download route resolves were two different rows
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        DateTime stamp;
        await using (var db = ctx.NewContext())
        {
            var opening = await db.TicketNachrichten.SingleAsync(m => m.TicketId == id);
            stamp = opening.CreatedAt;
            db.TicketNachrichten.Remove(opening);
            await db.SaveChangesAsync();
        }
        await using (var db = ctx.NewContext())
        {
            // inserted in the opposite order to the one the ids give, and with one shared stamp: insertion order
            // would put "zzz" first, the tie-break puts "aaa" first. The file hangs on "aaa", so a service that
            // ordered by the timestamp alone would resolve position 0 to the wrong row
            db.TicketNachrichten.Add(new TicketNachricht
            {
                Id = "zzz-zweite-zeile",
                TicketId = id,
                Audience = TicketMessageAudience.Buerger,
                Text = "Zweite Zeile ohne Anhang.",
                AuthorIsCitizen = true,
                CreatedAt = stamp,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = ctx.NewContext())
        {
            db.TicketNachrichten.Add(new TicketNachricht
            {
                Id = "aaa-erste-zeile",
                TicketId = id,
                Audience = TicketMessageAudience.Buerger,
                Text = "Erste Zeile mit Foto.",
                AuthorIsCitizen = true,
                CreatedAt = stamp,
                AttachmentFileName = "gespeichert.png",
                AttachmentOriginalName = "nachtrag.png",
                AttachmentContentType = "image/png",
            });
            await db.SaveChangesAsync();
        }

        var own = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Equal(2, own!.Messages.Count);
        // the tie-break decides, not the insertion order
        Assert.True(own.Messages[0].HasAttachment);

        // exactly the position the page renders is the one the route resolves
        Assert.Equal("nachtrag.png", (await host.Service.GetOwnAttachmentAsync(caseNumber, 0, Citizen()))!.OriginalName);
        Assert.Null(await host.Service.GetOwnAttachmentAsync(caseNumber, 1, Citizen()));
    }

    // ---- mentions in the internal thread ----

    [Fact]
    public async Task AMentionInAnInternalNoteAttachesAndRingsTheNamedAgent()
    {
        using var ctx = await SeededAsync();
        await NamedAgentAsync(ctx, NamedAgentId);
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.PostInternalNoteAsync(id,
            $"Bitte prüfen, {MentionParser.Token(nameof(Agent), NamedAgentId)}", Leader());

        var participants = await host.Service.GetParticipantsAsync(id, Leader());
        Assert.Equal(NamedAgentId, Assert.Single(participants).AgentId);
        await host.Notifications.Received(1).NotifyOnceAsync(NamedAgentId,
            NotificationType.PublicTicketInternal, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamingSomebodyTwiceAttachesThemOnce()
    {
        using var ctx = await SeededAsync();
        await NamedAgentAsync(ctx, NamedAgentId);
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        var token = MentionParser.Token(nameof(Agent), NamedAgentId);

        await host.Service.PostInternalNoteAsync(id, $"Erst {token}", Leader());
        await host.Service.PostInternalNoteAsync(id, $"Und nochmal {token}", Leader());

        Assert.Single(await host.Service.GetParticipantsAsync(id, Leader()));
    }

    [Fact]
    public async Task MentioningYourselfRingsNobody()
    {
        // the exclusion in AttachMentionedAsync only matters for an author whose id is a real GUID, since a
        // mention token cannot even address the fixture's short "lead"/"junior" ids
        using var ctx = await SeededAsync();
        await NamedAgentAsync(ctx, NamedAgentId, teamLead: false);
        var host = NewHost(ctx);
        var author = ClaimsPrincipalBuilder.Agent(NamedAgentId).WithRank(Rank.Director).Build();
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.PostInternalNoteAsync(id,
            $"Notiz an mich selbst {MentionParser.Token(nameof(Agent), NamedAgentId)}", author);

        // the desk covers participation regardless, so the point here is the notification, not the roster
        await host.Notifications.DidNotReceive().NotifyOnceAsync(NamedAgentId,
            NotificationType.PublicTicketInternal, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnselectableAgentIsSkippedRatherThanRefusingTheNote()
    {
        using var ctx = await SeededAsync();
        await NamedAgentAsync(ctx, NamedSupervisionId, teamLead: true);
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.PostInternalNoteAsync(id,
            $"Zur Kenntnis {MentionParser.Token(nameof(Agent), NamedSupervisionId)}", Leader());

        // the note is written, the read-only supervision simply does not land on the ticket
        Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));
        Assert.Empty(await host.Service.GetParticipantsAsync(id, Leader()));
    }

    [Fact]
    public async Task ATokenIsRefusedOnEveryCitizenFacingLine()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        var token = MentionParser.Token(nameof(Agent), NamedAgentId);

        // the agency answer
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ReplyToCitizenAsync(id, $"Guten Tag {token}, wir prüfen das.", Leader()));

        // the citizen's own reply
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.ReplyAsCitizenAsync(caseNumber, $"Danke {token} für die Rückmeldung.", Citizen()));

        // and opening one
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.OpenAsync(
            new TicketInput
            {
                Subject = "Anliegen mit Token",
                Text = $"Bitte an {token} weiterleiten, das ist mein Anliegen dazu.",
            },
            Citizen(OtherUserId)));
    }

    [Fact]
    public async Task EditingAnAgencyLineCannotSmuggleATokenIn()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.ReplyToCitizenAsync(id, "Wir prüfen das und melden uns.", Leader());
        var line = (await host.Service.GetMessagesAsync(id, TicketMessageAudience.Buerger, Leader()))
            .Single(m => !m.FromCitizen);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.EditMessageAsync(line.Id,
            $"Wir prüfen das, {MentionParser.Token(nameof(Agent), NamedAgentId)}.", Leader()));
    }

    // ---- follow-up reminder resets ----

    [Fact]
    public async Task AFreshReplyClearsAStaleReminderStamp()
    {
        // regression: a ticket already in WartetAufBuerger re-enters it without ever passing through another
        // status, so SetStatusAsync's own "leaving the wait" guard never fires here — this write is the only
        // other place the flag can go stale, or the worker skips the reminder on a whole new stretch of silence
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.ReplyToCitizenAsync(id, "Erste Antwort.", Leader());
        await using (var db = ctx.NewContext())
        {
            var row = await db.Tickets.SingleAsync(t => t.Id == id);
            row.NudgedAt = DateTime.UtcNow.AddDays(-10); // simulates a reminder already sent on the first stretch
            await db.SaveChangesAsync();
        }

        await host.Service.ReplyToCitizenAsync(id, "Zweite Antwort, noch keine Rückmeldung nötig.", Leader());

        await using var check = ctx.NewContext();
        var after = await check.Tickets.SingleAsync(t => t.Id == id);
        Assert.Equal(TicketStatus.WartetAufBuerger, after.Status);
        Assert.Null(after.NudgedAt);
    }

    // ---- priority ----

    [Fact]
    public async Task TheDeskOrderIsComputedNotStored()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host, subject: "Vermisste Schwester", category: TicketKategorie.Vermisstenmeldung);
        await OpenAsync(host, subject: "Frage zur Fahndung", category: TicketKategorie.Auskunft);

        var byPriority = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader(),
            byPriority: true);

        // the missing person outranks the request for information, though both arrived in the same second
        Assert.Equal("Vermisste Schwester", byPriority[0].Subject);
        Assert.All(byPriority, r => Assert.False(r.PriorityIsManual));
        Assert.All(byPriority, r => Assert.InRange(r.Priority, TicketPriority.Min, TicketPriority.Max));
    }

    [Fact]
    public async Task AHandSetOrderBeatsTheComputedOne()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var missing = await IdAsync(host,
            await OpenAsync(host, subject: "Vermisste Schwester", category: TicketKategorie.Vermisstenmeldung));
        var question = await IdAsync(host,
            await OpenAsync(host, subject: "Frage zur Fahndung", category: TicketKategorie.Auskunft));

        await host.Service.SetPriorityAsync(question, TicketPriority.Max, Leader());

        var byPriority = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader(),
            byPriority: true);
        Assert.Equal("Frage zur Fahndung", byPriority[0].Subject);
        Assert.True(byPriority[0].PriorityIsManual);
        Assert.Equal(TicketPriority.Max, byPriority[0].Priority);

        var detail = await host.Service.GetAsync(missing, Leader());
        Assert.Null(detail!.PriorityOverride);
    }

    [Fact]
    public async Task ClearingTheHandSetOrderHandsTheRowBack()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.SetPriorityAsync(id, 90, Leader());

        await host.Service.SetPriorityAsync(id, null, Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Null(detail!.PriorityOverride);
        var row = Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));
        Assert.False(row.PriorityIsManual);
    }

    [Fact]
    public async Task AnOutOfRangeOrderIsClampedRatherThanRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.SetPriorityAsync(id, 9_000, Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TicketPriority.Max, detail!.PriorityOverride);
    }

    [Fact]
    public async Task SettingTheOrderIsNotActivityAndOnlyTheDeskMay()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        var before = await host.Service.GetAsync(id, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetPriorityAsync(id, 50, Junior()));

        await host.Service.SetPriorityAsync(id, 50, Leader());
        var after = await host.Service.GetAsync(id, Leader());
        Assert.Equal(before!.LastActivityAt, after!.LastActivityAt);
    }

    // ---- closing reason ----

    [Fact]
    public async Task ClosingWithoutAReasonIsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader()));

        // and the ticket stays open: the refusal happens before the write
        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TicketStatus.Offen, detail!.Status);
        Assert.Null(detail.ClosedAt);
    }

    [Fact]
    public async Task TheClosureKeepsItsReasonAndNote()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(),
            TicketAbschlussgrund.Spam, "  Massenmail, drittes Konto  ");

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TicketAbschlussgrund.Spam, detail!.ClosingReason);
        Assert.Equal("Massenmail, drittes Konto", detail.ClosingNote);
        Assert.NotNull(detail.ClosedAt);
    }

    [Fact]
    public async Task ReopeningClearsTheWholeClosure()
    {
        // one set, not a history: a reopened ticket must not keep claiming it was closed as spam
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(),
            TicketAbschlussgrund.Spam, "Massenmail");

        await host.Service.SetStatusAsync(id, TicketStatus.InBearbeitung, Leader());

        var detail = await host.Service.GetAsync(id, Leader());
        Assert.Null(detail!.ClosedAt);
        Assert.Null(detail.ClosedByCodename);
        Assert.Null(detail.ClosingReason);
        Assert.Null(detail.ClosingNote);
    }

    [Fact]
    public async Task TheCitizenIsNeverToldWhy()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(),
            TicketAbschlussgrund.Spam, "Massenmail");

        // the outward record has no field for it at all; this pins that the wording stays the neutral one
        var own = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Equal(TicketStatus.Geschlossen, own!.Status);
        Assert.Equal("Abgeschlossen", TicketStatusDisplay.CitizenName(own.Status));
        Assert.DoesNotContain("Massenmail", string.Join(" ", own.Messages.Select(m => m.Text)));
    }

    [Fact]
    public async Task AnOverlongClosingNoteIsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(),
                TicketAbschlussgrund.Erledigt, new string('x', TicketRules.ClosingNoteMaxLength + 1)));
    }

    // ---- category ----

    [Fact]
    public async Task TheCitizensPickTravelsOntoTheTicket()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host, category: TicketKategorie.Vermisstenmeldung));

        var detail = await host.Service.GetAsync(id, Leader());

        Assert.Equal(TicketKategorie.Vermisstenmeldung, detail!.Category);
    }

    [Fact]
    public async Task AnInternalTicketLandsOnTheDefaultConcern()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());

        var row = Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));

        Assert.Equal(TicketKategorie.Sonstiges, row.Category);
    }

    [Fact]
    public async Task TheDeskCorrectsTheConcern()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host, category: TicketKategorie.Auskunft));
        var before = await host.Service.GetAsync(id, Leader());

        await host.Service.SetCategoryAsync(id, TicketKategorie.Beschwerde, Leader());

        var after = await host.Service.GetAsync(id, Leader());
        Assert.Equal(TicketKategorie.Beschwerde, after!.Category);
        // a correction of the filing is not activity: it must not jump the ticket to the top of the desk
        Assert.Equal(before!.LastActivityAt, after.LastActivityAt);
        // and it says nothing to the citizen
        await host.Notifications.DidNotReceive().NotifyOnceAsync(Arg.Any<string>(),
            NotificationType.PublicTicketAnswered, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnlyTheDeskMayCorrectTheConcern()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetCategoryAsync(id, TicketKategorie.Beschwerde, Junior()));
    }

    [Fact]
    public async Task TheDeskFiltersOneConcernOutOfATab()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host, subject: "Vermisste Schwester", category: TicketKategorie.Vermisstenmeldung);
        await OpenAsync(host, subject: "Frage zur Fahndung", category: TicketKategorie.Auskunft);

        var all = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader());
        var missing = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader(),
            TicketKategorie.Vermisstenmeldung);

        Assert.Equal(2, all.Count);
        Assert.Equal(TicketKategorie.Vermisstenmeldung, Assert.Single(missing).Category);
    }

    // ---- internal tickets ----

    [Fact]
    public async Task AnAgentOpensAnInternalTicketAndIsAttachedToIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());

        await using var db = host.Factory.CreateDbContext();
        var row = await db.Tickets.SingleAsync(t => t.CaseNumber == caseNumber);
        Assert.Equal(TicketArt.Intern, row.Kind);
        Assert.Null(row.CitizenProfileId);
        Assert.Equal("junior", row.OpenedByAgentId);
        // the opener is attached, or they would lose their own ticket the moment they leave the page
        Assert.True(await db.TicketBeteiligte.AnyAsync(p => p.TicketId == row.Id && p.AgentId == "junior"));
        // the opening message is the first internal note; an internal ticket has no citizen thread at all
        var message = await db.TicketNachrichten.SingleAsync(m => m.TicketId == row.Id);
        Assert.Equal(TicketMessageAudience.Intern, message.Audience);
    }

    [Fact]
    public async Task AnInternalTicketIgnoresTheCitizenQuotas()
    {
        // the caps count per citizen profile, and an internal ticket has none
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        for (var i = 0; i < TicketRules.PerDay + 2; i++)
        {
            await host.Service.OpenAsAgentAsync(
                new TicketInput { Subject = $"Anliegen {i}", Text = "Ich brauche eine Entscheidung dazu." },
                [], Junior());
        }

        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(TicketRules.PerDay + 2, await db.Tickets.CountAsync(t => t.Kind == TicketArt.Intern));
    }

    [Fact]
    public async Task AnInternalTicketIsOpenedEvenWithTheModuleOff()
    {
        // the switch is the OFF button for the citizen desk, not for the agency's own correspondence
        using var ctx = await SeededAsync(ticketsOn: false);
        var host = NewHost(ctx);

        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());

        Assert.False(string.IsNullOrWhiteSpace(caseNumber));
    }

    [Fact]
    public async Task AnInternalTicketOpensAtTheDesk()
    {
        // no citizen behind it, so the detail projection must survive the LEFT JOIN coming back empty
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());
        var id = await IdAsync(host, caseNumber);

        var detail = await host.Service.GetAsync(id, Junior());

        Assert.NotNull(detail);
        Assert.Equal(TicketArt.Intern, detail!.Kind);
        Assert.Equal("Wren", detail.OpenedByCodename);
        Assert.Equal(string.Empty, detail.CitizenName);
        Assert.False(detail.CitizenIsBlocked);
    }

    [Fact]
    public async Task ACitizenCannotOpenAnInternalTicket()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Citizen()));
    }

    [Fact]
    public async Task TheOwnParticipationListShowsUnreadInternalNotesOfOthersOnly()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await host.Service.OpenAsAgentAsync(
            new TicketInput { Subject = "Frage zur Dienstplanung", Text = "Ich brauche eine Entscheidung dazu." },
            [], Junior());
        var id = await IdAsync(host, caseNumber);
        await host.Service.PostInternalNoteAsync(id, "Antwort der Führung dazu.", Leader());

        var mine = Assert.Single(await host.Service.GetMyParticipationsAsync(Junior()));

        Assert.Equal(caseNumber, mine.CaseNumber);
        // the opener's own line does not count against them
        Assert.Equal(1, mine.UnreadInternal);
    }

    [Fact]
    public async Task The_read_only_supervision_reads_the_desk_but_cannot_answer()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        var rows = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Supervision());
        Assert.Single(rows);
        Assert.NotNull(await host.Service.GetAsync(id, Supervision()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.ReplyToCitizenAsync(id, "Wir prüfen das.", Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Supervision(), TicketAbschlussgrund.Erledigt));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.Service.PostInternalNoteAsync(id, "Notiz", Supervision()));
    }

    [Fact]
    public async Task A_foreign_ticket_is_simply_not_found_for_another_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);

        Assert.Null(await host.Service.GetOwnDetailAsync(caseNumber, Citizen(OtherUserId)));
        Assert.Empty(await host.Service.GetOwnAsync(Citizen(OtherUserId)));
    }

    [Fact]
    public async Task An_account_without_a_civilian_identity_sees_the_page_without_rows()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host);

        Assert.Empty(await host.Service.GetOwnAsync(Junior()));
        Assert.Equal(0, await host.Service.GetOwnUnreadCountAsync(Junior()));
    }

    // ---- the conversation ----

    [Fact]
    public async Task An_agency_answer_carries_no_agent_and_waits_for_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        await host.Service.ReplyToCitizenAsync(id, "Bitte senden Sie uns den Kaufvertrag.", Leader());

        await using var db = ctx.NewContext();
        var answer = await db.TicketNachrichten
            .Where(m => m.Audience == TicketMessageAudience.Buerger && !m.AuthorIsCitizen)
            .SingleAsync();
        Assert.Null(answer.AuthorAgentId);
        var row = await db.Tickets.SingleAsync();
        Assert.Equal(TicketStatus.WartetAufBuerger, row.Status);
        Assert.Equal("lead", row.HandlerId);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Equal(2, detail!.Messages.Count);
        // the outward projection has no author field at all, so there is nothing to strip
        Assert.DoesNotContain("AuthorCodename",
            typeof(CitizenTicketMessage).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task A_citizen_answer_moves_a_waiting_ticket_back_into_handling()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        await host.Service.ReplyToCitizenAsync(id, "Bitte senden Sie uns den Kaufvertrag.", Leader());

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Hier ist der Kaufvertrag vom 3. Mai.", Citizen());

        await using var db = ctx.NewContext();
        Assert.Equal(TicketStatus.InBearbeitung, (await db.Tickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_citizen_answer_leaves_an_untouched_ticket_open()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);

        await host.Service.ReplyAsCitizenAsync(caseNumber, "Ein Nachtrag zu meinem Anliegen von vorhin.", Citizen());

        await using var db = ctx.NewContext();
        Assert.Equal(TicketStatus.Offen, (await db.Tickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task Closed_is_closed_for_the_citizen_and_leadership_may_reopen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);

        Assert.False((await host.Service.GetOwnDetailAsync(caseNumber, Citizen()))!.MayReply);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Service.ReplyAsCitizenAsync(caseNumber, "Doch noch eine Frage dazu.", Citizen()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Service.ReplyToCitizenAsync(id, "Nachtrag.", Leader()));

        await host.Service.SetStatusAsync(id, TicketStatus.InBearbeitung, Leader());
        await host.Service.ReplyAsCitizenAsync(caseNumber, "Doch noch eine Frage dazu.", Citizen());

        await using var db = ctx.NewContext();
        var row = await db.Tickets.SingleAsync();
        Assert.Null(row.ClosedAt);
        Assert.Null(row.ClosedById);
    }

    [Fact]
    public async Task An_internal_note_never_reaches_the_citizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        await host.Service.PostInternalNoteAsync(id, "Halter laut Register weiterhin der Bürger.", Leader());

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        Assert.Single(detail!.Messages);
        var internals = await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader());
        Assert.Single(internals);
        Assert.Equal("Falcon", internals[0].AuthorCodename);
    }

    [Fact]
    public async Task Only_a_status_that_concerns_the_citizen_rings_their_bell()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        // taking the ticket on is news to the desk, not to the citizen
        await host.Service.AssignSelfAsync(id, Leader());
        await host.Notifications.DidNotReceive().NotifyOnceAsync(Arg.Any<string>(),
            NotificationType.PublicTicketAnswered, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
        await host.Notifications.Received(1).NotifyOnceAsync(CitizenUserId,
            NotificationType.PublicTicketAnswered, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unsupported_status_move_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Service.SetStatusAsync(id, TicketStatus.Offen, Leader()));
    }

    // ---- editing a line afterwards ----

    /// <summary>Hands one message to another account; <see cref="FixedUser"/> stamps every fixture write as
    /// "lead", so the author of a line has to be set by hand to test anyone else.</summary>
    private static async Task HandOverAsync(SqliteTestContext ctx, string messageId, string agentId)
    {
        // no interceptors on this context, so nothing re-stamps and ModifiedAt stays null
        await using var db = ctx.NewContext();
        var row = await db.TicketNachrichten.SingleAsync(m => m.Id == messageId);
        row.CreatedById = agentId;
        row.AuthorAgentId = row.Audience == TicketMessageAudience.Intern ? agentId : null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AHandlerRewritesHisOwnAnswerToTheCitizen()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        await host.Service.ReplyToCitizenAsync(id, "Ihr Fahrzeug bleibt ausgeschrieben.", Leader());
        var message = (await host.Service.GetMessagesAsync(id, TicketMessageAudience.Buerger, Leader()))
            .Single(m => !m.FromCitizen);
        Assert.Null(message.EditedAt);
        Assert.True(message.Mine);

        await host.Service.EditMessageAsync(message.Id, "  Ihr Fahrzeug bleibt vorerst ausgeschrieben.  ", Leader());

        var outside = (await host.Service.GetOwnDetailAsync(caseNumber, Citizen()))!.Messages
            .Single(m => !m.FromCitizen && m.EditedAt is not null);
        Assert.Equal("Ihr Fahrzeug bleibt vorerst ausgeschrieben.", outside.Text);
    }

    /// <summary>The text is the one field the change protocol never carries; /nachweis is open to every agent,
    /// while a ticket itself is leadership-only.</summary>
    [Fact]
    public async Task AnEditIsAuditedWithoutTheWording()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Alter Wortlaut.", Leader());
        var message = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        await host.Service.EditMessageAsync(message.Id, "Neuer Wortlaut.", Leader());

        await using var db = ctx.NewContext();
        var row = await db.AuditLogs.SingleAsync(
            a => a.EntityType == nameof(TicketNachricht) && a.Action == AuditAction.Modified);
        Assert.Equal("lead", row.AgentId);
        // no ChangesJson at all: Text was the only changed field, and it is redacted
        Assert.Null(row.ChangesJson);
    }

    [Fact]
    public async Task AnAttachedAgentRewritesHisOwnNoteButNoCitizenLine()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.AddParticipantAsync(id, "junior", Leader());
        await host.Service.PostInternalNoteAsync(id, "Vermerk des Beteiligten.", Leader());
        await host.Service.ReplyToCitizenAsync(id, "Wir prüfen das.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Junior()));
        var answer = (await host.Service.GetMessagesAsync(id, TicketMessageAudience.Buerger, Junior()))
            .Single(m => !m.FromCitizen);
        await HandOverAsync(ctx, note.Id, "junior");
        await HandOverAsync(ctx, answer.Id, "junior");

        await host.Service.EditMessageAsync(note.Id, "Vermerk ergänzt.", Junior());
        Assert.Equal("Vermerk ergänzt.", Assert.Single(
            await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Junior())).Text);

        // the citizen thread belongs to the desk, whoever wrote the line
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(answer.Id, "Anders", Junior()));
    }

    [Fact]
    public async Task AnAgentWhoIsNotAttachedMayNotRewriteAnInternalNote()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));
        await HandOverAsync(ctx, note.Id, "junior");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(note.Id, "Anders", Junior()));
    }

    [Fact]
    public async Task TheLineTheCitizenWroteIsNeverEditableFromTheDesk()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        var citizenLine = (await host.Service.GetMessagesAsync(id, TicketMessageAudience.Buerger, Leader()))
            .Single(m => m.FromCitizen);

        // the fixture stamps every row with the same account, so the author check alone would have let this through
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(citizenLine.Id, "Umgeschrieben", Leader()));
    }

    [Fact]
    public async Task AForeignLineIsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));
        await HandOverAsync(ctx, note.Id, "lead2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(note.Id, "Umformuliert", Leader()));
    }

    /// <summary>A typo is usually noticed after the ticket is closed, so the correction must survive the closure —
    /// and it must not look like activity the citizen is waiting on.</summary>
    [Fact]
    public async Task AClosedTicketStillTakesACorrectionWithoutMovingAnything()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));
        await host.Service.SetStatusAsync(id, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
        var before = (await host.Service.GetAsync(id, Leader()))!;

        await host.Service.EditMessageAsync(note.Id, "Vermerk berichtigt.", Leader());

        var after = (await host.Service.GetAsync(id, Leader()))!;
        Assert.Equal(TicketStatus.Geschlossen, after.Status);
        Assert.Equal(before.LastActivityAt, after.LastActivityAt);
        Assert.Equal("Vermerk berichtigt.", Assert.Single(
            await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader())).Text);
    }

    [Fact]
    public async Task AnUnchangedRewriteWritesNothing()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        await host.Service.EditMessageAsync(note.Id, "Vermerk der Führung.", Leader());

        Assert.Null(Assert.Single(
            await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader())).EditedAt);
        await using var db = ctx.NewContext();
        Assert.Empty(await db.AuditLogs
            .Where(a => a.EntityType == nameof(TicketNachricht) && a.Action == AuditAction.Modified)
            .ToListAsync());
    }

    [Fact]
    public async Task AnEmptyOrOversizedRewriteIsRefused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(note.Id, "   ", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.EditMessageAsync(
                note.Id, new string('x', TicketRules.MaxMessageLength + 1), Leader()));
    }

    [Fact]
    public async Task NeitherTheSupervisionNorAPartnerNorACitizenMayRewriteALine()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.PostInternalNoteAsync(id, "Vermerk der Führung.", Leader());
        var note = Assert.Single(await host.Service.GetMessagesAsync(id, TicketMessageAudience.Intern, Leader()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(note.Id, "Anders", Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(note.Id, "Anders", Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.EditMessageAsync(note.Id, "Anders", Citizen()));
    }

    // ---- read marks and counters ----

    [Fact]
    public async Task Each_side_moves_only_its_own_read_mark()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);
        await host.Service.ReplyToCitizenAsync(id, "Wir haben Ihr Anliegen erhalten.", Leader());

        Assert.Equal(1, await host.Service.GetOwnUnreadCountAsync(Citizen()));
        var deskBefore = await host.Service.GetInboxAsync(TicketInboxScope.Wartet, null, false, Leader());
        Assert.Single(deskBefore);
        Assert.Equal(1, deskBefore[0].UnreadCount);

        // the citizen reading clears the citizen count and leaves the desk count alone
        await host.Service.MarkCitizenReadAsync(caseNumber, Citizen());
        Assert.Equal(0, await host.Service.GetOwnUnreadCountAsync(Citizen()));
        var deskAfter = await host.Service.GetInboxAsync(TicketInboxScope.Wartet, null, false, Leader());
        Assert.Equal(1, deskAfter[0].UnreadCount);

        await host.Service.MarkAgentReadAsync(id, Leader());
        var deskRead = await host.Service.GetInboxAsync(TicketInboxScope.Wartet, null, false, Leader());
        Assert.Equal(0, deskRead[0].UnreadCount);
    }

    [Fact]
    public async Task The_supervision_sets_no_read_mark()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.MarkAgentReadAsync(id, Supervision());

        await using var db = ctx.NewContext();
        Assert.Null((await db.Tickets.SingleAsync()).AgentLastReadAt);
    }

    [Fact]
    public async Task The_badge_counts_running_tickets_only()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var first = await IdAsync(host, await OpenAsync(host));
        await OpenAsync(host);

        Assert.Equal(2, await host.Service.GetOpenCountAsync());
        await host.Service.SetStatusAsync(first, TicketStatus.Geschlossen, Leader(), TicketAbschlussgrund.Erledigt);
        Assert.Equal(1, await host.Service.GetOpenCountAsync());

        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));
        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Geschlossen, null, false, Leader()));
    }

    [Fact]
    public async Task The_desk_marks_a_ticket_awaiting_an_answer_while_the_citizen_spoke_last()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);
        var id = await IdAsync(host, caseNumber);

        var open = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader());
        Assert.True(open[0].AwaitingAnswer);
        Assert.Equal("Erika Musterfrau", open[0].CitizenName);

        await host.Service.ReplyToCitizenAsync(id, "Wir melden uns.", Leader());
        var waiting = await host.Service.GetInboxAsync(TicketInboxScope.Wartet, null, false, Leader());
        Assert.False(waiting[0].AwaitingAnswer);
    }

    [Fact]
    public async Task The_desk_search_finds_by_case_number_subject_and_citizen_name()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);

        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, caseNumber, false, Leader()));
        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, "Fahrzeug", false, Leader()));
        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, "Musterfrau", false, Leader()));
        Assert.Empty(await host.Service.GetInboxAsync(TicketInboxScope.Offen, "Kleber", false, Leader()));
    }

    // ---- trash ----

    [Fact]
    public async Task Deleting_is_a_soft_delete_and_restoring_brings_it_back()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await host.Service.DeleteAsync(id, Leader());
        Assert.Empty(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));
        var trash = await host.Service.GetTrashAsync();
        Assert.Single(trash);

        await host.Service.RestoreAsync(id, Leader());
        Assert.Single(await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader()));
        Assert.Empty(await host.Service.GetTrashAsync());
    }

    [Fact]
    public async Task The_trash_row_names_the_subject_but_not_the_citizen_or_the_conversation()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));
        await host.Service.DeleteAsync(id, Leader());

        var row = TrashProjection.Ticket((await host.Service.GetTrashAsync()).Single());

        Assert.Equal("tickets", row.Kind);
        Assert.Equal("Frage zu meinem Fahrzeug", row.Title);
        Assert.DoesNotContain("Musterfrau", row.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Kennzeichen", row.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_leadership_may_delete_or_restore()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await IdAsync(host, await OpenAsync(host));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.DeleteAsync(id, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.RestoreAsync(id, Supervision()));
    }

    // ---- the automatic confirmation ----

    private static async Task SeedConfirmationAsync(SqliteTestContext ctx, bool active = true)
    {
        await new PublicTemplateService(ctx.Factory).SaveAsync(
            new PublicTemplateInput(null, PublicTemplateKind.TicketEingang, "Eingang",
                "Guten Tag BUERGER, Ihr Anliegen AKTENZEICHEN ist eingegangen. Mit Gruss NAME", active, 10),
            Leader());
    }

    [Fact]
    public async Task Opening_with_an_active_template_confirms_without_naming_an_agent()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await OpenAsync(host);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var confirmation = Assert.Single(detail!.Messages, m => !m.FromCitizen);
        Assert.Contains("Erika Musterfrau", confirmation.Text, StringComparison.Ordinal);
        Assert.Contains(caseNumber, confirmation.Text, StringComparison.Ordinal);
        Assert.Contains(PublicTemplateRenderer.Redaction, confirmation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Falcon", confirmation.Text, StringComparison.Ordinal);

        await using var db = host.Factory.CreateDbContext();
        var row = await db.TicketNachrichten.SingleAsync(m => !m.AuthorIsCitizen);
        Assert.Null(row.AuthorAgentId);
        Assert.Equal(TicketMessageAudience.Buerger, row.Audience);
    }

    [Fact]
    public async Task The_confirmation_moves_no_status_and_rings_no_bell()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        var caseNumber = await OpenAsync(host);

        // an untouched ticket stays Offen; anything else would claim work nobody did
        var ticket = await host.Service.GetAsync(await IdAsync(host, caseNumber), Leader());
        Assert.Equal(TicketStatus.Offen, ticket!.Status);
        // the unread counter shows the confirmation by itself
        Assert.Equal(1, await host.Service.GetOwnUnreadCountAsync(Citizen()));
        await host.Notifications.DidNotReceive().NotifyManyOnceAsync(
            Arg.Any<IReadOnlyList<string>>(), NotificationType.PublicTicketAnswered, Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_an_active_template_no_confirmation_is_written()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx, active: false);

        var caseNumber = await OpenAsync(host);

        var detail = await host.Service.GetOwnDetailAsync(caseNumber, Citizen());
        var only = Assert.Single(detail!.Messages);
        Assert.True(only.FromCitizen);
        // and nothing leaked into the internal thread either
        Assert.Empty(await host.Service.GetMessagesAsync(await IdAsync(host, caseNumber),
            TicketMessageAudience.Intern, Leader()));
    }

    [Fact]
    public async Task A_fresh_ticket_still_awaits_an_answer_although_the_confirmation_is_newer()
    {
        // the audit interceptor stamps one timestamp per SaveChanges, so the opening message and the automatic
        // confirmation are simultaneous — without the tie-break the desk chip would go dark on every new ticket
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await SeedConfirmationAsync(ctx);

        await OpenAsync(host);

        var open = await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader());
        Assert.True(open[0].AwaitingAnswer);
        // and the desk's own unread counter ignores the agency line
        Assert.Equal(1, open[0].UnreadCount);
    }

    // ---- the link picker ----

    [Fact]
    public async Task ThePicker_findsATicket_byCaseNumberAndBySubject()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var caseNumber = await OpenAsync(host);

        var byNumber = await host.Service.SearchForLinkAsync(caseNumber, Leader());
        Assert.Equal(caseNumber, Assert.Single(byNumber).CaseNumber);

        var bySubject = await host.Service.SearchForLinkAsync("Fahrzeug", Leader());
        Assert.Equal(caseNumber, Assert.Single(bySubject).CaseNumber);

        Assert.NotEmpty(await host.Service.SearchForLinkAsync(null, Leader()));
    }

    [Fact]
    public async Task ThePicker_doesNotSearchTheCitizenName()
    {
        // the desk's own inbox does; this list feeds a link, and a hit by name would answer "which citizen writes
        // about this record" for anyone who can open the dialog
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host);

        Assert.Empty(await host.Service.SearchForLinkAsync("Musterfrau", Leader()));
        Assert.NotEmpty(await host.Service.GetInboxAsync(TicketInboxScope.Offen, "Musterfrau", false, Leader()));
    }

    [Fact]
    public async Task ThePicker_isTheDesks()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SearchForLinkAsync(null, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SearchForLinkAsync(null, Partner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SearchForLinkAsync(null, Citizen()));
        // the read-only supervision is part of RequireTicketRead and may pick
        Assert.NotEmpty(await host.Service.SearchForLinkAsync(null, Supervision()));
    }

    [Fact]
    public async Task ThePicker_leavesADeletedTicketOut()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await OpenAsync(host);
        var id = (await host.Service.GetInboxAsync(TicketInboxScope.Offen, null, false, Leader())).Single().Id;

        await host.Service.DeleteAsync(id, Leader());

        Assert.Empty(await host.Service.SearchForLinkAsync(null, Leader()));
    }
}
