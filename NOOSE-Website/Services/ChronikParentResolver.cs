using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>Resolves audit rows of child entities back to the record that owns them.
/// Mirror of <see cref="TimelineService"/>'s record-to-children fan-out; both must list the same child types.</summary>
public static class ChronikParentResolver
{
    /// <summary>A resolved owning record.</summary>
    public readonly record struct ParentRef(string Type, string Id);

    // children that carry their own EntityType/EntityId; they can hang off any record type
    private static readonly string[] Polymorphic =
    {
        nameof(Comment), nameof(Source), nameof(Followup), nameof(CustomFieldValue),
    };

    // children reachable only through a fixed foreign key
    private static readonly Dictionary<string, string> FixedParent = new(StringComparer.Ordinal)
    {
        [nameof(PersonDoc)] = nameof(Person),
        [nameof(PersonPhoto)] = nameof(Person),
        [nameof(PersonRelation)] = nameof(Person),
        [nameof(Observation)] = nameof(Person),
        [nameof(FactionMember)] = nameof(Faction),
        [nameof(FactionAgent)] = nameof(Faction),
        [nameof(FactionPhoto)] = nameof(Faction),
        [nameof(PersonGroupMember)] = nameof(PersonGroup),
        [nameof(PersonGroupAgent)] = nameof(PersonGroup),
        [nameof(PersonGroupPhoto)] = nameof(PersonGroup),
        [nameof(PartyMember)] = nameof(Party),
        [nameof(PartyAgent)] = nameof(Party),
        [nameof(PartyPhoto)] = nameof(Party),
        [nameof(DrugRouteStation)] = nameof(DrugRoute),
        [nameof(DrugRoutePerson)] = nameof(DrugRoute),
        [nameof(DrugRoutePhoto)] = nameof(DrugRoute),
        [nameof(OperationAgent)] = nameof(Operation),
        [nameof(CaseAgent)] = nameof(Case),
        [nameof(TaskforceAgent)] = nameof(Taskforce),
        [nameof(JobAssignment)] = nameof(Job),
        [nameof(OeffentlicheFahndung)] = nameof(Person),
        [nameof(OeffentlichesFraktionsprofil)] = nameof(Faction),
        [nameof(FahndungKopfgeldAnteil)] = nameof(Person),
        [nameof(Hinweis)] = nameof(Person),
        [nameof(HinweisBelohnung)] = nameof(Person),
        [nameof(FahndungEinspruch)] = nameof(Person),
    };

    /// <summary>True when the audit type is a child rather than a record itself.</summary>
    public static bool IsChild(string entityType)
        => Polymorphic.Contains(entityType) || FixedParent.ContainsKey(entityType) || entityType == nameof(Link);

    /// <summary>Audit entity types to query for the given record types: the records plus every child that can belong to them.
    /// Polymorphic children and links are always included because their parent type is only known after resolution.</summary>
    public static string[] AuditTypesFor(IReadOnlyCollection<string> recordTypes)
    {
        var types = new HashSet<string>(recordTypes, StringComparer.Ordinal);
        foreach (var (child, parent) in FixedParent)
        {
            if (recordTypes.Contains(parent))
            {
                types.Add(child);
            }
        }
        types.UnionWith(Polymorphic);
        types.Add(nameof(Link));
        return types.ToArray();
    }

    /// <summary>Batch-resolves child references to their owning record. Children whose parent is gone are simply absent from the map.</summary>
    public static async Task<Dictionary<(string Type, string Id), ParentRef>> ResolveAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<(string, string), ParentRef>();
        if (refs.Count == 0)
        {
            return map;
        }

        List<string> Ids(string type) => refs.Where(r => r.Type == type).Select(r => r.Id).Distinct().ToList();

        // ---- polymorphic children: parent sits on the row ----
        var commentIds = Ids(nameof(Comment));
        if (commentIds.Count > 0)
        {
            foreach (var x in await db.Comments.IgnoreQueryFilters().Where(k => commentIds.Contains(k.Id))
                .Select(k => new { k.Id, k.EntityType, k.EntityId }).ToListAsync(cancellationToken))
            {
                map[(nameof(Comment), x.Id)] = new ParentRef(x.EntityType, x.EntityId);
            }
        }

        var sourceIds = Ids(nameof(Source));
        if (sourceIds.Count > 0)
        {
            foreach (var x in await db.Sources.IgnoreQueryFilters().Where(q => sourceIds.Contains(q.Id))
                .Select(q => new { q.Id, q.EntityType, q.EntityId }).ToListAsync(cancellationToken))
            {
                map[(nameof(Source), x.Id)] = new ParentRef(x.EntityType, x.EntityId);
            }
        }

        var followupIds = Ids(nameof(Followup));
        if (followupIds.Count > 0)
        {
            foreach (var x in await db.Followups.IgnoreQueryFilters().Where(w => followupIds.Contains(w.Id))
                .Select(w => new { w.Id, w.EntityType, w.EntityId }).ToListAsync(cancellationToken))
            {
                map[(nameof(Followup), x.Id)] = new ParentRef(x.EntityType, x.EntityId);
            }
        }

        var customFieldIds = Ids(nameof(CustomFieldValue));
        if (customFieldIds.Count > 0)
        {
            foreach (var x in await db.CustomFieldValues.IgnoreQueryFilters().Where(c => customFieldIds.Contains(c.Id))
                .Select(c => new { c.Id, c.EntityType, c.EntityId }).ToListAsync(cancellationToken))
            {
                map[(nameof(CustomFieldValue), x.Id)] = new ParentRef(x.EntityType, x.EntityId);
            }
        }

        // manual links anchor on their source record; auto links are system noise
        var linkIds = Ids(nameof(Link));
        if (linkIds.Count > 0)
        {
            foreach (var x in await db.Links.IgnoreQueryFilters().Where(v => linkIds.Contains(v.Id) && !v.Automatic)
                .Select(v => new { v.Id, v.SourceType, v.SourceId }).ToListAsync(cancellationToken))
            {
                map[(nameof(Link), x.Id)] = new ParentRef(x.SourceType, x.SourceId);
            }
        }

        // ---- fixed-FK children: one flat WHERE Id IN per table (no LATERAL on MySQL/MariaDB) ----
        await FanInAsync(nameof(PersonDoc), nameof(Person), i => db.PersonDocs.IgnoreQueryFilters()
            .Where(d => i.Contains(d.Id)).Select(d => new Pair(d.Id, d.PersonId)));
        await FanInAsync(nameof(PersonPhoto), nameof(Person), i => db.PersonPhotos.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id)).Select(f => new Pair(f.Id, f.PersonId)));
        await FanInAsync(nameof(PersonRelation), nameof(Person), i => db.PersonRelations.IgnoreQueryFilters()
            .Where(b => i.Contains(b.Id)).Select(b => new Pair(b.Id, b.PersonAId)));
        await FanInAsync(nameof(Observation), nameof(Person), i => db.Observations.IgnoreQueryFilters()
            .Where(o => i.Contains(o.Id)).Select(o => new Pair(o.Id, o.PersonId)));
        await FanInAsync(nameof(FactionMember), nameof(Faction), i => db.FactionMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new Pair(m.Id, m.FactionId)));
        await FanInAsync(nameof(FactionAgent), nameof(Faction), i => db.FactionAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.FactionId)));
        await FanInAsync(nameof(FactionPhoto), nameof(Faction), i => db.FactionPhotos.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id)).Select(f => new Pair(f.Id, f.FactionId)));
        await FanInAsync(nameof(PersonGroupMember), nameof(PersonGroup), i => db.PersonGroupMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new Pair(m.Id, m.PersonGroupId)));
        await FanInAsync(nameof(PersonGroupAgent), nameof(PersonGroup), i => db.PersonGroupAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.PersonGroupId)));
        await FanInAsync(nameof(PersonGroupPhoto), nameof(PersonGroup), i => db.PersonGroupPhotos.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id)).Select(f => new Pair(f.Id, f.PersonGroupId)));
        await FanInAsync(nameof(PartyMember), nameof(Party), i => db.PartyMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new Pair(m.Id, m.PartyId)));
        await FanInAsync(nameof(PartyAgent), nameof(Party), i => db.PartyAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.PartyId)));
        await FanInAsync(nameof(PartyPhoto), nameof(Party), i => db.PartyPhotos.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id)).Select(f => new Pair(f.Id, f.PartyId)));
        await FanInAsync(nameof(DrugRouteStation), nameof(DrugRoute), i => db.DrugRouteStations.IgnoreQueryFilters()
            .Where(s => i.Contains(s.Id)).Select(s => new Pair(s.Id, s.DrugRouteId)));
        await FanInAsync(nameof(DrugRoutePerson), nameof(DrugRoute), i => db.DrugRoutePeople.IgnoreQueryFilters()
            .Where(p => i.Contains(p.Id)).Select(p => new Pair(p.Id, p.DrugRouteId)));
        await FanInAsync(nameof(DrugRoutePhoto), nameof(DrugRoute), i => db.DrugRoutePhotos.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id)).Select(f => new Pair(f.Id, f.DrugRouteId)));
        await FanInAsync(nameof(OperationAgent), nameof(Operation), i => db.OperationAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.OperationId)));
        await FanInAsync(nameof(CaseAgent), nameof(Case), i => db.CaseAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.CaseId)));
        await FanInAsync(nameof(TaskforceAgent), nameof(Taskforce), i => db.TaskforceAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new Pair(a.Id, a.TaskforceId)));
        await FanInAsync(nameof(JobAssignment), nameof(Job), i => db.JobAssignments.IgnoreQueryFilters()
            .Where(z => i.Contains(z.Id)).Select(z => new Pair(z.Id, z.JobId)));
        // PersonId is nullable on the snapshot (faction notices follow later) and Pair.ParentId is not, so a notice
        // without a person file simply resolves to nothing
        await FanInAsync(nameof(OeffentlicheFahndung), nameof(Person), i => db.OeffentlicheFahndungen.IgnoreQueryFilters()
            .Where(f => i.Contains(f.Id) && f.PersonId != null).Select(f => new Pair(f.Id, f.PersonId!)));
        await FanInAsync(nameof(OeffentlichesFraktionsprofil), nameof(Faction), i => db.OeffentlicheFraktionsprofile.IgnoreQueryFilters()
            .Where(p => i.Contains(p.Id)).Select(p => new Pair(p.Id, p.FactionId)));
        // two hops: share → notice → file. IgnoreQueryFilters sits at the root because it is compilation-scoped
        // anyway, and it is wanted here — a share of a deleted notice must still resolve, or its money vanishes
        // from the chronicle
        await FanInAsync(nameof(FahndungKopfgeldAnteil), nameof(Person), i => db.FahndungKopfgeldAnteile.IgnoreQueryFilters()
            .Where(k => i.Contains(k.Id) && k.Wanted!.PersonId != null).Select(k => new Pair(k.Id, k.Wanted!.PersonId!)));
        // two hops as well: tip → notice → file. A tip without a reference belongs to no record and stays unresolved
        await FanInAsync(nameof(Hinweis), nameof(Person), i => db.Hinweise.IgnoreQueryFilters()
            .Where(h => i.Contains(h.Id) && h.Wanted!.PersonId != null).Select(h => new Pair(h.Id, h.Wanted!.PersonId!)));
        // two hops: objection → notice → file, same reasoning as the tip it sits beside
        await FanInAsync(nameof(FahndungEinspruch), nameof(Person), i => db.FahndungEinsprueche.IgnoreQueryFilters()
            .Where(e => i.Contains(e.Id) && e.Wanted!.PersonId != null).Select(e => new Pair(e.Id, e.Wanted!.PersonId!)));
        // three hops: reward → share → notice → file, same reasoning as the share it hangs off
        await FanInAsync(nameof(HinweisBelohnung), nameof(Person), i => db.HinweisBelohnungen.IgnoreQueryFilters()
            .Where(b => i.Contains(b.Id) && b.Share!.Wanted!.PersonId != null)
            .Select(b => new Pair(b.Id, b.Share!.Wanted!.PersonId!)));

        return map;

        async Task FanInAsync(string childType, string parentType, Func<List<string>, IQueryable<Pair>> query)
        {
            var ids = Ids(childType);
            if (ids.Count == 0)
            {
                return;
            }
            foreach (var pair in await query(ids).ToListAsync(cancellationToken))
            {
                map[(childType, pair.ChildId)] = new ParentRef(parentType, pair.ParentId);
            }
        }
    }

    private sealed record Pair(string ChildId, string ParentId);
}
