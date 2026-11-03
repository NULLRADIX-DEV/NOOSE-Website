using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Change log. Leadership and the read-only supervision.</summary>
/// <remarks>
/// Invariant this relies on: <c>MayClassifiedRead</c> implies the viewer may see every record. That holds today
/// because <c>MayAllTaskforcesSee</c> and <c>MayClassifiedRead</c> are the same rule. If the two ever diverge,
/// this provider has to route its rows through <see cref="SearchParentResolver"/> instead.
/// </remarks>
public sealed class AuditLogSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(AuditLog);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var rows = await db.AuditLogs
            .Where(l => (l.AgentName != null && l.AgentName.Contains(s))
                || l.EntityType.Contains(s)
                || (l.ChangesJson != null && l.ChangesJson.Contains(s)))
            .OrderByDescending(l => l.Timestamp)
            .Take(query.PerCategory)
            .Select(l => new { l.Id, l.Timestamp, l.AgentName, l.EntityType, l.EntityId })
            .ToListAsync(cancellationToken);

        return rows.Select(l => new SearchHit(nameof(AuditLog), l.Id.ToString(),
                $"{SearchCatalog.German(l.EntityType)} geändert", string.Empty, string.Empty)
            {
                Timestamp = l.Timestamp,
                Actor = l.AgentName,
                Href = "/nachweis?tab=aenderungen",
            })
            .ToList();
    }
}

/// <summary>Access log. Same gate and the same invariant as the change log.</summary>
public sealed class AccessLogSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(AccessLog);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var rows = await db.AccessLogs
            .Where(l => (l.AgentName != null && l.AgentName.Contains(s)) || l.EntityType.Contains(s))
            .OrderByDescending(l => l.Timestamp)
            .Take(query.PerCategory)
            .Select(l => new { l.Id, l.Timestamp, l.AgentName, l.EntityType })
            .ToListAsync(cancellationToken);

        return rows.Select(l => new SearchHit(nameof(AccessLog), l.Id.ToString(),
                $"{SearchCatalog.German(l.EntityType)} geöffnet", string.Empty, string.Empty)
            {
                Timestamp = l.Timestamp,
                Actor = l.AgentName,
                Href = "/nachweis?tab=zugriffe",
            })
            .ToList();
    }
}

/// <summary>NOOSEI request log.</summary>
/// <remarks>
/// Two arms with different payloads. The counter-intelligence arm reaches every row but sees metadata only — the
/// prompt and the answer are the asking agent's words. Own rows carry their own text.
/// No amount, no rate, no cost field: real money is the AI owner's alone, and this row shape is why the panels
/// are file-scanned for it.
/// </remarks>
public sealed class LlmRequestLogSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(LlmRequestLog);

    public PartnerAccess Partner => PartnerAccess.Never;

    // partners, demo accounts and the read-only supervision cannot use NOOSEI at all, so they have no own rows;
    // the oversight arm is the counter-intelligence right
    public bool AppliesTo(SearchViewer viewer) => viewer.User.MayCounterIntel() || viewer.MeId is { Length: > 0 };

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var meId = query.Scope.MeId;
        var oversight = query.Viewer.User.MayCounterIntel();

        // own rows match on their text; foreign rows only on the feature label, so a hit never quotes someone
        // else's prompt back at a third party
        var features = oversight
            ? Enum.GetValues<LlmFeature>().Where(f => LlmFeatureDisplay.Name(f).Contains(s, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var q = oversight
            ? db.LlmRequests.Where(r => (r.AgentId == meId
                    && ((r.Prompt != null && r.Prompt.Contains(s)) || (r.Answer != null && r.Answer.Contains(s))))
                || features.Contains(r.Feature))
            : db.LlmRequests.Where(r => r.AgentId == meId
                && ((r.Prompt != null && r.Prompt.Contains(s)) || (r.Answer != null && r.Answer.Contains(s))));

        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(query.PerCategory)
            .Select(r => new { r.Id, r.AgentId, r.CreatedAt, r.Feature, r.Prompt })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SearchHit(nameof(LlmRequestLog), r.Id,
                $"FIBI · {LlmFeatureDisplay.Name(r.Feature)}",
                r.AgentId == meId ? SearchSnippet.Around(r.Prompt, s) : string.Empty,
                string.Empty)
            {
                Timestamp = r.CreatedAt,
                Href = "/einstellungen?tab=ki-anfragen",
            })
            .ToList();
    }
}
