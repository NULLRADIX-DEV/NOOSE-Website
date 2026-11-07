using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Paragraphs of the service regulation, found by heading, section title or current wording; internal agents only.</summary>
public sealed class RegulationSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(RegulationParagraph);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.IsInternalAgent && !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await RegulationService.Matching(db, db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, query.Scope), query.Text)
            .OrderBy(p => p.Heading)
            .Take(query.PerCategory)
            .Select(p => new { p.Id, p.Heading, p.RepealedAt })
            .ToListAsync(cancellationToken);
        var labels = await RegulationVisibility.LabelsAsync(db, rows.Select(r => r.Id).ToList(), cancellationToken);
        // the label names the section; the wording never goes into a snippet
        return rows.Select(p => new SearchHit(nameof(RegulationParagraph), p.Id, p.Heading,
                p.RepealedAt is null ? "Dienstverordnung" : "Dienstverordnung · aufgehoben",
                labels.GetValueOrDefault(p.Id) ?? "DVO"))
            .ToList();
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await RegulationService.Matching(db, db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, query.Scope), query.Text)
            .OrderBy(p => p.Heading)
            .Take(max)
            .Select(p => new { p.Id, p.Heading })
            .ToListAsync(cancellationToken);
        var labels = await RegulationVisibility.LabelsAsync(db, rows.Select(r => r.Id).ToList(), cancellationToken);
        return rows.Select(p => new QuickHit(nameof(RegulationParagraph), p.Id, p.Heading, labels.GetValueOrDefault(p.Id) ?? "DVO")).ToList();
    }
}
