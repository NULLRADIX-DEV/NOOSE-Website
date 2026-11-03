using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The two independent caps on what an external partner can reach through the search.</summary>
/// <remarks>The old implementation had a second, fully duplicated partner code path. It drifted: it never
/// consulted the rank allowlist, so a partner could find by search what their rank was not allowed to list.</remarks>
public class SearchPartnerCapTests
{
    private static ClaimsPrincipal PartnerUser(string id = "pa1", PartnerAgency agency = PartnerAgency.DoJ)
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(agency, PartnerRank.Chief).Build();

    private static SearchCriteria Text(string text) => new() { Text = text };

    private static PartnerShare Share(string type, string id)
        => new() { EntityType = type, EntityId = id, Agency = PartnerAgency.DoJ, PartnerAgentId = null };

    // ---- cap 1: the hard nine-type ceiling ----

    [Fact]
    public void Exactly_the_releasable_types_are_declared_ViaShare()
    {
        using var ctx = new SqliteTestContext();

        var viaShare = SearchTestHost.Providers(ctx)
            .Where(p => p.Partner == PartnerAccess.ViaShare)
            .Select(p => p.Category)
            .ToList();

        Assert.NotEmpty(viaShare);
        Assert.All(viaShare, category => Assert.True(PartnerVisibility.IsReleasableType(category),
            $"{category} is offered to partners but is not a releasable type"));
    }

    [Fact]
    public void No_provider_offers_a_partner_more_than_the_releasable_types_allow()
    {
        using var ctx = new SqliteTestContext();

        // a child provider is exempt from the direct list by construction: its parent check rejects a
        // non-releasable parent type before anything else, so the cap holds transitively
        var offenders = SearchTestHost.Providers(ctx)
            .Where(p => p.Partner == PartnerAccess.ViaShare && !PartnerVisibility.IsReleasableType(p.Category))
            .Select(p => p.Category);

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task A_partner_never_receives_an_internal_only_category()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Otto Offen"));
            db.Factions.Add(Seed.Faction("f1", "Otto-Bande"));
            db.KassenBuchungen.Add(new KassenBuchung { Id = "k1", CaseNumber = "FIB-K-2026-0001", Reason = "Otto" });
            // released to the agency, so the two record types are genuinely reachable
            db.PartnerShares.Add(Share(nameof(Person), "p1"));
            db.PartnerShares.Add(Share(nameof(Faction), "f1"));
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Otto"), PartnerUser());

        var categories = results.Groups.Select(g => g.Category).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(Person), categories);
        Assert.Contains(nameof(Faction), categories);
        // a cash booking is internal, whatever the search text says
        Assert.DoesNotContain(nameof(KassenBuchung), categories);
    }

    [Fact]
    public async Task An_unreleased_record_stays_invisible_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("shared", "Otto Offen"));
            db.People.Add(Seed.Person("private", "Otto Geheim"));
            db.PartnerShares.Add(Share(nameof(Person), "shared"));
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Otto"), PartnerUser());

        var people = results.Groups.Single(g => g.Category == nameof(Person));
        Assert.Equal(new[] { "shared" }, people.Hit.Select(h => h.TargetId).ToArray());
    }

    [Fact]
    public async Task A_released_but_classified_record_stays_invisible_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("vs", "Otto Offen", p => p.IsClassified = true));
            db.PartnerShares.Add(Share(nameof(Person), "vs"));
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Otto"), PartnerUser());

        Assert.Empty(results.Groups);
    }

    // ---- cap 2: the rank allowlist (this is the regression the old partner branch never applied) ----

    [Fact]
    public async Task A_rank_allowlist_narrows_the_partners_search_too_not_only_their_navigation()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Otto Offen"));
            db.Factions.Add(Seed.Faction("f1", "Otto-Bande"));
            db.PartnerShares.Add(Share(nameof(Person), "p1"));
            db.PartnerShares.Add(Share(nameof(Faction), "f1"));
            await db.SaveChangesAsync();
        }
        // the rank may list people, and nothing else
        var policy = new SearchTestHost.RankRestrictedPartnerPolicy(nameof(Person));

        var results = await SearchTestHost.NewService(ctx, policy).SearchAsync(Text("Otto"), PartnerUser());

        var categories = results.Groups.Select(g => g.Category).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(Person), categories);
        Assert.DoesNotContain(nameof(Faction), categories);
    }

    [Fact]
    public async Task The_rank_allowlist_does_not_touch_an_internal_agent()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Otto-Bande"));
            await db.SaveChangesAsync();
        }
        var policy = new SearchTestHost.RankRestrictedPartnerPolicy(nameof(Person));
        var agent = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Trainee).Build();

        var results = await SearchTestHost.NewService(ctx, policy).SearchAsync(Text("Otto"), agent);

        Assert.Contains(results.Groups, g => g.Category == nameof(Faction));
    }

    // ---- the category count a partner is told about ----

    [Fact]
    public async Task A_partner_is_told_how_many_categories_they_may_search_not_how_many_exist()
    {
        using var ctx = new SqliteTestContext();

        var asPartner = await SearchTestHost.NewService(ctx).SearchAsync(Text("nichts"), PartnerUser());
        var asAgent = await SearchTestHost.NewService(ctx)
            .SearchAsync(Text("nichts"), ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Trainee).Build());

        Assert.True(asPartner.VisibleCategories > 0);
        Assert.True(asPartner.VisibleCategories < asAgent.VisibleCategories,
            "a partner must not be told the internal category count");
    }
}
