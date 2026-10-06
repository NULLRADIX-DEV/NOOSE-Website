using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Taskforces. Gated on membership, not on classification.</summary>
public sealed class TaskforceSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Taskforce);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s) || t.CaseNumber.Contains(s)
                || (t.Purpose != null && t.Purpose.Contains(s))
                || (t.Remarks != null && t.Remarks.Contains(s)));
        }
        var hits = await q.OrderBy(t => t.Name).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(Taskforce), t.Id, t.Name, t.Purpose ?? string.Empty, t.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(t => t.ModifiedAt ?? t.CreatedAt).Take(query.FuzzyCandidates)
            .Select(t => new { t.Id, t.Name, t.CaseNumber, t.Purpose, t.Remarks }).ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Name, x.CaseNumber, x.Purpose ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Name, x.CaseNumber, x.Purpose, x.Remarks)
                : TextSimilarity.Tokens(x.Name, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Taskforce), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(t => ids.Contains(t.Id)).Take(take)
            .Select(t => new SearchHit(nameof(Taskforce), t.Id, t.Name, t.Purpose ?? string.Empty, t.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(t => t.Name.Contains(s) || t.CaseNumber.Contains(s))
            .OrderBy(t => t.Name).Take(max)
            .Select(t => new QuickHit(nameof(Taskforce), t.Id, t.Name, t.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(t => t.Name).Take(query.FuzzyCandidates)
            .Select(t => new { t.Id, t.Name, t.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Taskforce), hits, query,
            candidates.Select(x => (x.Id, x.Name, x.CaseNumber)), max);
    }

    private static IQueryable<Taskforce> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        // MayAllTaskforces, not MayClassifiedRead: they are the same value today and different rules
        var q = scope.PartnerAgency is { } agency
            ? db.Taskforces.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Taskforces.OnlyVisible(db, scope.MayAllTaskforces, scope.MeId);
        // one line for both waves: SearchAsync, ResolveIdsAsync, QuickAsync and the fuzzy pass come through here
        q = query.IncludeArchived ? q : q.OnlyActive();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(t => db.TagMappings.Any(z =>
                z.EntityType == nameof(Taskforce) && z.EntityId == t.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Jobs. A restricted job is visible to its creator, its assignees and supervision.</summary>
public sealed class JobSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Job);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(a => a.Title.Contains(s) || a.CaseNumber.Contains(s)
                || (a.Description != null && a.Description.Contains(s)));
        }
        var hits = await q.OrderBy(a => a.Title).Take(query.PerCategory)
            .Select(a => new SearchHit(nameof(Job), a.Id, a.Title, a.Description ?? string.Empty, a.CaseNumber))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(a => a.ModifiedAt ?? a.CreatedAt).Take(query.FuzzyCandidates)
            .Select(a => new { a.Id, a.Title, a.CaseNumber, a.Description }).ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Title, x.CaseNumber, x.Description ?? string.Empty,
            query.Deep
                ? TextSimilarity.Tokens(x.Title, x.CaseNumber, x.Description)
                : TextSimilarity.Tokens(x.Title, x.CaseNumber)));
        return SearchProviderKit.FuzzySupplement(nameof(Job), hits, query, candidates);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(a => ids.Contains(a.Id)).Take(take)
            .Select(a => new SearchHit(nameof(Job), a.Id, a.Title, a.Description ?? string.Empty, a.CaseNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var hits = await Visible(db, query).Where(a => a.Title.Contains(s) || a.CaseNumber.Contains(s))
            .OrderBy(a => a.Title).Take(max)
            .Select(a => new QuickHit(nameof(Job), a.Id, a.Title, a.CaseNumber))
            .ToListAsync(cancellationToken);
        if (hits.Count >= max)
        {
            return hits;
        }
        var candidates = await Visible(db, query).OrderBy(a => a.Title).Take(query.FuzzyCandidates)
            .Select(a => new { a.Id, a.Title, a.CaseNumber }).ToListAsync(cancellationToken);
        return SearchProviderKit.QuickFuzzy(nameof(Job), hits, query,
            candidates.Select(x => (x.Id, x.Title, x.CaseNumber)), max);
    }

    private static IQueryable<Job> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        var q = db.Jobs.OnlyVisible(db, scope.MayAllTaskforces, scope.MeId);
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(a => db.TagMappings.Any(z =>
                z.EntityType == nameof(Job) && z.EntityId == a.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Duty activities. The rich-text body is matched as stored HTML, so a phrase split by inline markup
/// will not hit; the fuzzy pass deliberately ignores the body.</summary>
public sealed class AgentActivitySearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(AgentActivity);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(a => a.Title.Contains(s) || (a.Kind != null && a.Kind.Contains(s)) || a.ContentHtml.Contains(s));
        }
        var hits = await q.OrderByDescending(a => a.ActivityDate).Take(query.PerCategory)
            .Select(a => new SearchHit(nameof(AgentActivity), a.Id, a.Title, a.Kind ?? string.Empty, string.Empty))
            .ToListAsync(cancellationToken);

        if (!query.WantsFuzzy(hits.Count))
        {
            return hits;
        }
        var raw = await Visible(db, query).OrderByDescending(a => a.ModifiedAt ?? a.CreatedAt).Take(query.FuzzyCandidates)
            .Select(a => new { a.Id, a.Title, a.Kind }).ToListAsync(cancellationToken);
        var candidates = raw.Select(x => new SearchProviderKit.Candidate(x.Id, x.Title, string.Empty, x.Kind ?? string.Empty,
            TextSimilarity.Tokens(x.Title, x.Kind)));
        return SearchProviderKit.FuzzySupplement(nameof(AgentActivity), hits, query, candidates);
    }

    private static IQueryable<AgentActivity> Visible(AppDbContext db, SearchQuery query)
    {
        var q = db.AgentActivities.AsQueryable();
        if (query.HasTags)
        {
            var tagIds = query.TagIds;
            q = q.Where(a => db.TagMappings.Any(z =>
                z.EntityType == nameof(AgentActivity) && z.EntityId == a.Id && tagIds.Contains(z.TagId)));
        }
        return q;
    }
}

/// <summary>Statutes. Never classified; a partner reaches them through an explicit release.</summary>
public sealed class LawSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Law);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(g => g.Title.Contains(s) || g.Paragraph.Contains(s)
                || g.LawBook.Contains(s) || g.Text.Contains(s)
                || (g.Sentence != null && g.Sentence.Contains(s)));
        }
        var rows = await q.OrderBy(g => g.LawBook).ThenBy(g => g.SortOrder).ThenBy(g => g.Paragraph).Take(query.PerCategory)
            .Select(g => new { g.Id, g.Paragraph, g.Title, g.LawBook }).ToListAsync(cancellationToken);
        return rows
            .Select(g => new SearchHit(nameof(Law), g.Id, $"{g.Paragraph} {g.Title}", g.LawBook, g.Paragraph))
            .ToList();
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(g => ids.Contains(g.Id)).Take(take)
            .Select(g => new SearchHit(nameof(Law), g.Id, $"{g.Paragraph} {g.Title}", g.LawBook, g.Paragraph))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The one gate, shared by recall and side-index resolution.</summary>
    private static IQueryable<Law> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        return scope.PartnerAgency is { } agency
            ? db.Laws.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Laws.AsQueryable();
    }
}
