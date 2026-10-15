using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <summary>Builds unified timeline from audit and semantic sources.</summary>
public class TimelineService(IDbContextFactory<AppDbContext> dbFactory) : ITimelineService
{
    private sealed record Raw(
        DateTime Timestamp, TimelineCategory Category, string Title, string? Detail,
        string? ActorName, string? ActorId, string? Href,
        IReadOnlyList<AuditDisplay.FieldChange>? Changes, DateTime? RecordedAt = null);

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(
        string entityType, string entityId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = new List<Raw>();
        var actorIds = new HashSet<string>();
        await BuildAsync(db, entityType, entityId, viewer, raw, actorIds, cancellationToken);
        return await MaterializeAsync(db, raw, actorIds, cancellationToken);
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetNewSinceAsync(
        string entityType, string entityId, ClaimsPrincipal viewer, DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var all = await GetTimelineAsync(entityType, entityId, viewer, cancellationToken);
        var meId = viewer.GetAgentId();
        return all.Where(e => RecordVisits.IsNew(e, sinceUtc, meId)).ToList();
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineForRecordsAsync(
        IReadOnlyList<(string Type, string Id)> records, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = new List<Raw>();
        var actorIds = new HashSet<string>();
        // one context for the whole batch; each record keeps its own visibility gate
        foreach (var (type, id) in records)
        {
            await BuildAsync(db, type, id, viewer, raw, actorIds, cancellationToken);
        }
        return await MaterializeAsync(db, raw, actorIds, cancellationToken);
    }

    // appends the visible events of one record into raw/actorIds; no-op if the viewer may not see it
    private async Task BuildAsync(
        AppDbContext db, string entityType, string entityId, ClaimsPrincipal viewer,
        List<Raw> raw, HashSet<string> actorIds, CancellationToken cancellationToken)
    {
        var scope = ViewerScope.From(viewer);
        var agency = scope.PartnerAgency;
        var isPartner = agency is not null;
        var mayClassified = viewer.MayClassifiedRead();
        var mayAllTf = viewer.MayAllTaskforcesSee();
        var meId = viewer.GetAgentId();

        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            return;
        }
        if (entityType == nameof(Job))
        {
            var visible = await JobVisibility.VisibleIdsAsync(db, new[] { entityId }, mayAllTf, meId, cancellationToken);
            if (!visible.Contains(entityId))
            {
                return;
            }
        }
        if (entityType == nameof(Appointment))
        {
            var visible = await AppointmentVisibility.VisibleIdsAsync(db, new[] { entityId }, mayAllTf, meId, cancellationToken);
            if (!visible.Contains(entityId))
            {
                return;
            }
        }

        // ---- 1) audit base ----
        string[] types;
        HashSet<string> auditIds;
        if (isPartner)
        {
            // record-self audit only, no child fan-out
            types = new[] { entityType };
            auditIds = new HashSet<string> { entityId };
        }
        else
        {
            (types, auditIds) = await AuditSourceAsync(db, entityType, entityId, cancellationToken);
        }
        foreach (var log in await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && auditIds.Contains(a.EntityId)
                && a.EntityType != nameof(Link))
            .Select(a => new { a.Timestamp, a.EntityType, a.Action, a.ChangesJson, a.AgentName, a.AgentId })
            .ToListAsync(cancellationToken))
        {
            var (kat, title) = TimelineDisplay.MapAudit(log.EntityType, log.Action);
            // a tip carries the submitting account as its actor, and an agent may report through his civilian
            // identity — naming him on the file he reported about is exactly what the promise forbids
            var hidden = TipAnonymity.HidesActor(log.EntityType);
            raw.Add(new Raw(log.Timestamp, kat, title, null, hidden ? null : log.AgentName, hidden ? null : log.AgentId,
                null, AuditDisplay.Parse(log.ChangesJson, entityType: log.EntityType)));
        }

        // ---- 2) classification history ----
        foreach (var e in await db.ClassificationHistory
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .Select(e => new { e.Value, e.Justification, e.Timestamp, e.AgentName, e.AgentId })
            .ToListAsync(cancellationToken))
        {
            raw.Add(new Raw(e.Timestamp, TimelineCategory.Classification,
                $"Einstufung: {ClassificationDisplay.Name(e.Value)}", e.Justification, e.AgentName, e.AgentId, null, null));
        }

        // ---- 3) comments ----
        var comments = await db.Comments
            .Where(k => k.EntityType == entityType && k.EntityId == entityId)
            .Select(k => new { k.Id, k.Text, k.AuthorName, k.CreatedAt, k.CreatedById })
            .ToListAsync(cancellationToken);
        if (isPartner)
        {
            comments = await PartnerVisibility.FilterChildrenAsync(db, entityType, entityId, nameof(Comment), comments, k => k.Id, agency!.Value, meId, cancellationToken);
        }
        foreach (var k in comments)
        {
            raw.Add(new Raw(k.CreatedAt, TimelineCategory.Comment,
                "Kommentar", TimelineDisplay.Truncate(k.Text), k.AuthorName, k.CreatedById, null, null));
        }

        // ---- 4) sources/attachments ----
        var sources = await db.Sources
            .Where(q => q.EntityType == entityType && q.EntityId == entityId)
            .Select(q => new { q.Id, q.Title, q.Type, q.CreatedAt, q.CreatedById })
            .ToListAsync(cancellationToken);
        if (isPartner)
        {
            // drop cross-ref source types, then child-release filter
            sources = sources.Where(q => q.Type != SourceType.Internal && q.Type != SourceType.Document).ToList();
            sources = await PartnerVisibility.FilterChildrenAsync(db, entityType, entityId, nameof(Source), sources, q => q.Id, agency!.Value, meId, cancellationToken);
        }
        foreach (var q in sources)
        {
            Remember(actorIds, q.CreatedById);
            var title = string.IsNullOrWhiteSpace(q.Title) ? "Quelle hinzugefügt" : $"Quelle hinzugefügt: {q.Title}";
            raw.Add(new Raw(q.CreatedAt, TimelineCategory.Source, title,
                SourceTypeDisplay.Name(q.Type), null, q.CreatedById, null, null));
        }

        // ---- 5) followups ----
        var followups = await db.Followups
            .Where(w => w.EntityType == entityType && w.EntityId == entityId)
            .Select(w => new { w.Id, w.DueAt, w.Note, w.Done, w.DoneAt, w.DoneById, w.CreatedAt, w.CreatedById })
            .ToListAsync(cancellationToken);
        if (isPartner)
        {
            followups = await PartnerVisibility.FilterChildrenAsync(db, entityType, entityId, nameof(Followup), followups, w => w.Id, agency!.Value, meId, cancellationToken);
        }
        foreach (var w in followups)
        {
            Remember(actorIds, w.CreatedById);
            var due = w.DueAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            var detail = string.IsNullOrWhiteSpace(w.Note) ? $"fällig am {due}" : $"fällig am {due} · {w.Note}";
            raw.Add(new Raw(w.CreatedAt, TimelineCategory.Followup, "Wiedervorlage angelegt",
                detail, null, w.CreatedById, null, null));
            if (w.Done && w.DoneAt is { } done)
            {
                Remember(actorIds, w.DoneById);
                raw.Add(new Raw(done, TimelineCategory.Followup, "Wiedervorlage erledigt",
                    w.Note, null, w.DoneById, null, null));
            }
        }

        // ---- 6) links ----
        // cross-ref: skip for partners
        var link = isPartner ? null : await db.Links.IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == entityType && v.SourceId == entityId)
                 || (v.TargetType == entityType && v.TargetId == entityId)))
            .Select(v => new { v.SourceType, v.SourceId, v.TargetType, v.TargetId, v.Label, v.CreatedAt, v.CreatedById, v.IsDeleted, v.DeletedAt, v.DeletedById })
            .ToListAsync(cancellationToken);
        if (link is { Count: > 0 })
        {
            (string, string) Counterpart(string sourceType, string sourceId, string targetType, string targetId)
                => sourceType == entityType && sourceId == entityId ? (targetType, targetId) : (sourceType, sourceId);

            var refs = link.Select(v => Counterpart(v.SourceType, v.SourceId, v.TargetType, v.TargetId)).Distinct().ToList();
            var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTf, meId);
            // groups and invites decide; a hidden one is left out like on the link list, label and all
            var presentationIds = refs.Where(r => r.Item1 == nameof(Presentation)).Select(r => r.Item2).ToList();
            var presentations = await PresentationVisibility.VisibleTitlesAsync(db, presentationIds, scope, cancellationToken);
            foreach (var (id, title) in presentations)
            {
                map[(nameof(Presentation), id)] = new(title, false, $"/praesentationen/{id}");
            }
            foreach (var v in link)
            {
                var counterpart = Counterpart(v.SourceType, v.SourceId, v.TargetType, v.TargetId);
                if (counterpart.Item1 == nameof(Presentation) && !presentations.ContainsKey(counterpart.Item2))
                {
                    continue;
                }
                var (name, href) = CounterpartDisplay(map, counterpart, "Akte");
                Remember(actorIds, v.CreatedById);
                raw.Add(new Raw(v.CreatedAt, TimelineCategory.Link, $"Verknüpft mit {name}",
                    v.Label, null, v.CreatedById, href, null));
                if (v.IsDeleted && v.DeletedAt is { } removed)
                {
                    Remember(actorIds, v.DeletedById);
                    raw.Add(new Raw(removed, TimelineCategory.Link, $"Verknüpfung mit {name} entfernt",
                        v.Label, null, v.DeletedById, href, null));
                }
            }
        }

        // ---- 7) person-specific ----
        if (entityType == nameof(Person))
        {
            var observations = await db.Observations
                .Where(o => o.PersonId == entityId)
                .Select(o => new { o.Id, o.Start, o.Location, o.Sighting, o.CreatedById, o.CreatedAt })
                .ToListAsync(cancellationToken);
            if (isPartner)
            {
                observations = await PartnerVisibility.FilterChildrenAsync(db, nameof(Person), entityId, nameof(Observation), observations, o => o.Id, agency!.Value, meId, cancellationToken);
            }
            foreach (var o in observations)
            {
                Remember(actorIds, o.CreatedById);
                var title = string.IsNullOrWhiteSpace(o.Location) ? "Observation" : $"Observation – {o.Location}";
                // placed by when it happened, judged new by when it was entered
                raw.Add(new Raw(o.Start, TimelineCategory.Observation, title,
                    TimelineDisplay.Truncate(o.Sighting), null, o.CreatedById, null, null, o.CreatedAt));
            }

            // photos now surface via the audit fan-out (Person → PersonPhoto)

            // cross-ref: skip for partners
            var bez = isPartner ? null : await db.PersonRelations
                .Where(b => b.PersonAId == entityId || b.PersonBId == entityId)
                .Select(b => new { b.PersonAId, b.PersonBId, b.Type, b.Note, b.CreatedAt, b.CreatedById })
                .ToListAsync(cancellationToken);
            if (bez is { Count: > 0 })
            {
                var refs = bez.Select(b => (nameof(Person), b.PersonAId == entityId ? b.PersonBId : b.PersonAId))
                    .Distinct().ToList();
                var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTf, meId);
                foreach (var b in bez)
                {
                    var otherId = b.PersonAId == entityId ? b.PersonBId : b.PersonAId;
                    var (name, href) = CounterpartDisplay(map, (nameof(Person), otherId), "Person");
                    Remember(actorIds, b.CreatedById);
                    raw.Add(new Raw(b.CreatedAt, TimelineCategory.Relation,
                        $"Beziehung ({RelationTypeDisplay.Name(b.Type)}): {name}", b.Note, null, b.CreatedById, href, null));
                }
            }
        }

        // ---- 8) faction-specific: activities linked to this faction (internal only) ----
        if (entityType == nameof(Faction) && !isPartner)
        {
            var activityIds = await db.AgentActivityLinks
                .Where(l => l.TargetType == nameof(Faction) && l.TargetId == entityId)
                .Select(l => l.AgentActivityId)
                .ToListAsync(cancellationToken);
            var activities = await db.AgentActivities
                .Where(a => activityIds.Contains(a.Id))
                .Select(a => new { a.Title, a.Kind, a.ActivityDate, a.ContentHtml, a.CreatedById, a.CreatedAt })
                .ToListAsync(cancellationToken);
            foreach (var a in activities)
            {
                Remember(actorIds, a.CreatedById);
                var title = string.IsNullOrWhiteSpace(a.Kind) ? $"Aktivität: {a.Title}" : $"Aktivität ({a.Kind}): {a.Title}";
                raw.Add(new Raw(a.ActivityDate, TimelineCategory.Activity, title, PlainSnippet(a.ContentHtml), null, a.CreatedById, null, null, a.CreatedAt));
            }
        }
    }

    // resolves actor names once, projects + sorts newest first
    private async Task<IReadOnlyList<TimelineEntry>> MaterializeAsync(
        AppDbContext db, List<Raw> raw, HashSet<string> actorIds, CancellationToken cancellationToken)
    {
        var names = actorIds.Count == 0
            ? new Dictionary<string, string?>()
            : await db.Users.Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename })
                .ToDictionaryAsync(u => u.Id, u => (string?)u.Codename, cancellationToken);

        return raw
            .Select(r => new TimelineEntry(r.Timestamp, r.Category, r.Title, r.Detail,
                ActorName(r, names), r.Href, r.Changes, r.ActorId, r.RecordedAt))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    // strip HTML to a short plain-text timeline detail
    private static string? PlainSnippet(string? html, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length == 0 ? null : text.Length > max ? text[..max] + "…" : text;
    }

    // record self + its audited children; a common tail fans in polymorphic per-record custom-field values
    private static async Task<(string[] Types, HashSet<string> Ids)> AuditSourceAsync(
        AppDbContext db, string type, string id, CancellationToken ct)
    {
        var ids = new HashSet<string> { id };
        var types = new List<string> { type };
        switch (type)
        {
            case nameof(Person):
                ids.UnionWith(await db.PersonDocs.IgnoreQueryFilters().Where(d => d.PersonId == id).Select(d => d.Id).ToListAsync(ct));
                ids.UnionWith(await db.PersonPhotos.IgnoreQueryFilters().Where(f => f.PersonId == id).Select(f => f.Id).ToListAsync(ct));
                var noticeIds = await db.OeffentlicheFahndungen.IgnoreQueryFilters()
                    .Where(f => f.PersonId == id).Select(f => f.Id).ToListAsync(ct);
                ids.UnionWith(noticeIds);
                // second hop: the money on a head hangs off the notice, not off the file
                ids.UnionWith(await db.FahndungKopfgeldAnteile
                    .Where(k => noticeIds.Contains(k.WantedId)).Select(k => k.Id).ToListAsync(ct));
                // second hop as well; the messages are left out on purpose, a conversation is not file history
                ids.UnionWith(await db.Hinweise.IgnoreQueryFilters()
                    .Where(h => h.WantedId != null && noticeIds.Contains(h.WantedId)).Select(h => h.Id).ToListAsync(ct));
                // second hop as well: an objection disputes the notice, so it belongs to the file behind it
                ids.UnionWith(await db.FahndungEinsprueche.IgnoreQueryFilters()
                    .Where(e => noticeIds.Contains(e.WantedId)).Select(e => e.Id).ToListAsync(ct));
                // third hop, and staged rather than nested: the reward hangs off a share, which hangs off the notice
                var shareIds = await db.FahndungKopfgeldAnteile
                    .Where(k => noticeIds.Contains(k.WantedId)).Select(k => k.Id).ToListAsync(ct);
                ids.UnionWith(await db.HinweisBelohnungen
                    .Where(b => shareIds.Contains(b.ShareId)).Select(b => b.Id).ToListAsync(ct));
                types.AddRange([nameof(PersonDoc), nameof(PersonPhoto), nameof(OeffentlicheFahndung),
                    nameof(FahndungKopfgeldAnteil), nameof(Hinweis), nameof(HinweisBelohnung),
                    nameof(FahndungEinspruch)]);
                break;
            case nameof(Faction):
                ids.UnionWith(await db.FactionMembers.IgnoreQueryFilters().Where(m => m.FactionId == id).Select(m => m.Id).ToListAsync(ct));
                ids.UnionWith(await db.FactionAgents.IgnoreQueryFilters().Where(a => a.FactionId == id).Select(a => a.Id).ToListAsync(ct));
                ids.UnionWith(await db.FactionPhotos.IgnoreQueryFilters().Where(f => f.FactionId == id).Select(f => f.Id).ToListAsync(ct));
                ids.UnionWith(await db.OeffentlicheFraktionsprofile.IgnoreQueryFilters().Where(p => p.FactionId == id).Select(p => p.Id).ToListAsync(ct));
                types.AddRange([nameof(FactionMember), nameof(FactionAgent), nameof(FactionPhoto),
                    nameof(OeffentlichesFraktionsprofil)]);
                break;
            case nameof(PersonGroup):
                ids.UnionWith(await db.PersonGroupMembers.IgnoreQueryFilters().Where(m => m.PersonGroupId == id).Select(m => m.Id).ToListAsync(ct));
                ids.UnionWith(await db.PersonGroupAgents.IgnoreQueryFilters().Where(a => a.PersonGroupId == id).Select(a => a.Id).ToListAsync(ct));
                ids.UnionWith(await db.PersonGroupPhotos.IgnoreQueryFilters().Where(f => f.PersonGroupId == id).Select(f => f.Id).ToListAsync(ct));
                types.AddRange([nameof(PersonGroupMember), nameof(PersonGroupAgent), nameof(PersonGroupPhoto)]);
                break;
            case nameof(Party):
                ids.UnionWith(await db.PartyMembers.IgnoreQueryFilters().Where(m => m.PartyId == id).Select(m => m.Id).ToListAsync(ct));
                ids.UnionWith(await db.PartyAgents.IgnoreQueryFilters().Where(a => a.PartyId == id).Select(a => a.Id).ToListAsync(ct));
                ids.UnionWith(await db.PartyPhotos.IgnoreQueryFilters().Where(f => f.PartyId == id).Select(f => f.Id).ToListAsync(ct));
                types.AddRange([nameof(PartyMember), nameof(PartyAgent), nameof(PartyPhoto)]);
                break;
            case nameof(Operation):
                ids.UnionWith(await db.OperationAgents.IgnoreQueryFilters().Where(a => a.OperationId == id).Select(a => a.Id).ToListAsync(ct));
                types.Add(nameof(OperationAgent));
                break;
            case nameof(AgentAbduction):
                ids.UnionWith(await db.AbductionCompromises.Where(c => c.AbductionId == id).Select(c => c.Id).ToListAsync(ct));
                types.Add(nameof(AbductionCompromise));
                break;
            case nameof(Case):
                ids.UnionWith(await db.CaseAgents.IgnoreQueryFilters().Where(a => a.CaseId == id).Select(a => a.Id).ToListAsync(ct));
                types.Add(nameof(CaseAgent));
                break;
            case nameof(Taskforce):
                ids.UnionWith(await db.TaskforceAgents.IgnoreQueryFilters().Where(a => a.TaskforceId == id).Select(a => a.Id).ToListAsync(ct));
                types.Add(nameof(TaskforceAgent));
                break;
            case nameof(Job):
                ids.UnionWith(await db.JobAssignments.IgnoreQueryFilters().Where(z => z.JobId == id).Select(z => z.Id).ToListAsync(ct));
                types.Add(nameof(JobAssignment));
                break;
            case nameof(Appointment):
                ids.UnionWith(await db.AppointmentAssignments.IgnoreQueryFilters().Where(z => z.AppointmentId == id).Select(z => z.Id).ToListAsync(ct));
                types.Add(nameof(AppointmentAssignment));
                break;
            case nameof(Meeting):
                ids.UnionWith(await db.MeetingAgendaItems.IgnoreQueryFilters().Where(x => x.MeetingId == id).Select(x => x.Id).ToListAsync(ct));
                ids.UnionWith(await db.MeetingAttendances.Where(x => x.MeetingId == id).Select(x => x.Id).ToListAsync(ct));
                ids.UnionWith(await db.MeetingSignOffs.Where(x => x.MeetingId == id).Select(x => x.Id).ToListAsync(ct));
                types.AddRange([nameof(MeetingAgendaItem), nameof(MeetingAttendance), nameof(MeetingSignOff)]);
                break;
            case nameof(FinancingRequest):
                // lines are not soft-deletable, so no filter to ignore; quantity cuts show as line changes
                ids.UnionWith(await db.FinancingRequestLines.Where(l => l.RequestId == id).Select(l => l.Id).ToListAsync(ct));
                types.Add(nameof(FinancingRequestLine));
                break;
        }

        // polymorphic attachment: custom-field values on any record type
        var customFieldIds = await db.CustomFieldValues
            .Where(v => v.EntityType == type && v.EntityId == id)
            .Select(v => v.Id).ToListAsync(ct);
        if (customFieldIds.Count > 0)
        {
            ids.UnionWith(customFieldIds);
            types.Add(nameof(CustomFieldValue));
        }

        return (types.ToArray(), ids);
    }

    private static (string Name, string? Href) CounterpartDisplay(
        Dictionary<(string, string), RecordsReference.Resolution> map, (string, string) target, string covertNoun)
    {
        if (!map.TryGetValue(target, out var on))
        {
            return ($"nicht verfügbare {covertNoun}", null);
        }
        return on.Classified ? ($"verdeckte {covertNoun}", null) : (on.Display, on.Href);
    }

    private static void Remember(HashSet<string> ids, string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            ids.Add(id);
        }
    }

    private static string? ActorName(Raw r, Dictionary<string, string?> names)
    {
        if (!string.IsNullOrWhiteSpace(r.ActorName))
        {
            return r.ActorName;
        }
        if (r.ActorId is { } id && names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
        return null;
    }
}
