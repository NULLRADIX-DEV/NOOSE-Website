using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Operations hand-linked to factions and groups as involved; sole source of how a deployment counts for them.</summary>
public static class OperationLinks
{
    /// <summary>Ids of operations hand-linked to the record, in either direction.</summary>
    public static IQueryable<string> OperationIdsFor(AppDbContext db, string type, string id)
        => db.Links
            .Where(v => !v.Automatic && v.Kind == LinkKind.Default
                && ((v.SourceType == nameof(Operation) && v.TargetType == type && v.TargetId == id)
                 || (v.TargetType == nameof(Operation) && v.SourceType == type && v.SourceId == id)))
            .Select(v => v.SourceType == nameof(Operation) ? v.SourceId : v.TargetId);

    /// <summary>Ids of the factions hand-linked to one operation.</summary>
    public static Task<List<string>> FactionIdsAsync(AppDbContext db, string operationId, CancellationToken cancellationToken)
        => db.Links
            .Where(v => !v.Automatic && v.Kind == LinkKind.Default
                && ((v.SourceType == nameof(Operation) && v.SourceId == operationId && v.TargetType == nameof(Faction))
                 || (v.TargetType == nameof(Operation) && v.TargetId == operationId && v.SourceType == nameof(Faction))))
            .Select(v => v.SourceType == nameof(Operation) ? v.TargetId : v.SourceId)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>A deployment counts once it took place; a planned one is only an intention.</summary>
    public static bool Counts(OperationStatus status) => status != OperationStatus.Planned;

    /// <summary>When the deployment happened, for decay and ordering.</summary>
    public static DateTime When(Operation o) => o.Start ?? o.CreatedAt;
}
