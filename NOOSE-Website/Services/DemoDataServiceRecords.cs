using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Demo cases, operations, parties, groups and taskforces.</summary>
public partial class DemoDataService
{
    /// <summary>Seeded records by title for later links.</summary>
    private sealed class DemoRecords
    {
        public Dictionary<string, Case> Cases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Operation> Operations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Taskforce> Taskforces { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Party> Parties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PersonGroup> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creation stamp to restore after the save.</summary>
    private sealed record Restamp(Type Entity, string Id, DateTime CreatedAt, string CreatedById);

    private const string CaseHarbour = "Waffenschmuggel über den Containerhafen";
    private const string CaseLaundering = "Geldwäsche im Umfeld des Vinewood Syndikats";
    private const string OperationRaid = "Zugriff Lagerhalle Elysian Island";
    private const string OperationWatch = "Observation Hafenterminal Nord";
    private const string TaskforceHarbour = "Taskforce Hafen";
    private const string TaskforceCartels = "Taskforce Kartelle";

    private sealed record CaseSpec(
        string Title, string Type, CaseStatus Status, Classification Classification, bool Classified, int CreatedDaysAgo,
        string Description, string? Summary, string? ClosingNote, (string Agent, bool Lead)[] Agents,
        string[] People, string[] Factions);

    private static readonly CaseSpec[] CaseSpecs =
    [
        new(CaseHarbour, "Ermittlungsverfahren", CaseStatus.InProcessing, Classification.SuspicionCase, false, 41,
            "Über den Containerhafen kommen seit dem Sommer Waffen in die Stadt. Die Spur führt über zwei Spediteure zu den Los Santos Vagos.",
            "Zwei Lieferungen bestätigt, eine dritte wird für die kommende Woche erwartet. Observation am Terminal Nord läuft.",
            null, [(Habicht, true), (Fuchs, false), (DemoIdentity.AgentId, false), (Specht, false)],
            ["Ricardo Mendez", "Hector Rodriguez", "Pablo Cruz"], ["Los Santos Vagos", "Marabunta Grande"]),
        new(CaseLaundering, "Finanzermittlung", CaseStatus.Open, Classification.SecuredStateThreatening, true, 66,
            "Das Syndikat wäscht Geld über Filmproduktionen und zwei Nachtclubs. Kontobewegungen werden mit dem DoJ ausgewertet.",
            "Belastbare Zahlungen über 2,4 Mio. $ nachgewiesen, Zugriff auf die Buchhaltung steht aus.",
            null, [(Kranich, true), (Eule, false), (Luchs, false)],
            ["Devin Weston", "Solomon Richards"], ["Vinewood Syndikat"]),
        new("Revierkonflikt Grove Street und Ballas", "Gefahrenabwehr", CaseStatus.Dormant, Classification.ReviewCase, false, 95,
            "Wiederkehrende Schießereien zwischen den Grove Street Families und den Ballas im Süden der Stadt.",
            "Seit drei Wochen ruhig. Wiedervorlage, sobald es neue Vorfälle gibt.",
            null, [(Marder, true), (Habicht, false)],
            ["Lamar Davis", "Kane Williams", "Tyrell Banks"], ["Grove Street Families", "Ballas"]),
        new("Überfall auf die Fleeca-Bank an der Route 68", "Ermittlungsverfahren", CaseStatus.Completed, Classification.SuspicionCase, false, 120,
            "Bewaffneter Überfall durch drei Täter, Fluchtfahrzeug später in Sandy Shores aufgefunden.",
            "Zwei Täter ermittelt und festgenommen, der dritte ist zur Fahndung ausgeschrieben.",
            "Abgeschlossen mit Übergabe an das DoJ. Offene Fahndung läuft über die Personenakte weiter.",
            [(Habicht, true), (Baer, false)],
            ["Vinnie Moretti", "Terry Marsh"], ["The Lost MC"]),
        new("Schutzgelderpressung in Little Seoul", "Ermittlungsverfahren", CaseStatus.InProcessing, Classification.SuspicionCase, false, 52,
            "Mehrere Geschäfte in Little Seoul zahlen wöchentlich an zwei konkurrierende Gruppen. Wer nicht zahlt, dem wird das Schaufenster eingeworfen.",
            "Vier Geschädigte haben ausgesagt, zwei davon nur anonym. Die Übergaben laufen immer freitags.",
            null, [(Luchs, true), (Eule, false), (Reiher, false)],
            ["Kenji Tanaka", "Haruto Sato", "Wei Cheng", "Lin Cheng"], ["Yakuza Kanto-kai", "Triaden Wah Ching"]),
        new("Menschenhandel über Paleto Bay", "Ermittlungsverfahren", CaseStatus.Open, Classification.SecuredStateThreatening, true, 28,
            "Hinweise auf Menschen, die nachts in Paleto Bay von Booten in Lieferwagen umgeladen werden. Mutmaßlich organisiert die Sinaloa Connection die Transporte.",
            "Zwei Fahrzeuge identifiziert, die Halter sind Strohleute. Observation am Pier wird vorbereitet.",
            null, [(Kranich, true), (Greif, false), (Eule, false)],
            ["Joaquin Reyes", "Camila Reyes"], ["Sinaloa Connection"]),
        new("Brandanschlag auf die Werkstatt in Sandy Shores", "Ermittlungsverfahren", CaseStatus.InProcessing, Classification.ReviewCase, false, 19,
            "In der Nacht brannte die Werkstatt eines Zeugen aus dem Fleeca-Verfahren. Brandbeschleuniger nachgewiesen.",
            "Zwei Mitglieder von The Lost MC wurden kurz vorher in der Nähe gesehen. Alibis werden geprüft.",
            null, [(Wolf, true), (Baer, false)],
            ["Johnny Klebitz", "Clay Simons"], ["The Lost MC", "Rotten Brotherhood MC"]),
        new("Waffenlieferungen an Merryweather", "Vorermittlung", CaseStatus.Open, Classification.ReviewCase, false, 9,
            "Ein Hafenarbeiter berichtet, dass Kisten mit Merryweather-Aufdruck in denselben Containern lagen wie die Waffen der Vagos.",
            null, null, [(Greif, true), (Habicht, false)],
            ["Don Percival"], ["Merryweather Security"]),
        new("Tötungsdelikt Brad Snider", "Ermittlungsverfahren", CaseStatus.Completed, Classification.SuspicionCase, false, 160,
            "Snider wurde in North Yankton tot aufgefunden. Spuren führten zu einem früheren Komplizen.",
            "Tatverdächtiger ermittelt, Beweislage an das DoJ übergeben.",
            "Abgeschlossen. Das Verfahren liegt beim DoJ, die Personenakte bleibt offen.",
            [(Habicht, true), (Fuchs, false)],
            ["Brad Snider", "Trevor Philips"], []),
    ];

    private async Task<int> SeedCasesAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions,
        DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        foreach (var existing in await db.Cases.IgnoreQueryFilters().ToListAsync(ct))
        {
            records.Cases.TryAdd(existing.Title, existing);
        }
        var added = 0;
        foreach (var spec in CaseSpecs)
        {
            if (records.Cases.ContainsKey(spec.Title))
            {
                continue;
            }
            var item = new Case
            {
                CaseNumber = await caseNumbers.NextAsync(db, "V", ct),
                Title = spec.Title,
                Type = spec.Type,
                Status = spec.Status,
                Classification = spec.Classification,
                IsClassified = spec.Classified,
                Description = spec.Description,
                Summary = spec.Summary,
                ClosingNote = spec.ClosingNote,
                CompletedAt = spec.Status == CaseStatus.Completed ? DateTime.UtcNow.AddDays(-20) : null,
            };
            foreach (var (agent, lead) in spec.Agents)
            {
                item.Agents.Add(new CaseAgent { AgentId = agent, IsCaseLead = lead });
            }
            db.Cases.Add(item);
            records.Cases[spec.Title] = item;
            var lead0 = spec.Agents.First(a => a.Lead).Agent;
            restamps.Add(new Restamp(typeof(Case), item.Id, DateTime.UtcNow.AddDays(-spec.CreatedDaysAgo), lead0));
            added += 1 + item.Agents.Count;
            added += AddLinks(db, nameof(Case), item.Id, spec.People.Select(p => people.GetValueOrDefault(p)?.Id), nameof(Person));
            added += AddLinks(db, nameof(Case), item.Id, spec.Factions.Select(f => factions.GetValueOrDefault(f)?.Id), nameof(Faction));
        }
        return added;
    }

    private static int AddLinks(AppDbContext db, string sourceType, string sourceId, IEnumerable<string?> targetIds, string targetType, string? label = null)
    {
        var added = 0;
        foreach (var id in targetIds.Where(id => id is not null).Distinct())
        {
            db.Links.Add(new Link
            {
                SourceType = sourceType,
                SourceId = sourceId,
                TargetType = targetType,
                TargetId = id!,
                Label = label,
            });
            added++;
        }
        return added;
    }

    private sealed record OperationSpec(
        string Title, string Type, OperationStatus Status, int StartOffsetDays, int? DurationHours, string Location,
        string? Legitimation, string? Agencies, string? Result, string? Evidence, (string Agent, bool Lead)[] Agents,
        (OperationLogKind Kind, int MinutesAfterStart, string Author, string Text)[] Log, string? Case, string[] Factions);

    private static readonly OperationSpec[] OperationSpecs =
    [
        new(OperationRaid, "Zugriff", OperationStatus.Completed, -24, 5, "Lagerhalle, Elysian Island",
            "Durchsuchungsbeschluss des Bezirksgerichts", "LSPD (Absperrung), LSMD (Bereitschaft)",
            "Sechs Festnahmen, 14 Faustfeuerwaffen und 320.000 $ Bargeld sichergestellt. Keine Verletzten auf eigener Seite.",
            "Waffen und Bargeld in der Asservatenkammer, Mobiltelefone bei der Technik.",
            [(Wolf, true), (Baer, false), (Dachs, false), (Habicht, false)],
            [
                (OperationLogKind.Situation, 0, "Wolf", "Kräfte in Bereitstellung an der Zufahrt Nord. LSPD sperrt die Hafenstraße."),
                (OperationLogKind.Request, 12, "Habicht", "Bitte um Freigabe für den Zugriff, Zielpersonen im Gebäude bestätigt."),
                (OperationLogKind.Situation, 15, "Wolf", "Zugriff erfolgt über zwei Eingänge."),
                (OperationLogKind.Result, 31, "Bär", "Gebäude gesichert, sechs Personen festgenommen."),
                (OperationLogKind.Result, 140, "Habicht", "Durchsuchung abgeschlossen, Funde an die Asservatenkammer übergeben."),
            ], CaseHarbour, ["Los Santos Vagos"]),
        new(OperationWatch, "Observation", OperationStatus.Running, -3, null, "Hafenterminal Nord, Kran 4",
            "Anordnung der Ermittlungsführung", null, null, null,
            [(Fuchs, true), (Specht, false), (DemoIdentity.AgentId, false)],
            [
                (OperationLogKind.Situation, 0, "Fuchs", "Beobachtungsposten im Bürocontainer bezogen."),
                (OperationLogKind.Situation, 1500, "Specht", "Weißer Lieferwagen ohne Kennzeichen fährt zweimal am Tor vorbei."),
                (OperationLogKind.Request, 2900, "Fuchs", "Bitte um Ablösung für die Nacht, zwei Agenten."),
            ], CaseHarbour, ["Los Santos Vagos"]),
        new("Durchsuchung Clubhaus The Lost MC", "Durchsuchung", OperationStatus.Planned, 4, null, "Clubhaus, Stab City",
            "Durchsuchungsbeschluss beantragt", "LSPD", null, null,
            [(Wolf, true), (Baer, false), (Dachs, false), (DemoIdentity.AgentId, false)],
            [], null, ["The Lost MC"]),
        new("Zugriff Autowerkstatt Strawberry", "Zugriff", OperationStatus.Completed, -12, 3, "Autowerkstatt, Strawberry Ave",
            "Haftbefehl gegen zwei Mitglieder der Ballas", "LSPD (Absperrung)",
            "Zwei Festnahmen, ein gestohlenes Fahrzeug sichergestellt. Ein Täter flüchtete zu Fuß und wurde zwei Straßen weiter gestellt.",
            "Fahrzeug an das LSPD übergeben.",
            [(Wolf, true), (Dachs, false), (Baer, false), (Marder, false)],
            [
                (OperationLogKind.Situation, 0, "Wolf", "Zwei Teams an Vorder- und Hinterausgang."),
                (OperationLogKind.Situation, 9, "Dachs", "Ein Täter flüchtet über den Hof Richtung Süden."),
                (OperationLogKind.Result, 14, "Dachs", "Flüchtiger an der Ecke Davis Ave gestellt."),
                (OperationLogKind.Result, 45, "Wolf", "Lage beendet, beide Festgenommenen auf dem Weg ins HQ."),
            ], null, ["Ballas"]),
        new("Verdeckte Ermittlung Club Bahama Mamas", "Verdeckte Ermittlung", OperationStatus.Running, -6, null, "Bahama Mamas, Del Perro",
            "Anordnung der Führung", null, null, null,
            [(Luchs, true), (Eule, false)],
            [
                (OperationLogKind.Situation, 0, "Luchs", "Eule ist als Gast im Club, Funkkontakt steht."),
                (OperationLogKind.Situation, 1600, "Eule", "Der Geschäftsführer trifft sich jeden Abend mit zwei Männern aus dem Umfeld von Weston."),
                (OperationLogKind.Request, 4400, "Luchs", "Bitte um Freigabe einer Kamera im Hinterzimmer."),
            ], CaseLaundering, ["Vinewood Syndikat"]),
        new("Festnahme Joaquin Reyes", "Festnahme", OperationStatus.Aborted, -30, 2, "Lagerhaus, Paleto Bay",
            "Haftbefehl des Bezirksgerichts", "LSPD",
            "Abgebrochen, die Zielperson war nicht vor Ort. Vermutlich wurde sie gewarnt.", null,
            [(Wolf, true), (Dachs, false), (Kranich, false)],
            [
                (OperationLogKind.Situation, 0, "Wolf", "Lagerhaus umstellt, keine Bewegung im Gebäude."),
                (OperationLogKind.Result, 35, "Wolf", "Gebäude leer. Einsatz abgebrochen, Spurensicherung angefordert."),
            ], "Menschenhandel über Paleto Bay", ["Sinaloa Connection"]),
        new("Sicherung der Hauptverhandlung Moretti", "Objektschutz", OperationStatus.Planned, 6, null, "Bezirksgericht Los Santos",
            "Ersuchen des DoJ", "LSPD", null, null,
            [(Baer, true), (Dachs, false), (Reiher, false)],
            [], "Überfall auf die Fleeca-Bank an der Route 68", ["The Lost MC"]),
    ];

    private async Task<int> SeedOperationsAsync(
        AppDbContext db, Dictionary<string, Faction> factions, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        foreach (var existing in await db.Operations.IgnoreQueryFilters().ToListAsync(ct))
        {
            records.Operations.TryAdd(existing.Title, existing);
        }
        var added = 0;
        foreach (var spec in OperationSpecs)
        {
            if (records.Operations.ContainsKey(spec.Title))
            {
                continue;
            }
            var start = DateTime.UtcNow.Date.AddDays(spec.StartOffsetDays).AddHours(20);
            var operation = new Operation
            {
                CaseNumber = await caseNumbers.NextAsync(db, "OP", ct),
                Title = spec.Title,
                Type = spec.Type,
                Status = spec.Status,
                Location = spec.Location,
                Start = start,
                End = spec.DurationHours is int hours ? start.AddHours(hours) : null,
                Legitimation = spec.Legitimation,
                InvolvedAgencies = spec.Agencies,
                Result = spec.Result,
                Evidence = spec.Evidence,
                Classification = Classification.SuspicionCase,
            };
            foreach (var (agent, lead) in spec.Agents)
            {
                operation.Agents.Add(new OperationAgent { AgentId = agent, IsInvestigationLead = lead });
            }
            db.Operations.Add(operation);
            records.Operations[spec.Title] = operation;
            var lead0 = spec.Agents.First(a => a.Lead).Agent;
            restamps.Add(new Restamp(typeof(Operation), operation.Id, start.AddDays(-2), lead0));
            added += 1 + operation.Agents.Count;

            foreach (var (kind, minutes, author, text) in spec.Log)
            {
                var entry = new OperationLogEntry
                {
                    OperationId = operation.Id,
                    Kind = kind,
                    Text = text,
                    Location = spec.Location,
                    AuthorName = author,
                };
                db.OperationLogEntries.Add(entry);
                restamps.Add(new Restamp(typeof(OperationLogEntry), entry.Id, start.AddMinutes(minutes), Colleague(author)));
                added++;
            }
            if (spec.Case is not null && records.Cases.TryGetValue(spec.Case, out var item))
            {
                added += AddLinks(db, nameof(Operation), operation.Id, [item.Id], nameof(Case));
            }
            added += AddLinks(db, nameof(Operation), operation.Id, spec.Factions.Select(f => factions.GetValueOrDefault(f)?.Id), nameof(Faction));
        }
        return added;
    }

    private async Task<int> SeedPartiesAndGroupsAsync(
        AppDbContext db, Dictionary<string, Person> people, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        foreach (var existing in await db.Parties.IgnoreQueryFilters().ToListAsync(ct))
        {
            records.Parties.TryAdd(existing.Name, existing);
        }
        foreach (var existing in await db.PersonGroups.IgnoreQueryFilters().ToListAsync(ct))
        {
            records.Groups.TryAdd(existing.Name, existing);
        }
        var added = 0;

        (string Name, Classification Classification, int DaysAgo, string Description, string Targets, string Lead, (string Person, string Role, bool IsLead)[] Members)[] parties =
        [
            ("Bewegung Neues San Andreas", Classification.ReviewCase, 58,
                "Kleine Partei mit lautem Auftritt, wirbt im Umfeld von Söldnerfirmen um Mitglieder.",
                "Private Sicherheitsdienste sollen polizeiliche Aufgaben übernehmen.", Luchs,
                [("Don Percival", "Vorsitzender", true), ("Frank Doyle", "Schatzmeister", false)]),
            ("Liste Vinewood Zukunft", Classification.SuspicionCase, 33,
                "Lokale Liste für die Stadtratswahl, Spenden stammen zum Teil aus dem Umfeld des Vinewood Syndikats.",
                "Lockerung der Auflagen für Clubs und Filmproduktionen.", Kranich,
                [("Solomon Richards", "Spitzenkandidat", true), ("Patricia Madrazo", "Unterstützerin", false)]),
            ("Bürgerliste Sandy Shores", Classification.ReviewCase, 14,
                "Lokale Liste im Blaine County, mehrere Kandidaten stehen dem Umfeld der Bikergangs nahe.",
                "Weniger Polizeipräsenz im Blaine County.", Wolf,
                [("Terry Marsh", "Kandidat", false), ("Frank Doyle", "Vorsitzender", true)]),
        ];
        foreach (var (name, classification, daysAgo, description, targets, lead, members) in parties)
        {
            if (records.Parties.ContainsKey(name))
            {
                continue;
            }
            var party = new Party
            {
                CaseNumber = await caseNumbers.NextAsync(db, "PT", ct),
                Name = name,
                Classification = classification,
                Description = description,
                Targets = targets,
            };
            party.Agents.Add(new PartyAgent { AgentId = lead, IsInvestigationLead = true });
            foreach (var (person, role, isLead) in members)
            {
                if (people.TryGetValue(person, out var p))
                {
                    party.Members.Add(new PartyMember { PersonId = p.Id, Role = role, IsLead = isLead });
                }
            }
            db.Parties.Add(party);
            records.Parties[name] = party;
            restamps.Add(new Restamp(typeof(Party), party.Id, DateTime.UtcNow.AddDays(-daysAgo), lead));
            added += 1 + party.Agents.Count + party.Members.Count;
        }

        (string Name, GroupsKind Kind, Classification Classification, int? Estimated, int DaysAgo, string Description, string Lead, (string Person, string Role, bool IsLead)[] Members)[] groups =
        [
            ("Hafenarbeiter-Netzwerk Terminal Nord", GroupsKind.Grouping, Classification.SuspicionCase, 8, 39,
                "Hafenarbeiter, die Container für die Vagos am Zoll vorbeischleusen.", Habicht,
                [("Ricardo Mendez", "Kontaktmann", true), ("Pablo Cruz", "Kranführer", false)]),
            ("Schutzgeld-Eintreiber Little Seoul", GroupsKind.Grouping, Classification.SuspicionCase, 5, 52,
                "Lose Gruppe, die Geschäfte in Little Seoul unter Druck setzt.", Luchs,
                [("Kenji Tanaka", "Anführer", true), ("Haruto Sato", "Eintreiber", false), ("Wei Cheng", "Mittelsmann", false)]),
            ("Umfeld Lester Crest", GroupsKind.PersonOfInterest, Classification.ReviewCase, null, 17,
                "Technischer Unterstützer mehrerer Gruppierungen, beschafft Pläne und Zugangsdaten.", Eule,
                [("Lester Crest", "Zielperson", true), ("Michael De Santa", "Kontakt", false)]),
            ("Familie Rodriguez", GroupsKind.Grouping, Classification.SuspicionCase, 6, 70,
                "Die Familie hält die Führung der Los Santos Vagos. Geschäfte laufen über Verwandte und deren Konten.", Habicht,
                [("Hector Rodriguez", "Bruder", true), ("Miguel Rodriguez", "Bruder", false), ("Maria Rodriguez", "Schwester", false), ("Sofia Rodriguez", "Cousine", false)]),
            ("Fluchthelfer Sandy Shores", GroupsKind.Grouping, Classification.ReviewCase, 3, 21,
                "Bringen Gesuchte über die Wüste nach Norden, gegen Bargeld.", Wolf,
                [("Terry Marsh", "Fahrer", true), ("Clay Simons", "Unterkunft", false)]),
        ];
        foreach (var (name, kind, classification, estimated, daysAgo, description, lead, members) in groups)
        {
            if (records.Groups.ContainsKey(name))
            {
                continue;
            }
            var group = new PersonGroup
            {
                CaseNumber = await caseNumbers.NextAsync(db, "G", ct),
                Name = name,
                Kind = kind,
                Classification = classification,
                EstimatedMemberCount = estimated,
                Description = description,
            };
            group.Agents.Add(new PersonGroupAgent { AgentId = lead, IsInvestigationLead = true });
            foreach (var (person, role, isLead) in members)
            {
                if (people.TryGetValue(person, out var p))
                {
                    group.Members.Add(new PersonGroupMember { PersonId = p.Id, Role = role, IsLead = isLead });
                }
            }
            db.PersonGroups.Add(group);
            records.Groups[name] = group;
            restamps.Add(new Restamp(typeof(PersonGroup), group.Id, DateTime.UtcNow.AddDays(-daysAgo), lead));
            added += 1 + group.Agents.Count + group.Members.Count;
        }
        return added;
    }

    private sealed record TaskforceSpec(
        string Name, string Purpose, TaskforceStatus Status, bool Classified, int CreatedDaysAgo,
        (string Agent, TaskforceRole Role)[] Agents, (int HoursAgo, string Author, string Text)[] Chat);

    private static readonly TaskforceSpec[] TaskforceSpecs =
    [
        new(TaskforceHarbour, "Waffenschmuggel über den Containerhafen aufklären und die Lieferkette zerschlagen.",
            TaskforceStatus.Approved, false, 40,
            [(Habicht, TaskforceRole.LeadInvestigator), (Fuchs, TaskforceRole.CidLead), (Wolf, TaskforceRole.TruLead),
                (Dachs, TaskforceRole.Member), (Specht, TaskforceRole.Member), (DemoIdentity.AgentId, TaskforceRole.Member)],
            [
                (70, "Habicht", "Lagebesprechung morgen um 19 Uhr im Lagezentrum. Bitte alle mit aktuellem Stand."),
                (52, "Fuchs", "Das Mobiltelefon von Mendez ist entsperrt, die Technik wertet die Chats aus."),
                (30, "Wolf", "HRT steht für den Zugriff am Clubhaus bereit, sobald der Beschluss da ist."),
                (9, "Specht", "Am Terminal Nord ist wieder der weiße Lieferwagen aufgetaucht, Fotos liegen im Einsatztagebuch."),
                (2, "Habicht", "Gute Arbeit. Wir verlängern die Observation um drei Tage."),
            ]),
        new(TaskforceCartels, "Strukturen und Geldflüsse der Kartelle Madrazo und Sinaloa erfassen.",
            TaskforceStatus.Approved, true, 75,
            [(Kranich, TaskforceRole.LeadInvestigator), (Luchs, TaskforceRole.CidLead), (Eule, TaskforceRole.Member),
                (DemoIdentity.AgentId, TaskforceRole.Member)],
            [
                (96, "Kranich", "Die Kontoauszüge aus der Sicherstellung sind da. Eule übernimmt die Auswertung."),
                (40, "Eule", "Drei Zahlungen laufen über dieselbe Briefkastenfirma, Bericht folgt bis Freitag."),
                (5, "Luchs", "Bin ab morgen eine Woche weg, Greif vertritt mich."),
            ]),
        new("Taskforce Biker", "Gewalt und Waffenhandel von The Lost MC im Blaine County beenden.",
            TaskforceStatus.Resolved, false, 140,
            [(Wolf, TaskforceRole.LeadInvestigator), (Baer, TaskforceRole.TruLead), (Habicht, TaskforceRole.CidLead), (Dachs, TaskforceRole.Member)],
            [
                (2100, "Wolf", "Die Führungsebene des Clubs sitzt in Haft. Wir lösen die Taskforce zum Monatsende auf."),
                (2050, "Habicht", "Abschlussbericht ist im Dokument der Taskforce, bitte bis Freitag gegenlesen."),
            ]),
        new("Taskforce Nachtleben", "Drogenhandel in den Clubs von Vinewood und Del Perro.",
            TaskforceStatus.Requested, false, 1,
            [(Habicht, TaskforceRole.LeadInvestigator), (Marder, TaskforceRole.Member)],
            []),
    ];

    private async Task<int> SeedTaskforcesAsync(AppDbContext db, DemoRecords records, List<Restamp> restamps, CancellationToken ct)
    {
        foreach (var existing in await db.Taskforces.IgnoreQueryFilters().ToListAsync(ct))
        {
            records.Taskforces.TryAdd(existing.Name, existing);
        }
        var added = 0;
        foreach (var spec in TaskforceSpecs)
        {
            if (records.Taskforces.ContainsKey(spec.Name))
            {
                continue;
            }
            var taskforce = new Taskforce
            {
                CaseNumber = await caseNumbers.NextAsync(db, "TF", ct),
                Name = spec.Name,
                Purpose = spec.Purpose,
                Scope = TaskforceScope.InternalAgency,
                Status = spec.Status,
                IsClassified = spec.Classified,
            };
            foreach (var (agent, role) in spec.Agents)
            {
                // recent ones unread
                taskforce.Agents.Add(new TaskforceAgent
                {
                    AgentId = agent,
                    Role = role,
                    LastReadAt = agent == DemoIdentity.AgentId ? DateTime.UtcNow.AddHours(-6) : DateTime.UtcNow,
                });
            }
            db.Taskforces.Add(taskforce);
            records.Taskforces[spec.Name] = taskforce;
            restamps.Add(new Restamp(typeof(Taskforce), taskforce.Id, DateTime.UtcNow.AddDays(-spec.CreatedDaysAgo), spec.Agents[0].Agent));
            added += 1 + taskforce.Agents.Count;

            foreach (var (hoursAgo, author, text) in spec.Chat)
            {
                var message = new TaskforceMessage { TaskforceId = taskforce.Id, Text = text, AuthorName = author };
                db.TaskforceMessages.Add(message);
                restamps.Add(new Restamp(typeof(TaskforceMessage), message.Id, DateTime.UtcNow.AddHours(-hoursAgo), Colleague(author)));
                added++;
            }
        }

        // taskforce owns case
        if (records.Taskforces.TryGetValue(TaskforceHarbour, out var harbour) && records.Cases.TryGetValue(CaseHarbour, out var harbourCase)
            && !await db.Links.AnyAsync(l => l.SourceId == harbour.Id && l.TargetId == harbourCase.Id, ct))
        {
            added += AddLinks(db, nameof(Taskforce), harbour.Id, [harbourCase.Id], nameof(Case));
        }
        if (records.Taskforces.TryGetValue(TaskforceCartels, out var cartels) && records.Cases.TryGetValue(CaseLaundering, out var launderingCase)
            && !await db.Links.AnyAsync(l => l.SourceId == cartels.Id && l.TargetId == launderingCase.Id, ct))
        {
            added += AddLinks(db, nameof(Taskforce), cartels.Id, [launderingCase.Id], nameof(Case));
        }
        return added;
    }

    /// <summary>Seed faction leads, stock and record keepers.</summary>
    private static async Task<int> SeedFactionDetailsAsync(
        AppDbContext db, Dictionary<string, Person> people, HashSet<string> newPeople, Dictionary<string, Faction> factions, CancellationToken ct)
    {
        var added = 0;
        (string Faction, string Lead, string? Second)[] leads =
        [
            ("Los Santos Vagos", Habicht, Fuchs), ("Ballas", Marder, null), ("Grove Street Families", Marder, Specht),
            ("Cartel Madrazo", Kranich, Eule), ("Sinaloa Connection", Kranich, null), ("Bratva Los Santos", Luchs, Eule),
            ("The Lost MC", Wolf, Baer), ("Vinewood Syndikat", Kranich, Eule), ("Yakuza Kanto-kai", Luchs, null),
            ("Marabunta Grande", Habicht, null),
        ];
        var assigned = (await db.FactionAgents.Select(a => a.FactionId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var (name, lead, second) in leads)
        {
            if (!factions.TryGetValue(name, out var faction) || assigned.Contains(faction.Id))
            {
                continue;
            }
            db.FactionAgents.Add(new FactionAgent { FactionId = faction.Id, AgentId = lead, IsInvestigationLead = true });
            added++;
            if (second is not null)
            {
                db.FactionAgents.Add(new FactionAgent { FactionId = faction.Id, AgentId = second });
                added++;
            }
        }

        (string Faction, (string Item, string Quantity)[] Weapons, (string Item, string Quantity)[] Inventory)[] stocks =
        [
            ("Los Santos Vagos", [("Combat Pistol", "ca. 20"), ("Micro SMG", "8 bis 10")], [("Kokain", "4 kg"), ("Bargeld", "ca. 150.000 $")]),
            ("The Lost MC", [("Sawed-off Shotgun", "6"), ("Pistol .50", "12")], [("Methamphetamin", "2 kg")]),
            ("Cartel Madrazo", [("Carbine Rifle", "über 30"), ("Sticky Bomb", "unbekannt")], [("Kokain", "über 50 kg")]),
            ("Bratva Los Santos", [("Assault Rifle", "15")], [("Gefälschte Pässe", "ca. 40")]),
        ];
        var stocked = (await db.FactionWeaponStocks.Select(w => w.FactionId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var (name, weapons, inventory) in stocks)
        {
            if (!factions.TryGetValue(name, out var faction) || stocked.Contains(faction.Id))
            {
                continue;
            }
            foreach (var (item, quantity) in weapons)
            {
                db.FactionWeaponStocks.Add(new FactionWeaponStock { FactionId = faction.Id, Designation = item, Quantity = quantity });
                added++;
            }
            foreach (var (item, quantity) in inventory)
            {
                db.FactionInventories.Add(new FactionInventory { FactionId = faction.Id, Designation = item, Quantity = quantity });
                added++;
            }
        }

        (string Person, string Keeper, string? Deputy)[] keepers =
        [
            ("Ricardo Mendez", Fuchs, Specht), ("Hector Rodriguez", Habicht, null), ("Devin Weston", Kranich, Eule),
            ("Vinnie Moretti", Habicht, Baer), ("Dimitri Volkov", Luchs, null), ("Johnny Klebitz", Wolf, null),
        ];
        foreach (var (name, keeper, deputy) in keepers)
        {
            if (newPeople.Contains(name) && people.TryGetValue(name, out var person))
            {
                person.KeeperId = keeper;
                person.DeputyId = deputy;
            }
        }
        return added;
    }

    /// <summary>Restore creation stamps after the save.</summary>
    private static async Task ApplyRestampsAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        foreach (var stamp in restamps)
        {
            var entity = db.Model.FindEntityType(stamp.Entity)
                ?? throw new InvalidOperationException($"{stamp.Entity.Name} ist kein Teil des Modells.");
            var table = entity.GetTableName();
            var id = entity.FindProperty("Id")!.GetColumnName();
            var at = entity.FindProperty("CreatedAt")!.GetColumnName();
            var by = entity.FindProperty("CreatedById")!.GetColumnName();
            var sql = $"UPDATE `{table}` SET `{at}` = {{0}}, `{by}` = {{1}} WHERE `{id}` = {{2}}";
            await db.Database.ExecuteSqlRawAsync(sql, [stamp.CreatedAt, stamp.CreatedById, stamp.Id], ct);
        }
    }
}
