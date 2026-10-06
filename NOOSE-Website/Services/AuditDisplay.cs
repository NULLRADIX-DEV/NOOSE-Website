using System.Globalization;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Formats audit entry JSON into readable field changes.</summary>
public static class AuditDisplay
{
    /// <summary>Single field change record.</summary>
    public record FieldChange(string Field, string Alt, string New);

    // Skip meta fields. The interceptor stamps CLR property names; the German spellings are kept
    // because audit rows written before the codebase was anglicised still carry them.
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        // legacy German names in older rows
        "ErstelltAm", "ErstelltVonId", "GeaendertAm", "GeaendertVonId",
        "GeloeschtAm", "GeloeschtVonId", "IstGeloescht",
        "PersonId", "FraktionId", "PersonengruppeId", "AgentId", "BesprechungId",
        "AnwesenheitAbgeschlossenAm", "ErinnerungGesendetAm",
        // CLR names written today
        "CreatedAt", "CreatedById", "ModifiedAt", "ModifiedById",
        "DeletedAt", "DeletedById", "IsDeleted",
        "MeetingId", "AttendanceClosedAt", "ReminderSentAt", "NotifiedAt",
        "PreviousMeetingId", "CarriedFromItemId", "AcknowledgedById", "DoneById", "MarkedById",
        // the follow-up worker's own stamp, exactly like NotifiedAt above: it says a reminder went out, which
        // the bell already did, and every waiting ticket would otherwise write a row into the protocol hourly
        "NudgedAt",
    };

    // Document bodies, layouts and score snapshots are unreadable as a before/after pair and swamp
    // every timeline they appear in; the audit row still records that the field changed.
    private static bool IsPayload(string field, string? entityType)
        => field.EndsWith("Html", StringComparison.Ordinal) || field.EndsWith("Json", StringComparison.Ordinal)
           // a paragraph's text is HTML under a name that does not say so
           || (field == "Text" && entityType == "Law");

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        // legacy German names in older rows
        ["Name"] = "Name", ["Beschreibung"] = "Beschreibung", ["Aktenzeichen"] = "Aktenzeichen",
        ["Lebensstatus"] = "Lebensstatus", ["TotBis"] = "Tot-Fenster", ["Einstufung"] = "Einstufung",
        ["IstVerschlusssache"] = "Verschlusssache",
        ["Grund"] = "Grund", ["Fraktion"] = "Fraktion (Freitext)", ["OrgTyp"] = "Verknüpfte Org (Typ)",
        ["OrgId"] = "Verknüpfte Org", ["ErhalteneInformationen"] = "Erhaltene Informationen",
        ["Wahrheitsserum"] = "Wahrheitsserum", ["Ausgang"] = "Maßnahme-Ausgang",
        ["GedaechtnisGeloescht"] = "Gedächtnisverlust", ["Zeitpunkt"] = "Zeitpunkt",
        ["Art"] = "Art", ["Funk"] = "Funk", ["Darkchat"] = "Darkchat",
        ["Ausstellungszeiten"] = "Aufstellungszeiten", ["Anwesen"] = "Anwesen",
        ["Erkennungsfarbe"] = "Erkennungsfarbe", ["Ziele"] = "Ziele",
        ["Rang"] = "Rang", ["Rolle"] = "Rolle", ["IstLeitung"] = "Leitung",
        ["GeschaetzteMitgliederzahl"] = "Geschätzte Mitgliederzahl", ["Label"] = "Bezeichnung",
        ["Codename"] = "Codename", ["Klarname"] = "Klarname", ["Dienstnummer"] = "Dienstnummer",
        ["VonDatum"] = "Von", ["BisDatum"] = "Bis (einschließlich)", ["Tage"] = "Tage",
        ["Abmeldegrund"] = "Abmeldegrund", ["KenntnisGenommenAm"] = "Kenntnis genommen am",
        ["KenntnisGenommenVonName"] = "Kenntnis genommen von",
        ["Titel"] = "Titel", ["Beginn"] = "Beginn", ["Ende"] = "Ende", ["Ort"] = "Ort",
        ["Sortierung"] = "Reihenfolge", ["Erledigt"] = "Erledigt", ["Herkunft"] = "Herkunft",
        ["AgentCodename"] = "Agent", ["ErfasstAm"] = "Erfasst am",

        // CLR names written today; without these every field renders as its raw English identifier
        ["CaseNumber"] = "Aktenzeichen", ["Description"] = "Beschreibung", ["Title"] = "Titel",
        ["LifeStatus"] = "Lebensstatus", ["DeadUntil"] = "Tot-Fenster",
        ["Classification"] = "Einstufung", ["IsClassified"] = "Verschlusssache",
        ["IsTRUClassified"] = "VS-Stufe TRU", ["IsHRBClassified"] = "VS-Stufe HRB",
        ["IsWanted"] = "Zur Fahndung", ["WantedReason"] = "Fahndungsgrund",
        ["AgingDisabled"] = "Aktualitäts-Ausnahme",
        ["ThreatScore"] = "Bedrohungs-Score", ["ThreatConfidence"] = "Score-Konfidenz",
        ["ScoreCalculatedAt"] = "Score berechnet am",
        ["Category"] = "Kategorie", ["Pinned"] = "Angepinnt", ["Type"] = "Art", ["Kind"] = "Art",
        ["Status"] = "Status", ["Summary"] = "Zusammenfassung", ["ClosingNote"] = "Abschlussvermerk",
        ["ClosingReason"] = "Abschlussgrund", ["PriorityOverride"] = "Priorität (von Hand)",
        // the id itself stays: a ticket, a tip and an informant all name their handler this way, and
        // hiding it would leave a re-assignment as a change row with no field in it
        ["HandlerId"] = "Bearbeiter",
        ["CompletedAt"] = "Abgeschlossen am", ["Priority"] = "Priorität",
        ["DueDate"] = "Fällig am", ["DueAt"] = "Fällig am", ["DoneAt"] = "Erledigt am",
        ["Done"] = "Erledigt", ["IsRestricted"] = "Eingeschränkt",
        ["Radio"] = "Funk", ["IssuingTimes"] = "Aufstellungszeiten", ["Estate"] = "Anwesen",
        ["RecognitionColor"] = "Erkennungsfarbe", ["Targets"] = "Ziele",
        ["IsStateFaction"] = "Staatsfraktion", ["IsBadFaction"] = "Badfrak", ["EstimatedMemberCount"] = "Geschätzte Mitgliederzahl",
        ["Location"] = "Ort", ["Start"] = "Beginn", ["End"] = "Ende", ["Expiry"] = "Ablauf",
        ["Result"] = "Ergebnis", ["Remarks"] = "Bemerkungen",
        ["Rank"] = "Rang", ["Role"] = "Rolle", ["IsLead"] = "Leitung",
        ["Text"] = "Text", ["Note"] = "Notiz", ["Reason"] = "Grund", ["Url"] = "Link",
        ["Frequency"] = "Frequenz", ["Scope"] = "Bereich", ["Agency"] = "Partnerbehörde",
        ["TaskforceId"] = "Taskforce",
        ["Outcome"] = "Maßnahme-Ausgang", ["TruthSerum"] = "Wahrheitsserum",
        ["MemoryDeleted"] = "Gedächtnisverlust", ["ReceivedInformation"] = "Erhaltene Informationen",
        ["Timestamp"] = "Zeitpunkt", ["OrgType"] = "Verknüpfte Org (Typ)",
        ["FromDate"] = "Von", ["ToDate"] = "Bis (einschließlich)", ["Days"] = "Tage",
        ["AcknowledgedAt"] = "Kenntnis genommen am", ["AcknowledgedByName"] = "Kenntnis genommen von",
        ["Sighting"] = "Wahrnehmung", ["IsInternalOnly"] = "Nur intern",
        ["IsInvestigationLead"] = "Ermittlungsleitung",
        ["PageRoute"] = "Seite", ["PageTab"] = "Seiten-Tab",
        ["Response"] = "Antwort", ["DeciderName"] = "Entschieden von", ["DecidedAt"] = "Entschieden am",
        ["LawBook"] = "Gesetzbuch", ["Paragraph"] = "Paragraf", ["Sentence"] = "Strafmaß",
        ["Section"] = "Abschnitt", ["SortOrder"] = "Reihenfolge", ["Abbreviation"] = "Kürzel",
        ["IsPublic"] = "Öffentlich",
    };

    // whole-day values carry no instant, so they must never be shifted into a time zone
    private static readonly HashSet<string> DayOnlyFields = new(StringComparer.Ordinal)
    {
        "VonDatum", "BisDatum", "FromDate", "ToDate",
    };

    private static readonly HashSet<string> InstantFields = new(StringComparer.Ordinal)
    {
        "Zeitpunkt", "TotBis", "Beginn", "Ende",
        "Timestamp", "DeadUntil", "Start", "End", "Expiry",
        "DueDate", "DueAt", "DoneAt", "CompletedAt", "ScoreCalculatedAt", "AcknowledgedAt", "DecidedAt",
    };

    /// <summary>Parses JSON into field changes; empty on null/invalid.
    /// <paramref name="maxValueLength"/> above zero clips long values — for feeds, not for the audit log itself.
    /// <paramref name="entityType"/> disambiguates property names that several entities share.</summary>
    public static IReadOnlyList<FieldChange> Parse(string? json, int maxValueLength = 0, string? entityType = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<FieldChange>();
        }

        Dictionary<string, JsonElement[]>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement[]>>(json);
        }
        catch (JsonException)
        {
            return Array.Empty<FieldChange>();
        }
        if (raw is null)
        {
            return Array.Empty<FieldChange>();
        }

        var list = new List<FieldChange>();
        foreach (var (field, values) in raw)
        {
            if (Hidden.Contains(field) || IsPayload(field, entityType))
            {
                continue;
            }
            var alt = Clip(values.Length > 0 ? Format(field, values[0], entityType) : "—", maxValueLength);
            var @new = Clip(values.Length > 1 ? Format(field, values[1], entityType) : "—", maxValueLength);
            list.Add(new FieldChange(Labels.GetValueOrDefault(field, field), alt, @new));
        }
        return list;
    }

    private static string Clip(string value, int max)
        => max <= 0 || value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

    private static string Format(string field, JsonElement value, string? entityType)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return "—";
            case JsonValueKind.True:
                return "Ja";
            case JsonValueKind.False:
                return "Nein";
            case JsonValueKind.Number when value.TryGetInt32(out var n):
                return FormatEnum(field, n, entityType);
            case JsonValueKind.String:
                var s = value.GetString();
                if (string.IsNullOrEmpty(s))
                {
                    return "—";
                }
                if (DayOnlyFields.Contains(field)
                    && DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    return day.ToString("dd.MM.yyyy");
                }
                if (InstantFields.Contains(field)
                    && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                }
                return s;
            default:
                return value.ToString();
        }
    }

    // enum to string; both the legacy German field names and the CLR names written today
    private static string FormatEnum(string field, int n, string? entityType) => field switch
    {
        "Einstufung" or "Classification" => ClassificationDisplay.Name((Classification)n),
        "Lebensstatus" or "LifeStatus" => LifeStatusDisplay.Name((LifeStatus)n),
        "Ausgang" or "Outcome" => MeasureOutcomeDisplay.Name((MeasureOutcome)n),
        "Abmeldegrund" => AbsenceCategoryDisplay.Name((AbsenceCategory)n),
        "Category" => CategoryName(n, entityType),
        "Herkunft" or "Origin" => MeetingAbsenceOriginDisplay.Name((MeetingAbsenceOrigin)n),
        // a ticket status and its closing reason would otherwise stand in the protocol as bare numbers
        // Scope is a RadioChannel block here and a Taskforce Geltungsbereich elsewhere, so it must be qualified
        "Bereich" or "Scope" when entityType == nameof(RadioChannel) => RadioScopeDisplay.Name((RadioScope)n),
        "Partnerbehoerde" or "Agency" => PartnerAgencyDisplay.Name((PartnerAgency)n),
        "Status" when entityType == nameof(Ticket) => TicketStatusDisplay.Name((TicketStatus)n),
        "ClosingReason" when entityType == nameof(Ticket)
            => TicketAbschlussgrundDisplay.Name((TicketAbschlussgrund)n),
        _ => n.ToString(),
    };

    // three entities carry a Category, all three enums run 0..4, and the audit row holds only the property name:
    // unqualified, an appointment moved to Einsatz reads "Krank". Absence stays the answer without a type, because
    // the per-record timelines pass none and no ticket or appointment reaches them
    private static string CategoryName(int n, string? entityType) => entityType switch
    {
        nameof(Ticket) => TicketKategorieDisplay.Name((TicketKategorie)n),
        nameof(Appointment) => AppointmentCategoryDisplay.Name((AppointmentCategory)n),
        _ => AbsenceCategoryDisplay.Name((AbsenceCategory)n),
    };
}
