using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;

namespace NOOSE_Website.Services;

/// <summary>Maps audit events to timeline categories/titles. Shared by per-record timeline + global chronicle.</summary>
public static class TimelineDisplay
{
    /// <summary>Category + title for an audit row of the given entity type/action.</summary>
    public static (TimelineCategory Kat, string Title) MapAudit(string entityType, AuditAction action)
    {
        // the field names in the row say which change the reason belongs to
        if (action == AuditAction.Justified)
        {
            return (TimelineCategory.Change, "Begründung");
        }

        string Verb(string created, string deleted) => action switch
        {
            AuditAction.Created => created,
            AuditAction.Modified => "geändert",
            AuditAction.Deleted => deleted,
            AuditAction.Restored => "wiederhergestellt",
            AuditAction.Archived => "archiviert",
            AuditAction.Unarchived => "aus dem Archiv geholt",
            _ => action.ToString(),
        };

        if (entityType == nameof(PersonDoc))
        {
            return (TimelineCategory.Doc, $"Dok {Verb("angelegt", "gelöscht")}");
        }
        if (entityType is nameof(FactionMember) or nameof(PersonGroupMember) or nameof(PartyMember))
        {
            return (TimelineCategory.Membership, $"Mitglied {Verb("aufgenommen", "entfernt")}");
        }
        if (entityType is nameof(FactionAgent) or nameof(PersonGroupAgent) or nameof(PartyAgent)
            or nameof(OperationAgent) or nameof(CaseAgent) or nameof(TaskforceAgent) or nameof(JobAssignment)
            or nameof(AppointmentAssignment))
        {
            return (TimelineCategory.Allocation, $"Agent {Verb("zugeteilt", "entfernt")}");
        }
        if (entityType is nameof(PersonPhoto) or nameof(FactionPhoto) or nameof(PersonGroupPhoto) or nameof(PartyPhoto))
        {
            return (TimelineCategory.Photo, $"Foto {Verb("hinzugefügt", "entfernt")}");
        }
        if (entityType == nameof(CustomFieldValue))
        {
            return (TimelineCategory.Change, $"Sonderfeld {Verb("gesetzt", "entfernt")}");
        }
        if (entityType == nameof(MeetingAgendaItem))
        {
            return (TimelineCategory.Agenda, $"Tagesordnungspunkt {Verb("angelegt", "gelöscht")}");
        }
        if (entityType == nameof(MeetingAttendance))
        {
            return (TimelineCategory.Attendance, $"Anwesenheit {Verb("erfasst", "entfernt")}");
        }
        if (entityType == nameof(MeetingSignOff))
        {
            return (TimelineCategory.SignOff, $"Abmeldung {Verb("eingetragen", "entfernt")}");
        }
        if (entityType == nameof(Comment))
        {
            return (TimelineCategory.Comment, $"Kommentar {Verb("geschrieben", "gelöscht")}");
        }
        if (entityType == nameof(Source))
        {
            return (TimelineCategory.Source, $"Quelle {Verb("hinzugefügt", "entfernt")}");
        }
        if (entityType == nameof(Followup))
        {
            return (TimelineCategory.Followup, $"Wiedervorlage {Verb("angelegt", "gelöscht")}");
        }
        if (entityType == nameof(PersonRelation))
        {
            return (TimelineCategory.Relation, $"Beziehung {Verb("angelegt", "entfernt")}");
        }
        if (entityType == nameof(Link))
        {
            return (TimelineCategory.Link, $"Verknüpfung {Verb("angelegt", "entfernt")}");
        }
        if (entityType == nameof(Observation))
        {
            return (TimelineCategory.Observation, $"Observation {Verb("erfasst", "gelöscht")}");
        }
        if (entityType == nameof(AbductionCompromise))
        {
            return (TimelineCategory.Link, $"Kompromittierung {Verb("markiert", "aufgehoben")}");
        }
        if (entityType == nameof(AgentAbduction))
        {
            return (TimelineCategory.Change, $"Entführung {Verb("dokumentiert", "gelöscht")}");
        }
        if (entityType == nameof(EvidenceItem))
        {
            return (TimelineCategory.Change, $"Asservat-Item {Verb("angelegt", "gelöscht")}");
        }
        if (entityType == nameof(EvidenceEntry))
        {
            return (TimelineCategory.Change, $"Asservat-Eintrag {Verb("gebucht", "gelöscht")}");
        }
        if (entityType == nameof(KassenBuchung))
        {
            return (TimelineCategory.Change, $"Kassenbuchung {Verb("gebucht", "storniert")}");
        }
        if (entityType == nameof(FinancingRequest))
        {
            return (TimelineCategory.Change, $"Finanzierungsantrag {Verb("gestellt", "gelöscht")}");
        }
        if (entityType == nameof(FinancingRequestLine))
        {
            return (TimelineCategory.Change, $"Antragsposition {Verb("aufgenommen", "entfernt")}");
        }
        // publish and retract both arrive here; the interceptor's Status old/new pair carries which one it was
        if (entityType == nameof(OeffentlicheFahndung))
        {
            return (TimelineCategory.Change, $"Öffentliche Ausschreibung {Verb("angelegt", "gelöscht")}");
        }
        // publish and retract both arrive here as well; the Status old/new pair carries which one it was
        if (entityType == nameof(OeffentlichesFraktionsprofil))
        {
            return (TimelineCategory.Change, $"Öffentliches Organisationsprofil {Verb("angelegt", "gelöscht")}");
        }
        // pledge, approval, deposit and withdrawal all arrive here; the Status old/new pair says which
        if (entityType == nameof(FahndungKopfgeldAnteil))
        {
            return (TimelineCategory.Change, $"Kopfgeld-Anteil {Verb("gestiftet", "entfernt")}");
        }
        // the actor is stripped by TipAnonymity before this ever renders; the title must stand on its own
        if (entityType == nameof(Hinweis))
        {
            return (TimelineCategory.Change, $"Bürgerhinweis {Verb("eingegangen", "gelöscht")}");
        }
        // no citizen and no argument in the title: an objection disputes what was published, and the file's
        // timeline records that it happened, not who said what
        if (entityType == nameof(FahndungEinspruch))
        {
            return (TimelineCategory.Change, $"Einspruch gegen die Ausschreibung {Verb("eingegangen", "gelöscht")}");
        }
        // no recipient in the title: the reward names a citizen, the file's timeline does not
        if (entityType == nameof(HinweisBelohnung))
        {
            return (TimelineCategory.Change, "Belohnung ausgezahlt");
        }

        var kat = action switch
        {
            AuditAction.Created => TimelineCategory.Asset,
            AuditAction.Deleted => TimelineCategory.Deletion,
            AuditAction.Restored => TimelineCategory.Restoration,
            _ => TimelineCategory.Change,
        };
        return (kat, $"Akte {Verb("angelegt", "gelöscht")}");
    }

    /// <summary>Trim free text to a short timeline detail.</summary>
    public static string? Truncate(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim();
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
    }
}
