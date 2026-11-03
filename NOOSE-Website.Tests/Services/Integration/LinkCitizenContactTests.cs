using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Search;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>
/// Citizen tips and citizen tickets as counterparts of a link. Two different rules on purpose: a tip is readable by
/// every internal agent, a ticket only by the desk and by the one agent attached to it — and neither ever shows more
/// than its case number, because the subject and the text are the citizen's words.
/// </summary>
public sealed class LinkCitizenContactTests
{
    private const string ProfileId = "profile-1";

    private static LinkService NewService(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IThreatScoreService>());

    private static ClaimsPrincipal Desk(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Partner(string id = "partner")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    /// <summary>A person, a tip and a ticket, plus the link from the person to whichever of the two is asked for.</summary>
    private static void SeedRows(SqliteTestContext ctx, string linkTargetType, string linkTargetId)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("junior", Rank.Trainee));
        db.People.Add(Seed.Person("p1", "Max"));
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId,
            FirstName = "Jane",
            LastName = "Doe",
        });
        db.Hinweise.Add(new Hinweis
        {
            Id = "h1",
            CaseNumber = "FIB-H-2026-0001",
            CitizenProfileId = ProfileId,
            Status = TipStatus.Neu,
            Text = "Der gesuchte Van stand heute Nacht am Hafen.",
        });
        db.Tickets.Add(new Ticket
        {
            Id = "t1",
            CaseNumber = "FIB-T-2026-0001",
            CitizenProfileId = ProfileId,
            Status = TicketStatus.Offen,
            Subject = "Beschwerde über eine Streifenkontrolle",
            LastActivityAt = DateTime.UtcNow,
        });
        db.Links.Add(new Link
        {
            SourceType = nameof(Person),
            SourceId = "p1",
            TargetType = linkTargetType,
            TargetId = linkTargetId,
        });
        db.SaveChanges();
    }

    private static void Attach(SqliteTestContext ctx, string agentId)
    {
        using var db = ctx.NewContext();
        db.TicketBeteiligte.Add(new TicketParticipant { TicketId = "t1", AgentId = agentId });
        db.SaveChanges();
    }

    // ---- the tip as a link counterpart ----

    [Fact]
    public async Task Tip_resolves_to_its_case_number_for_an_internal_agent()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Hinweis), "h1");

        var result = await NewService(ctx)
            .GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(Junior()));

        var link = Assert.Single(result);
        Assert.Equal(nameof(Hinweis), link.OtherType);
        Assert.Equal("Bürgerhinweis FIB-H-2026-0001", link.OtherDesignation);
        Assert.Equal("/hinweise/h1", link.Href);
    }

    [Fact]
    public async Task Tip_designation_names_no_citizen()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Hinweis), "h1");

        var result = await NewService(ctx)
            .GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(Desk()));

        var link = Assert.Single(result);
        Assert.DoesNotContain("Jane", link.OtherDesignation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Doe", link.OtherDesignation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hafen", link.OtherDesignation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tip_is_not_visible_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        using var db = ctx.NewContext();
        db.Hinweise.Add(new Hinweis
        {
            Id = "h1",
            CaseNumber = "FIB-H-2026-0001",
            CitizenProfileId = ProfileId,
            Status = TipStatus.Neu,
            Text = "Der gesuchte Van stand heute Nacht am Hafen.",
        });
        db.SaveChanges();

        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Hinweis), "h1", ViewerScope.From(Junior())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Hinweis), "h1", ViewerScope.From(Partner())));
    }

    // ---- the ticket as a link counterpart ----

    [Fact]
    public async Task Ticket_resolves_to_its_case_number_for_the_desk()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");

        var result = await NewService(ctx)
            .GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(Desk()));

        var link = Assert.Single(result);
        Assert.Equal(nameof(Ticket), link.OtherType);
        Assert.Equal("Bürger-Ticket FIB-T-2026-0001", link.OtherDesignation);
        Assert.Equal("/tickets/t1", link.Href);
        // the subject is the citizen's own wording and stays on the ticket page
        Assert.DoesNotContain("Streifenkontrolle", link.OtherDesignation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ticket_resolves_for_the_agent_attached_to_it()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");
        Attach(ctx, "junior");

        var result = await NewService(ctx)
            .GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(Junior()));

        var link = Assert.Single(result);
        Assert.Equal("Bürger-Ticket FIB-T-2026-0001", link.OtherDesignation);
    }

    [Fact]
    public async Task Ticket_is_hidden_whole_from_an_uninvolved_agent()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");

        var result = await NewService(ctx)
            .GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(Junior()));

        // not a row carrying the raw id either: an unresolved KNOWN type drops out, and that is what keeps the
        // existence of the correspondence out of the record
        Assert.Empty(result);
    }

    [Fact]
    public async Task Ticket_visibility_follows_the_desk_and_the_participant()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");

        using var db = ctx.NewContext();
        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Ticket), "t1", ViewerScope.From(Desk())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Ticket), "t1", ViewerScope.From(Junior())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Ticket), "t1", ViewerScope.From(Partner())));

        Attach(ctx, "junior");
        using var after = ctx.NewContext();
        Assert.True(await Visibility.IsRecordVisibleAsync(after, nameof(Ticket), "t1", ViewerScope.From(Junior())));
    }

    [Fact]
    public async Task Unknown_ticket_id_is_not_visible()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");

        using var db = ctx.NewContext();
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Ticket), "ghost", ViewerScope.From(Desk())));
    }

    // ---- creating such a link ----

    [Fact]
    public async Task CreateAsync_refuses_a_ticket_the_actor_may_not_open()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Hinweis), "h1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService(ctx)
            .CreateAsync(nameof(Person), "p1", nameof(Ticket), "t1", null, Junior()));
    }

    [Fact]
    public async Task CreateAsync_accepts_a_ticket_for_the_desk()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Hinweis), "h1");

        await NewService(ctx).CreateAsync(nameof(Person), "p1", nameof(Ticket), "t1", null, Desk());

        using var db = ctx.NewContext();
        Assert.True(db.Links.Any(v => v.TargetType == nameof(Ticket) && v.TargetId == "t1"));
    }

    [Fact]
    public async Task CreateAsync_refuses_a_tip_for_a_partner()
    {
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService(ctx)
            .CreateAsync(nameof(Person), "p1", nameof(Hinweis), "h1", null, Partner()));
    }

    // ---- the cross-reference resolver ----

    [Fact]
    public async Task RecordsReference_marks_a_ticket_classified()
    {
        // TimelineService.CounterpartDisplay takes no rank: a classified reference reads "verdeckte Akte" for every
        // viewer. Flipping this to false would write the ticket number onto the timeline of every internal agent,
        // which is precisely what this resolver cannot decide — it has no participant list
        using var ctx = new SqliteTestContext();
        SeedRows(ctx, nameof(Ticket), "t1");
        using var db = ctx.NewContext();

        var map = await RecordsReference.ResolveAsync(db, [(nameof(Ticket), "t1"), (nameof(Hinweis), "h1")]);

        var ticket = map[(nameof(Ticket), "t1")];
        Assert.True(ticket.Classified);
        Assert.Equal("Bürger-Ticket FIB-T-2026-0001", ticket.Display);
        Assert.DoesNotContain("Streifenkontrolle", ticket.Display, StringComparison.OrdinalIgnoreCase);

        // the tip is the deliberate counterexample: every internal agent may read it, so it keeps its case number
        Assert.False(map[(nameof(Hinweis), "h1")].Classified);
    }

    // ---- the drift guard behind the group headings ----

    [Fact]
    public void Every_linkable_type_has_a_catalog_row()
    {
        // a known type without a catalog row renders its raw CLR name as a group heading, which is exactly what
        // the copied TypeDisplay switches used to do for Law, Meeting and Hinweis
        var missing = LinkService.KnownTypes.Where(t => SearchCatalog.Find(t) is null).ToArray();

        Assert.True(missing.Length == 0,
            "Jeder verknüpfbare Typ braucht eine SearchCatalog-Zeile: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_linkable_type_has_a_label_a_plural_and_an_icon()
    {
        foreach (var type in LinkService.KnownTypes)
        {
            Assert.False(string.IsNullOrWhiteSpace(RecordTypeDisplay.Name(type)));
            Assert.False(string.IsNullOrWhiteSpace(RecordTypeDisplay.Plural(type)));
            Assert.NotEqual(MudBlazor.Icons.Material.Filled.Hub, RecordTypeDisplay.Icon(type));
        }
    }

    [Fact]
    public void Citizen_contact_types_are_named_in_German()
    {
        Assert.Equal("Bürgerhinweis", RecordTypeDisplay.Name(nameof(Hinweis)));
        Assert.Equal("Bürgerhinweise", RecordTypeDisplay.Plural(nameof(Hinweis)));
        Assert.Equal("Bürger-Ticket", RecordTypeDisplay.Name(nameof(Ticket)));
        Assert.Equal("Bürger-Tickets", RecordTypeDisplay.Plural(nameof(Ticket)));
    }
}
