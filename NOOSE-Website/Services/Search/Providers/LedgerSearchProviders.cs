using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Agent abductions. The victim appears as a codename, never a real name.</summary>
public sealed class AbductionSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(AgentAbduction);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.AgentAbductions.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(a => a.CaseNumber.Contains(s)
                || (a.Location != null && a.Location.Contains(s))
                || (a.Notes != null && a.Notes.Contains(s)));
        }
        return await q.OrderByDescending(a => a.Timestamp).Take(query.PerCategory)
            .Select(a => new SearchHit(nameof(AgentAbduction), a.Id,
                db.Users.Where(u => u.Id == a.VictimAgentId).Select(u => u.Codename).FirstOrDefault() ?? "Entführung",
                a.Location ?? string.Empty, a.CaseNumber))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Evidence catalogue items.</summary>
public sealed class EvidenceItemSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(EvidenceItem);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.EvidenceItems.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(i => i.Name.Contains(s)
                || (i.Description != null && i.Description.Contains(s))
                || (i.Category != null && i.Category.Contains(s)));
        }
        var rows = await q.OrderBy(i => i.Name).Take(query.PerCategory)
            .Select(Row).ToListAsync(cancellationToken);
        return rows.Select(Hit).ToList();
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.EvidenceItems.Where(i => ids.Contains(i.Id)).Take(take)
            .Select(Row).ToListAsync(cancellationToken);
        return rows.Select(Hit).ToList();
    }

    private sealed record ItemRow(string Id, string Name, string? Description, string? Category);

    private static readonly System.Linq.Expressions.Expression<Func<EvidenceItem, ItemRow>> Row =
        i => new ItemRow(i.Id, i.Name, i.Description, i.Category);

    // category leads the snippet so a category match is visibly the reason for the hit
    private static SearchHit Hit(ItemRow i)
        => new(nameof(EvidenceItem), i.Id, i.Name,
            string.Join(" · ", new[] { i.Category, i.Description }.Where(p => !string.IsNullOrWhiteSpace(p))),
            string.Empty);
}

/// <summary>Evidence in/out entries.</summary>
public sealed class EvidenceEntrySearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(EvidenceEntry);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.EvidenceEntries.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(e => e.CaseNumber.Contains(s) || (e.Notes != null && e.Notes.Contains(s)));
        }
        var rows = await q.OrderByDescending(e => e.Timestamp).Take(query.PerCategory).ToListAsync(cancellationToken);
        return rows.Select(e => new SearchHit(nameof(EvidenceEntry), e.Id,
                EvidenceEntryTypeDisplay.Name(e.Type), e.Notes ?? string.Empty, e.CaseNumber))
            .ToList();
    }
}

/// <summary>Cash ledger bookings.</summary>
public sealed class KassenBuchungSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(KassenBuchung);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.KassenBuchungen.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(k => k.CaseNumber.Contains(s) || (k.Reason != null && k.Reason.Contains(s)));
        }
        var rows = await q.OrderByDescending(k => k.Timestamp).Take(query.PerCategory).ToListAsync(cancellationToken);
        return rows.Select(k => new SearchHit(nameof(KassenBuchung), k.Id,
                $"{KassenKontoDisplay.Name(k.Account)} · {KassenBuchungArtDisplay.Name(k.Kind)}",
                k.Reason ?? string.Empty, k.CaseNumber))
            .ToList();
    }
}

/// <summary>Funding requests: the requester's own, plus everything for leadership and read-only supervision.</summary>
public sealed class FinancingRequestSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(FinancingRequest);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = query.Scope;
        var q = db.FinancingRequests.OnlyVisible(scope.MayClassifiedRead, scope.MeId);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(f => f.CaseNumber.Contains(s) || f.Justification.Contains(s));
        }
        var rows = await q.OrderByDescending(f => f.CreatedAt).Take(query.PerCategory).ToListAsync(cancellationToken);
        return rows.Select(f => new SearchHit(nameof(FinancingRequest), f.Id,
                $"Finanzierung · {FinancingStatusDisplay.Name(f.Status)}", f.Justification, f.CaseNumber))
            .ToList();
    }
}

/// <summary>Food requests: the requester's own, plus everything for leadership and read-only supervision.</summary>
public sealed class FoodRequestSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(FoodRequest);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = query.Scope;
        var q = db.FoodRequests.OnlyVisible(scope.MayClassifiedRead, scope.MeId);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(f => f.CaseNumber.Contains(s) || (f.Justification != null && f.Justification.Contains(s)));
        }
        var rows = await q.OrderByDescending(f => f.CreatedAt).Take(query.PerCategory).ToListAsync(cancellationToken);
        return rows.Select(f => new SearchHit(nameof(FoodRequest), f.Id,
                $"Verpflegung · {FoodRequestStatusDisplay.Name(f.Status)}", f.Justification ?? string.Empty, f.CaseNumber))
            .ToList();
    }
}
