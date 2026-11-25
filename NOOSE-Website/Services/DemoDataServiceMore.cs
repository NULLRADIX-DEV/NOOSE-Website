using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Demo warrants, requests, presentations, administration and record histories.</summary>
public partial class DemoDataService
{
    private async Task<int> SeedWarrantsAsync(
        AppDbContext db, Dictionary<string, Person> people, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        var laws = (await db.Laws.ToListAsync(ct)).Concat(db.Laws.Local).DistinctBy(l => l.Id).ToList();
        Law? LawFor(string paragraph) => laws.FirstOrDefault(l => l.Paragraph == paragraph);

        (string Subject, WarrantType Type, WarrantStatus Status, string Person, string? Case, string[] Paragraphs, int DaysAgo, int Validity, string Requester, string Justification, string? Note)[] specs =
        [
            ("Durchsuchung Clubhaus The Lost MC, Stab City", WarrantType.Search, WarrantStatus.Requested, "Johnny Klebitz", null,
                ["§ 52", "§ 29a"], 1, 7, "Wolf",
                "Bei der Razzia in der Lagerhalle wurden Waffen mit Spuren aus dem Clubhaus gefunden. Zeugen sahen Klebitz dort Kisten abladen.", null),
            ("Durchsuchung Wohnung Ricardo Mendez, Rancho", WarrantType.Search, WarrantStatus.Signed, "Ricardo Mendez", CaseHarbour,
                ["§ 52", "§ 129"], 18, 14, "Habicht",
                "Mendez koordiniert laut Chatverlauf die Anlieferung der Waffen. In seiner Wohnung werden Lieferlisten vermutet.", "Erlassen, befristet auf 14 Tage."),
            ("Beschlagnahme Konten Briefkastenfirma Del Perro", WarrantType.Seizure, WarrantStatus.Signed, "Devin Weston", CaseLaundering,
                ["§ 129"], 25, 30, "Kranich",
                "Über die Firma liefen nachweislich 2,4 Mio. $ aus dem Umfeld des Syndikats. Ohne Sicherung droht der Abzug der Gelder.", "Erlassen."),
            ("Haftbefehl Vinnie Moretti", WarrantType.Arrest, WarrantStatus.Signed, "Vinnie Moretti", null,
                ["§ 250"], 60, 90, "Habicht",
                "Dringend tatverdächtig des schweren Raubes auf die Fleeca-Bank an der Route 68, flüchtig.", "Erlassen, Fahndung läuft."),
            ("Durchsuchung Werkstatt Strawberry", WarrantType.Search, WarrantStatus.Rejected, "Kane Williams", null,
                ["§ 52"], 40, 7, "Marder",
                "Hinweis eines Anwohners auf gelagerte Waffen.", "Abgelehnt, ein einzelner anonymer Hinweis reicht nicht."),
        ];

        var known = (await db.Warrants.IgnoreQueryFilters().Select(w => w.Subject).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var (subject, type, status, personName, caseTitle, paragraphs, daysAgo, validity, requester, justification, note) in specs)
        {
            if (!known.Add(subject) || !people.TryGetValue(personName, out var person))
            {
                continue;
            }
            var lawList = paragraphs.Select(LawFor).Where(l => l is not null).Select(l => l!).ToList();
            var decided = status is WarrantStatus.Signed or WarrantStatus.Rejected ? DateTime.UtcNow.AddDays(-daysAgo + 1) : (DateTime?)null;
            var warrant = new Warrant
            {
                CaseNumber = await caseNumbers.NextAsync(db, WarrantRules.CaseNumberPrefix, ct),
                Type = type,
                Status = status,
                PersonId = person.Id,
                CaseId = caseTitle is null ? null : records.Cases.GetValueOrDefault(caseTitle)?.Id,
                Subject = subject,
                Justification = justification,
                LegalBasis = string.Join("\n", lawList.Select(WarrantRules.LawLabel)),
                LawIds = string.Join(",", lawList.Select(l => l.Id)),
                ValidityDays = validity,
                ValidFrom = status == WarrantStatus.Signed ? decided : null,
                ValidUntil = status == WarrantStatus.Signed ? decided!.Value.AddDays(validity) : null,
                RequesterName = requester,
                DeciderName = decided is null ? null : "Richterin M. Hollis",
                DeciderFunction = decided is null ? null : "Department of Justice, Bezirksgericht Los Santos",
                DecidedAt = decided,
                DecisionNote = note,
            };
            db.Warrants.Add(warrant);
            restamps.Add(new Restamp(typeof(Warrant), warrant.Id, DateTime.UtcNow.AddDays(-daysAgo), Colleague(requester)));
            added++;
        }
        return added;
    }

    private static async Task<int> SeedRequestsAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions, List<Restamp> restamps, CancellationToken ct)
    {
        (string Type, string Name, Classification Target, RequestStatus Status, int DaysAgo, string Requester, string Justification, string? Note)[] specs =
        [
            (nameof(Person), "Ricardo Mendez", Classification.SecuredStateThreatening, RequestStatus.Requested, 1, "Habicht",
                "Mendez organisiert die Waffenlieferungen über den Hafen selbst. Die Chats belegen Kontakte zum Cartel Madrazo.", null),
            (nameof(Faction), "Yakuza Kanto-kai", Classification.SecuredStateThreatening, RequestStatus.Requested, 3, "Luchs",
                "Die Schutzgelderpressung in Little Seoul ist organisiert und bewaffnet. Zwei Geschäftsleute wurden bedroht.", null),
            (nameof(Person), "Lamar Davis", Classification.SuspicionCase, RequestStatus.Approved, 22, "Marder",
                "Davis war bei zwei Schießereien mit den Ballas vor Ort, Zeugen nennen ihn als Schützen.", "Zugestimmt."),
            (nameof(Faction), "Grove Street Families", Classification.SuspicionCase, RequestStatus.Rejected, 35, "Marder",
                "Revierkonflikt mit den Ballas eskaliert.", "Abgelehnt, die Lage ist seit Wochen ruhig. Erneut vorlegen, wenn es neue Vorfälle gibt."),
        ];
        var known = (await db.Requests.IgnoreQueryFilters().Select(r => r.TargetDesignation).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var (type, name, target, status, daysAgo, requester, justification, note) in specs)
        {
            var id = type == nameof(Person) ? people.GetValueOrDefault(name)?.Id : factions.GetValueOrDefault(name)?.Id;
            if (id is null || !known.Add(name))
            {
                continue;
            }
            var request = new Request
            {
                Type = RequestType.Upgrade,
                TargetType = type,
                TargetId = id,
                TargetDesignation = name,
                TargetClassification = target,
                Justification = justification,
                Status = status,
                RequesterName = requester,
                DeciderName = status == RequestStatus.Requested ? null : "Falke",
                DecidedAt = status == RequestStatus.Requested ? null : DateTime.UtcNow.AddDays(-daysAgo + 1),
                DecisionNote = note,
            };
            db.Requests.Add(request);
            restamps.Add(new Restamp(typeof(Request), request.Id, DateTime.UtcNow.AddDays(-daysAgo), Colleague(requester)));
            added++;
        }
        return added;
    }

    private static async Task<int> SeedPresentationsAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        (string Title, string Description, PresentationAccess Access, Rank? MinRank, string Owner, int DaysAgo, (SlideLayout Layout, string Title, string? Body, string? Notes)[] Slides)[] specs =
        [
            ("Lagebild Hafen", "Stand der Ermittlungen zum Waffenschmuggel für die Wochenbesprechung.", PresentationAccess.Public, null, Habicht, 4,
            [
                (SlideLayout.Title, "Lagebild Hafen", "<p>Taskforce Hafen, Stand dieser Woche</p>", "Kurz begrüßen, Ablauf nennen."),
                (SlideLayout.TitleText, "Was wir wissen",
                    "<ul><li>Zwei Lieferungen über Terminal Nord bestätigt</li><li>Empfänger: Los Santos Vagos</li><li>Kontaktmann: Ricardo Mendez</li></ul>",
                    "Die Chats aus dem Handy erwähnen."),
                (SlideLayout.TitleText, "Zugriff Lagerhalle",
                    "<p>Sechs Festnahmen, 14 Faustfeuerwaffen und 320.000 $ sichergestellt. Keine Verletzten.</p>", null),
                (SlideLayout.TitleText, "Nächste Schritte",
                    "<ol><li>Observation Terminal Nord verlängern</li><li>Zeugenvernehmung Hafenarbeiter</li><li>Zugriff bei der dritten Lieferung</li></ol>",
                    "Freiwillige für die Nachtschicht suchen."),
            ]),
            ("Einarbeitung neuer Trainees", "Die ersten Wochen im FIB: Akten, Funk, Verschlusssachen.", PresentationAccess.Public, null, Fuchs, 12,
            [
                (SlideLayout.Title, "Willkommen im FIB", "<p>Einarbeitung für Trainees</p>", null),
                (SlideLayout.Section, "Akten", null, null),
                (SlideLayout.TitleText, "Jede Person hat eine Akte",
                    "<p>Alles zu einer Person läuft in ihrer Akte zusammen: Doks, Observationen, Verknüpfungen und Kommentare.</p>", null),
                (SlideLayout.Section, "Funk", null, null),
                (SlideLayout.TitleText, "Funkdisziplin",
                    "<p>Kurz und sachlich. Keine Namen, Adressen oder Kennzeichen im offenen Funk.</p>", "Auf die Ermahnung von neulich hinweisen, ohne Namen."),
            ]),
            ("Personalplanung Führungsrunde", "Bedarf je Abteilung für das nächste Quartal.", PresentationAccess.Closed, Rank.SectionChief, Greif, 2,
            [
                (SlideLayout.Title, "Personalplanung", "<p>Führungsrunde</p>", null),
                (SlideLayout.TitleText, "Bedarf",
                    "<table><tbody><tr><td><p><strong>CID</strong></p></td><td><p>+3</p></td></tr><tr><td><p><strong>CTD</strong></p></td><td><p>+2</p></td></tr>"
                    + "<tr><td><p><strong>HRT</strong></p></td><td><p>+1</p></td></tr></tbody></table>", null),
            ]),
        ];
        var known = (await db.Presentations.IgnoreQueryFilters().Select(p => p.Title).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (title, description, access, minRank, owner, daysAgo, slides) in specs)
        {
            if (!known.Add(title))
            {
                continue;
            }
            var presentation = new Presentation
            {
                Title = title,
                Description = description,
                Access = access,
                Audience = access == PresentationAccess.Closed ? AnnouncementAudience.FromRank : null,
                MinRank = minRank,
                OwnerId = owner,
            };
            db.Presentations.Add(presentation);
            restamps.Add(new Restamp(typeof(Presentation), presentation.Id, DateTime.UtcNow.AddDays(-daysAgo), owner));
            added++;
            var order = 0;
            foreach (var (layout, slideTitle, body, notes) in slides)
            {
                db.PresentationSlides.Add(new PresentationSlide
                {
                    PresentationId = presentation.Id,
                    Sorting = order++,
                    Layout = layout,
                    Title = slideTitle,
                    BodyHtml = body,
                    Notes = notes,
                });
                added++;
            }
        }
        return added;
    }

    /// <summary>Seed proceedings, transfers, references, inquiries and proposals.</summary>
    private async Task<int> SeedAdministrationAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        var added = 0;

        if (!await db.DisciplinaryProceedings.IgnoreQueryFilters().AnyAsync(ct))
        {
            (string Agent, DisciplinaryStatus Status, DisciplinaryMeasure? Measure, int DaysAgo, string Grounds, string? Facts, string? Reasoning)[] proceedings =
            [
                (Marder, DisciplinaryStatus.Open, null, 5,
                    "Wiederholte Verstöße gegen die Funkdisziplin und unentschuldigtes Fernbleiben vom Dienst seit fast drei Wochen.",
                    "Am 12. des Monats nannte Marder im offenen Funk den Klarnamen einer Zielperson. Seitdem keine Rückmeldung.", null),
                (Baer, DisciplinaryStatus.Decided, DisciplinaryMeasure.Reprimand, 16,
                    "Warnschuss auf der Route 68 erst am Folgetag gemeldet.",
                    "Der Warnschuss wurde nicht sofort, sondern erst am nächsten Morgen gemeldet.",
                    "Der Schuss selbst war gerechtfertigt. Die verspätete Meldung verstößt gegen die Meldepflicht, eine Rüge genügt."),
            ];
            foreach (var (agent, status, measure, daysAgo, grounds, facts, reasoning) in proceedings)
            {
                var proceeding = new DisciplinaryProceeding
                {
                    CaseNumber = await caseNumbers.NextAsync(db, Administration.DisciplinaryService.CaseNumberPrefix, ct),
                    AgentId = agent,
                    Simplified = measure == DisciplinaryMeasure.Reprimand,
                    Status = status,
                    Grounds = grounds,
                    OpenedAt = DateTime.UtcNow.AddDays(-daysAgo),
                    Measure = measure,
                    Facts = facts,
                    Reasoning = reasoning,
                    ClosedAt = status == DisciplinaryStatus.Decided ? DateTime.UtcNow.AddDays(-daysAgo + 4) : null,
                    DecidedByName = status == DisciplinaryStatus.Decided ? "Kranich" : null,
                    RankAtClose = status == DisciplinaryStatus.Decided ? Rank.SeniorSpecialAgent : null,
                };
                db.DisciplinaryProceedings.Add(proceeding);
                restamps.Add(new Restamp(typeof(DisciplinaryProceeding), proceeding.Id, proceeding.OpenedAt, Kranich));
                added++;
            }
        }

        if (!await db.TransferRequests.IgnoreQueryFilters().AnyAsync(ct))
        {
            (string Agent, Division From, Division To, TransferRequestStatus Status, int DaysAgo, string Reason, string? Decision)[] transfers =
            [
                (Eule, Division.Ctd, Division.Cid, TransferRequestStatus.Open, 3,
                    "Ich möchte meine Erfahrung aus den Finanzermittlungen in der CID einbringen.", null),
                (Dachs, Division.Hrt, Division.Cid, TransferRequestStatus.Refused, 30,
                    "Wunsch nach mehr Ermittlungsarbeit.", "Abgelehnt, das HRT ist mit drei Agenten unterbesetzt. In drei Monaten erneut prüfen."),
            ];
            foreach (var (agent, from, to, status, daysAgo, reason, decision) in transfers)
            {
                var transfer = new TransferRequest
                {
                    AgentId = agent,
                    FromDivision = from,
                    ToDivision = to,
                    Reason = reason,
                    Status = status,
                    RequesterName = CodenameOf(agent),
                    DecisionNote = decision,
                    DecidedByName = decision is null ? null : "Greif",
                    DecidedAt = decision is null ? null : DateTime.UtcNow.AddDays(-daysAgo + 5),
                };
                db.TransferRequests.Add(transfer);
                restamps.Add(new Restamp(typeof(TransferRequest), transfer.Id, DateTime.UtcNow.AddDays(-daysAgo), agent));
                added++;
            }
        }

        if (!await db.ServiceReferences.IgnoreQueryFilters().AnyAsync(ct))
        {
            var reference = new ServiceReference
            {
                AgentId = Moewe,
                Reason = ServiceReferenceReason.Leaving,
                Grade = EvaluationGrade.Good,
                TextHtml = "<p>Herr Fielding war sieben Monate als Special Agent in der CID tätig. Er arbeitete zuverlässig und gründlich, "
                    + "seine Berichte waren stets vollständig.</p><p>Er verlässt das FIB auf eigenen Wunsch. Wir wünschen ihm für seinen weiteren Weg alles Gute.</p>",
                IssuedOn = DateTime.UtcNow.Date.AddDays(-37),
                IssuedByName = "Falke",
                RankSnapshot = Rank.SpecialAgent,
                DivisionSnapshot = Division.Cid,
                ServiceStart = DateTime.UtcNow.Date.AddDays(-230),
            };
            db.ServiceReferences.Add(reference);
            restamps.Add(new Restamp(typeof(ServiceReference), reference.Id, DateTime.UtcNow.AddDays(-37), Falke));
            added++;
        }

        if (!await db.ParliamentaryInquiries.IgnoreQueryFilters().AnyAsync(ct))
        {
            (string Title, string Question, string Submitter, int DaysAgo, int DueDays, ParliamentaryInquiryStatus Status, string? Assigned, string? Answer)[] inquiries =
            [
                ("Einsatz von Wahrheitsserum", "Wie oft hat das FIB im letzten Quartal Wahrheitsserum eingesetzt, und wer ordnet den Einsatz an?",
                    "Abg. R. Delgado", 4, 10, ParliamentaryInquiryStatus.InProgress, Fuchs, null),
                ("Kosten der Taskforce Hafen", "Welche Kosten sind durch die Taskforce Hafen bisher entstanden?",
                    "Abg. T. Whitmore", 26, 14, ParliamentaryInquiryStatus.Answered, Kranich,
                    "Die Taskforce verursachte bisher Sachkosten von 28.200 $. Personalkosten entstehen nicht gesondert."),
                ("Personalstärke des FIB", "Wie viele Agenten beschäftigt das FIB derzeit je Abteilung?",
                    "Abg. R. Delgado", 1, 14, ParliamentaryInquiryStatus.Received, null, null),
            ];
            foreach (var (title, question, submitter, daysAgo, dueDays, status, assigned, answer) in inquiries)
            {
                var received = DateTime.UtcNow.AddDays(-daysAgo);
                var inquiry = new ParliamentaryInquiry
                {
                    CaseNumber = await caseNumbers.NextAsync(db, Administration.ParliamentService.CaseNumberPrefix, ct),
                    Title = title,
                    Question = question,
                    SubmitterName = submitter,
                    FromParliament = true,
                    ReceivedAt = received,
                    DueAt = received.AddDays(dueDays),
                    AssignedToId = assigned,
                    Status = status,
                    Answer = answer,
                    AnsweredByName = answer is null ? null : "Kranich",
                    AnsweredAt = answer is null ? null : received.AddDays(dueDays - 3),
                };
                db.ParliamentaryInquiries.Add(inquiry);
                restamps.Add(new Restamp(typeof(ParliamentaryInquiry), inquiry.Id, received, Falke));
                added++;
            }
        }

        if (!await db.DecorationProposals.IgnoreQueryFilters().AnyAsync(ct)
            && await db.Decorations.Where(d => d.IsActive).OrderBy(d => d.Order).Select(d => d.Id).FirstOrDefaultAsync(ct) is { } decorationId)
        {
            (string Agent, string Reason, string Proposer, int DaysAgo)[] proposals =
            [
                (Dachs, "Hat beim Zugriff im Hafenviertel einen verletzten Kollegen aus der Schusslinie gezogen.", "Wolf", 5),
                (Eule, "Ihre Auswertung der Konten hat die Ermittlungen gegen das Vinewood Syndikat entscheidend vorangebracht.", "Luchs", 9),
            ];
            foreach (var (agent, reason, proposer, daysAgo) in proposals)
            {
                var proposal = new DecorationProposal
                {
                    AgentId = agent,
                    DecorationId = decorationId,
                    Reason = reason,
                    ProposerName = proposer,
                };
                db.DecorationProposals.Add(proposal);
                restamps.Add(new Restamp(typeof(DecorationProposal), proposal.Id, DateTime.UtcNow.AddDays(-daysAgo), Colleague(proposer)));
                added++;
            }
        }
        return added;
    }

    /// <summary>Seed classification steps and weekly threat scores.</summary>
    private static async Task<int> SeedRecordHistoryAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions, CancellationToken ct)
    {
        var classified = (await db.ClassificationHistory.Select(h => h.EntityId).Distinct().ToListAsync(ct)).ToHashSet();
        var scored = (await db.ThreatScoreHistory.Select(h => h.EntityId).Distinct().ToListAsync(ct)).ToHashSet();
        var added = 0;

        var records = factions.Values.Select(f => (Type: nameof(Faction), f.Id, f.Classification, f.ThreatScore))
            .Concat(people.Values.Select(p => (Type: nameof(Person), p.Id, p.Classification, p.ThreatScore)));
        foreach (var (type, id, classification, score) in records)
        {
            if (classification != Classification.Unknown && classified.Add(id))
            {
                var steps = (int)classification;
                for (var step = 1; step <= steps; step++)
                {
                    db.ClassificationHistory.Add(new ClassificationHistory
                    {
                        EntityType = type,
                        EntityId = id,
                        Value = (Classification)step,
                        Justification = step == 1 ? "Erste Erkenntnisse aus Hinweisen und Observation."
                            : step == 2 ? "Mehrere unabhängige Quellen bestätigen den Verdacht."
                            : "Bewaffnete, organisierte Strukturen nachgewiesen.",
                        Timestamp = DateTime.UtcNow.AddDays(-150 + step * 35),
                        AgentId = step == 3 ? Falke : Kranich,
                        AgentName = step == 3 ? "Falke" : "Kranich",
                    });
                    added++;
                }
            }
            if (score is int current && scored.Add(id))
            {
                // climbs to today
                for (var week = 8; week >= 1; week--)
                {
                    db.ThreatScoreHistory.Add(new ThreatScoreHistory
                    {
                        EntityType = type,
                        EntityId = id,
                        Score = Math.Clamp(current - week * 3 + (week % 3) * 2, 0, 100),
                        Confidence = 60 + week,
                        Timestamp = DateTime.UtcNow.AddDays(-week * 7),
                    });
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>Seed meeting measures pointing at seeded jobs.</summary>
    private static async Task<int> SeedMeetingMeasuresAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.MeetingMeasures.AnyAsync(ct))
        {
            return 0;
        }
        (string Agenda, string Job)[] specs =
        [
            ("Stand Hafenobservation", "Mobiltelefon von Ricardo Mendez auswerten"),
            ("Auswertung Bürgerhinweise", "Zeugenvernehmung Hafenarbeiter vorbereiten"),
            ("Rückblick Zugriff Hafenviertel", "Durchsuchungsbeschluss für das Clubhaus beantragen"),
        ];
        var agenda = db.MeetingAgendaItems.Local.Concat(await db.MeetingAgendaItems.ToListAsync(ct)).DistinctBy(a => a.Id).ToList();
        var jobs = db.Jobs.Local.Concat(await db.Jobs.ToListAsync(ct)).DistinctBy(j => j.Id).ToList();
        var added = 0;
        foreach (var (agendaTitle, jobTitle) in specs)
        {
            var item = agenda.FirstOrDefault(a => a.Title == agendaTitle);
            var job = jobs.FirstOrDefault(j => j.Title == jobTitle);
            if (item is null || job is null)
            {
                continue;
            }
            db.MeetingMeasures.Add(new MeetingMeasure { AgendaItemId = item.Id, Kind = MeetingMeasureKind.Aufgabe, TargetId = job.Id });
            added++;
        }
        return added;
    }
}
