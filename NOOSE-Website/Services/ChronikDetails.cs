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
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Loads the short detail line for chronicle child events. Only ever called for one page of events.</summary>
public static class ChronikDetails
{
    /// <summary>A detail line: either finished text, or prefix/suffix around a record name the caller must vet first.</summary>
    public readonly record struct Detail(
        string? Text,
        (string Type, string Id)? Reference = null,
        string? Prefix = null,
        string? Suffix = null);

    /// <summary>Builds detail lines for the given child references; missing children are simply absent.</summary>
    public static async Task<Dictionary<(string Type, string Id), Detail>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<(string, string), Detail>();
        if (refs.Count == 0)
        {
            return map;
        }

        List<string> Ids(string type) => refs.Where(r => r.Type == type).Select(r => r.Id).Distinct().ToList();

        var commentIds = Ids(nameof(Comment));
        if (commentIds.Count > 0)
        {
            foreach (var x in await db.Comments.IgnoreQueryFilters().Where(k => commentIds.Contains(k.Id))
                .Select(k => new { k.Id, k.Text }).ToListAsync(cancellationToken))
            {
                map[(nameof(Comment), x.Id)] = new Detail(TimelineDisplay.Truncate(x.Text));
            }
        }

        var sourceIds = Ids(nameof(Source));
        if (sourceIds.Count > 0)
        {
            foreach (var x in await db.Sources.IgnoreQueryFilters().Where(q => sourceIds.Contains(q.Id))
                .Select(q => new { q.Id, q.Title, q.Type }).ToListAsync(cancellationToken))
            {
                var kind = SourceTypeDisplay.Name(x.Type);
                map[(nameof(Source), x.Id)] = new Detail(
                    string.IsNullOrWhiteSpace(x.Title) ? kind : $"{kind}: {x.Title}");
            }
        }

        var followupIds = Ids(nameof(Followup));
        if (followupIds.Count > 0)
        {
            foreach (var x in await db.Followups.IgnoreQueryFilters().Where(w => followupIds.Contains(w.Id))
                .Select(w => new { w.Id, w.DueAt, w.Note, w.Done }).ToListAsync(cancellationToken))
            {
                var due = $"fällig am {x.DueAt.ToLocalTime():dd.MM.yyyy HH:mm}";
                var state = x.Done ? "erledigt · " : string.Empty;
                map[(nameof(Followup), x.Id)] = new Detail(
                    string.IsNullOrWhiteSpace(x.Note) ? state + due : $"{state}{due} · {TimelineDisplay.Truncate(x.Note)}");
            }
        }

        var docIds = Ids(nameof(PersonDoc));
        if (docIds.Count > 0)
        {
            foreach (var x in await db.PersonDocs.IgnoreQueryFilters().Where(d => docIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Outcome, d.Reason }).ToListAsync(cancellationToken))
            {
                var outcome = $"Ausgang: {MeasureOutcomeDisplay.Name(x.Outcome)}";
                map[(nameof(PersonDoc), x.Id)] = new Detail(
                    string.IsNullOrWhiteSpace(x.Reason) ? outcome : $"{outcome} · {TimelineDisplay.Truncate(x.Reason)}");
            }
        }

        var observationIds = Ids(nameof(Observation));
        if (observationIds.Count > 0)
        {
            foreach (var x in await db.Observations.IgnoreQueryFilters().Where(o => observationIds.Contains(o.Id))
                .Select(o => new { o.Id, o.Location, o.Sighting }).ToListAsync(cancellationToken))
            {
                var text = string.IsNullOrWhiteSpace(x.Location)
                    ? TimelineDisplay.Truncate(x.Sighting)
                    : $"{x.Location}{(string.IsNullOrWhiteSpace(x.Sighting) ? "" : $" · {TimelineDisplay.Truncate(x.Sighting)}")}";
                map[(nameof(Observation), x.Id)] = new Detail(text);
            }
        }

        var stationIds = Ids(nameof(DrugRouteStation));
        if (stationIds.Count > 0)
        {
            foreach (var x in await db.DrugRouteStations.IgnoreQueryFilters().Where(s => stationIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Kind, s.Location }).ToListAsync(cancellationToken))
            {
                var kind = DrugRouteStationKindDisplay.Name(x.Kind);
                map[(nameof(DrugRouteStation), x.Id)] = new Detail(
                    string.IsNullOrWhiteSpace(x.Location) ? kind : $"{kind}: {TimelineDisplay.Truncate(x.Location)}");
            }
        }

        // ---- details that name another record; the caller must run them through its visibility gate ----

        var relationIds = Ids(nameof(PersonRelation));
        if (relationIds.Count > 0)
        {
            foreach (var x in await db.PersonRelations.IgnoreQueryFilters().Where(b => relationIds.Contains(b.Id))
                .Select(b => new { b.Id, b.PersonBId, b.Type, b.Note }).ToListAsync(cancellationToken))
            {
                var suffix = string.IsNullOrWhiteSpace(x.Note) ? null : $" · {TimelineDisplay.Truncate(x.Note)}";
                map[(nameof(PersonRelation), x.Id)] = new Detail(null, (nameof(Person), x.PersonBId),
                    $"{RelationTypeDisplay.Name(x.Type)}: ", suffix);
            }
        }

        var linkIds = Ids(nameof(Link));
        if (linkIds.Count > 0)
        {
            foreach (var x in await db.Links.IgnoreQueryFilters().Where(v => linkIds.Contains(v.Id))
                .Select(v => new { v.Id, v.TargetType, v.TargetId, v.Label }).ToListAsync(cancellationToken))
            {
                var suffix = string.IsNullOrWhiteSpace(x.Label) ? null : $" · {x.Label}";
                map[(nameof(Link), x.Id)] = new Detail(null, (x.TargetType, x.TargetId), "mit ", suffix);
            }
        }

        await MemberAsync(nameof(FactionMember), i => db.FactionMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new MemberRow(m.Id, m.PersonId, m.Rank)));
        await MemberAsync(nameof(PersonGroupMember), i => db.PersonGroupMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new MemberRow(m.Id, m.PersonId, m.Role)));
        await MemberAsync(nameof(PartyMember), i => db.PartyMembers.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new MemberRow(m.Id, m.PersonId, m.Role)));
        await MemberAsync(nameof(DrugRoutePerson), i => db.DrugRoutePeople.IgnoreQueryFilters()
            .Where(m => i.Contains(m.Id)).Select(m => new MemberRow(m.Id, m.PersonId, m.Role)));

        // ---- agent assignments: codenames are internal-visible, no gate needed ----

        var assignments = new List<(string Type, string Id, string AgentId)>();
        await AssignmentAsync(nameof(FactionAgent), i => db.FactionAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(PersonGroupAgent), i => db.PersonGroupAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(PartyAgent), i => db.PartyAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(OperationAgent), i => db.OperationAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(CaseAgent), i => db.CaseAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(TaskforceAgent), i => db.TaskforceAgents.IgnoreQueryFilters()
            .Where(a => i.Contains(a.Id)).Select(a => new MemberRow(a.Id, a.AgentId, null)));
        await AssignmentAsync(nameof(JobAssignment), i => db.JobAssignments.IgnoreQueryFilters()
            .Where(z => i.Contains(z.Id)).Select(z => new MemberRow(z.Id, z.AgentId, null)));

        if (assignments.Count > 0)
        {
            var agentIds = assignments.Select(a => a.AgentId).Distinct().ToList();
            var codenames = await db.Users.Where(u => agentIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename })
                .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
            foreach (var (type, id, agentId) in assignments)
            {
                map[(type, id)] = new Detail(codenames.GetValueOrDefault(agentId));
            }
        }

        return map;

        async Task MemberAsync(string type, Func<List<string>, IQueryable<MemberRow>> query)
        {
            var ids = Ids(type);
            if (ids.Count == 0)
            {
                return;
            }
            foreach (var row in await query(ids).ToListAsync(cancellationToken))
            {
                var suffix = string.IsNullOrWhiteSpace(row.Label) ? null : $" · {row.Label}";
                map[(type, row.Id)] = new Detail(null, (nameof(Person), row.RefId), null, suffix);
            }
        }

        async Task AssignmentAsync(string type, Func<List<string>, IQueryable<MemberRow>> query)
        {
            var ids = Ids(type);
            if (ids.Count == 0)
            {
                return;
            }
            foreach (var row in await query(ids).ToListAsync(cancellationToken))
            {
                assignments.Add((type, row.Id, row.RefId));
            }
        }
    }

    private sealed record MemberRow(string Id, string RefId, string? Label);
}
