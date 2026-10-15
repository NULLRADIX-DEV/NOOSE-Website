using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Authorization;

namespace NOOSE_Website.Services.Search;

/// <summary>A parent record as a content hit displays it.</summary>
public sealed record ParentView(string Type, string Id, string Title, string CaseNumber);

/// <summary>Resolves the parent references of polymorphic content to a title the viewer may see.</summary>
/// <remarks>
/// <para>Contract: the map contains ONLY visible parents — absent means hide the child. That is the exact inverse
/// of <see cref="Visibility.IsRecordVisibleAsync"/>'s tail, which answers "visible" for a type it does not know,
/// and the inversion is the point: a parent type nobody taught this resolver about hides its children rather than
/// exposing them.</para>
/// <para>The predecessor knew eight parent types and silently dropped the rest, so a comment or source on a
/// document, meeting, evidence item, cash entry, abduction, funding request, personnel file or informant was
/// simply unfindable.</para>
/// <para>Batching is one flat <c>WHERE Id IN (…)</c> per parent type actually present, never per row. No
/// <c>SelectMany</c> and no correlated collection projection — Pomelo turns those into CROSS APPLY, which MySQL
/// does not have.</para>
/// </remarks>
public static class SearchParentResolver
{
    /// <summary>Visible parents of the given references, keyed by (type, id).</summary>
    /// <param name="tagIds">When non-empty, a parent must also carry one of these tags to be resolved.</param>
    public static async Task<IReadOnlyDictionary<(string Type, string Id), ParentView>> ResolveVisibleAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, SearchViewer viewer,
        IReadOnlyCollection<string>? tagIds = null, CancellationToken cancellationToken = default,
        bool includeArchived = false)
    {
        var map = new Dictionary<(string, string), ParentView>();
        if (refs.Count == 0)
        {
            return map;
        }

        var byType = refs.GroupBy(r => r.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).Distinct().ToList(), StringComparer.Ordinal);
        List<string> Ids(string type) => byType.TryGetValue(type, out var ids) ? ids : [];

        var scope = viewer.Scope;
        void Put(string type, string id, string? title, string? caseNumber)
            => map[(type, id)] = new ParentView(type, id, string.IsNullOrWhiteSpace(title) ? SearchCatalog.German(type) : title!,
                caseNumber ?? string.Empty);

        // ---- the six classifiable records; a partner reaches them only through a release ----
        if (Ids(nameof(Person)) is { Count: > 0 } personIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.People.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.People.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(p => personIds.Contains(p.Id))
                         .Select(p => new { p.Id, p.Name, p.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Person), x.Id, x.Name, x.CaseNumber);
            }
        }
        if (Ids(nameof(Faction)) is { Count: > 0 } factionIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Factions.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Factions.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(f => factionIds.Contains(f.Id))
                         .Select(f => new { f.Id, f.Name, f.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Faction), x.Id, x.Name, x.CaseNumber);
            }
        }
        if (Ids(nameof(PersonGroup)) is { Count: > 0 } groupIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.PersonGroups.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.PersonGroups.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(g => groupIds.Contains(g.Id))
                         .Select(g => new { g.Id, g.Name, g.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(PersonGroup), x.Id, x.Name, x.CaseNumber);
            }
        }
        if (Ids(nameof(Party)) is { Count: > 0 } partyIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Parties.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Parties.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(p => partyIds.Contains(p.Id))
                         .Select(p => new { p.Id, p.Name, p.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Party), x.Id, x.Name, x.CaseNumber);
            }
        }
        if (Ids(nameof(Operation)) is { Count: > 0 } operationIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Operations.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Operations.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(o => operationIds.Contains(o.Id))
                         .Select(o => new { o.Id, o.Title, o.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Operation), x.Id, x.Title, x.CaseNumber);
            }
        }
        if (Ids(nameof(Case)) is { Count: > 0 } caseIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Cases.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Cases.OnlyVisible(scope);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(v => caseIds.Contains(v.Id))
                         .Select(v => new { v.Id, v.Title, v.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Case), x.Id, x.Title, x.CaseNumber);
            }
        }

        // ---- membership / assignment gated ----
        if (Ids(nameof(Taskforce)) is { Count: > 0 } taskforceIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Taskforces.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Taskforces.OnlyVisible(db, scope.MayAllTaskforces, scope.MeId);
            q = includeArchived ? q : q.OnlyActive();
            foreach (var x in await q.Where(t => taskforceIds.Contains(t.Id))
                         .Select(t => new { t.Id, t.Name, t.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Taskforce), x.Id, x.Name, x.CaseNumber);
            }
        }
        if (!viewer.IsPartner && Ids(nameof(Job)) is { Count: > 0 } jobIds)
        {
            foreach (var x in await db.Jobs.OnlyVisible(db, scope.MayAllTaskforces, scope.MeId)
                         .Where(a => jobIds.Contains(a.Id))
                         .Select(a => new { a.Id, a.Title, a.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Job), x.Id, x.Title, x.CaseNumber);
            }
        }
        if (!viewer.IsPartner && Ids(nameof(Appointment)) is { Count: > 0 } appointmentIds)
        {
            foreach (var x in await db.Appointments.OnlyVisible(db, scope.MayAllTaskforces, scope.MeId)
                         .Where(t => appointmentIds.Contains(t.Id))
                         .Select(t => new { t.Id, t.Title, t.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Appointment), x.Id, x.Title, x.CaseNumber);
            }
        }
        if (!viewer.IsPartner && Ids(nameof(FinancingRequest)) is { Count: > 0 } financingIds)
        {
            foreach (var x in await db.FinancingRequests.OnlyVisible(scope.MayClassifiedRead, scope.MeId)
                         .Where(f => financingIds.Contains(f.Id))
                         .Select(f => new { f.Id, f.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(FinancingRequest), x.Id, x.CaseNumber, x.CaseNumber);
            }
        }
        if (!viewer.IsPartner && Ids(nameof(Informant)) is { Count: > 0 } informantIds)
        {
            foreach (var x in await db.Informants.Where(i => informantIds.Contains(i.Id))
                         .Select(i => new { i.Id, i.RealName, i.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Informant), x.Id, x.RealName ?? x.CaseNumber, x.CaseNumber);
            }
        }

        // ---- documents: secrecy, owning taskforce, per-agent revocation ----
        if (Ids(nameof(Document)) is { Count: > 0 } documentIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Documents.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Documents.OnlyVisible(db, scope.AsDocumentScope());
            foreach (var x in await q.Where(d => documentIds.Contains(d.Id))
                         .Select(d => new { d.Id, d.Title }).ToListAsync(cancellationToken))
            {
                Put(nameof(Document), x.Id, x.Title, null);
            }
        }

        // ---- time gated ----
        if (!viewer.IsPartner && Ids(nameof(Meeting)) is { Count: > 0 } meetingIds)
        {
            foreach (var x in await db.Meetings.Where(m => meetingIds.Contains(m.Id))
                         .Select(m => new { m.Id, m.Title, m.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Meeting), x.Id, x.Title, x.CaseNumber);
            }
        }

        // ---- role gated ----
        if (scope.MayClassifiedRead && Ids(nameof(Agent)) is { Count: > 0 } agentIds)
        {
            var mayRealName = viewer.User.MayRealNameSee();
            foreach (var x in await db.Users.Where(u => agentIds.Contains(u.Id))
                         .Select(u => new { u.Id, u.Codename, u.RealName }).ToListAsync(cancellationToken))
            {
                Put(nameof(Agent), x.Id, AgentNameDisplay.Pick(x.Codename, x.RealName, mayRealName), null);
            }
        }
        if (scope.MayRecruiting && Ids(nameof(Bewerbung)) is { Count: > 0 } bewerbungIds)
        {
            foreach (var x in await db.Bewerbungen.Where(b => bewerbungIds.Contains(b.Id))
                         .Select(b => new { b.Id, b.Name, b.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Bewerbung), x.Id, x.Name, x.CaseNumber);
            }
        }

        // ---- internal, no further gate of their own ----
        if (!viewer.IsPartner)
        {
            if (Ids(nameof(AgentAbduction)) is { Count: > 0 } abductionIds)
            {
                foreach (var x in await db.AgentAbductions.Where(e => abductionIds.Contains(e.Id))
                             .Select(e => new { e.Id, e.CaseNumber }).ToListAsync(cancellationToken))
                {
                    Put(nameof(AgentAbduction), x.Id, x.CaseNumber, x.CaseNumber);
                }
            }
            if (Ids(nameof(EvidenceItem)) is { Count: > 0 } evidenceItemIds)
            {
                foreach (var x in await db.EvidenceItems.Where(e => evidenceItemIds.Contains(e.Id))
                             .Select(e => new { e.Id, e.Name }).ToListAsync(cancellationToken))
                {
                    Put(nameof(EvidenceItem), x.Id, x.Name, null);
                }
            }
            if (Ids(nameof(EvidenceEntry)) is { Count: > 0 } evidenceEntryIds)
            {
                foreach (var x in await db.EvidenceEntries.Where(e => evidenceEntryIds.Contains(e.Id))
                             .Select(e => new { e.Id, e.CaseNumber }).ToListAsync(cancellationToken))
                {
                    Put(nameof(EvidenceEntry), x.Id, x.CaseNumber, x.CaseNumber);
                }
            }
            if (Ids(nameof(KassenBuchung)) is { Count: > 0 } kasseIds)
            {
                foreach (var x in await db.KassenBuchungen.Where(k => kasseIds.Contains(k.Id))
                             .Select(k => new { k.Id, k.CaseNumber }).ToListAsync(cancellationToken))
                {
                    Put(nameof(KassenBuchung), x.Id, x.CaseNumber, x.CaseNumber);
                }
            }
            if (Ids(nameof(AgentActivity)) is { Count: > 0 } activityIds)
            {
                foreach (var x in await db.AgentActivities.Where(a => activityIds.Contains(a.Id))
                             .Select(a => new { a.Id, a.Title }).ToListAsync(cancellationToken))
                {
                    Put(nameof(AgentActivity), x.Id, x.Title, null);
                }
            }
            if (Ids(nameof(Announcement)) is { Count: > 0 } announcementIds)
            {
                var myTaskforces = await AnnouncementVisibility.MyTaskforceIdsAsync(db, scope.MeId, cancellationToken);
                foreach (var x in await db.Announcements.OnlyVisible(viewer.User, myTaskforces)
                             .Where(a => announcementIds.Contains(a.Id))
                             .Select(a => new { a.Id, a.Title, a.CaseNumber }).ToListAsync(cancellationToken))
                {
                    Put(nameof(Announcement), x.Id, x.Title, x.CaseNumber);
                }
            }
        }

        // laws are released to partners individually and never classified
        if (Ids(nameof(Law)) is { Count: > 0 } lawIds)
        {
            var q = scope.PartnerAgency is { } agency
                ? db.Laws.OnlyPartnerVisible(db, agency, scope.MeId)
                : db.Laws.AsQueryable();
            foreach (var x in await q.Where(g => lawIds.Contains(g.Id))
                         .Select(g => new { g.Id, g.Paragraph, g.Title }).ToListAsync(cancellationToken))
            {
                Put(nameof(Law), x.Id, $"{x.Paragraph} {x.Title}".Trim(), x.Paragraph);
            }
        }

        // Citizen tips are not a polymorphic parent; they are a LINK endpoint. TipTakeoverService writes
        // Link.SourceType = nameof(Hinweis), and LinkSearchProvider drops any link whose either end fails to
        // resolve here — so without this arm every takeover link is silently unfindable. Never the citizen:
        // the resolved title feeds a snippet that renders both ends.
        if (!viewer.IsPartner && viewer.User.IsInternalAgent()
            && Ids(nameof(Hinweis)) is { Count: > 0 } tipIds)
        {
            foreach (var x in await db.Hinweise.Where(h => tipIds.Contains(h.Id))
                         .Select(h => new { h.Id, h.CaseNumber }).ToListAsync(cancellationToken))
            {
                Put(nameof(Hinweis), x.Id, "Bürgerhinweis " + x.CaseNumber, x.CaseNumber);
            }
        }

        // a link endpoint too; group, rank and invites decide, as on the presentation list
        if (!viewer.IsPartner && Ids(nameof(Presentation)) is { Count: > 0 } presentationIds)
        {
            foreach (var (id, title) in await PresentationVisibility.VisibleTitlesAsync(db, presentationIds, scope, cancellationToken))
            {
                Put(nameof(Presentation), id, title, null);
            }
        }

        // NOTE: no default arm. An unhandled parent type stays absent, which hides its children.

        if (tagIds is { Count: > 0 })
        {
            await NarrowToTaggedAsync(db, map, tagIds, cancellationToken);
        }
        return map;
    }

    /// <summary>Drops parents that carry none of the selected tags.</summary>
    private static async Task NarrowToTaggedAsync(AppDbContext db,
        Dictionary<(string, string), ParentView> map, IReadOnlyCollection<string> tagIds, CancellationToken cancellationToken)
    {
        var ids = map.Keys.Select(k => k.Item2).Distinct().ToList();
        var tags = tagIds.ToList();
        // one pass over the mapping table; the (EntityType, EntityId) pair is re-checked in memory
        var tagged = (await db.TagMappings
                .Where(z => tags.Contains(z.TagId) && ids.Contains(z.EntityId))
                .Select(z => new { z.EntityType, z.EntityId })
                .ToListAsync(cancellationToken))
            .Select(z => (z.EntityType, z.EntityId))
            .ToHashSet();

        foreach (var key in map.Keys.Where(k => !tagged.Contains(k)).ToList())
        {
            map.Remove(key);
        }
    }
}
