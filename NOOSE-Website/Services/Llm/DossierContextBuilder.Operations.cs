using Microsoft.EntityFrameworkCore;
using System.Text;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>Dossiers for what the agency runs on: duty, personnel, ledger and recruiting records.</summary>
/// <remarks>Same contract as the record kinds in the main file — German plain text, every free text through
/// <c>Free</c>, every linked record masked at its own secrecy level. What is NOT here is the visibility decision:
/// <see cref="Visibility.IsRecordVisibleAsync" /> has already said yes before a builder runs, and a builder that
/// re-decides it would be a second copy of a rule that lives elsewhere.</remarks>
public static partial class DossierContextBuilder
{
    /// <summary>Dispatches the operational record kinds; null for a type no builder covers.</summary>
    private static async Task<DossierContext?> BuildOperationsAsync(
        AppDbContext db, string entityType, string entityId, ViewerScope? scope, CancellationToken ct)
        => entityType switch
        {
            nameof(Job) => await BuildJobAsync(db, entityId, scope, ct),
            nameof(Appointment) => await BuildAppointmentAsync(db, entityId, scope, ct),
            nameof(Meeting) => await BuildMeetingAsync(db, entityId, scope, ct),
            nameof(Agent) => await BuildAgentAsync(db, entityId, scope, ct),
            nameof(Bewerbung) => await BuildBewerbungAsync(db, entityId, scope, ct),
            nameof(Informant) => await BuildInformantAsync(db, entityId, scope, ct),
            nameof(EvidenceItem) => await BuildEvidenceItemAsync(db, entityId, scope, ct),
            nameof(EvidenceEntry) => await BuildEvidenceEntryAsync(db, entityId, scope, ct),
            nameof(KassenBuchung) => await BuildKassenBuchungAsync(db, entityId, scope, ct),
            nameof(FinancingRequest) => await BuildFinancingRequestAsync(db, entityId, scope, ct),
            nameof(AgentAbduction) => await BuildAbductionAsync(db, entityId, scope, ct),
            nameof(Announcement) => await BuildAnnouncementAsync(db, entityId, scope, ct),
            nameof(Presentation) => await BuildPresentationAsync(db, entityId, scope, ct),
            nameof(Absence) => await BuildAbsenceAsync(db, entityId, scope, ct),
            nameof(SituationReport) => await BuildSituationReportAsync(db, entityId, ct),
            nameof(LibraryFile) => await BuildLibraryFileAsync(db, entityId, ct),
            nameof(AgentActivity) => await BuildActivityAsync(db, entityId, scope, ct),
            nameof(Request) => await BuildRequestAsync(db, entityId, scope, ct),
            nameof(TrainingModule) => await BuildTrainingModuleAsync(db, entityId, ct),
            nameof(CounterIntelRule) => await BuildCounterIntelRuleAsync(db, entityId, ct),
            nameof(Data.Entities.Feedback.Feedback) => await BuildFeedbackAsync(db, entityId, ct),
            nameof(Hinweis) => await BuildTipAsync(db, entityId, scope, ct),
            _ => null,
        };

    // ---- duty ----

    static async Task<DossierContext?> BuildJobAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var a = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Aufgabe");
        Line(sb, "Aktenzeichen", a.CaseNumber);
        Line(sb, "Titel", a.Title);
        Line(sb, "Status", JobStatusDisplay.Name(a.Status));
        Line(sb, "Priorität", JobPriorityDisplay.Name(a.Priority));
        Line(sb, "Fällig", Fmt(a.DueDate));
        Line(sb, "Erledigt am", Fmt(a.DoneAt));
        Line(sb, "Eingeschränkt", a.IsRestricted);
        Line(sb, "Beschreibung", a.Description);

        await AppendAssignedAgentsAsync(sb, db,
            db.JobAssignments.AsNoTracking().Where(z => z.JobId == id).Select(z => z.AgentId), ct);
        await AppendAttachmentsAsync(sb, db, nameof(Job), id, includeClassificationHistory: false, view, ct);

        return new DossierContext(a.Title, sb.ToString(), a.IsRestricted);
    }

    static async Task<DossierContext?> BuildAppointmentAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var t = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Termin");
        Line(sb, "Aktenzeichen", t.CaseNumber);
        Line(sb, "Titel", t.Title);
        Line(sb, "Art", AppointmentCategoryDisplay.Name(t.Category));
        Line(sb, "Status", AppointmentStatusDisplay.Name(t.Status));
        Line(sb, "Beginn", Fmt(t.Start));
        Line(sb, "Ende", Fmt(t.End));
        Line(sb, "Ganztägig", t.AllDay);
        Line(sb, "Ort", t.Location);
        Line(sb, "Sichtbarkeit", AppointmentVisibilityLevelDisplay.Name(t.Visibility));
        Line(sb, "Beschreibung", t.Description);

        await AppendAssignedAgentsAsync(sb, db,
            db.AppointmentAssignments.AsNoTracking().Where(z => z.AppointmentId == id).Select(z => z.AgentId), ct);
        await AppendAttachmentsAsync(sb, db, nameof(Appointment), id, includeClassificationHistory: false, view, ct);

        return new DossierContext(t.Title, sb.ToString(), t.Visibility != AppointmentVisibilityLevel.Public);
    }

    static async Task<DossierContext?> BuildMeetingAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var m = await db.Meetings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Besprechung");
        Line(sb, "Aktenzeichen", m.CaseNumber);
        Line(sb, "Titel", m.Title);
        Line(sb, "Status", MeetingStatusDisplay.Name(m.Status));
        Line(sb, "Beginn", Fmt(m.Start));
        Line(sb, "Ende", Fmt(m.End));
        Line(sb, "Ort", m.Location);

        // agenda and minutes open to rank/supervision at once, to any other internal agent 2h after the meeting
        if (MeetingVisibility.MayReadAgenda(view, m.Start, m.End, DateTime.UtcNow))
        {
            Line(sb, "Protokoll", StripHtml(m.MinutesHtml));

            var (agenda, agendaTotal) = await TakeAsync(
                db.MeetingAgendaItems.AsNoTracking().Where(p => p.MeetingId == id)
                    .OrderBy(p => p.Sorting)
                    .Select(p => new { p.Title, p.Done, p.NotesHtml }), ct);
            if (agendaTotal > 0)
            {
                sb.AppendLine($"— Tagesordnung ({agendaTotal}) —");
                foreach (var p in agenda)
                {
                    sb.Append("• ").Append(p.Done ? "[erledigt] " : "[offen] ").Append(Free(p.Title));
                    if (StripHtml(p.NotesHtml) is { Length: > 0 } note)
                    {
                        sb.Append(" — ").Append(Free(note));
                    }
                    sb.AppendLine();
                }
            }
        }
        else
        {
            sb.AppendLine("Tagesordnung und Protokoll sind für dich noch nicht freigegeben.");
        }

        var (attendance, attendanceTotal) = await TakeAsync(
            db.MeetingAttendances.AsNoTracking().Where(x => x.MeetingId == id)
                .Select(x => new { x.AgentCodename, x.Status }), ct);
        if (attendanceTotal > 0)
        {
            sb.AppendLine($"— Anwesenheit ({attendanceTotal}) —");
            foreach (var x in attendance)
            {
                sb.Append("• ").Append(string.IsNullOrWhiteSpace(x.AgentCodename) ? "(unbenannter Agent)" : x.AgentCodename)
                    .Append(": ").AppendLine(MeetingAttendanceStatusDisplay.Name(x.Status));
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Meeting), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(m.Title, sb.ToString(), IsClassified: false);
    }

    /// <summary>Citizen tip. Carries no citizen field at all, and that is a cache rule as much as a promise.</summary>
    /// <remarks>
    /// The brief is cached once per record at minimum privilege and read by everyone who may see the tip, so an
    /// identity in it would outlive the audited leadership act that resolved the anonymity. Not even the flag goes
    /// in: whether a tipster asked to stay anonymous is a fact about the person, not about the tip. Classified so
    /// the cached row is never handed to a viewer the gate has not passed.
    /// </remarks>
    static async Task<DossierContext?> BuildTipAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var h = await db.Hinweise.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.CaseNumber, x.Status, x.Text, x.Priority, x.CreatedAt,
                HasAttachment = x.AttachmentFileName != null,
                HandlerCodename = x.Handler!.Codename,
                WantedCaseNumber = x.Wanted!.CaseNumber,
                WantedDisplayName = x.Wanted!.DisplayName,
            })
            .FirstOrDefaultAsync(ct);
        if (h is null)
        {
            return null;
        }

        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var title = "Bürgerhinweis " + h.CaseNumber;
        var sb = new StringBuilder();
        sb.AppendLine("Bürgerhinweis");
        Line(sb, "Aktenzeichen", h.CaseNumber);
        Line(sb, "Status", TipStatusDisplay.Name(h.Status));
        Line(sb, "Priorität", h.Priority.ToString());
        Line(sb, "Eingegangen", Fmt(h.CreatedAt));
        Line(sb, "Bearbeiter", h.HandlerCodename);
        Line(sb, "Bildanhang", h.HasAttachment);
        if (h.WantedCaseNumber is { Length: > 0 } notice)
        {
            Line(sb, "Bezug", notice + " · " + h.WantedDisplayName);
        }
        Line(sb, "Meldung", h.Text);

        await AppendAttachmentsAsync(sb, db, nameof(Hinweis), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(title, sb.ToString(), IsClassified: true);
    }

    // ---- personnel ----

    static async Task<DossierContext?> BuildAgentAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var a = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.Leadership);
        var title = AgentNameDisplay.Pick(a.Codename, a.RealName, view.MayRealName);

        var sb = new StringBuilder();
        sb.AppendLine("Personalakte");
        Line(sb, "Codename", a.Codename);
        // the real name is leadership-only and never reaches the read-only supervision; MayRealName is the one
        // flag that expresses that subtraction, so this must not fall back to a broader one
        if (view.MayRealName)
        {
            Line(sb, "Klarname", a.RealName);
        }
        Line(sb, "Dienstnummer", a.BadgeNumber);
        Line(sb, "Dienstgrad", a.Rank is { } rank ? RankDisplay.Name(rank) : null);
        Line(sb, "Status", AgentStatusDisplay.Name(a.Status));
        Line(sb, "TRU", a.IsTRU);
        Line(sb, "HRB", a.IsHRB);
        Line(sb, "Partnerbehörde", a.PartnerAgency is { } agency ? PartnerAgencyDisplay.Name(agency) : null);
        Line(sb, "Registriert", Fmt(a.RegisteredAt));
        Line(sb, "Freigegeben", Fmt(a.ReleasedAt));
        Line(sb, "Gekündigt", Fmt(a.TerminatedAt));
        Line(sb, "Kündigungsgrund", a.TerminationReason);

        var (ranks, ranksTotal) = await TakeAsync(
            db.AgentRankHistories.AsNoTracking().Where(h => h.AgentId == id)
                .OrderByDescending(h => h.Timestamp)
                .Select(h => new { h.Timestamp, h.Alt, h.New, h.ActorName, h.Reason }), ct);
        if (ranksTotal > 0)
        {
            sb.AppendLine($"— Dienstgrad-Verlauf ({ranksTotal}) —");
            foreach (var h in ranks)
            {
                sb.Append("• ").Append(Fmt(h.Timestamp)).Append(": ")
                    .Append(h.Alt is { } old ? RankDisplay.Name(old) : "—")
                    .Append(" → ").Append(RankDisplay.Name(h.New));
                if (Free(h.Reason) is { Length: > 0 } why)
                {
                    sb.Append(" — ").Append(why);
                }
                sb.AppendLine();
            }
        }

        var (notes, notesTotal) = await TakeAsync(
            db.AgentNotes.AsNoTracking().Where(n => n.AgentId == id)
                .OrderByDescending(n => n.EntryDate)
                .Select(n => new { n.EntryDate, n.Kind, n.ArtFreetext, n.Text, n.AuthorName }), ct);
        if (notesTotal > 0)
        {
            sb.AppendLine($"— Vermerke ({notesTotal}) —");
            foreach (var n in notes)
            {
                var kind = string.IsNullOrWhiteSpace(n.ArtFreetext) ? AgentNoteKindDisplay.Name(n.Kind) : n.ArtFreetext;
                sb.Append("• ").Append(Fmt(n.EntryDate)).Append(" [").Append(Free(kind)).Append("] ")
                    .AppendLine(Free(n.Text));
            }
        }

        var (promotions, promotionsTotal) = await TakeAsync(
            db.AgentPromotionRequests.AsNoTracking().Where(p => p.AgentId == id)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new { p.CreatedAt, p.TargetRank, p.Status, p.Justification }), ct);
        if (promotionsTotal > 0)
        {
            sb.AppendLine($"— Beförderungsanträge ({promotionsTotal}) —");
            foreach (var p in promotions)
            {
                sb.Append("• ").Append(Fmt(p.CreatedAt)).Append(": ").Append(RankDisplay.Name(p.TargetRank))
                    .Append(" — ").Append(PromotionStatusDisplay.Name(p.Status));
                if (Free(p.Justification) is { Length: > 0 } why)
                {
                    sb.Append(" — ").Append(why);
                }
                sb.AppendLine();
            }
        }

        var (modules, modulesTotal) = await TakeAsync(
            db.AgentModuleCompletions.AsNoTracking().Where(c => c.AgentId == id)
                .Join(db.TrainingModules, c => c.ModuleId, m => m.Id, (c, m) => new { m.Name, c.CompletedAt, c.Note }), ct);
        if (modulesTotal > 0)
        {
            sb.AppendLine($"— Ausbildungsmodule ({modulesTotal}) —");
            foreach (var m in modules)
            {
                sb.Append("• ").Append(Free(m.Name)).Append(" — ").Append(Fmt(m.CompletedAt));
                if (Free(m.Note) is { Length: > 0 } note)
                {
                    sb.Append(" (").Append(note).Append(')');
                }
                sb.AppendLine();
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Agent), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(title, sb.ToString(), IsClassified: true);
    }

    static async Task<DossierContext?> BuildAbsenceAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var a = await db.Absences.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var codenames = await CodenamesAsync(db, [a.AgentId, a.AcknowledgedById], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Abmeldung");
        Line(sb, "Agent", Codename(codenames, a.AgentId));
        Line(sb, "Von", a.FromDate.ToString("dd.MM.yyyy"));
        Line(sb, "Bis", a.ToDate.ToString("dd.MM.yyyy"));
        Line(sb, "Tage", a.Days.ToString());
        Line(sb, "Art", AbsenceCategoryDisplay.Name(a.Category));
        // the roster tier is "who is away", not "why": the reason and the acknowledgement are owner-only fields
        var granted = view.MayClassifiedRead ? AbsenceViewScope.All : AbsenceViewScope.Team;
        if (AbsenceVisibility.MayReadPrivateFields(granted, a.AgentId == view.MeId))
        {
            Line(sb, "Grund", a.Reason);
            Line(sb, "Bestätigt am", Fmt(a.AcknowledgedAt));
            Line(sb, "Bestätigt von", a.AcknowledgedById is null ? null : Codename(codenames, a.AcknowledgedById));
        }

        var title = $"Abmeldung {Codename(codenames, a.AgentId)} {a.FromDate:dd.MM.yyyy}";
        return new DossierContext(title, sb.ToString(), IsClassified: false);
    }

    // ---- recruiting ----

    static async Task<DossierContext?> BuildBewerbungAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var b = await db.Bewerbungen.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.Hrb);

        var sb = new StringBuilder();
        sb.AppendLine("Bewerbung");
        Line(sb, "Aktenzeichen", b.CaseNumber);
        Line(sb, "Name", b.Name);
        Line(sb, "Titel", b.AcademicDegree);
        Line(sb, "Geburtsdatum", Fmt(b.BirthDate));
        Line(sb, "Arbeitgeber", b.Employer);
        Line(sb, "Vorerfahrung", b.PriorExperience);
        Line(sb, "Status", BewerbungStatusDisplay.Name(b.Status));
        Line(sb, "Eingereicht", Fmt(b.SubmittedAt));
        Line(sb, "Zuständig", b.AssignedAgentName);
        Line(sb, "Sicherheitsprüfung", b.SecurityCheckPassed switch
        {
            true => "Bestanden",
            false => "Nicht bestanden",
            null => "Offen",
        });
        Line(sb, "Entschieden am", Fmt(b.DecidedAt));
        Line(sb, "Entschieden von", b.DecidedByName);
        Line(sb, "Entscheidungsvermerk", b.DecisionNote);
        Line(sb, "Anschreiben", StripHtml(b.CoverLetter));
        Line(sb, "Anlage", b.AttachmentOriginalName);

        if (b.LinkedPersonId is { Length: > 0 } personId)
        {
            var person = await db.People.AsNoTracking().Where(p => p.Id == personId)
                .Select(p => new { p.Name, p.CaseNumber, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified })
                .FirstOrDefaultAsync(ct);
            if (person is not null)
            {
                Line(sb, "Verknüpfte Personenakte",
                    view.CanSee(DossierScope.LevelOf(person.IsClassified, person.IsTRUClassified, person.IsHRBClassified))
                        ? $"{person.Name} ({person.CaseNumber})"
                        : "(Verschlusssache)");
            }
        }

        var (tests, testsTotal) = await TakeAsync(
            db.BewerbungTestAssignments.AsNoTracking().Where(z => z.BewerbungId == id)
                .Join(db.BewerbungTests, z => z.TestId, t => t.Id, (z, t) => new { t.Title, z.CompletedAt, z.AssignedByName }), ct);
        if (testsTotal > 0)
        {
            sb.AppendLine($"— Eignungstests ({testsTotal}) —");
            foreach (var t in tests)
            {
                sb.Append("• ").Append(Free(t.Title))
                    .Append(t.CompletedAt is null ? " — offen" : $" — abgeschlossen {Fmt(t.CompletedAt)}");
                if (Free(t.AssignedByName) is { Length: > 0 } by)
                {
                    sb.Append(" (zugeteilt von ").Append(by).Append(')');
                }
                sb.AppendLine();
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Bewerbung), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(b.Name, sb.ToString(), IsClassified: true);
    }

    // ---- intelligence ----

    static async Task<DossierContext?> BuildInformantAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var i = await db.Informants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.Leadership);
        var codenames = await CodenamesAsync(db, [i.HandlerId], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Informantenakte");
        Line(sb, "Aktenzeichen", i.CaseNumber);
        // record access implies full detail here: there is no second tier, the gate already decided everything
        Line(sb, "Klarname", i.RealName);
        Line(sb, "Kontakt", i.ContactInfo);
        Line(sb, "Zuverlässigkeit", InformantEnumDisplay.Reliability(i.Reliability));
        Line(sb, "Status", InformantEnumDisplay.Status(i.Status));
        Line(sb, "Führender Agent", Codename(codenames, i.HandlerId));
        Line(sb, "Beschreibung", i.Description);
        Line(sb, "Notizen", i.Notes);

        if (i.PersonId is { Length: > 0 } personId)
        {
            var person = await db.People.AsNoTracking().Where(p => p.Id == personId)
                .Select(p => new { p.Name, p.CaseNumber, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified })
                .FirstOrDefaultAsync(ct);
            if (person is not null)
            {
                Line(sb, "Personenakte",
                    view.CanSee(DossierScope.LevelOf(person.IsClassified, person.IsTRUClassified, person.IsHRBClassified))
                        ? $"{person.Name} ({person.CaseNumber})"
                        : "(Verschlusssache)");
            }
        }
        if (i.FactionId is { Length: > 0 } factionId)
        {
            var faction = await db.Factions.AsNoTracking().Where(f => f.Id == factionId)
                .Select(f => new { f.Name, f.CaseNumber, f.IsClassified, f.IsTRUClassified, f.IsHRBClassified })
                .FirstOrDefaultAsync(ct);
            if (faction is not null)
            {
                Line(sb, "Fraktion",
                    view.CanSee(DossierScope.LevelOf(faction.IsClassified, faction.IsTRUClassified, faction.IsHRBClassified))
                        ? $"{faction.Name} ({faction.CaseNumber})"
                        : "(Verschlusssache)");
            }
        }

        var (meetings, meetingsTotal) = await TakeAsync(
            db.InformantMeetings.AsNoTracking().Where(m => m.InformantId == id)
                .OrderByDescending(m => m.MeetingDate)
                .Select(m => new { m.MeetingDate, m.Location, m.Content }), ct);
        if (meetingsTotal > 0)
        {
            sb.AppendLine($"— Treffen ({meetingsTotal}) —");
            foreach (var m in meetings)
            {
                sb.Append("• ").Append(Fmt(m.MeetingDate));
                if (Free(m.Location) is { Length: > 0 } place)
                {
                    sb.Append(" [").Append(place).Append(']');
                }
                sb.Append(": ").AppendLine(Free(m.Content));
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Informant), id, includeClassificationHistory: false, view, ct);
        var title = string.IsNullOrWhiteSpace(i.RealName) ? i.CaseNumber : i.RealName!;
        return new DossierContext(title, sb.ToString(), IsClassified: true);
    }

    static async Task<DossierContext?> BuildAbductionAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var e = await db.AgentAbductions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var codenames = await CodenamesAsync(db, [e.VictimAgentId], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Entführung");
        Line(sb, "Aktenzeichen", e.CaseNumber);
        Line(sb, "Betroffener Agent", Codename(codenames, e.VictimAgentId));
        Line(sb, "Zeitpunkt", Fmt(e.Timestamp));
        Line(sb, "Freigelassen", Fmt(e.ReleasedAt));
        Line(sb, "Ort", e.Location);
        Line(sb, "Ausgang", AbductionOutcomeDisplay.Name(e.Outcome));
        Line(sb, "Wahrheitsserum", e.TruthSerum);
        Line(sb, "Informationsabfluss", e.InformationLeaked);
        if (e.InformationLeaked)
        {
            Line(sb, "Schwere", LeakSeverityDisplay.Name(e.LeakSeverity));
            Line(sb, "Betroffene Bereiche",
                string.Join(", ", LeakCategoryDisplay.Flags(e.LeakCategories).Select(LeakCategoryDisplay.Name)));
        }
        Line(sb, "Notizen", e.Notes);

        var perpetrator = await RecordsReference.ResolveAsync(db, [(e.PerpetratorType, e.PerpetratorId)], ct,
            mayAllTaskforces: view.MayAllTaskforces, meId: view.MeId);
        if (perpetrator.TryGetValue((e.PerpetratorType, e.PerpetratorId), out var who))
        {
            Line(sb, "Urheber", who.Classified && !view.MayClassifiedRead ? "(Verschlusssache)" : who.Display);
        }

        var (compromises, compromisesTotal) = await TakeAsync(
            db.AbductionCompromises.AsNoTracking().Where(c => c.AbductionId == id)
                .Select(c => new { c.TargetType, c.TargetId, c.Status, c.Note, c.ClearedAt }), ct);
        if (compromisesTotal > 0)
        {
            var targets = await RecordsReference.ResolveAsync(db,
                compromises.Select(c => (c.TargetType, c.TargetId)).Distinct().ToList(), ct,
                mayAllTaskforces: view.MayAllTaskforces, meId: view.MeId);
            sb.AppendLine($"— Kompromittierungen ({compromisesTotal}) —");
            foreach (var c in compromises)
            {
                var display = targets.TryGetValue((c.TargetType, c.TargetId), out var target)
                    ? target.Classified && !view.MayClassifiedRead ? "(Verschlusssache)" : target.Display
                    : $"{SearchCatalog.German(c.TargetType)} (unbekannt)";
                sb.Append("• ").Append(display).Append(" — ").Append(CompromiseStatusDisplay.Name(c.Status));
                if (Free(c.Note) is { Length: > 0 } note)
                {
                    sb.Append(" — ").Append(note);
                }
                sb.AppendLine();
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(AgentAbduction), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"Entführung {e.CaseNumber}", sb.ToString(), IsClassified: false);
    }

    // ---- ledger ----

    static async Task<DossierContext?> BuildEvidenceItemAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var i = await db.EvidenceItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Asservat");
        Line(sb, "Name", i.Name);
        Line(sb, "Kategorie", i.Category);
        Line(sb, "Beschreibung", i.Description);

        // stock is a sum over the entry lines, and the sign follows the entry type
        var lines = await db.EvidenceEntryLines.AsNoTracking()
            .Where(l => l.ItemId == id)
            .Join(db.EvidenceEntries, l => l.EntryId, e => e.Id, (l, e) => new { l.Quantity, e.Type })
            .ToListAsync(ct);
        var onHand = lines.Sum(l => l.Type == EvidenceEntryType.Deposit ? l.Quantity : -l.Quantity);
        Line(sb, "Bestand", onHand.ToString());
        Line(sb, "Bewegungen", lines.Count.ToString());

        await AppendAttachmentsAsync(sb, db, nameof(EvidenceItem), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(i.Name, sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildEvidenceEntryAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var e = await db.EvidenceEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var codenames = await CodenamesAsync(db, [e.HandlerAgentId], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Asservat-Eintrag");
        Line(sb, "Aktenzeichen", e.CaseNumber);
        Line(sb, "Art", EvidenceEntryTypeDisplay.Name(e.Type));
        Line(sb, "Zeitpunkt", Fmt(e.Timestamp));
        Line(sb, "Bearbeitender Agent", Codename(codenames, e.HandlerAgentId));
        Line(sb, "Notizen", e.Notes);

        if (e.OwnerId is { Length: > 0 } ownerId && !string.IsNullOrWhiteSpace(e.OwnerType))
        {
            var owner = await RecordsReference.ResolveAsync(db, [(e.OwnerType, ownerId)], ct,
                mayAllTaskforces: view.MayAllTaskforces, meId: view.MeId);
            if (owner.TryGetValue((e.OwnerType, ownerId), out var who))
            {
                Line(sb, "Eigentümer", who.Classified && !view.MayClassifiedRead ? "(Verschlusssache)" : who.Display);
            }
        }

        var (positions, positionsTotal) = await TakeAsync(
            db.EvidenceEntryLines.AsNoTracking().Where(l => l.EntryId == id)
                .Join(db.EvidenceItems, l => l.ItemId, i => i.Id, (l, i) => new { i.Name, l.Quantity }), ct);
        if (positionsTotal > 0)
        {
            sb.AppendLine($"— Positionen ({positionsTotal}) —");
            foreach (var p in positions)
            {
                sb.Append("• ").Append(Free(p.Name)).Append(" ×").AppendLine(p.Quantity.ToString());
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(EvidenceEntry), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"Asservat-Eintrag {e.CaseNumber}", sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildKassenBuchungAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var b = await db.KassenBuchungen.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var codenames = await CodenamesAsync(db, [b.BookedById], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Kassenbuchung");
        Line(sb, "Aktenzeichen", b.CaseNumber);
        Line(sb, "Konto", KassenKontoDisplay.Name(b.Account));
        Line(sb, "Art", KassenBuchungArtDisplay.Name(b.Kind));
        Line(sb, "Betrag", b.Amount.ToString("N0") + " $");
        Line(sb, "Zeitpunkt", Fmt(b.Timestamp));
        Line(sb, "Gebucht von", b.BookedById is null ? null : Codename(codenames, b.BookedById));
        Line(sb, "Grund", b.Reason);

        await AppendAttachmentsAsync(sb, db, nameof(KassenBuchung), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"Kassenbuchung {b.CaseNumber}", sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildFinancingRequestAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var r = await db.FinancingRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var codenames = await CodenamesAsync(db, [r.AgentId], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Finanzierungsantrag");
        Line(sb, "Aktenzeichen", r.CaseNumber);
        Line(sb, "Antragsteller", Codename(codenames, r.AgentId));
        Line(sb, "Status", FinancingStatusDisplay.Name(r.Status));
        Line(sb, "Begründung", r.Justification);
        Line(sb, "Gesamtbetrag", r.RequestedGross.ToString("N0") + " $");
        Line(sb, "Beantragter Zuschuss", r.RequestedSubsidy.ToString("N0") + " $");
        Line(sb, "Bewilligter Zuschuss", r.ApprovedSubsidy is { } ok ? ok.ToString("N0") + " $" : null);
        Line(sb, "Budgetperiode", r.BudgetYear is { } year ? $"{r.BudgetMonth:00}/{year}" : null);
        Line(sb, "Entschieden am", Fmt(r.DecidedAt));
        Line(sb, "Entschieden von", r.DeciderName);
        Line(sb, "Entscheidungsvermerk", r.DecisionNote);
        Line(sb, "Ausgezahlt am", Fmt(r.PaidAt));

        var (positions, positionsTotal) = await TakeAsync(
            db.FinancingRequestLines.AsNoTracking().Where(l => l.RequestId == id)
                .OrderBy(l => l.Sorting)
                .Select(l => new { l.ItemName, l.Category, l.Quantity, l.ApprovedQuantity, l.UnitPrice, l.SubsidyPercent }), ct);
        if (positionsTotal > 0)
        {
            sb.AppendLine($"— Positionen ({positionsTotal}) —");
            foreach (var p in positions)
            {
                sb.Append("• ").Append(Free(p.ItemName)).Append(" ×").Append(p.Quantity);
                if (p.ApprovedQuantity is { } approved && approved != p.Quantity)
                {
                    sb.Append(" (bewilligt ×").Append(approved).Append(')');
                }
                sb.Append(" | ").Append(p.UnitPrice.ToString("N0")).Append(" $")
                    .Append(" | Zuschuss ").Append(p.SubsidyPercent).AppendLine(" %");
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(FinancingRequest), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"Finanzierungsantrag {r.CaseNumber}", sb.ToString(), IsClassified: false);
    }

    // ---- knowledge and administration ----

    static async Task<DossierContext?> BuildAnnouncementAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var a = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Ankündigung");
        Line(sb, "Aktenzeichen", a.CaseNumber);
        Line(sb, "Titel", a.Title);
        Line(sb, "Wichtig", a.Important);
        Line(sb, "Zielgruppe", AnnouncementAudienceDisplay.Name(a.Audience));
        Line(sb, "Mindest-Dienstgrad", a.MinRank is { } rank ? RankDisplay.Name(rank) : null);
        Line(sb, "Quittierung erforderlich", a.AcknowledgmentRequired);
        Line(sb, "Veröffentlicht", Fmt(a.CreatedAt));
        Line(sb, "Inhalt", StripHtml(a.Content));

        if (a.AcknowledgmentRequired)
        {
            // no status clause on the roster here: a terminated agent's row must stay in the total, exactly as
            // the acknowledgement counter on the board counts it
            var total = await db.AnnouncementAcknowledgments.CountAsync(k => k.AnnouncementId == id, ct);
            var done = await db.AnnouncementAcknowledgments
                .CountAsync(k => k.AnnouncementId == id && k.AcknowledgedAt != null, ct);
            Line(sb, "Quittierungen", $"{done} von {total}");
        }

        await AppendAttachmentsAsync(sb, db, nameof(Announcement), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(a.Title, sb.ToString(), IsClassified: false);
    }

    /// <summary>What the slide section may take: per title, per text, for all slide lines, for all notes.</summary>
    /// <remarks>Two pools, so the notes an owner gets never cost them slide text the audience sees; together they
    /// leave room inside the tool budget for the links behind the slides.</remarks>
    const int SlideTitleCap = 80;
    const int SlideTextCap = 400;
    const int SlidesBudget = 3_000;
    const int SlideNotesBudget = 1_000;

    /// <summary>A presentation with its slides in order, as plain text.</summary>
    /// <remarks>
    /// Speaker notes go out only to the owner and leadership, the rule the page shows them by. A cached brief is
    /// built without a viewer, so it never carries them. A Google slide is a picture of a PDF page; its text lives
    /// in Google, so only its page is named. Classified unless public, like a non-public appointment, for the egress
    /// guard of a generated brief.
    /// </remarks>
    static async Task<DossierContext?> BuildPresentationAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var p = await db.Presentations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);
        var withNotes = PresentationVisibility.MayManage(view, p);

        var taskforces = p.Audience == AnnouncementAudience.Taskforce && p.TargetId is { } taskforceId
            ? await db.Taskforces.AsNoTracking().Where(t => t.Id == taskforceId)
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct)
            : new Dictionary<string, string>();
        var owner = await CodenamesAsync(db, [p.OwnerId], ct);
        var title = string.IsNullOrWhiteSpace(p.Title) ? "Präsentation" : p.Title;

        var sb = new StringBuilder();
        sb.AppendLine("Präsentation");
        Line(sb, "Titel", title);
        Line(sb, "Beschreibung", p.Description);
        Line(sb, "Sichtbarkeit", PresentationService.AccessLabel(p.Access, p.Audience, p.TargetId, p.MinRank, taskforces));
        Line(sb, "Erstellt von", Codename(owner, p.OwnerId));
        Line(sb, "Geändert am", Fmt(p.ModifiedAt ?? p.CreatedAt));

        var (slides, slidesTotal) = await TakeAsync(
            db.PresentationSlides.AsNoTracking().Where(s => s.PresentationId == id)
                .OrderBy(s => s.Sorting)
                .Select(s => new { s.Layout, s.Title, s.BodyHtml, s.ImageId, s.PageNumber, s.Notes }), ct);
        if (slidesTotal > 0)
        {
            sb.AppendLine($"— Folien ({slidesTotal}) —");
            var listed = 0;
            var slidesRoom = SlidesBudget;
            var notesRoom = SlideNotesBudget;
            var notesCut = false;
            foreach (var s in slides)
            {
                var entry = new StringBuilder().Append(listed + 1).Append(". ");
                if (s.Layout == SlideLayout.PdfPage)
                {
                    entry.Append("Google-Folie");
                    if (s.PageNumber is { } page)
                    {
                        entry.Append(", Seite ").Append(page);
                    }
                }
                else
                {
                    entry.Append(OneLine(Free(s.Title)) is { Length: > 0 } slideTitle ? Cap(slideTitle, SlideTitleCap) : "(ohne Titel)");
                    // stale body after a layout switch
                    if (PresentationDisplay.HasText(s.Layout) && Free(StripHtml(s.BodyHtml)) is { Length: > 0 } body)
                    {
                        entry.Append(" — ").Append(Cap(body, SlideTextCap));
                    }
                    if (PresentationDisplay.HasImage(s.Layout) && s.ImageId is not null)
                    {
                        entry.Append(" [Bild]");
                    }
                }
                // whole slides only: the list stops where the next one no longer fits
                if (entry.Length > slidesRoom && listed > 0)
                {
                    break;
                }
                slidesRoom -= entry.Length;
                sb.Append(entry).AppendLine();
                listed++;

                if (withNotes && OneLine(Free(s.Notes)) is { Length: > 0 } notes)
                {
                    if (notesRoom <= 0)
                    {
                        notesCut = true;
                        continue;
                    }
                    var shown = Cap(notes, Math.Min(SlideTextCap, notesRoom));
                    notesRoom -= shown.Length;
                    sb.Append("   Notizen: ").AppendLine(shown);
                }
            }
            if (listed < slidesTotal)
            {
                sb.AppendLine($"Folien {listed + 1} bis {slidesTotal} nicht aufgeführt; den vollen Inhalt zeigt die Präsentation.");
            }
            if (notesCut)
            {
                sb.AppendLine("Weitere Sprechernotizen nicht aufgeführt.");
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Presentation), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(title, sb.ToString(), p.Access != PresentationAccess.Public);
    }

    /// <summary>Plain text on one line, so a slide stays one entry.</summary>
    static string OneLine(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Text cut to a length, marked when cut.</summary>
    static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + " …";

    static async Task<DossierContext?> BuildActivityAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var a = await db.AgentActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Aktivität");
        Line(sb, "Titel", a.Title);
        Line(sb, "Art", a.Kind);
        Line(sb, "Datum", Fmt(a.ActivityDate));
        Line(sb, "Inhalt", StripHtml(a.ContentHtml));

        var links = await db.AgentActivityLinks.AsNoTracking()
            .Where(l => l.AgentActivityId == id)
            .Select(l => new { l.TargetType, l.TargetId }).Take(Cap(50)).ToListAsync(ct);
        if (links.Count > 0)
        {
            var resolved = await RecordsReference.ResolveAsync(db,
                links.Select(l => (l.TargetType, l.TargetId)).Distinct().ToList(), ct,
                mayAllTaskforces: view.MayAllTaskforces, meId: view.MeId);
            sb.AppendLine($"— Bezüge ({links.Count}) —");
            foreach (var l in links)
            {
                sb.Append("• ").AppendLine(resolved.TryGetValue((l.TargetType, l.TargetId), out var r)
                    ? r.Classified && !view.MayClassifiedRead ? "(Verschlusssache)" : r.Display
                    : $"{SearchCatalog.German(l.TargetType)} (unbekannt)");
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(AgentActivity), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(a.Title, sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildRequestAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var r = await db.Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Antrag");
        Line(sb, "Art", RequestTypeDisplay.Name(r.Type));
        Line(sb, "Status", RequestStatusDisplay.Name(r.Status));
        Line(sb, "Betrifft", $"{SearchCatalog.German(r.TargetType)}: {Free(r.TargetDesignation)}");
        Line(sb, "Einstufung der Zielakte", ClassificationDisplay.Name(r.TargetClassification));
        Line(sb, "Antragsteller", r.RequesterName);
        Line(sb, "Begründung", r.Justification);
        Line(sb, "Entschieden am", Fmt(r.DecidedAt));
        Line(sb, "Entschieden von", r.DeciderName);
        Line(sb, "Entscheidungsvermerk", r.DecisionNote);
        Line(sb, "Freigabe an", r.FreigabeAgency is { } agency ? PartnerAgencyDisplay.Name(agency) : null);

        await AppendAttachmentsAsync(sb, db, nameof(Request), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"Antrag: {r.TargetDesignation}", sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildSituationReportAsync(AppDbContext db, string id, CancellationToken ct)
    {
        var r = await db.SituationReports.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Title, x.Year, x.Month, x.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (r is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Lagebericht");
        Line(sb, "Titel", r.Title);
        Line(sb, "Berichtszeitraum", $"{r.Month:00}/{r.Year}");
        Line(sb, "Erstellt", Fmt(r.CreatedAt));
        // the figures are a stored month-end snapshot; hole_kennzahlen answers about the current stock
        sb.AppendLine("Die Kennzahlen dieses Berichts sind eine Momentaufnahme zum Monatsende.");

        return new DossierContext(r.Title, sb.ToString(), IsClassified: true);
    }

    static async Task<DossierContext?> BuildLibraryFileAsync(AppDbContext db, string id, CancellationToken ct)
    {
        var f = await db.LibraryFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return null;
        }

        var level = DossierScope.LevelOf(f.IsClassified, f.IsTRUClassified, f.IsHRBClassified);
        var sb = new StringBuilder();
        sb.AppendLine("Bibliotheks-Datei");
        Line(sb, "Titel", f.Title);
        Line(sb, "Kategorie", f.Category);
        Line(sb, "Dateiname", f.OriginalName);
        Line(sb, "Dateityp", f.ContentType);
        Line(sb, "Größe", $"{f.SizeBytes / 1024} KB");
        Line(sb, "Verschlusssache", level == DocumentClassification.None ? "Nein" : $"Ja ({level})");
        Line(sb, "Hochgeladen", Fmt(f.CreatedAt));
        // the file itself is a binary the assistant never reads; only what the library knows about it
        sb.AppendLine("Der Dateiinhalt selbst steht nicht als Text zur Verfügung.");

        return new DossierContext(f.Title, sb.ToString(), level != DocumentClassification.None);
    }

    static async Task<DossierContext?> BuildTrainingModuleAsync(AppDbContext db, string id, CancellationToken ct)
    {
        var m = await db.TrainingModules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Ausbildungsmodul");
        Line(sb, "Name", m.Name);
        Line(sb, "Beschreibung", m.Description);
        Line(sb, "Aktiv", m.IsActive);
        var completions = await db.AgentModuleCompletions.CountAsync(c => c.ModuleId == id, ct);
        Line(sb, "Abschlüsse", completions.ToString());

        return new DossierContext(m.Name, sb.ToString(), IsClassified: false);
    }

    static async Task<DossierContext?> BuildCounterIntelRuleAsync(AppDbContext db, string id, CancellationToken ct)
    {
        var r = await db.CounterIntelRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Gegenaufklärungs-Regel");
        Line(sb, "Name", r.Name);
        Line(sb, "Beschreibung", r.Description);
        Line(sb, "Schwere", CounterIntelSeverityDisplay.Name(r.Severity));
        Line(sb, "Aktiv", r.IsActive);
        // the definition is the rule's filter JSON: structure the model cannot act on, and noise if pasted raw
        Line(sb, "Bedingungen hinterlegt", r.DefinitionJson.Length > 2);

        return new DossierContext(r.Name, sb.ToString(), IsClassified: true);
    }

    static async Task<DossierContext?> BuildFeedbackAsync(AppDbContext db, string id, CancellationToken ct)
    {
        var f = await db.Feedbacks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return null;
        }
        var codenames = await CodenamesAsync(db, [f.AgentId], ct);

        var sb = new StringBuilder();
        sb.AppendLine("Feedback");
        Line(sb, "Art", FeedbackKindDisplay.Name(f.Kind));
        Line(sb, "Status", FeedbackStatusDisplay.Name(f.Status));
        Line(sb, "Gemeldet von", Codename(codenames, f.AgentId));
        Line(sb, "Seite", f.PageRoute);
        Line(sb, "Gemeldet am", Fmt(f.CreatedAt));
        Line(sb, "Text", f.Text);
        Line(sb, "Antwort", f.Response);
        Line(sb, "Beantwortet von", f.DeciderName);

        var title = Free(f.Text) is { Length: > 0 } text
            ? text[..Math.Min(60, text.Length)]
            : FeedbackKindDisplay.Name(f.Kind);
        return new DossierContext(title, sb.ToString(), IsClassified: false);
    }

    // ---- shared ----

    /// <summary>Appends the agents assigned to a record, by codename.</summary>
    static async Task AppendAssignedAgentsAsync(
        StringBuilder sb, AppDbContext db, IQueryable<string> agentIds, CancellationToken ct)
    {
        var ids = await agentIds.Take(Cap(50)).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }
        var codenames = await CodenamesAsync(db, ids, ct);
        sb.AppendLine($"— Zugeteilte Agenten ({ids.Count}) —");
        foreach (var agentId in ids)
        {
            sb.Append("• ").AppendLine(Codename(codenames, agentId));
        }
    }
}
