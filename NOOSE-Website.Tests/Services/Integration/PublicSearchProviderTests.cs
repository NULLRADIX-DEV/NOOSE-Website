using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The public-area categories inside the internal search: who may find them, and what a hit may say.</summary>
/// <remarks>
/// Asserts over the whole result rather than one group, like <see cref="SearchVisibilityTests"/>: a name that leaks
/// through a snippet is still a leak, and a hit landing in the wrong category is still a hit.
/// </remarks>
public class PublicSearchProviderTests
{
    private const string Needle = "Kupferdraht";

    private static ClaimsPrincipal Plain(string me = "agent-1")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Senior(string me = "senior")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.SeniorSpecialAgent).Build();

    private static ClaimsPrincipal Leadership(string me = "lead")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal PartnerViewer(string me = "partner")
        => ClaimsPrincipalBuilder.Agent(me).AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();

    private static SearchCriteria Text(string text) => new() { Text = text };

    private static IEnumerable<string> AllText(SearchResults results)
        => results.Groups.SelectMany(g => g.Hit).SelectMany(h => new[] { h.Title, h.Snippet, h.CaseNumber });

    private static IEnumerable<string> Categories(SearchResults results)
        => results.Groups.Select(g => g.Category);

    private static BuergerProfil Citizen(string id = "b1")
        => new() { Id = id, UserId = "u1", FirstName = "Erika", LastName = "Mustermann" };

    // ---- notices: the parent file is the gate ----

    [Fact]
    public async Task A_notice_on_a_classified_file_is_not_findable_by_a_plain_reader()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p-open", "Otto Offen", p => p.CaseNumber = "FIB-P-2026-0001"));
            db.People.Add(Seed.Person("p-vs", "Vera Verschluss", p =>
            {
                p.CaseNumber = "FIB-P-2026-0002";
                p.IsClassified = true;
            }));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-2026-0001", PersonId = "p-open", DisplayName = Needle + " offen",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow.AddDays(-1),
            });
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f2", CaseNumber = "FA-2026-0002", PersonId = "p-vs", DisplayName = Needle + " geheim",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var senior = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Senior());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        Assert.Contains(AllText(senior), t => t.Contains("Otto Offen", StringComparison.Ordinal));
        Assert.DoesNotContain(AllText(senior), t => t.Contains("Vera Verschluss", StringComparison.Ordinal));
        Assert.Contains(AllText(lead), t => t.Contains("Vera Verschluss", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_notice_hit_targets_the_file_it_was_published_from()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Otto Offen", p => p.CaseNumber = "FIB-P-2026-0001"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-2026-0001", PersonId = "p1", DisplayName = Needle,
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Senior());
        var hit = results.Groups.SelectMany(g => g.Hit).Single(h => h.Category == nameof(OeffentlicheFahndung));

        Assert.Equal("p1", hit.TargetId);
        Assert.Equal(nameof(Person), hit.TargetType);
        Assert.Equal("Otto Offen", hit.Title);
    }

    [Fact]
    public async Task A_rank_two_agent_does_not_get_the_notice_category_at_all()
    {
        // AppliesTo false removes the category from the viewer's catalog: "you may search here and nothing matched"
        // and "this is not yours" must not look the same
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Otto Offen"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-2026-0001", PersonId = "p1", DisplayName = Needle,
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());

        Assert.DoesNotContain(nameof(OeffentlicheFahndung), Categories(results));
    }

    [Fact]
    public async Task A_notice_whose_file_was_deleted_is_not_findable()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Otto Offen", p => p.IsDeleted = true));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-2026-0001", PersonId = "p1", DisplayName = Needle,
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        Assert.DoesNotContain(nameof(OeffentlicheFahndung), Categories(results));
    }

    // ---- organisation profiles: the faction file is the gate ----

    [Fact]
    public async Task A_profile_of_a_classified_faction_is_not_findable_by_a_plain_reader()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f-open", "Offene Bande"));
            db.Factions.Add(Seed.Faction("f-vs", "Geheime Bande", f => f.IsClassified = true));
            db.OeffentlicheFraktionsprofile.Add(new OeffentlichesFraktionsprofil
            {
                Id = "p1", FactionId = "f-open", DisplayName = Needle + " offen",
                Status = PublicProfileStatus.Veroeffentlicht,
            });
            db.OeffentlicheFraktionsprofile.Add(new OeffentlichesFraktionsprofil
            {
                Id = "p2", FactionId = "f-vs", DisplayName = Needle + " geheim",
                Status = PublicProfileStatus.Veroeffentlicht,
            });
            await db.SaveChangesAsync();
        }

        var senior = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Senior());

        Assert.Contains(AllText(senior), t => t.Contains("Offene Bande", StringComparison.Ordinal));
        Assert.DoesNotContain(AllText(senior), t => t.Contains("Geheime Bande", StringComparison.Ordinal));
    }

    // ---- tips: every internal agent, and never the citizen ----

    [Fact]
    public async Task A_tip_is_findable_by_every_internal_agent_and_never_names_the_citizen()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "b1",
                Text = "Am Hafen lag " + Needle + " herum.", Status = TipStatus.Neu,
            });
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());

        Assert.Contains(nameof(Hinweis), Categories(plain));
        Assert.DoesNotContain(AllText(plain), t => t.Contains("Erika", StringComparison.Ordinal));
        Assert.DoesNotContain(AllText(plain), t => t.Contains("Mustermann", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tip_whose_citizen_profile_was_deleted_is_still_findable()
    {
        // the provider projects no citizen field, so there is no required navigation EF could INNER-join it out on
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(new BuergerProfil
            {
                Id = "b1", UserId = "u1", FirstName = "Erika", LastName = "Mustermann", IsDeleted = true,
            });
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "b1",
                Text = "Am Hafen lag " + Needle + " herum.", Status = TipStatus.Neu,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());

        Assert.Contains(nameof(Hinweis), Categories(results));
    }

    [Fact]
    public async Task A_partner_finds_no_public_area_category_at_all()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "b1",
                Text = Needle, Status = TipStatus.Neu,
            });
            db.Tickets.Add(new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-2026-0001", CitizenProfileId = "b1", Subject = Needle,
                Status = TicketStatus.Offen, LastActivityAt = DateTime.UtcNow,
            });
            db.Pressemitteilungen.Add(new Pressemitteilung
            {
                Id = "m1", Title = Needle, Teaser = "Kurz", Status = PressReleaseStatus.Entwurf,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), PartnerViewer());

        Assert.DoesNotContain(nameof(Hinweis), Categories(results));
        Assert.DoesNotContain(nameof(Ticket), Categories(results));
        Assert.DoesNotContain(nameof(Pressemitteilung), Categories(results));
    }

    // ---- tickets: leadership only ----

    [Fact]
    public async Task A_ticket_is_findable_by_leadership_only_and_never_names_the_citizen()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.Tickets.Add(new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-2026-0001", CitizenProfileId = "b1",
                Subject = "Anliegen " + Needle, Status = TicketStatus.Offen, LastActivityAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        Assert.DoesNotContain(nameof(Ticket), Categories(plain));
        Assert.Contains(nameof(Ticket), Categories(lead));
        Assert.DoesNotContain(AllText(lead), t => t.Contains("Erika", StringComparison.Ordinal));
    }

    // ---- objections: the wanted-desk audience ----

    [Fact]
    public async Task An_objection_survives_the_deletion_of_the_notice_it_disputes()
    {
        // the desk roots over the soft-delete filter so a removed notice cannot make its objection unfindable
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.People.Add(Seed.Person("p1", "Otto Offen"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-2026-0001", PersonId = "p1", DisplayName = "Otto Offen",
                Status = PublicWantedStatus.Zurueckgezogen, IsDeleted = true,
            });
            db.FahndungEinsprueche.Add(new FahndungEinspruch
            {
                Id = "e1", CaseNumber = "EIN-2026-0001", WantedId = "f1", CitizenProfileId = "b1",
                Text = "Das ist " + Needle + ", ich war es nicht.", Status = ObjectionStatus.Neu,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Senior());

        Assert.Contains(nameof(FahndungEinspruch), Categories(results));
        Assert.DoesNotContain(AllText(results), t => t.Contains("Erika", StringComparison.Ordinal));
    }

    // ---- editorial surfaces: leadership and supervision, drafts included ----

    [Fact]
    public async Task An_unpublished_press_release_is_findable_internally_but_only_by_leadership()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Pressemitteilungen.Add(new Pressemitteilung
            {
                Id = "m1", Title = "Entwurf " + Needle, Teaser = "Noch nicht draußen",
                Status = PressReleaseStatus.Entwurf,
            });
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        Assert.DoesNotContain(nameof(Pressemitteilung), Categories(plain));
        Assert.Contains(nameof(Pressemitteilung), Categories(lead));
    }

    [Fact]
    public async Task The_four_editorial_categories_all_answer_leadership()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Pressemitteilungen.Add(new Pressemitteilung
            {
                Id = "m1", Title = Needle + " Presse", Teaser = "x", Status = PressReleaseStatus.Entwurf,
            });
            db.OeffentlicheSeiten.Add(new OeffentlicheSeite
            {
                Id = "s1", Slug = "auftrag", Title = Needle + " Seite", Status = PublicPageStatus.Entwurf,
            });
            db.OeffentlicheWarnungen.Add(new OeffentlicheWarnung
            {
                Id = "w1", Title = Needle + " Warnung", Status = PublicWarningStatus.Entwurf,
            });
            db.OeffentlicheLageberichte.Add(new OeffentlicherLagebericht
            {
                Id = "r1", Year = 2026, Month = 8, Title = Needle + " Bericht", Status = PublicReportStatus.Entwurf,
            });
            await db.SaveChangesAsync();
        }

        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        Assert.Contains(nameof(Pressemitteilung), Categories(lead));
        Assert.Contains(nameof(OeffentlicheSeite), Categories(lead));
        Assert.Contains(nameof(OeffentlicheWarnung), Categories(lead));
        Assert.Contains(nameof(OeffentlicherLagebericht), Categories(lead));
    }

    // ---- the resolver arm: a tip is a link ENDPOINT, and both ends must resolve ----

    [Fact]
    public async Task ATakeoverLinkIsFindable_BecauseTheResolverKnowsATip()
    {
        // LinkSearchProvider resolves both ends and skips the row when either is absent. Before the Hinweis arm
        // existed the resolver had none and no default arm, so every takeover link was silently unfindable.
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.People.Add(Seed.Person("p1", "Otto Offen", p => p.CaseNumber = "FIB-P-2026-0001"));
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "b1",
                Text = "Meldung", Status = TipStatus.InPruefung,
            });
            db.Links.Add(new Link
            {
                Id = "l1", SourceType = nameof(Hinweis), SourceId = "h1",
                TargetType = nameof(Person), TargetId = "p1",
                Label = "Übernahme " + Needle, Kind = LinkKind.Default, Automatic = false,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());

        Assert.Contains(nameof(Link), Categories(results));
        // the tip end resolves to the wording the whole house uses for it
        Assert.Contains(AllText(results), t => t.Contains("Bürgerhinweis FIB-H-2026-0001", StringComparison.Ordinal));
        // and never to the citizen behind it
        Assert.DoesNotContain(AllText(results), t => t.Contains("Erika", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATakeoverLinkOfADeletedTipStaysHidden()
    {
        // the resolver's contract: an absent parent hides the child, and a deleted tip must stay absent
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.People.Add(Seed.Person("p1", "Otto Offen"));
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "b1",
                Text = "Meldung", Status = TipStatus.InPruefung, IsDeleted = true,
            });
            db.Links.Add(new Link
            {
                Id = "l1", SourceType = nameof(Hinweis), SourceId = "h1",
                TargetType = nameof(Person), TargetId = "p1",
                Label = "Übernahme " + Needle, Kind = LinkKind.Default, Automatic = false,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());

        Assert.DoesNotContain(nameof(Link), Categories(results));
    }

    // ---- who may search what: the negative half of every AppliesTo ----

    [Fact]
    public async Task TheRankGateOfEveryNewCategoryIsNegativelyTested()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(Citizen());
            db.People.Add(Seed.Person("p1", Needle + " Person"));
            db.Factions.Add(Seed.Faction("f1", Needle + " Bande"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "fa1", CaseNumber = "FA-1", PersonId = "p1", DisplayName = Needle,
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = DateTime.UtcNow,
            });
            db.OeffentlicheFraktionsprofile.Add(new OeffentlichesFraktionsprofil
            {
                Id = "pr1", FactionId = "f1", DisplayName = Needle,
                Status = PublicProfileStatus.Veroeffentlicht,
            });
            db.FahndungEinsprueche.Add(new FahndungEinspruch
            {
                Id = "e1", CaseNumber = "EIN-1", WantedId = "fa1", CitizenProfileId = "b1",
                Text = Needle, Status = ObjectionStatus.Neu,
            });
            db.OeffentlicheSeiten.Add(new OeffentlicheSeite
            {
                Id = "s1", Slug = "auftrag", Title = Needle, Status = PublicPageStatus.Entwurf,
            });
            db.OeffentlicheWarnungen.Add(new OeffentlicheWarnung
            {
                Id = "w1", Title = Needle, Status = PublicWarningStatus.Entwurf,
            });
            db.OeffentlicheLageberichte.Add(new OeffentlicherLagebericht
            {
                Id = "r1", Year = 2026, Month = 8, Title = Needle, Status = PublicReportStatus.Entwurf,
            });
            await db.SaveChangesAsync();
        }

        var junior = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Plain());
        var senior = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Senior());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text(Needle), Leadership());

        // the wanted desk: senior and up, so a junior sees neither the notice nor the profile nor the objection
        foreach (var deskCategory in new[]
                 {
                     nameof(OeffentlicheFahndung), nameof(OeffentlichesFraktionsprofil), nameof(FahndungEinspruch),
                 })
        {
            Assert.DoesNotContain(deskCategory, Categories(junior));
            Assert.Contains(deskCategory, Categories(senior));
        }

        // the editorial surfaces: leadership and supervision only, so not even a senior agent
        foreach (var editorial in new[]
                 {
                     nameof(OeffentlicheSeite), nameof(OeffentlicheWarnung), nameof(OeffentlicherLagebericht),
                 })
        {
            Assert.DoesNotContain(editorial, Categories(junior));
            Assert.DoesNotContain(editorial, Categories(senior));
            Assert.Contains(editorial, Categories(lead));
        }
    }
}
