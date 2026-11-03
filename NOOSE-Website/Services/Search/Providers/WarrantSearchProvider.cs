using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Warrants, as far as <see cref="WarrantVisibility"/> lets the viewer see their person and case.</summary>
public sealed class WarrantSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Warrant);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.IsInternalAgent && !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.Warrants.AsNoTracking().OnlyVisible(db, query.Scope);
        if (query.HasText)
        {
            var s = query.Text;
            // the justification stays out: it is the internal half of the paper
            q = q.Where(w => w.CaseNumber.Contains(s) || w.Subject.Contains(s));
        }
        var rows = await q.OrderByDescending(w => w.CreatedAt).Take(query.PerCategory).ToListAsync(cancellationToken);
        var now = query.Viewer.NowUtc;
        return rows.Select(w => new SearchHit(nameof(Warrant), w.Id,
                $"{WarrantTypeDisplay.Name(w.Type)} · {WarrantStateDisplay.Name(WarrantRules.StateAt(w, now))}",
                w.Subject, w.CaseNumber))
            .ToList();
    }
}
