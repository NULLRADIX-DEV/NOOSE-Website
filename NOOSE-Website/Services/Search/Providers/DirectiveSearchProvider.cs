using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Service directives, found by number and title within <see cref="DirectiveVisibility"/>.</summary>
public sealed class DirectiveSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Directive);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.IsInternalAgent && !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // no subject
        var rows = await db.Directives.AsNoTracking()
            .OnlyVisible(query.Scope)
            .Matching(query.Text)
            .OrderBy(d => d.Number)
            .Take(query.PerCategory)
            .Select(d => new { d.Id, d.Number, d.Title, d.Subject, d.RepealedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(d => new SearchHit(nameof(Directive), d.Id, d.Title, Snippet(d.Subject, d.RepealedAt is not null),
                DirectiveRules.Label(d.Number)))
            .ToList();
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Directives.AsNoTracking()
            .OnlyVisible(query.Scope)
            .Matching(query.Text)
            .OrderBy(d => d.Number)
            .Take(max)
            .Select(d => new { d.Id, d.Number, d.Title })
            .ToListAsync(cancellationToken);
        return rows.Select(d => new QuickHit(nameof(Directive), d.Id, d.Title, DirectiveRules.Label(d.Number))).ToList();
    }

    private static string Snippet(string? subject, bool repealed)
    {
        var text = subject?.Trim() ?? string.Empty;
        if (!repealed)
        {
            return text;
        }
        return text.Length == 0 ? "aufgehoben" : text + " · aufgehoben";
    }
}
