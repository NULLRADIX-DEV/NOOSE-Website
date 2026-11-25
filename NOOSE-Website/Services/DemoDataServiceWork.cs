using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Demo data for jobs, calendar, board, notes, library and inbox.</summary>
public partial class DemoDataService
{
    private const string JobBriefing = "Lagebild Süd für die Wochenbesprechung";

    private async Task<int> SeedJobsAsync(AppDbContext db, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        (string Title, string Description, JobStatus Status, JobPriority Priority, int? DueInDays, bool Restricted, string Author, string[] Assignees, string? Case)[] specs =
        [
            ("Mobiltelefon von Ricardo Mendez auswerten", "Chats und Kontakte auf Hinweise zur nächsten Lieferung prüfen.",
                JobStatus.InProcessing, JobPriority.High, 2, false, "Habicht", [Fuchs, DemoIdentity.AgentId], CaseHarbour),
            (JobBriefing, "Kurzes Lagebild für die Besprechung am Donnerstag, mit den Vorfällen der letzten Woche.",
                JobStatus.Open, JobPriority.Normal, 3, false, "Falke", [DemoIdentity.AgentId, Habicht], null),
            ("Zeugenvernehmung Hafenarbeiter vorbereiten", "Fragenkatalog und Termin mit dem Zeugen abstimmen.",
                JobStatus.Open, JobPriority.High, -1, false, "Fuchs", [Specht], CaseHarbour),
            ("Fahrzeughalter zum Kennzeichen 4KXR221 ermitteln", "Abfrage beim LSPD.",
                JobStatus.Done, JobPriority.Low, -6, false, "Habicht", [Reiher], CaseHarbour),
            ("Durchsuchungsbeschluss für das Clubhaus beantragen", "Antrag mit den Funden aus der Lagerhalle begründen.",
                JobStatus.InProcessing, JobPriority.High, 1, false, "Wolf", [Wolf, Habicht], null),
            ("Rückmeldung von Marder einholen", "Seit fast drei Wochen ohne Lebenszeichen. Gespräch führen und vermerken.",
                JobStatus.Open, JobPriority.Normal, 4, true, "Kranich", [Kranich, DemoIdentity.AgentId], null),
            ("Kontobewegungen Briefkastenfirma auswerten", "Zahlungen an die Filmproduktion mit den Kontoauszügen abgleichen.",
                JobStatus.InProcessing, JobPriority.Normal, 5, false, "Kranich", [Eule], CaseLaundering),
        ];
        var known = (await db.Jobs.IgnoreQueryFilters().Select(j => j.Title).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var created = 9;
        foreach (var (title, description, status, priority, dueInDays, restricted, author, assignees, caseTitle) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var due = dueInDays is int days ? DateTime.UtcNow.Date.AddDays(days).AddHours(18) : (DateTime?)null;
            var job = new Job
            {
                CaseNumber = await caseNumbers.NextAsync(db, "A", ct),
                Title = title,
                Description = description,
                Status = status,
                Priority = priority,
                DueDate = due,
                DoneAt = status == JobStatus.Done ? due?.AddDays(-1) : null,
                IsRestricted = restricted,
                // keeps reminders quiet
                DueReminderStage = dueInDays switch
                {
                    null => JobDueReminderStage.None,
                    <= 0 => JobDueReminderStage.DueDay,
                    1 => JobDueReminderStage.OneDay,
                    <= 3 => JobDueReminderStage.ThreeDays,
                    _ => JobDueReminderStage.None,
                },
            };
            foreach (var agent in assignees)
            {
                job.Assignments.Add(new JobAssignment { AgentId = agent });
            }
            db.Jobs.Add(job);
            restamps.Add(new Restamp(typeof(Job), job.Id, DateTime.UtcNow.AddDays(-created--), Colleague(author)));
            added += 1 + job.Assignments.Count;
            if (caseTitle is not null && records.Cases.TryGetValue(caseTitle, out var item))
            {
                added += AddLinks(db, nameof(Job), job.Id, [item.Id], nameof(Case));
            }
        }
        return added;
    }

    private async Task<int> SeedAppointmentsAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        (string Title, AppointmentCategory Category, AppointmentStatus Status, int DayOffset, int Hour, int? Hours, bool AllDay,
            string? Location, string Description, AppointmentVisibilityLevel Visibility, string Author, string[] Participants)[] specs =
        [
            ("Hauptverhandlung gegen Vinnie Moretti", AppointmentCategory.CourtDate, AppointmentStatus.Planned, 6, 10, 3, false,
                "Bezirksgericht Los Santos, Saal 2", "Aussage als Zeuge zum Überfall auf die Fleeca-Bank.",
                AppointmentVisibilityLevel.Public, "Habicht", [Habicht, Baer, DemoIdentity.AgentId]),
            ("Zugriff Clubhaus The Lost MC", AppointmentCategory.Deployment, AppointmentStatus.Planned, 4, 21, 4, false,
                "Stab City", "Einsatz nach Erlass des Durchsuchungsbeschlusses. Briefing eine Stunde vorher im HQ.",
                AppointmentVisibilityLevel.Restricted, "Wolf", [Wolf, Baer, Dachs, DemoIdentity.AgentId]),
            ("Schießtraining HRT", AppointmentCategory.Misc, AppointmentStatus.Planned, 2, 17, 2, false,
                "Schießstand Pillbox Hill", "Pflichttraining, Ausrüstung wird gestellt.",
                AppointmentVisibilityLevel.Public, "Wolf", [Wolf, Baer, Dachs]),
            ("Frist: Zwischenbericht an das DoJ", AppointmentCategory.Deadline, AppointmentStatus.Planned, 9, 0, null, true,
                null, "Zwischenbericht zur Finanzermittlung gegen das Vinewood Syndikat.",
                AppointmentVisibilityLevel.Public, "Kranich", [Kranich, Eule, DemoIdentity.AgentId]),
            ("Abstimmung mit dem LSPD zur Hafenlage", AppointmentCategory.Meeting, AppointmentStatus.Perceived, -3, 15, 1, false,
                "Mission Row Police Station", "Gemeinsames Lagebild, Absperrungen für den nächsten Zugriff.",
                AppointmentVisibilityLevel.Public, "Habicht", [Habicht, Fuchs, DemoIdentity.AgentId]),
            ("Einweisung der neuen Trainees", AppointmentCategory.Meeting, AppointmentStatus.Planned, 1, 18, 2, false,
                "Schulungsraum, FIB HQ", "Akten, Funk, Verschlusssachen. Bitte das Handbuch vorher lesen.",
                AppointmentVisibilityLevel.Public, "Fuchs", [Fuchs, Specht, Reiher]),
            ("Personalgespräch Marder", AppointmentCategory.Meeting, AppointmentStatus.Postponed, 2, 16, 1, false,
                "Büro Section Chief CID", "Nur für die Beteiligten.",
                AppointmentVisibilityLevel.Private, "Kranich", [Kranich, DemoIdentity.AgentId]),
        ];
        var known = (await db.Appointments.IgnoreQueryFilters().Select(a => a.Title).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (title, category, status, dayOffset, hour, hours, allDay, location, description, visibility, author, participants) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var start = DateTime.UtcNow.Date.AddDays(dayOffset).AddHours(hour);
            var appointment = new Appointment
            {
                CaseNumber = await caseNumbers.NextAsync(db, "TM", ct),
                Title = title,
                Category = category,
                Status = status,
                Start = start,
                End = hours is int h ? start.AddHours(h) : null,
                AllDay = allDay,
                Location = location,
                Description = description,
                Visibility = visibility,
            };
            foreach (var agent in participants)
            {
                appointment.Participant.Add(new AppointmentAssignment { AgentId = agent });
            }
            db.Appointments.Add(appointment);
            restamps.Add(new Restamp(typeof(Appointment), appointment.Id, start.AddDays(-Math.Max(2, dayOffset + 5)), Colleague(author)));
            added += 1 + appointment.Participant.Count;
        }
        return added;
    }

    private async Task<int> SeedAnnouncementsAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        var everyone = ActiveColleagues.Select(c => c.Id).Append(DemoIdentity.AgentId).ToList();

        (string Title, string Content, bool Important, AnnouncementAudience Audience, Rank? MinRank, int DaysAgo, int? AckDays, string Author, string[] Acked)[] specs =
        [
            ("Neue Fassung der Dienstverordnung",
                "Die Dienstverordnung hat eine neue Fassung. Bitte lest die geänderten Absätze zur Funkdisziplin und bestätigt das Lesen auf dem Brett.",
                true, AnnouncementAudience.AllActive, null, 3, 2, "Falke",
                [Falke, Greif, Kranich, Wolf, Habicht, Fuchs, Eule, Dachs]),
            ("Gefahrenlage auf DEFCON 4 angehoben",
                "Nach den Schusswechseln im Hafenviertel gilt ab sofort DEFCON 4. Im Außendienst nur noch zu zweit und mit Weste.",
                true, AnnouncementAudience.AllActive, null, 8, null, "Falke", []),
            ("Schießtraining am Wochenende",
                "Das HRT trainiert am Samstag ab 17 Uhr auf dem Schießstand Pillbox Hill. Teilnahme ist Pflicht.",
                false, AnnouncementAudience.TruUnit, null, 5, null, "Wolf", []),
            ("Personalplanung für das nächste Quartal",
                "Bitte meldet bis Freitag, wie viele Agenten eure Abteilung zusätzlich braucht und für welche Aufgaben.",
                false, AnnouncementAudience.FromRank, Rank.SectionChief, 4, null, "Greif", []),
            ("Willkommen an Specht und Reiher",
                "Wir begrüßen zwei neue Trainees in der CID. Fuchs übernimmt die Einarbeitung, Fragen gern direkt an sie.",
                false, AnnouncementAudience.AllActive, null, 11, null, "Kranich", []),
        ];
        var known = (await db.Announcements.IgnoreQueryFilters().Select(a => a.Title).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (title, content, important, audience, minRank, daysAgo, ackDays, author, acked) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var created = DateTime.UtcNow.AddDays(-daysAgo);
            var due = ackDays is int d ? created.AddDays(d) : (DateTime?)null;
            var announcement = new Announcement
            {
                CaseNumber = await caseNumbers.NextAsync(db, "N", ct),
                Title = title,
                Content = Paragraph(content),
                Important = important,
                Audience = audience,
                MinRank = minRank,
                AsBroadcast = false,
                AcknowledgmentRequired = ackDays is not null,
                AcknowledgmentDueAt = due,
                // reminder already sent
                AcknowledgmentRemindedAt = due < DateTime.UtcNow ? due.Value.AddMinutes(5) : null,
            };
            if (ackDays is not null)
            {
                foreach (var agent in everyone)
                {
                    announcement.Acknowledgments.Add(new AnnouncementAcknowledgment
                    {
                        AgentId = agent,
                        AcknowledgedAt = acked.Contains(agent) ? created.AddHours(2 + Array.IndexOf(acked, agent) * 3) : null,
                    });
                }
            }
            db.Announcements.Add(announcement);
            restamps.Add(new Restamp(typeof(Announcement), announcement.Id, created, Colleague(author)));
            added += 1 + announcement.Acknowledgments.Count;
        }
        return added;
    }

    /// <summary>Seed follow-ups, comments, tags and watchlist.</summary>
    private static async Task<int> SeedRecordNotesAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions,
        DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        var added = 0;
        string? PersonId(string name) => people.GetValueOrDefault(name)?.Id;
        string? FactionId(string name) => factions.GetValueOrDefault(name)?.Id;
        string? CaseId(string title) => records.Cases.GetValueOrDefault(title)?.Id;
        string? OperationId(string title) => records.Operations.GetValueOrDefault(title)?.Id;

        (string Type, string? Id, int DueInDays, string Note, string Responsible)[] followups =
        [
            (nameof(Person), PersonId("Ricardo Mendez"), 2, "Ergebnis der Handyauswertung in die Akte übernehmen.", Fuchs),
            (nameof(Faction), FactionId("Ballas"), -1, "Lage nach dem Drive-by neu bewerten, Einstufung prüfen.", Marder),
            (nameof(Case), CaseId(CaseHarbour), 5, "Stand der Observation für die Taskforce zusammenfassen.", Habicht),
            (nameof(Person), PersonId("Vinnie Moretti"), 1, "Fahndungsstand vor der Hauptverhandlung prüfen.", DemoIdentity.AgentId),
            (nameof(Faction), FactionId("Bratva Los Santos"), 9, "Neue Mitglieder aus der Funkaufklärung eintragen.", Luchs),
        ];
        var knownFollowups = (await db.Followups.IgnoreQueryFilters().Select(f => f.Note).ToListAsync(ct))
            .Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, id, dueInDays, note, responsible) in followups)
        {
            if (id is null || !knownFollowups.Add(note))
            {
                continue;
            }
            var due = DateTime.UtcNow.Date.AddDays(dueInDays).AddHours(12);
            var overdue = due < DateTime.UtcNow;
            db.Followups.Add(new Followup
            {
                EntityType = type,
                EntityId = id,
                DueAt = due,
                Note = note,
                ResponsibleAgentId = responsible,
                // worker already notified
                NotifiedAt = overdue ? due.AddMinutes(1) : null,
                EscalatedAt = overdue ? due.AddHours(1) : null,
            });
            added++;
        }

        (string Type, string? Id, int HoursAgo, string Author, string Text)[] comments =
        [
            (nameof(Person), PersonId("Ricardo Mendez"), 50, "Fuchs", "Mendez telefoniert auffällig oft mit einer Nummer aus Paleto Bay. Halter wird ermittelt."),
            (nameof(Person), PersonId("Ricardo Mendez"), 20, "Habicht", "Danke. Bitte auch mit den Funkdaten vom Terminal abgleichen."),
            (nameof(Faction), FactionId("Los Santos Vagos"), 72, "Habicht", "Nach dem Zugriff wirkt die Führung nervös, zwei Mitglieder sind untergetaucht."),
            (nameof(Faction), FactionId("Los Santos Vagos"), 30, "Specht", "Am Terminal Nord wurden heute zwei bekannte Gesichter gesehen, Fotos im Einsatztagebuch."),
            (nameof(Faction), FactionId("Ballas"), 100, "Marder", "Lage ist ruhig, ich bleibe dran."),
            (nameof(Case), CaseId(CaseHarbour), 26, "Fuchs", "Die dritte Lieferung kommt laut Chatverlauf nächste Woche Dienstag."),
            (nameof(Case), CaseId(CaseHarbour), 8, "Habicht", "Dann planen wir den Zugriff für Dienstag Nacht. HRT ist informiert."),
            (nameof(Case), CaseId(CaseLaundering), 60, "Eule", "Die Briefkastenfirma sitzt in Del Perro, Geschäftsführer ist ein Strohmann."),
            (nameof(Operation), OperationId(OperationRaid), 500, "Wolf", "Nachbesprechung: Zugriff über den Nordeingang hat gut funktioniert, beim nächsten Mal früher absperren."),
        ];
        var knownComments = (await db.Comments.IgnoreQueryFilters().Select(c => c.Text).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, id, hoursAgo, author, text) in comments)
        {
            if (id is null || !knownComments.Add(text))
            {
                continue;
            }
            var comment = new Comment { EntityType = type, EntityId = id, Text = text, AuthorName = author };
            db.Comments.Add(comment);
            restamps.Add(new Restamp(typeof(Comment), comment.Id, DateTime.UtcNow.AddHours(-hoursAgo), Colleague(author)));
            added++;
        }

        (string Tag, (string Type, string? Id)[] Records)[] tags =
        [
            ("Hafen", [(nameof(Person), PersonId("Ricardo Mendez")), (nameof(Person), PersonId("Pablo Cruz")),
                (nameof(Case), CaseId(CaseHarbour)), (nameof(Operation), OperationId(OperationWatch))]),
            ("Waffenhandel", [(nameof(Faction), FactionId("Los Santos Vagos")), (nameof(Faction), FactionId("Merryweather Security")),
                (nameof(Case), CaseId(CaseHarbour))]),
            ("Geldwäsche", [(nameof(Faction), FactionId("Vinewood Syndikat")), (nameof(Person), PersonId("Devin Weston")),
                (nameof(Case), CaseId(CaseLaundering))]),
            ("Betäubungsmittel", [(nameof(Faction), FactionId("Cartel Madrazo")), (nameof(Faction), FactionId("Sinaloa Connection")),
                (nameof(Faction), FactionId("The Lost MC"))]),
            ("Zeuge", [(nameof(Person), PersonId("Sofia Rodriguez")), (nameof(Person), PersonId("Denise Johnson"))]),
            ("Priorität", [(nameof(Person), PersonId("Dimitri Volkov")), (nameof(Faction), FactionId("Bratva Los Santos"))]),
        ];
        var tagMap = (await db.Tags.ToListAsync(ct))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var mapped = (await db.TagMappings.Select(m => m.TagId + "|" + m.EntityType + "|" + m.EntityId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (name, targets) in tags)
        {
            if (!tagMap.TryGetValue(name, out var tag))
            {
                tag = new Tag { Name = name };
                db.Tags.Add(tag);
                tagMap[name] = tag;
                added++;
            }
            foreach (var (type, id) in targets)
            {
                if (id is null || !mapped.Add(tag.Id + "|" + type + "|" + id))
                {
                    continue;
                }
                db.TagMappings.Add(new TagMapping { TagId = tag.Id, EntityType = type, EntityId = id });
                added++;
            }
        }

        (string Type, string? Id)[] watched =
        [
            (nameof(Person), PersonId("Ricardo Mendez")), (nameof(Person), PersonId("Vinnie Moretti")),
            (nameof(Faction), FactionId("Los Santos Vagos")), (nameof(Faction), FactionId("Cartel Madrazo")),
            (nameof(Case), CaseId(CaseHarbour)), (nameof(Operation), OperationId(OperationWatch)),
            (nameof(Taskforce), records.Taskforces.GetValueOrDefault(TaskforceHarbour)?.Id),
        ];
        var following = (await db.Watchlists.IgnoreQueryFilters().Where(w => w.AgentId == DemoIdentity.AgentId)
                .Select(w => w.EntityType + "|" + w.EntityId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (type, id) in watched)
        {
            if (id is null || !following.Add(type + "|" + id))
            {
                continue;
            }
            db.Watchlists.Add(new WatchlistEntry { AgentId = DemoIdentity.AgentId, EntityType = type, EntityId = id });
            added++;
        }
        return added;
    }

    /// <summary>Seed library documents.</summary>
    private static async Task<int> SeedLibraryAsync(AppDbContext db, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        (string Title, string Category, bool Pinned, bool Classified, string? Taskforce, int DaysAgo, string Author, string Html)[] specs =
        [
            ("Leitfaden Vernehmung", "Leitfäden", true, false, null, 90, "Fuchs",
                "<h2>Vorbereitung</h2><p>Akte lesen, Fragen notieren, Rollen im Team klären.</p>"
                + "<h2>Ablauf</h2><ol><li>Belehrung</li><li>Offene Fragen zuerst</li><li>Vorhalt mit Beweisen</li><li>Zusammenfassung</li></ol>"
                + "<h2>Nachbereitung</h2><p>Dok an der Personenakte anlegen, neue Erkenntnisse verknüpfen.</p>"),
            ("Checkliste Zugriff", "Einsatz", true, false, null, 120, "Wolf",
                "<ul data-checked=\"false\"><li>Durchsuchungsbeschluss liegt vor</li><li>Absperrung mit dem LSPD abgestimmt</li>"
                + "<li>Rettungsdienst in Bereitschaft</li><li>Funkkanal und Rufnamen vergeben</li></ul>"),
            ("Funkrufnamen und Kanäle", "Organisation", false, false, null, 60, "Kranich",
                "<p>CID ruft mit <strong>Adler</strong>, CTD mit <strong>Falke</strong>, HRT mit <strong>Wolf</strong> plus Nummer.</p>"
                + "<p>Einsätze laufen auf dem Einsatzkanal, Lagemeldungen auf dem Leitkanal.</p>"),
            ("Lagebild Kartelle (vertraulich)", "Lagebilder", false, true, null, 14, "Kranich",
                "<p>Das Cartel Madrazo und die Sinaloa Connection teilen sich seit dem Sommer die Route über Paleto Bay.</p>"
                + "<p>Geldflüsse laufen über das Vinewood Syndikat. Die Taskforce Kartelle wertet die Konten aus.</p>"),
            ("Arbeitsstand Taskforce Hafen", "Arbeitsstand", false, false, TaskforceHarbour, 7, "Habicht",
                "<p><strong>Erledigt:</strong> Zugriff Lagerhalle, Handyauswertung begonnen.</p>"
                + "<p><strong>Offen:</strong> Zeugenvernehmung, Durchsuchungsbeschluss Clubhaus, dritte Lieferung.</p>"),
        ];
        var known = (await db.Documents.IgnoreQueryFilters().Select(d => d.Title).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (title, category, pinned, classified, taskforce, daysAgo, author, html) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var document = new Document
            {
                Title = title,
                Category = category,
                Pinned = pinned,
                IsClassified = classified,
                OwnerTaskforceId = taskforce is null ? null : records.Taskforces.GetValueOrDefault(taskforce)?.Id,
                ContentHtml = html,
            };
            db.Documents.Add(document);
            restamps.Add(new Restamp(typeof(Document), document.Id, DateTime.UtcNow.AddDays(-daysAgo), Colleague(author)));
            added++;
        }
        return added;
    }

    /// <summary>Seed inbox entries for the demo agent.</summary>
    private static async Task<int> SeedNotificationsAsync(
        AppDbContext db, Dictionary<string, Person> people, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        var job = await db.Jobs.Where(j => j.Title == JobBriefing).Select(j => j.Id).FirstOrDefaultAsync(ct)
            ?? db.Jobs.Local.FirstOrDefault(j => j.Title == JobBriefing)?.Id;
        (NotificationType Type, string Title, string? Href, int HoursAgo, bool Read)[] specs =
        [
            (NotificationType.TaskforceChat, "Neue Nachricht in der Taskforce Hafen",
                records.Taskforces.GetValueOrDefault(TaskforceHarbour) is { } tf ? $"/taskforces/{tf.Id}?tab=chat" : null, 2, false),
            (NotificationType.Mention, "Fuchs hat dich in einem Kommentar erwähnt",
                people.GetValueOrDefault("Ricardo Mendez") is { } p ? $"/personen/{p.Id}" : null, 20, false),
            (NotificationType.JobAssigned, "Neue Aufgabe: " + JobBriefing, job is null ? null : $"/aufgaben/{job}", 30, false),
            (NotificationType.AppointmentAssigned, "Du bist Teilnehmer: Hauptverhandlung gegen Vinnie Moretti", "/kalender", 60, true),
            (NotificationType.Announcement, "Neue Fassung der Dienstverordnung", "/brett", 72, true),
            (NotificationType.FirearmUseReported, "Schusswaffengebrauch gemeldet: Dachs", "/personal", 144, true),
        ];
        var known = (await db.Notifications.Where(n => n.RecipientId == DemoIdentity.AgentId).Select(n => n.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var (type, title, href, hoursAgo, read) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var at = DateTime.UtcNow.AddHours(-hoursAgo);
            var notification = new Notification
            {
                RecipientId = DemoIdentity.AgentId,
                Type = type,
                Title = title,
                Href = href,
                ReadAt = read ? at.AddHours(1) : null,
            };
            db.Notifications.Add(notification);
            restamps.Add(new Restamp(typeof(Notification), notification.Id, at, DemoIdentity.AgentId));
            added++;
        }
        return added;
    }
}
