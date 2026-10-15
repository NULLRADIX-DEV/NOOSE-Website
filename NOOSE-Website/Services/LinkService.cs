using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ILinkService" />
public class LinkService(IDbContextFactory<AppDbContext> dbFactory, IThreatScoreService threat) : ILinkService
{
    /// <summary>Every type this service can resolve. A known type that stays unresolved is hidden, not guessed.</summary>
    /// <remarks>
    /// Public so a test can hold it against <c>SearchCatalog</c>: a type listed here without a catalog row renders
    /// its raw CLR name as a group heading, which is how "Law" and "Hinweis" once reached the screen.
    /// </remarks>
    public static readonly IReadOnlyList<string> KnownTypes =
    [
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party),
        nameof(Operation), nameof(Taskforce), nameof(Case), nameof(Agent),
        nameof(PersonDoc), nameof(Observation), nameof(Job), nameof(Law), nameof(Document),
        nameof(Bewerbung), nameof(Meeting), nameof(MeetingAgendaItem), nameof(Hinweis), nameof(Ticket),
        nameof(Presentation),
    ];

    /// <summary>Known types the scope-free <see cref="RecordsReference"/> also names, for mentions and compromises.</summary>
    /// <remarks>A presentation is left out: who sees one depends on groups and invites, which that resolver is
    /// never handed, so a reference to it would never resolve.</remarks>
    public static readonly IReadOnlyList<string> ReferenceTypes =
        KnownTypes.Where(t => t != nameof(Presentation)).ToList();

    /// <summary>What an operation or taskforce lists as involved: people and organisations, nothing else.</summary>
    public static readonly IReadOnlyList<string> InvolvedTypes =
        [nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party)];

    public Task<List<LinkDisplay>> GetForRecordAsync(string entityType, string entityId, bool isLeadership, string? meId, LinkKind? kind = null, CancellationToken cancellationToken = default)
        => GetForRecordAsync(entityType, entityId, new ViewerScope(isLeadership, isLeadership, meId, null), kind, cancellationToken);

    public async Task<List<LinkDisplay>> GetForRecordAsync(string entityType, string entityId, ViewerScope scope, LinkKind? kind = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var isLeadership = scope.MayClassifiedRead;
        var meId = scope.MeId;
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            return new();
        }

        var query = db.Links
            .Where(v => (v.SourceType == entityType && v.SourceId == entityId)
                     || (v.TargetType == entityType && v.TargetId == entityId));
        if (kind is not null)
        {
            query = query.Where(v => v.Kind == kind.Value);
        }
        var raw = await query
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        // determine the "other side" relative to the viewed record
        var pairs = raw.Select(v =>
        {
            var isBy = v.SourceType == entityType && v.SourceId == entityId;
            return (V: v,
                    OtherType: isBy ? v.TargetType : v.SourceType,
                    OtherId: isBy ? v.TargetId : v.SourceId);
        }).ToList();

        // resolve targets per type, merged into one lookup map
        var targets = new Dictionary<(string Type, string Id), (string Designation, bool Classified, string? Href)>();

        var personIds = pairs.Where(p => p.OtherType == nameof(Person)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.People.Where(p => personIds.Contains(p.Id))
                     .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Person), x.Id)] = ($"{x.Name} ({x.CaseNumber})", x.IsClassified, $"/personen/{x.Id}");
        }

        var factionIds = pairs.Where(p => p.OtherType == nameof(Faction)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Factions.Where(f => factionIds.Contains(f.Id))
                     .Select(f => new { f.Id, f.Name, f.CaseNumber, f.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Faction), x.Id)] = ($"{x.Name} ({x.CaseNumber})", x.IsClassified, $"/fraktionen/{x.Id}");
        }

        var groupsIds = pairs.Where(p => p.OtherType == nameof(PersonGroup)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.PersonGroups.Where(g => groupsIds.Contains(g.Id))
                     .Select(g => new { g.Id, g.Name, g.CaseNumber, g.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(PersonGroup), x.Id)] = ($"{x.Name} ({x.CaseNumber})", x.IsClassified, $"/personengruppen/{x.Id}");
        }

        var partyIds = pairs.Where(p => p.OtherType == nameof(Party)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Parties.Where(p => partyIds.Contains(p.Id))
                     .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Party), x.Id)] = ($"{x.Name} ({x.CaseNumber})", x.IsClassified, $"/parteien/{x.Id}");
        }

        var operationIds = pairs.Where(p => p.OtherType == nameof(Operation)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Operations.Where(o => operationIds.Contains(o.Id))
                     .Select(o => new { o.Id, o.Title, o.CaseNumber, o.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Operation), x.Id)] = ($"{x.Title} ({x.CaseNumber})", x.IsClassified, $"/operationen/{x.Id}");
        }

        // resolve only taskforces visible to the viewer; others stay hidden
        var taskforceIds = pairs.Where(p => p.OtherType == nameof(Taskforce)).Select(p => p.OtherId).Distinct().ToList();
        var visibleTf = await TaskforceVisibility.VisibleIdsAsync(db, taskforceIds, isLeadership, meId, cancellationToken);
        foreach (var x in await db.Taskforces.Where(t => visibleTf.Contains(t.Id))
                     .Select(t => new { t.Id, t.Name, t.CaseNumber, t.IsClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Taskforce), x.Id)] = ($"{x.Name} ({x.CaseNumber})", x.IsClassified, $"/taskforces/{x.Id}");
        }

        // cases honour the viewer's full secrecy scope (TRU/HRB audience), not just leadership
        var caseIds = pairs.Where(p => p.OtherType == nameof(Case)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Cases.Where(v => caseIds.Contains(v.Id))
                     .Select(v => new { v.Id, v.Title, v.CaseNumber, v.IsClassified, v.IsTRUClassified, v.IsHRBClassified }).ToListAsync(cancellationToken))
        {
            var level = !x.IsClassified ? DocumentClassification.None
                : x.IsTRUClassified ? DocumentClassification.Tru
                : x.IsHRBClassified ? DocumentClassification.Hrb
                : DocumentClassification.Leadership;
            if (!scope.CanSee(level))
            {
                continue;
            }
            // already vetted by CanSee; flag as unclassified so the leadership-only filter below never re-hides it
            targets[(nameof(Case), x.Id)] = ($"{x.Title} ({x.CaseNumber})", false, $"/vorgaenge/{x.Id}");
        }

        // jobs have no classification concept (team board)
        var jobIds = pairs.Where(p => p.OtherType == nameof(Job)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Jobs.Where(a => jobIds.Contains(a.Id))
                     .Select(a => new { a.Id, a.Title, a.CaseNumber }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Job), x.Id)] = ($"{x.Title} ({x.CaseNumber})", false, $"/aufgaben/{x.Id}");
        }

        // agent: no classification, no detail page (href null), codename only
        var agentIds = pairs.Where(p => p.OtherType == nameof(Agent)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Users.Where(u => agentIds.Contains(u.Id))
                     .Select(u => new { u.Id, u.Codename }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Agent), x.Id)] = (string.IsNullOrWhiteSpace(x.Codename) ? "(unbenannter Agent)" : x.Codename, false, null);
        }

        // person doc inherits visibility from its person via the join
        var docIds = pairs.Where(p => p.OtherType == nameof(PersonDoc)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.PersonDocs.Where(d => docIds.Contains(d.Id))
                     .Join(db.People, d => d.PersonId, p => p.Id,
                           (d, p) => new { d.Id, d.Timestamp, PersonId = p.Id, PersonName = p.Name, p.IsClassified })
                     .ToListAsync(cancellationToken))
        {
            targets[(nameof(PersonDoc), x.Id)] = ($"Dok – {x.PersonName} ({x.Timestamp.ToLocalTime():dd.MM.yyyy})", x.IsClassified, $"/personen/{x.PersonId}?tab=doks");
        }

        // observation inherits visibility from its person via the join
        var observationIds = pairs.Where(p => p.OtherType == nameof(Observation)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Observations.Where(o => observationIds.Contains(o.Id))
                     .Join(db.People, o => o.PersonId, p => p.Id,
                           (o, p) => new { o.Id, o.Start, PersonId = p.Id, PersonName = p.Name, p.IsClassified })
                     .ToListAsync(cancellationToken))
        {
            targets[(nameof(Observation), x.Id)] = ($"Observation – {x.PersonName} ({x.Start.ToLocalTime():dd.MM.yyyy})", x.IsClassified, $"/personen/{x.PersonId}?tab=ueberwachung");
        }

        // citizen tips: the case number only. A tip carries a citizen behind it, and the designation must not
        var tipIds = pairs.Where(p => p.OtherType == nameof(Hinweis)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Hinweise.Where(h => tipIds.Contains(h.Id))
                     .Select(h => new { h.Id, h.CaseNumber }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Hinweis), x.Id)] = ($"Bürgerhinweis {x.CaseNumber}", false, $"/hinweise/{x.Id}");
        }

        // citizen tickets: the case number only — the subject is written by the citizen and can name them. Resolved
        // for the desk and for an agent attached to the row, exactly TicketVisibility. Vetted here, so it is flagged
        // unclassified like the case arm above, or the leadership filter below would re-hide it from a participant.
        var ticketIds = pairs.Where(p => p.OtherType == nameof(Ticket)).Select(p => p.OtherId).Distinct().ToList();
        var readableTickets = await TicketVisibility.ReadableIdsAsync(db, ticketIds, scope, cancellationToken);
        if (readableTickets.Count > 0)
        {
            foreach (var x in await db.Tickets.Where(t => readableTickets.Contains(t.Id))
                         .Select(t => new { t.Id, t.CaseNumber }).ToListAsync(cancellationToken))
            {
                targets[(nameof(Ticket), x.Id)] = ($"Bürger-Ticket {x.CaseNumber}", false, $"/tickets/{x.Id}");
            }
        }

        // library documents: classified if any VS flag is set, leadership-gated below
        var documentIds = pairs.Where(p => p.OtherType == nameof(Document)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Documents.Where(d => documentIds.Contains(d.Id))
                     .Select(d => new { d.Id, d.Title, Classified = d.IsClassified || d.IsTRUClassified || d.IsHRBClassified }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Document), x.Id)] = (string.IsNullOrWhiteSpace(x.Title) ? "Dokument" : x.Title, x.Classified, $"/dokumente/{x.Id}");
        }

        // laws have no classification concept (knowledge base)
        var lawIds = pairs.Where(p => p.OtherType == nameof(Law)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Laws.Where(g => lawIds.Contains(g.Id))
                     .Select(g => new { g.Id, g.Paragraph, g.Title, g.LawBook }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Law), x.Id)] = ($"{x.Paragraph} {x.Title} ({x.LawBook})", false, $"/gesetze/{x.Id}");
        }

        // applications: HRB or leadership only (never read-only supervision); others see nothing (kept unresolved -> hidden below)
        if (scope.MayRecruiting && scope.PartnerAgency is null)
        {
            var bewerbungIds = pairs.Where(p => p.OtherType == nameof(Bewerbung)).Select(p => p.OtherId).Distinct().ToList();
            foreach (var x in await db.Bewerbungen.Where(b => bewerbungIds.Contains(b.Id))
                         .Select(b => new { b.Id, b.Name, b.CaseNumber }).ToListAsync(cancellationToken))
            {
                targets[(nameof(Bewerbung), x.Id)] = ($"{x.Name} ({x.CaseNumber})", false, $"/bewerbungen/{x.Id}");
            }
        }

        // meetings have no classification concept; only their agenda is rank-gated
        var meetingIds = pairs.Where(p => p.OtherType == nameof(Meeting)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var x in await db.Meetings.Where(m => meetingIds.Contains(m.Id))
                     .Select(m => new { m.Id, m.Title, m.CaseNumber }).ToListAsync(cancellationToken))
        {
            targets[(nameof(Meeting), x.Id)] = ($"{x.Title} ({x.CaseNumber})", false, $"/besprechungen/{x.Id}");
        }

        // agenda items: rank 3+ see all; other internal viewers see an item 2h after its meeting; partners never
        var agendaItemIds = pairs.Where(p => p.OtherType == nameof(MeetingAgendaItem)).Select(p => p.OtherId).Distinct().ToList();
        if (agendaItemIds.Count > 0 && !scope.IsPartner)
        {
            var now = DateTime.UtcNow;
            foreach (var x in await db.MeetingAgendaItems.Where(p => agendaItemIds.Contains(p.Id))
                         .Join(db.Meetings, p => p.MeetingId, m => m.Id,
                               (p, m) => new { p.Id, p.Title, MeetingId = m.Id, MeetingTitle = m.Title, m.Start, m.End })
                         .ToListAsync(cancellationToken))
            {
                if (scope.MayAgenda || now >= MeetingVisibility.PublicFrom(x.Start, x.End))
                {
                    targets[(nameof(MeetingAgendaItem), x.Id)] =
                        ($"{x.MeetingTitle} – {x.Title}", false, $"/besprechungen/{x.MeetingId}?tab=agenda#top-{x.Id}");
                }
            }
        }

        // vetted by PresentationVisibility, never flagged classified
        var presentationIds = pairs.Where(p => p.OtherType == nameof(Presentation)).Select(p => p.OtherId).Distinct().ToList();
        foreach (var (id, title) in await PresentationVisibility.VisibleTitlesAsync(db, presentationIds, scope, cancellationToken))
        {
            targets[(nameof(Presentation), id)] = (title, false, $"/praesentationen/{id}");
        }

        // partners: only links to released, releasable-type records
        HashSet<(string, string)>? releasedTargets = null;
        if (scope.PartnerAgency is { } agency)
        {
            releasedTargets = new();
            foreach (var t in new[] { nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Operation), nameof(Case), nameof(Law) })
            {
                var ids = pairs.Where(p => p.OtherType == t).Select(p => p.OtherId).Distinct().ToList();
                foreach (var id in await PartnerVisibility.ReleasedParentIdsAsync(db, t, ids, agency, scope.MeId, cancellationToken))
                {
                    releasedTargets.Add((t, id));
                }
            }
        }

        var result = new List<LinkDisplay>();
        foreach (var p in pairs)
        {
            if (releasedTargets is not null && !releasedTargets.Contains((p.OtherType, p.OtherId)))
            {
                continue;
            }
            if (targets.TryGetValue((p.OtherType, p.OtherId), out var info))
            {
                // classified visible to leadership only
                if (info.Classified && !isLeadership)
                {
                    continue;
                }
                result.Add(new LinkDisplay(p.V.Id, p.OtherType, p.OtherId, p.V.Label, info.Designation, p.V.Automatic, info.Href));
            }
            else if (KnownTypes.Contains(p.OtherType))
            {
                // known type but unresolved (trashed/unknown) -> hide
            }
            else
            {
                // unknown type -> raw label, no nav target
                result.Add(new LinkDisplay(p.V.Id, p.OtherType, p.OtherId, p.V.Label, p.OtherId, p.V.Automatic));
            }
        }
        return result;
    }

    /// <param name="allowedTargetTypes">
    /// The types the calling panel offers. Passed in and checked here because the picker's own filter is a
    /// convenience: the socket takes whatever it is sent, so a conflict list could otherwise be handed a ticket
    /// or a citizen tip over the circuit and would render it forever after as a record of the wrong kind.
    /// </param>
    public async Task CreateAsync(string sourceType, string sourceId, string targetType, string targetId, string? label, ClaimsPrincipal actor, LinkKind kind = LinkKind.Default, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? allowedTargetTypes = null)
    {
        if (allowedTargetTypes is { Count: > 0 } erlaubt && !erlaubt.Contains(targetType, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Dieser Aktentyp ist an dieser Stelle nicht verknüpfbar.");
        }
        if (sourceType == targetType && sourceId == targetId)
        {
            throw new InvalidOperationException("Eine Akte kann nicht mit sich selbst verknüpft werden.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // don't let forged calls link to a classified/restricted target the actor can't see
        if (targetType is nameof(Person) or nameof(Faction) or nameof(PersonGroup) or nameof(Party)
                or nameof(Operation) or nameof(Taskforce) or nameof(Case) or nameof(Document) or nameof(Bewerbung)
                or nameof(Meeting) or nameof(MeetingAgendaItem) or nameof(Hinweis) or nameof(Ticket)
                or nameof(Presentation)
            && !await Visibility.IsRecordVisibleAsync(db, targetType, targetId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Auf diese Akte darfst du nicht verlinken (Verschlusssache oder nicht vorhanden).");
        }

        // prevent a duplicate active link of the same kind in either direction
        var exists = await db.Links.AnyAsync(v => v.Kind == kind
            && ((v.SourceType == sourceType && v.SourceId == sourceId && v.TargetType == targetType && v.TargetId == targetId)
             || (v.SourceType == targetType && v.SourceId == targetId && v.TargetType == sourceType && v.TargetId == sourceId)),
            cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException("Diese Verknüpfung besteht bereits.");
        }

        db.Links.Add(new Link
        {
            SourceType = sourceType,
            SourceId = sourceId,
            TargetType = targetType,
            TargetId = targetId,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Kind = kind,
        });
        await db.SaveChangesAsync(cancellationToken);

        // links touching a faction affect its threat score -> recompute
        await ThreatNewCalculateAsync(sourceType, sourceId, targetType, targetId, cancellationToken);
    }

    public async Task<BatchOutcome> CreateManyAsync(string anchorType, string anchorId,
        IReadOnlyCollection<(string Type, string Id)> targets, string? label, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var wanted = RecordBatch.Normalize(targets);
        var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (trimmed is { Length: > 200 })
        {
            throw new InvalidOperationException("Die Bezeichnung darf höchstens 200 Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = ViewerScope.From(actor);
        if (!RecordBatch.LinkAnchors.Contains(anchorType)
            || !await RecordBatch.ExistsAndVisibleAsync(db, anchorType, anchorId, scope, cancellationToken))
        {
            throw new UnauthorizedAccessException("Mit dieser Akte lässt sich nicht gesammelt verknüpfen.");
        }

        var allowed = RecordBatch.LinkTargetsFor(anchorType);
        var refused = 0;
        var candidates = new List<(string Type, string Id)>();
        foreach (var t in wanted)
        {
            if (!allowed.Contains(t.Type) || (t.Type == anchorType && t.Id == anchorId)
                || !await RecordBatch.ExistsAndVisibleAsync(db, t.Type, t.Id, scope, cancellationToken))
            {
                refused++;
                continue;
            }
            candidates.Add(t);
        }

        // one query for both directions; automatic colleague links count as linked too
        var ids = candidates.Select(c => c.Id).Distinct().ToList();
        var existing = (await db.Links.AsNoTracking()
                .Where(v => v.Kind == LinkKind.Default
                    && ((v.SourceType == anchorType && v.SourceId == anchorId && ids.Contains(v.TargetId))
                        || (v.TargetType == anchorType && v.TargetId == anchorId && ids.Contains(v.SourceId))))
                .Select(v => new { v.SourceType, v.SourceId, v.TargetType, v.TargetId })
                .ToListAsync(cancellationToken))
            .Select(v => v.SourceType == anchorType && v.SourceId == anchorId ? (v.TargetType, v.TargetId) : (v.SourceType, v.SourceId))
            .ToHashSet();

        var created = candidates.Where(c => !existing.Contains(c)).ToList();
        foreach (var c in created)
        {
            db.Links.Add(new Link
            {
                SourceType = anchorType,
                SourceId = anchorId,
                TargetType = c.Type,
                TargetId = c.Id,
                Label = trimmed,
                Kind = LinkKind.Default,
            });
        }
        if (created.Count > 0)
        {
            // one save: one audit batch and one follower fan-out for the whole selection
            await db.SaveChangesAsync(cancellationToken);
            await RecalculateOnceAsync(created.Append((anchorType, anchorId)), cancellationToken);
        }
        return new BatchOutcome(created.Count, candidates.Count - created.Count, refused);
    }

    /// <summary>Recomputes each touched faction and person once; the links stand even if a score fails.</summary>
    private async Task RecalculateOnceAsync(IEnumerable<(string Type, string Id)> records, CancellationToken cancellationToken)
    {
        foreach (var (type, id) in records.Distinct())
        {
            try
            {
                if (type == nameof(Faction))
                {
                    await threat.NewCalculateAsync(id, cancellationToken);
                }
                else if (type == nameof(Person))
                {
                    await threat.NewCalculatePersonScoreAsync(id, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Recomputes the threat score of each faction involved in the link (no-op for non-factions).</summary>
    private async Task ThreatNewCalculateAsync(string sourceType, string sourceId, string targetType, string targetId, CancellationToken cancellationToken)
    {
        if (sourceType == nameof(Faction))
        {
            await threat.NewCalculateAsync(sourceId, cancellationToken);
        }
        if (targetType == nameof(Faction))
        {
            await threat.NewCalculateAsync(targetId, cancellationToken);
        }
        if (sourceType == nameof(Person))
        {
            await threat.NewCalculatePersonScoreAsync(sourceId, cancellationToken);
        }
        if (targetType == nameof(Person))
        {
            await threat.NewCalculatePersonScoreAsync(targetId, cancellationToken);
        }
    }

    public async Task UpdateLabelAsync(string linkId, string? label, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var v = await db.Links.FirstOrDefaultAsync(x => x.Id == linkId, cancellationToken);
        if (v is null)
        {
            return;
        }
        v.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string linkId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var v = await db.Links.FirstOrDefaultAsync(x => x.Id == linkId, cancellationToken);
        if (v is null)
        {
            return;
        }
        db.Links.Remove(v); // soft-delete via interceptor
        await db.SaveChangesAsync(cancellationToken);

        await ThreatNewCalculateAsync(v.SourceType, v.SourceId, v.TargetType, v.TargetId, cancellationToken);
    }
}
