using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Requests;
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
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <summary>Central visibility check for records.</summary>
public static class Visibility
{
    /// <summary>The three secrecy bool columns of a classifiable record.</summary>
    private sealed record SecrecyRow(bool Classified, bool Tru, bool Hrb);

    /// <summary>True if record is visible to the viewer; partners see only released, non-classified records.</summary>
    public static async Task<bool> IsRecordVisibleAsync(
        AppDbContext db, string entityType, string entityId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.PartnerAgency is { } agency)
        {
            return await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, entityType, entityId, agency, scope.MeId, cancellationToken);
        }
        // taskforces gate on membership (MayAllTaskforces), not the classified-read flag
        if (entityType == nameof(Taskforce))
        {
            return await TaskforceVisibility.IsVisibleAsync(db, entityId, scope.MayAllTaskforces, scope.MeId, cancellationToken);
        }
        // leadership only
        if (entityType == nameof(Agent))
        {
            return scope.MayClassifiedRead;
        }
        // applications: HRB or leadership, mirrors the page policy
        // (bool-shim callers like mention-notification fanout can only carry the leadership flag, so keep MayClassifiedRead here)
        if (entityType == nameof(Bewerbung))
        {
            return (scope.MayClassifiedRead || scope.IsHrb)
                && await db.Bewerbungen.AnyAsync(b => b.Id == entityId, cancellationToken);
        }
        // funding requests: the requester plus anyone who reads everything (leadership/supervision).
        // MUST stay explicit — the tail of this method treats an unknown type as visible to all.
        if (entityType == nameof(FinancingRequest))
        {
            return await FinancingVisibility.IsVisibleAsync(db, entityId, scope.MayClassifiedRead, scope.MeId, cancellationToken);
        }
        // documents: secrecy level alone is not the gate — the owning taskforce and per-agent revocation count too
        if (entityType == nameof(Document))
        {
            return await DocumentVisibility.IsVisibleAsync(db, entityId, scope.AsDocumentScope(), cancellationToken);
        }

        SecrecyRow? row = entityType switch
        {
            nameof(Person) => await db.People
                .Where(p => p.Id == entityId)
                .Select(p => new SecrecyRow(p.IsClassified, p.IsTRUClassified, p.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(Faction) => await db.Factions
                .Where(f => f.Id == entityId)
                .Select(f => new SecrecyRow(f.IsClassified, f.IsTRUClassified, f.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups
                .Where(g => g.Id == entityId)
                .Select(g => new SecrecyRow(g.IsClassified, g.IsTRUClassified, g.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(Party) => await db.Parties
                .Where(p => p.Id == entityId)
                .Select(p => new SecrecyRow(p.IsClassified, p.IsTRUClassified, p.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(Operation) => await db.Operations
                .Where(o => o.Id == entityId)
                .Select(o => new SecrecyRow(o.IsClassified, o.IsTRUClassified, o.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(Case) => await db.Cases
                .Where(v => v.Id == entityId)
                .Select(v => new SecrecyRow(v.IsClassified, v.IsTRUClassified, v.IsHRBClassified))
                .FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };

        // classifiable types: visible per the viewer's secrecy scope (null = not found)
        if (entityType is nameof(Person) or nameof(Faction) or nameof(PersonGroup)
            or nameof(Party) or nameof(Operation) or nameof(Case))
        {
            return row is not null && RecordVisibility.IsVisible(scope, row.Classified, row.Tru, row.Hrb);
        }

        // gated by their own helper: existence alone would answer past the rule that owns the type
        switch (entityType)
        {
            // restricted jobs belong to their creator and assignees; this used to be an existence check,
            // which handed every restricted job to every agent that asked for it by id
            case nameof(Job):
                return await db.Jobs.OnlyVisible(db, scope.MayClassifiedRead, scope.MeId)
                    .AnyAsync(a => a.Id == entityId, cancellationToken);
            // same story for a non-public appointment
            case nameof(Appointment):
                return await db.Appointments.OnlyVisible(db, scope.MayClassifiedRead, scope.MeId)
                    .AnyAsync(t => t.Id == entityId, cancellationToken);
            // agenda: rank/supervision at once, any other internal agent 2h after the meeting, partners never
            case nameof(MeetingAgendaItem):
                return await AgendaItemVisibleAsync(db, entityId, scope, cancellationToken);
            case nameof(Informant):
                return await InformantVisibleAsync(db, entityId, scope, cancellationToken);
            case nameof(Announcement):
                return await db.Announcements
                    .OnlyVisible(scope, await AnnouncementVisibility.MyTaskforceIdsAsync(db, scope.MeId, cancellationToken))
                    .AnyAsync(a => a.Id == entityId, cancellationToken);
            // the roster tier grants the row but not the reason; that split lives in the reader, not here
            case nameof(Absence):
                return await db.Absences
                    .OnlyVisible(db, scope.MayClassifiedRead ? AbsenceViewScope.All : AbsenceViewScope.Team, scope.MeId)
                    .AnyAsync(a => a.Id == entityId, cancellationToken);
            case nameof(LibraryFile):
                return await db.LibraryFiles.OnlyVisible(scope.AsDocumentScope())
                    .AnyAsync(f => f.Id == entityId, cancellationToken);
            // a request is as visible as the record it is about, plus always to its own requester
            case nameof(Request):
                return await RequestVisibleAsync(db, entityId, scope, cancellationToken);
            // recruiting material: HRB or leadership, same audience as the application itself
            case nameof(BewerbungMessage):
                return scope.MayRecruiting
                    && await db.BewerbungMessages.AnyAsync(m => m.Id == entityId, cancellationToken);
            case nameof(Bewerbungssperre):
                return scope.MayRecruiting
                    && await db.Bewerbungssperren.AnyAsync(s => s.Id == entityId, cancellationToken);
            case nameof(BewerbungTest):
                return scope.MayRecruiting
                    && await db.BewerbungTests.AnyAsync(t => t.Id == entityId, cancellationToken);
            // leadership-only material
            case nameof(SituationReport):
                return scope.MayClassifiedRead
                    && await db.SituationReports.AnyAsync(r => r.Id == entityId, cancellationToken);
            case nameof(AgentNote):
                return scope.MayClassifiedRead
                    && await db.AgentNotes.AnyAsync(n => n.Id == entityId, cancellationToken);
            // counter-intelligence: leadership but never read-only supervision, which a scope cannot express.
            // Narrower than MayCounterIntel() by exactly that, which is an omission and never a leak.
            case nameof(CounterIntelRule):
                return scope.IsLeadership
                    && await db.CounterIntelRules.AnyAsync(r => r.Id == entityId, cancellationToken);
            // Citizen tips: every internal agent, the read-only supervision included — the audience of
            // Permission.RequireTipRead, expressed on the scope. Written as IsInternalAgent and NOT as
            // MayClassifiedRead because this gate also decides comments, sources, followups, links and watchlist
            // entries on a tip; the narrower flag would silently empty those panels the day one is rendered.
            // A partner, a citizen and an applicant never pass.
            case nameof(Hinweis):
                return scope.IsInternalAgent
                    && await db.Hinweise.AnyAsync(h => h.Id == entityId, cancellationToken);
            // Citizen tickets: the desk, or the one agent attached to this row — TicketVisibility owns that rule,
            // this is only its scope-shaped caller. Named rather than left to the tail below, where the final
            // "_ => true" would answer "visible" for a partner and for every uninvolved agent alike.
            case nameof(Ticket):
                return await TicketVisibility.MayReadAsync(db, entityId, scope, cancellationToken);
        }

        // open to every internal agent, but named rather than left to the tail below: a type that falls through
        // there is answered "visible" without anyone having decided that
        return entityType switch
        {
            nameof(Law) => await db.Laws.AnyAsync(g => g.Id == entityId, cancellationToken),
            nameof(Meeting) => await db.Meetings.AnyAsync(m => m.Id == entityId, cancellationToken),
            nameof(EvidenceItem) => await db.EvidenceItems.AnyAsync(i => i.Id == entityId, cancellationToken),
            nameof(EvidenceEntry) => await db.EvidenceEntries.AnyAsync(e => e.Id == entityId, cancellationToken),
            nameof(KassenBuchung) => await db.KassenBuchungen.AnyAsync(b => b.Id == entityId, cancellationToken),
            nameof(AgentAbduction) => await db.AgentAbductions.AnyAsync(a => a.Id == entityId, cancellationToken),
            nameof(AgentActivity) => await db.AgentActivities.AnyAsync(a => a.Id == entityId, cancellationToken),
            nameof(TrainingModule) => await db.TrainingModules.AnyAsync(m => m.Id == entityId, cancellationToken),
            // fully qualified: the entity shares its simple name with the namespace it lives in, and an alias
            // would make nameof() yield the alias instead of the CLR name
            nameof(Data.Entities.Feedback.Feedback) =>
                await db.Feedbacks.AnyAsync(f => f.Id == entityId, cancellationToken),
            // unknown type = visible
            _ => true,
        };
    }

    /// <summary>Informant gate: every internal agent, never a partner — fail-closed on a missing record.</summary>
    private static async Task<bool> InformantVisibleAsync(
        AppDbContext db, string informantId, ViewerScope scope, CancellationToken cancellationToken)
        => InformantVisibility.MaySeeRecord(scope)
        && await db.Informants.AsNoTracking().AnyAsync(i => i.Id == informantId, cancellationToken);

    /// <summary>Request gate: its own requester always, everyone else exactly as far as the target record reaches.</summary>
    /// <remarks>Mirrors the search provider, which resolves a request through its target rather than through a rank.
    /// One hop only — a request never targets another request.</remarks>
    private static async Task<bool> RequestVisibleAsync(
        AppDbContext db, string requestId, ViewerScope scope, CancellationToken cancellationToken)
    {
        var row = await db.Requests.AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => new { r.CreatedById, r.TargetType, r.TargetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (scope.MeId is { Length: > 0 } && row.CreatedById == scope.MeId)
        {
            return true;
        }
        // no record behind it yet
        if (row.TargetType == PartnerShareService.InquiryTarget)
        {
            return scope.IsLeadership && scope.PartnerAgency is null;
        }
        return await IsRecordVisibleAsync(db, row.TargetType, row.TargetId, scope, cancellationToken);
    }

    /// <summary>Agenda item visibility: leadership keeps its immediate access; other internal agents gain it 2h after the meeting.</summary>
    private static async Task<bool> AgendaItemVisibleAsync(
        AppDbContext db, string itemId, ViewerScope scope, CancellationToken cancellationToken)
    {
        // preserve the exact rank-gate behaviour: existence only, independent of the meeting time
        if (scope.MayAgenda)
        {
            return await db.MeetingAgendaItems.AnyAsync(p => p.Id == itemId, cancellationToken);
        }
        var when = await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.Id == itemId)
            .Join(db.Meetings, p => p.MeetingId, m => m.Id, (p, m) => new { m.Start, m.End })
            .FirstOrDefaultAsync(cancellationToken);
        return when is not null && MeetingVisibility.MayReadAgenda(scope, when.Start, when.End, DateTime.UtcNow);
    }

    /// <summary>True if record is visible to caller; leadership-scoped shim around the full scope check.</summary>
    public static Task<bool> IsRecordVisibleAsync(
        AppDbContext db, string entityType, string entityId, bool isLeadership, CancellationToken cancellationToken = default, string? meId = null)
        // leadership (rank 4) implies the agenda rank (3), so the shim may carry it
        => IsRecordVisibleAsync(db, entityType, entityId,
               new ViewerScope(isLeadership, isLeadership, meId, null, MayAgenda: isLeadership), cancellationToken);
}
