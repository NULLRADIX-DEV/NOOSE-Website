using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Authorization;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>What the write barrier lets a partner through, and what it still refuses.</summary>
/// <remarks>
/// The barrier is the last word on a partner write: the service guards are looser for the citizen area, so a hole
/// here would open the whole record surface. The audit interceptor is deliberately NOT attached — this fixture asks
/// what the barrier does, so the rows carry their author by hand.
/// </remarks>
public sealed class ReadOnlyBarrierInterceptorTests
{
    private const string PartnerId = "partner";
    private const string ProfileId = "profil1";

    private sealed class FixedUser(CurrentUserInfo info) : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(info);

        public CurrentUserInfo Get() => info;
    }

    private static CurrentUserInfo Partner() => new(PartnerId, "Ward", false, true, false);

    private static CurrentUserInfo OnlyReader() => new("aufsicht", "Owl", true, false, false);

    private static CurrentUserInfo Demo() => new("demo-agent", "Demo", false, false, true);

    /// <summary>A context on the shared database with the barrier attached, acting as the given user.</summary>
    private static AppDbContext Guarded(SqliteTestContext ctx, CurrentUserInfo user)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new ReadOnlyBarrierInterceptor(new FixedUser(user)))
            .Options);

    /// <summary>Barrier plus audit interceptor, in the production order.</summary>
    /// <remarks>
    /// The own-row carve-out compares <c>CreatedById</c> against the acting user, and that column is written by the
    /// audit interceptor rather than by any service. Only this pairing shows that the two agree — the barrier-only
    /// fixture sets the author by hand, and the service fixtures run without the barrier.
    /// </remarks>
    private static AppDbContext FullChain(SqliteTestContext ctx, CurrentUserInfo user)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(
                new ReadOnlyBarrierInterceptor(new FixedUser(user)),
                new AuditSaveChangesInterceptor(new FixedUser(user)))
            .Options);

    /// <summary>Opens a ticket and answers in it through the full chain, exactly as the service does.</summary>
    private static async Task OpenAndAnswerAsync(SqliteTestContext ctx, CurrentUserInfo user)
    {
        await using (var open = FullChain(ctx, user))
        {
            // no CreatedById by hand: the audit interceptor stamps it, and that is the point of this test
            open.Tickets.Add(new Ticket
            {
                Id = "t1",
                CaseNumber = "NOOSE-T-2026-0001",
                CitizenProfileId = ProfileId,
                Status = TicketStatus.Offen,
                Subject = "Anfrage an die Führungsebene",
                LastActivityAt = DateTime.UtcNow,
            });
            open.TicketNachrichten.Add(new TicketNachricht
            {
                TicketId = "t1",
                Audience = TicketMessageAudience.Buerger,
                Text = "Wir bitten um eine Einschätzung zu einem laufenden Verfahren.",
                AuthorIsCitizen = true,
            });
            await open.SaveChangesAsync();
        }

        await using var reply = FullChain(ctx, user);
        var row = await reply.Tickets.SingleAsync();
        row.Status = TicketStatus.InBearbeitung;
        row.LastActivityAt = DateTime.UtcNow;
        reply.TicketNachrichten.Add(new TicketNachricht
        {
            TicketId = row.Id,
            Audience = TicketMessageAudience.Buerger,
            Text = "Eine Ergänzung dazu.",
            AuthorIsCitizen = true,
        });
        await reply.SaveChangesAsync();
    }

    private static Ticket Ticket(string id, string author) => new()
    {
        Id = id,
        CaseNumber = $"NOOSE-T-2026-{id}",
        CitizenProfileId = ProfileId,
        Status = TicketStatus.Offen,
        Subject = "Anfrage an die Führungsebene",
        LastActivityAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        CreatedById = author,
    };

    [Fact]
    public async Task A_partner_may_file_an_inquiry_but_never_a_release()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = Guarded(ctx, Partner()))
        {
            db.Requests.Add(new NOOSE_Website.Data.Entities.Requests.Request
            {
                Type = RequestType.PartnerAnfrage, TargetType = "PartnerAnfrage", TargetId = "", TargetDesignation = "John Doe",
                Justification = "Ausschuss", CreatedById = PartnerId,
            });
            await db.SaveChangesAsync();
        }

        await using (var release = Guarded(ctx, Partner()))
        {
            release.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = "p1", Agency = PartnerAgency.Parlament });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => release.SaveChangesAsync());
        }

        // any other request type stays closed
        await using var upgrade = Guarded(ctx, Partner());
        upgrade.Requests.Add(new NOOSE_Website.Data.Entities.Requests.Request
        {
            Type = RequestType.Upgrade, TargetType = "Person", TargetId = "p1", TargetDesignation = "John Doe", CreatedById = PartnerId,
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => upgrade.SaveChangesAsync());
    }

    [Fact]
    public async Task A_partner_may_write_their_own_civilian_identity()
    {
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Partner());

        db.BuergerProfile.Add(new BuergerProfil { Id = ProfileId, UserId = PartnerId, FirstName = "Trevor", LastName = "Ward" });

        await db.SaveChangesAsync();
        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.BuergerProfile.CountAsync());
    }

    [Fact]
    public async Task A_partner_may_open_a_ticket_with_its_first_message()
    {
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Partner());

        db.Tickets.Add(Ticket("0001", PartnerId));
        db.TicketNachrichten.Add(new TicketNachricht
        {
            TicketId = "0001",
            Audience = TicketMessageAudience.Buerger,
            Text = "Wir bitten um eine Einschätzung zu einem laufenden Verfahren.",
            AuthorIsCitizen = true,
        });

        await db.SaveChangesAsync();
        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.Tickets.CountAsync());
    }

    [Fact]
    public async Task A_partner_may_answer_in_a_ticket_they_opened_themselves()
    {
        // the reply moves the status and the activity stamp of the ticket, so the create carve-out alone is not enough
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Tickets.Add(Ticket("0001", PartnerId));
            await seed.SaveChangesAsync();
        }

        await using var db = Guarded(ctx, Partner());
        var row = await db.Tickets.SingleAsync();
        row.Status = TicketStatus.InBearbeitung;
        row.LastActivityAt = DateTime.UtcNow;
        db.TicketNachrichten.Add(new TicketNachricht
        {
            TicketId = row.Id,
            Audience = TicketMessageAudience.Buerger,
            Text = "Wir hängen den Vorgang an unsere Akte an.",
            AuthorIsCitizen = true,
        });

        await db.SaveChangesAsync();
        await using var check = ctx.NewContext();
        Assert.Equal(TicketStatus.InBearbeitung, (await check.Tickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_partner_may_file_a_tip_with_its_first_message()
    {
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Partner());

        db.Hinweise.Add(new Hinweis
        {
            Id = "h1",
            CaseNumber = "NOOSE-H-2026-0001",
            CitizenProfileId = ProfileId,
            Status = TipStatus.Neu,
            Text = "Der gesuchte Van stand heute Nacht am Hafen.",
        });
        db.HinweisNachrichten.Add(new HinweisNachricht
        {
            HinweisId = "h1",
            Audience = TipMessageAudience.Buerger,
            Text = "Das Kennzeichen kam aus Blaine County.",
            AuthorIsCitizen = true,
        });

        await db.SaveChangesAsync();
        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.Hinweise.CountAsync());
    }

    [Fact]
    public async Task A_partner_answers_in_its_own_ticket_through_the_whole_interceptor_chain()
    {
        using var ctx = new SqliteTestContext();

        await OpenAndAnswerAsync(ctx, Partner());

        await using var check = ctx.NewContext();
        var row = await check.Tickets.SingleAsync();
        Assert.Equal(PartnerId, row.CreatedById);
        Assert.Equal(TicketStatus.InBearbeitung, row.Status);
        Assert.Equal(2, await check.TicketNachrichten.CountAsync());
    }

    [Fact]
    public async Task The_read_only_supervision_answers_in_its_own_ticket_through_the_whole_chain()
    {
        using var ctx = new SqliteTestContext();

        await OpenAndAnswerAsync(ctx, OnlyReader());

        await using var check = ctx.NewContext();
        var row = await check.Tickets.SingleAsync();
        Assert.Equal("aufsicht", row.CreatedById);
        Assert.Equal(TicketStatus.InBearbeitung, row.Status);
    }

    [Fact]
    public async Task A_partner_may_not_touch_a_ticket_somebody_else_opened()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Tickets.Add(Ticket("0001", "buerger1"));
            await seed.SaveChangesAsync();
        }

        await using var db = Guarded(ctx, Partner());
        var row = await db.Tickets.SingleAsync();
        row.Status = TicketStatus.Geschlossen;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_partner_may_not_delete_their_own_ticket()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Tickets.Add(Ticket("0001", PartnerId));
            await seed.SaveChangesAsync();
        }

        await using var db = Guarded(ctx, Partner());
        db.Tickets.Remove(await db.Tickets.SingleAsync());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_read_only_supervision_files_its_own_civilian_identity_and_ticket()
    {
        // the person behind the supervision account plays a civilian too; none of this is record material
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, OnlyReader());

        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId, UserId = "aufsicht", FirstName = "Owl", LastName = "Ward",
        });
        db.Tickets.Add(Ticket("0001", "aufsicht"));

        await db.SaveChangesAsync();
        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.Tickets.CountAsync());
    }

    [Fact]
    public async Task The_demo_visitor_files_nothing_at_all()
    {
        // the synthetic principal is shared by every anonymous guest of the demo instance, so its "own" civilian
        // identity would be everyone's — it is the one account the citizen carve-out does not cover
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Demo());

        db.Tickets.Add(Ticket("0001", "demo-agent"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_read_only_supervision_still_writes_no_agency_content()
    {
        // the second axis: what a partner may author is record material, and the supervision authors none of it
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, OnlyReader());

        db.Documents.Add(new Document { Id = "d1", Title = "Vermerk", CreatedById = "aufsicht" });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_partner_may_file_its_own_application()
    {
        // applying does not cost the partner role, so the row arrives from an account the barrier otherwise stops
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Partner());

        db.Bewerbungen.Add(new Bewerbung
        {
            CaseNumber = "NOOSE-BEW-2026-0001", ApplicantUserId = PartnerId, Name = "Trevor Ward",
        });

        await db.SaveChangesAsync();
        Assert.Equal(1, await ctx.NewContext().Bewerbungen.CountAsync());
    }

    [Fact]
    public async Task A_partner_may_answer_in_its_own_application_thread()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Bewerbungen.Add(new Bewerbung
            {
                Id = "bew1", CaseNumber = "NOOSE-BEW-2026-0001", ApplicantUserId = PartnerId, Name = "Trevor Ward",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = Guarded(ctx, Partner());
        db.BewerbungMessages.Add(new BewerbungMessage
        {
            BewerbungId = "bew1", Text = "Nachreichung", AuthorIsApplicant = true,
        });

        await db.SaveChangesAsync();
        Assert.Equal(1, await ctx.NewContext().BewerbungMessages.CountAsync());
    }

    [Fact]
    public async Task A_partner_may_save_and_resave_its_own_test_answer()
    {
        // the aptitude test autosaves, so the same row is written again until the attempt is handed in
        using var ctx = new SqliteTestContext();
        await using (var open = FullChain(ctx, Partner()))
        {
            open.BewerbungTestAnswers.Add(new BewerbungTestAnswer { Id = "a1", AssignmentId = "z1", QuestionId = "f1" });
            await open.SaveChangesAsync();
        }

        await using var db = FullChain(ctx, Partner());
        var answer = await db.BewerbungTestAnswers.SingleAsync();
        answer.FreeTextAnswer = "geänderte Antwort";

        await db.SaveChangesAsync();
        Assert.Equal("geänderte Antwort", (await ctx.NewContext().BewerbungTestAnswers.SingleAsync()).FreeTextAnswer);
    }

    [Fact]
    public async Task A_partner_may_not_touch_an_application_somebody_else_filed()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Bewerbungen.Add(new Bewerbung
            {
                Id = "bew1", CaseNumber = "NOOSE-BEW-2026-0001", ApplicantUserId = "fremd", Name = "Wer anders",
                CreatedById = "fremd",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = Guarded(ctx, Partner());
        (await db.Bewerbungen.SingleAsync()).Name = "Trevor Ward";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_read_only_supervision_may_not_file_an_application()
    {
        // an oversight seat is not a candidate; the application axis is the partner's alone
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, OnlyReader());

        db.Bewerbungen.Add(new Bewerbung
        {
            CaseNumber = "NOOSE-BEW-2026-0001", ApplicantUserId = "aufsicht", Name = "Owl",
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_demo_visitor_may_not_file_an_application()
    {
        using var ctx = new SqliteTestContext();
        await using var db = Guarded(ctx, Demo());

        db.Bewerbungen.Add(new Bewerbung
        {
            CaseNumber = "NOOSE-BEW-2026-0001", ApplicantUserId = "demo-agent", Name = "Demo",
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }
}
