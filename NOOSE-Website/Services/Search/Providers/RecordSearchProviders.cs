using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Person records: name, case number, description and aliases; in deep mode the profile side fields too.</summary>
public sealed class PersonSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Person);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    // every viewer has people; the secrecy filter narrows, it never removes the category
    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            var deep = query.Deep;
            q = q.Where(p => p.Name.Contains(s) || p.CaseNumber.Contains(s)
                || (p.Description != null && p.Description.Contains(s))
                || p.Aliases.Any(a => a.AliasName.Contains(s))
                || (deep && (
                       p.PhoneNumbers.Any(t => t.Number.Contains(s) || (t.Designation != null && t.Designation.Contains(s)))
                    || p.Vehicles.Any(f => f.Designation.Contains(s) || (f.LicensePlate != null && f.LicensePlate.Contains(s)))
                    || p.Locations.Any(o => o.Text.Contains(s) || (o.Note != null && o.Note.Contains(s)))
                    || p.Weapons.Any(w => w.Text.Contains(s)))));
        }
        var hits = await q.OrderBy(p => p.Name).Take(query.PerCategory)
            .Select(p => new SearchHit(nameof(Person), p.Id, p.Name, p.Description ?? string.Empty, p.CaseNumber))
            .ToListAsync(cancellationToken);

        return query.WantsFuzzy(hits.Count) ? await FuzzyAsync(db, query, hits, cancellationToken) : hits;
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(p => ids.Contains(p.Id)).Take(take)
            .Select(p => new SearchHit(nameof(Person), p.Id, p.Name, p.Description ?? string.Empty, p.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(p => p.Name.Contains(s) || p.CaseNumber.Contains(s))
            .OrderBy(p => p.Name).Take(max)
            .Select(p => new QuickHit(nameof(Person), p.Id, p.Name, p.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(p => p.Name).Take(query.FuzzyCandidates)
            .Select(p => new { p.Id, p.Name, p.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Person), hits, query,
            candidates.Select(x => (x.Id, x.Name, x.CaseNumber)), max);
    }

    /// <summary>The one visibility rule, shared by recall, side-index resolution and the palette.</summary>
    /// <remarks>Two copies is how the old side-index pass came to ignore both the partner and the tag filter.</remarks>
    private static IQueryable<Person> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.People.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.People.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(p => db.TagMappings.Any(z =>
                z.EntityType == nameof(Person) && z.EntityId == p.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }

    private static async Task<IReadOnlyList<SearchHit>> FuzzyAsync(
        AppDbContext db, SearchQuery query, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        var raw = await Visible(db, query).OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt).Take(query.FuzzyCandidates)
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.Description }).ToListAsync(cancellationToken);
        // aliases via flat WHERE PersonId IN; a collection projection becomes an untranslatable CROSS APPLY on MySQL
        var ids = raw.Select(x => x.Id).ToList();
        var aliasByPerson = (await db.PersonAliases.Where(a => ids.Contains(a.PersonId))
                .Select(a => new { a.PersonId, a.AliasName }).ToListAsync(cancellationToken))
            .GroupBy(a => a.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.AliasName).ToList(), StringComparer.Ordinal);

        var candidates = raw.Select(x =>
        {
            var aliases = aliasByPerson.TryGetValue(x.Id, out var list) ? list : [];
            return new SearchProviderKit.Candidate(x.Id, x.Name, x.CaseNumber, x.Description ?? string.Empty,
                query.Deep
                    ? TextSimilarity.Tokens([x.Name, x.CaseNumber, x.Description, .. aliases])
                    : TextSimilarity.Tokens([x.Name, x.CaseNumber, .. aliases]));
        });
        return SearchProviderKit.FuzzySupplement(nameof(Person), hits, query, candidates);
    }
}

/// <summary>Faction records: name, kind, targets, radio; in deep mode estate, darkchat and issuing times.</summary>
/// <remarks>The radio frequency sits in the ordinary recall rather than behind the deep scan: it is the one field
/// here that somebody overhears and types in verbatim, and a lookup that only works with a checkbox nobody knows
/// about is a lookup that does not work.</remarks>
public sealed class FactionSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Faction);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            // both spellings of the query: this column is written by hand and never normalised, so the stored
            // value may carry either mark - normalising only the query would miss half the combinations
            var frequency = RadioFrequency.Normalize(s);
            var frequencyComma = RadioFrequency.Comma(s);
            var deep = query.Deep;
            q = q.Where(f => f.Name.Contains(s) || f.CaseNumber.Contains(s)
                || (f.Kind != null && f.Kind.Contains(s))
                || (f.Description != null && f.Description.Contains(s))
                || (f.Targets != null && f.Targets.Contains(s))
                || (f.Radio != null && (f.Radio.Contains(frequency) || f.Radio.Contains(frequencyComma)))
                || (deep && (
                       (f.Estate != null && f.Estate.Contains(s))
                    || (f.Darkchat != null && f.Darkchat.Contains(s))
                    || (f.IssuingTimes != null && f.IssuingTimes.Contains(s)))));
        }
        var hits = await q.OrderBy(f => f.Name).Take(query.PerCategory)
            .Select(f => new SearchHit(nameof(Faction), f.Id, f.Name, f.Kind ?? string.Empty, f.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(f => f.ModifiedAt ?? f.CreatedAt).Take(query.FuzzyCandidates)
            .Select(f => new { f.Id, f.Name, f.CaseNumber, f.Kind, f.Description, f.Targets, f.Estate, f.Radio, f.Darkchat, f.IssuingTimes })
            .ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Name, x.CaseNumber, x.Kind ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Name, x.CaseNumber, x.Kind, x.Description, x.Targets, x.Estate, x.Radio, x.Darkchat, x.IssuingTimes)
                : TextSimilarity.Tokens(x.Name, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Faction), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(f => ids.Contains(f.Id)).Take(take)
            .Select(f => new SearchHit(nameof(Faction), f.Id, f.Name, f.Kind ?? string.Empty, f.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var frequency = RadioFrequency.Normalize(s);
        var frequencyComma = RadioFrequency.Comma(s);
        var hits = await Visible(db, query)
            .Where(f => f.Name.Contains(s) || f.CaseNumber.Contains(s)
                || (f.Radio != null && (f.Radio.Contains(frequency) || f.Radio.Contains(frequencyComma))))
            .OrderBy(f => f.Name).Take(max)
            .Select(f => new QuickHit(nameof(Faction), f.Id, f.Name, f.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(f => f.Name).Take(query.FuzzyCandidates)
            .Select(f => new { f.Id, f.Name, f.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Faction), hits, query,
            candidates.Select(x => (x.Id, x.Name, x.CaseNumber)), max);
    }

    private static IQueryable<Faction> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.Factions.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Factions.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(f => db.TagMappings.Any(z =>
                z.EntityType == nameof(Faction) && z.EntityId == f.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Person groups; also matched by their kind's display label.</summary>
public sealed class PersonGroupSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(PersonGroup);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            // the kind is an enum; match its German label rather than its stored value
            var matchingKinds = GroupsKindDisplay.All
                .Where(a => GroupsKindDisplay.Name(a).Contains(s, StringComparison.OrdinalIgnoreCase))
                .ToList();
            q = q.Where(g => g.Name.Contains(s) || g.CaseNumber.Contains(s)
                || (g.Description != null && g.Description.Contains(s))
                || (g.Targets != null && g.Targets.Contains(s))
                || matchingKinds.Contains(g.Kind));
        }
        var hits = await q.OrderBy(g => g.Name).Take(query.PerCategory)
            .Select(g => new SearchHit(nameof(PersonGroup), g.Id, g.Name, g.Description ?? string.Empty, g.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(g => g.ModifiedAt ?? g.CreatedAt).Take(query.FuzzyCandidates)
            .Select(g => new { g.Id, g.Name, g.CaseNumber, g.Description, g.Targets }).ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Name, x.CaseNumber, x.Description ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Name, x.CaseNumber, x.Description, x.Targets)
                : TextSimilarity.Tokens(x.Name, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(PersonGroup), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(g => ids.Contains(g.Id)).Take(take)
            .Select(g => new SearchHit(nameof(PersonGroup), g.Id, g.Name, g.Description ?? string.Empty, g.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(g => g.Name.Contains(s) || g.CaseNumber.Contains(s))
            .OrderBy(g => g.Name).Take(max)
            .Select(g => new QuickHit(nameof(PersonGroup), g.Id, g.Name, g.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(g => g.Name).Take(query.FuzzyCandidates)
            .Select(g => new { g.Id, g.Name, g.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(PersonGroup), hits, query,
            candidates.Select(x => (x.Id, x.Name, x.CaseNumber)), max);
    }

    private static IQueryable<PersonGroup> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.PersonGroups.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.PersonGroups.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(g => db.TagMappings.Any(z =>
                z.EntityType == nameof(PersonGroup) && z.EntityId == g.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Parties: name, description, targets, remarks.</summary>
public sealed class PartySearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Party);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(p => p.Name.Contains(s) || p.CaseNumber.Contains(s)
                || (p.Description != null && p.Description.Contains(s))
                || (p.Targets != null && p.Targets.Contains(s))
                || (p.Remarks != null && p.Remarks.Contains(s)));
        }
        var hits = await q.OrderBy(p => p.Name).Take(query.PerCategory)
            .Select(p => new SearchHit(nameof(Party), p.Id, p.Name, p.Description ?? string.Empty, p.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt).Take(query.FuzzyCandidates)
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.Description, p.Targets, p.Remarks }).ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Name, x.CaseNumber, x.Description ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Name, x.CaseNumber, x.Description, x.Targets, x.Remarks)
                : TextSimilarity.Tokens(x.Name, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Party), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(p => ids.Contains(p.Id)).Take(take)
            .Select(p => new SearchHit(nameof(Party), p.Id, p.Name, p.Description ?? string.Empty, p.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(p => p.Name.Contains(s) || p.CaseNumber.Contains(s))
            .OrderBy(p => p.Name).Take(max)
            .Select(p => new QuickHit(nameof(Party), p.Id, p.Name, p.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(p => p.Name).Take(query.FuzzyCandidates)
            .Select(p => new { p.Id, p.Name, p.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Party), hits, query,
            candidates.Select(x => (x.Id, x.Name, x.CaseNumber)), max);
    }

    private static IQueryable<Party> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.Parties.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Parties.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(p => db.TagMappings.Any(z =>
                z.EntityType == nameof(Party) && z.EntityId == p.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Drug routes: designation, substance, description, findings, remarks.</summary>
public sealed class DrugRouteSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(DrugRoute);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(r => r.Designation.Contains(s) || r.CaseNumber.Contains(s)
                || (r.Substance != null && r.Substance.Contains(s))
                || (r.Description != null && r.Description.Contains(s))
                || (r.Findings != null && r.Findings.Contains(s))
                || (r.Remarks != null && r.Remarks.Contains(s)));
        }
        var hits = await q.OrderBy(r => r.Designation).Take(query.PerCategory)
            .Select(r => new SearchHit(nameof(DrugRoute), r.Id, r.Designation, r.Description ?? string.Empty, r.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(r => r.ModifiedAt ?? r.CreatedAt).Take(query.FuzzyCandidates)
            .Select(r => new { r.Id, r.Designation, r.CaseNumber, r.Substance, r.Description, r.Findings, r.Remarks })
            .ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Designation, x.CaseNumber, x.Description ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Designation, x.CaseNumber, x.Substance, x.Description, x.Findings, x.Remarks)
                : TextSimilarity.Tokens(x.Designation, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(DrugRoute), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(r => ids.Contains(r.Id)).Take(take)
            .Select(r => new SearchHit(nameof(DrugRoute), r.Id, r.Designation, r.Description ?? string.Empty, r.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(r => r.Designation.Contains(s) || r.CaseNumber.Contains(s))
            .OrderBy(r => r.Designation).Take(max)
            .Select(r => new QuickHit(nameof(DrugRoute), r.Id, r.Designation, r.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(r => r.Designation).Take(query.FuzzyCandidates)
            .Select(r => new { r.Id, r.Designation, r.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(DrugRoute), hits, query,
            candidates.Select(x => (x.Id, x.Designation, x.CaseNumber)), max);
    }

    private static IQueryable<DrugRoute> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.DrugRoutes.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.DrugRoutes.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(r => db.TagMappings.Any(z =>
                z.EntityType == nameof(DrugRoute) && z.EntityId == r.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Operations: title, type, location, expiry, result, remarks.</summary>
public sealed class OperationSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Operation);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(o => o.Title.Contains(s) || o.CaseNumber.Contains(s)
                || (o.Expiry != null && o.Expiry.Contains(s))
                || (o.Result != null && o.Result.Contains(s))
                || (o.Location != null && o.Location.Contains(s))
                || (o.Type != null && o.Type.Contains(s))
                || (o.Remarks != null && o.Remarks.Contains(s)));
        }
        var hits = await q.OrderBy(o => o.Title).Take(query.PerCategory)
            .Select(o => new SearchHit(nameof(Operation), o.Id, o.Title, o.Expiry ?? o.Type ?? string.Empty, o.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(o => o.ModifiedAt ?? o.CreatedAt).Take(query.FuzzyCandidates)
            .Select(o => new { o.Id, o.Title, o.CaseNumber, o.Type, o.Location, o.Expiry, o.Result, o.Remarks })
            .ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Title, x.CaseNumber,
            x.Expiry ?? x.Type ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Title, x.CaseNumber, x.Type, x.Location, x.Expiry, x.Result, x.Remarks)
                : TextSimilarity.Tokens(x.Title, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Operation), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(o => ids.Contains(o.Id)).Take(take)
            .Select(o => new SearchHit(nameof(Operation), o.Id, o.Title, o.Type ?? string.Empty, o.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(o => o.Title.Contains(s) || o.CaseNumber.Contains(s))
            .OrderBy(o => o.Title).Take(max)
            .Select(o => new QuickHit(nameof(Operation), o.Id, o.Title, o.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(o => o.Title).Take(query.FuzzyCandidates)
            .Select(o => new { o.Id, o.Title, o.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Operation), hits, query,
            candidates.Select(x => (x.Id, x.Title, x.CaseNumber)), max);
    }

    private static IQueryable<Operation> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.Operations.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Operations.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(o => db.TagMappings.Any(z =>
                z.EntityType == nameof(Operation) && z.EntityId == o.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Cases: title, type, description, summary, closing note.</summary>
public sealed class CaseSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Case);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(v => v.Title.Contains(s) || v.CaseNumber.Contains(s)
                || (v.Type != null && v.Type.Contains(s))
                || (v.Description != null && v.Description.Contains(s))
                || (v.Summary != null && v.Summary.Contains(s))
                || (v.ClosingNote != null && v.ClosingNote.Contains(s)));
        }
        var hits = await q.OrderBy(v => v.Title).Take(query.PerCategory)
            .Select(v => new SearchHit(nameof(Case), v.Id, v.Title, v.Description ?? v.Type ?? string.Empty, v.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(v => v.ModifiedAt ?? v.CreatedAt).Take(query.FuzzyCandidates)
            .Select(v => new { v.Id, v.Title, v.CaseNumber, v.Type, v.Description, v.Summary, v.ClosingNote })
            .ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Title, x.CaseNumber,
            x.Description ?? x.Type ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Title, x.CaseNumber, x.Type, x.Description, x.Summary, x.ClosingNote)
                : TextSimilarity.Tokens(x.Title, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Case), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(v => ids.Contains(v.Id)).Take(take)
            .Select(v => new SearchHit(nameof(Case), v.Id, v.Title, v.Type ?? string.Empty, v.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(v => v.Title.Contains(s) || v.CaseNumber.Contains(s))
            .OrderBy(v => v.Title).Take(max)
            .Select(v => new QuickHit(nameof(Case), v.Id, v.Title, v.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(v => v.Title).Take(query.FuzzyCandidates)
            .Select(v => new { v.Id, v.Title, v.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Case), hits, query,
            candidates.Select(x => (x.Id, x.Title, x.CaseNumber)), max);
    }

    private static IQueryable<Case> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = scope.PartnerAgency is { } agency
            ? db.Cases.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Cases.OnlyVisible(scope);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(v => db.TagMappings.Any(z =>
                z.EntityType == nameof(Case) && z.EntityId == v.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}
