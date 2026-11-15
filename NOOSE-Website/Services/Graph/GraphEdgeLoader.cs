using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>One graph edge before node resolution.</summary>
internal readonly record struct RawEdge(string Source, string Target, string? Label, LinkKind Kind, bool Automatic);

/// <summary>Loads the raw relationship edges (links, person relations, memberships). Shared by graph + leads.</summary>
internal static class GraphEdgeLoader
{
    internal static async Task<List<RawEdge>> LoadRawEdgesAsync(AppDbContext db, LinkKind? kindFilter, CancellationToken cancellationToken)
    {
        // Skip clique edges.
        var vq = db.Links.Where(v => !v.Automatic);
        if (kindFilter is not null)
        {
            vq = vq.Where(v => v.Kind == kindFilter.Value);
        }
        var link = await vq
            .Select(v => new { v.SourceType, v.SourceId, v.TargetType, v.TargetId, v.Label, v.Kind, v.Automatic })
            .ToListAsync(cancellationToken);

        var edges = new List<RawEdge>(link.Count);
        foreach (var v in link)
        {
            edges.Add(new RawEdge($"{v.SourceType}:{v.SourceId}", $"{v.TargetType}:{v.TargetId}", v.Label, v.Kind, v.Automatic));
        }

        var bez = await db.PersonRelations
            .Select(b => new { b.PersonAId, b.PersonBId, b.Type })
            .ToListAsync(cancellationToken);
        foreach (var b in bez)
        {
            var kind = b.Type switch
            {
                RelationType.Enemy => LinkKind.Conflict,
                RelationType.Ally => LinkKind.Alliance,
                _ => LinkKind.Default,
            };
            if (kindFilter is not null && kind != kindFilter.Value)
            {
                continue;
            }
            edges.Add(new RawEdge(
                $"{nameof(Person)}:{b.PersonAId}",
                $"{nameof(Person)}:{b.PersonBId}",
                RelationTypeDisplay.Name(b.Type),
                kind,
                false));
        }

        // Star topology: memberships.
        if (kindFilter is null || kindFilter == LinkKind.Default)
        {
            foreach (var m in await db.FactionMembers
                .Select(m => new { m.PersonId, OrgId = m.FactionId, m.IsLead }).ToListAsync(cancellationToken))
            {
                edges.Add(new RawEdge($"{nameof(Person)}:{m.PersonId}", $"{nameof(Faction)}:{m.OrgId}",
                    m.IsLead ? "Leitung" : null, LinkKind.Default, true));
            }
            foreach (var m in await db.PersonGroupMembers
                .Select(m => new { m.PersonId, OrgId = m.PersonGroupId, m.IsLead }).ToListAsync(cancellationToken))
            {
                edges.Add(new RawEdge($"{nameof(Person)}:{m.PersonId}", $"{nameof(PersonGroup)}:{m.OrgId}",
                    m.IsLead ? "Leitung" : null, LinkKind.Default, true));
            }
            foreach (var m in await db.PartyMembers
                .Select(m => new { m.PersonId, OrgId = m.PartyId, m.IsLead }).ToListAsync(cancellationToken))
            {
                edges.Add(new RawEdge($"{nameof(Person)}:{m.PersonId}", $"{nameof(Party)}:{m.OrgId}",
                    m.IsLead ? "Leitung" : null, LinkKind.Default, true));
            }
            // a drug route hangs on its faction and on the people involved
            foreach (var r in await db.DrugRoutes.Where(r => r.FactionId != null)
                .Select(r => new { r.Id, r.FactionId }).ToListAsync(cancellationToken))
            {
                edges.Add(new RawEdge($"{nameof(DrugRoute)}:{r.Id}", $"{nameof(Faction)}:{r.FactionId}",
                    null, LinkKind.Default, true));
            }
            foreach (var m in await db.DrugRoutePeople
                .Select(m => new { m.PersonId, m.DrugRouteId, m.Role }).ToListAsync(cancellationToken))
            {
                edges.Add(new RawEdge($"{nameof(Person)}:{m.PersonId}", $"{nameof(DrugRoute)}:{m.DrugRouteId}",
                    string.IsNullOrWhiteSpace(m.Role) ? null : m.Role, LinkKind.Default, true));
            }
        }

        return edges;
    }
}
