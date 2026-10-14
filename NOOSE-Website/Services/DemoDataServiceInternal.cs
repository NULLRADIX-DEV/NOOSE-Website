using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Decorations;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Gamification;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using FeedbackEntry = NOOSE_Website.Data.Entities.Feedback.Feedback;

namespace NOOSE_Website.Services;

/// <summary>Demo data for the internal areas the demo database never had: treasury, evidence, financing, abductions, feedback, informants, meetings, absences and badges.</summary>
public partial class DemoDataService
{
    private const string EvidencePrefix = "ASS";
    private const string FinancingPrefix = "FIN";
    private const string AbductionPrefix = "ENT";
    private const string InformantPrefix = "VP";
    private const string MeetingPrefix = "BS";

    /// <summary>Opening balances first: the ledger folds forward, so a payout may never precede its deposit.</summary>
    private async Task<int> SeedTreasuryAsync(AppDbContext db, CancellationToken ct)
    {
        var added = 0;
        (KassenKonto Account, KassenBuchungArt Kind, decimal Amount, string Reason, int DaysAgo)[] bookings =
        [
            (KassenKonto.Gruengeld, KassenBuchungArt.Einzahlung, 750_000m, "Haushaltszuweisung des Innenministeriums Q3", 120),
            (KassenKonto.Gruengeld, KassenBuchungArt.Auszahlung, 18_400m, "Beschaffung Schutzausrüstung", 96),
            (KassenKonto.Gruengeld, KassenBuchungArt.Auszahlung, 9_800m, "Instandsetzung Dienstfahrzeug", 74),
            (KassenKonto.Gruengeld, KassenBuchungArt.Einzahlung, 250_000m, "Nachtragshaushalt Sonderlage Hafen", 60),
            (KassenKonto.Schwarzgeld, KassenBuchungArt.Einzahlung, 320_000m, "Sicherstellung bei der Razzia Rancho-Lagerhaus", 88),
            (KassenKonto.Schwarzgeld, KassenBuchungArt.Auszahlung, 12_000m, "V-Personen-Honorar", 52),
            (KassenKonto.Schwarzgeld, KassenBuchungArt.Einzahlung, 95_000m, "Sicherstellung Containerhafen", 33),
        ];

        var existing = (await db.KassenBuchungen.IgnoreQueryFilters().Select(b => b.Reason).ToListAsync(ct))
            .Where(r => r is not null).Select(r => r!).ToHashSet(StringComparer.Ordinal);

        foreach (var (account, kind, amount, reason, daysAgo) in bookings)
        {
            if (!existing.Add(reason))
            {
                continue;
            }
            db.KassenBuchungen.Add(new KassenBuchung
            {
                CaseNumber = await caseNumbers.NextAsync(db, KassePrefix, ct),
                Account = account,
                Kind = kind,
                Amount = amount,
                Reason = reason,
                BookedById = DemoIdentity.AgentId,
                Timestamp = DateTime.UtcNow.AddDays(-daysAgo),
            });
            added++;
        }

        (string Name, KassenKonto Account, KassenBuchungArt Kind, decimal Amount, string Reason, int Sort)[] templates =
        [
            ("Nachschub Schutzwesten", KassenKonto.Gruengeld, KassenBuchungArt.Auszahlung, 12_000m, "Beschaffung Schutzausrüstung", 10),
            ("Amnestie-Spritzen", KassenKonto.Gruengeld, KassenBuchungArt.Auszahlung, 2_500m, "Nachschub Amnestie-Spritzen", 20),
            ("V-Personen-Honorar", KassenKonto.Schwarzgeld, KassenBuchungArt.Auszahlung, 3_000m, "Honorar für eine Vertrauensperson", 30),
        ];
        var knownTemplates = (await db.KassenVorlagen.IgnoreQueryFilters().Select(v => v.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, account, kind, amount, reason, sort) in templates)
        {
            if (!knownTemplates.Add(name))
            {
                continue;
            }
            db.KassenVorlagen.Add(new KassenBuchungVorlage
            {
                Name = name,
                Account = account,
                Kind = kind,
                Amount = amount,
                Reason = reason,
                IsActive = true,
                Sorting = sort,
            });
            added++;
        }
        return added;
    }

    /// <summary>Evidence catalogue plus a ledger of deposits and one withdrawal; stock is folded from the entries.</summary>
    private async Task<int> SeedEvidenceAsync(
        AppDbContext db, Dictionary<string, Person> people, CancellationToken ct)
    {
        (string Name, string Category, string Description, int Deposited, int Withdrawn)[] specs =
        [
            ("Combat Pistol", "Waffen", "Bei der Razzia im Rancho-Lagerhaus sichergestellt.", 14, 2),
            ("Pump Shotgun", "Waffen", "Aus einem Fahrzeugversteck an der Route 68.", 6, 0),
            ("Kevlarweste (NOOSE-Ausgabe)", "Schutzausrüstung", "Aus einer Lieferung entwendet und wieder aufgefunden.", 9, 3),
            ("Bargeld, gebündelt", "Wertsachen", "Sicherstellung Containerhafen, Bündel zu je 10.000 $.", 32, 0),
            ("Mobiltelefon, entsperrt", "Kommunikation", "Auswertung durch die Technik läuft.", 5, 1),
            ("Amnestie-Spritze", "Medizinisch", "Bestand der Behörde.", 25, 8),
            ("Funkgerät, modifiziert", "Kommunikation", "Abgehört auf einer Behördenfrequenz.", 3, 0),
        ];

        var items = (await db.EvidenceItems.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var suggestions = (await db.ProfileSuggestions
                .Where(s => s.Type == SuggestionType.EvidenceCategory)
                .Select(s => s.Value).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var fresh = new List<(EvidenceItem Item, int Deposited, int Withdrawn)>();
        foreach (var (name, category, description, deposited, withdrawn) in specs)
        {
            if (suggestions.Add(category))
            {
                db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.EvidenceCategory, Value = category });
                added++;
            }
            if (items.ContainsKey(name))
            {
                continue;
            }
            var item = new EvidenceItem { Name = name, Category = category, Description = description };
            db.EvidenceItems.Add(item);
            items[name] = item;
            fresh.Add((item, deposited, withdrawn));
            added++;
        }
        if (fresh.Count == 0)
        {
            return added;
        }

        var owner = people.Values.FirstOrDefault(p => !p.IsClassified && !p.IsDeleted);

        var deposit = new EvidenceEntry
        {
            CaseNumber = await caseNumbers.NextAsync(db, EvidencePrefix, ct),
            Type = EvidenceEntryType.Deposit,
            OwnerType = "NOOSE",
            OwnerId = null,
            HandlerAgentId = DemoIdentity.AgentId,
            Timestamp = DateTime.UtcNow.AddDays(-60),
            Notes = "Sammel-Einlagerung nach den Sicherstellungen der vergangenen Wochen.",
        };
        db.EvidenceEntries.Add(deposit);
        added++;
        foreach (var (item, quantity, _) in fresh)
        {
            db.EvidenceEntryLines.Add(new EvidenceEntryLine { EntryId = deposit.Id, ItemId = item.Id, Quantity = quantity });
            added++;
        }

        var withdrawals = fresh.Where(f => f.Withdrawn > 0).ToList();
        if (withdrawals.Count > 0)
        {
            var withdrawal = new EvidenceEntry
            {
                CaseNumber = await caseNumbers.NextAsync(db, EvidencePrefix, ct),
                Type = EvidenceEntryType.Withdrawal,
                OwnerType = owner is null ? "NOOSE" : nameof(Person),
                OwnerId = owner?.Id,
                HandlerAgentId = DemoIdentity.AgentId,
                Timestamp = DateTime.UtcNow.AddDays(-18),
                Notes = "Herausgabe an die Ermittlungsführung für die Auswertung.",
            };
            db.EvidenceEntries.Add(withdrawal);
            added++;
            foreach (var (item, _, quantity) in withdrawals)
            {
                db.EvidenceEntryLines.Add(new EvidenceEntryLine { EntryId = withdrawal.Id, ItemId = item.Id, Quantity = quantity });
                added++;
            }
        }
        return added;
    }

    /// <summary>Equipment catalogue plus three requests, one of them paid out against a real treasury booking.</summary>
    private async Task<int> SeedFinancingAsync(AppDbContext db, CancellationToken ct)
    {
        (string Name, string Category, decimal Price, int Subsidy, Rank MinRank, int Max, int Sort)[] catalogue =
        [
            ("Dienstwaffe Combat Pistol", "Bewaffnung", 4_500m, 80, Rank.JuniorAgent, 1, 10),
            ("Schutzweste, verstärkt", "Schutzausrüstung", 6_200m, 90, Rank.JuniorAgent, 1, 20),
            ("Funkgerät, verschlüsselt", "Kommunikation", 2_800m, 100, Rank.JuniorAgent, 2, 30),
            ("Dienstfahrzeug, ziviler Aufbau", "Fahrzeuge", 68_000m, 60, Rank.SeniorSpecialAgent, 1, 40),
            ("Nachtsichtgerät", "Technik", 9_400m, 70, Rank.SpecialAgent, 1, 50),
        ];

        var suggestions = (await db.ProfileSuggestions
                .Where(s => s.Type == SuggestionType.FinancingCategory)
                .Select(s => s.Value).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = (await db.FinancingItems.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var (name, category, price, subsidy, minRank, max, sort) in catalogue)
        {
            if (suggestions.Add(category))
            {
                db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.FinancingCategory, Value = category });
                added++;
            }
            if (items.ContainsKey(name))
            {
                continue;
            }
            var item = new FinancingItem
            {
                Name = name,
                Category = category,
                Description = null,
                UnitPrice = price,
                SubsidyPercent = subsidy,
                MinimumRank = minRank,
                MaxQuantity = max,
                IsActive = true,
                Sorting = sort,
            };
            db.FinancingItems.Add(item);
            items[name] = item;
            added++;
        }

        var existingRequests = (await db.FinancingRequests.IgnoreQueryFilters()
                .Select(r => r.Justification).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        (string Justification, FinancingStatus Status, string ItemName, int Quantity, int DaysAgo)[] requests =
        [
            ("Ersatzbeschaffung nach dem Einsatz in Paleto Bay. Die alte Weste wurde dabei beschädigt.",
                FinancingStatus.Paid, "Schutzweste, verstärkt", 1, 21),
            ("Für die verdeckte Observation im Hafenbereich wird ein Nachtsichtgerät benötigt.",
                FinancingStatus.Approved, "Nachtsichtgerät", 1, 9),
            ("Zweites Funkgerät für den Streifendienst, das vorhandene fällt regelmäßig aus.",
                FinancingStatus.Requested, "Funkgerät, verschlüsselt", 1, 2),
        ];

        foreach (var (justification, status, itemName, quantity, daysAgo) in requests)
        {
            if (!existingRequests.Add(justification) || !items.TryGetValue(itemName, out var item))
            {
                continue;
            }
            var gross = item.UnitPrice * quantity;
            var subsidy = Math.Round(gross * item.SubsidyPercent / 100m, 2);
            var decided = status is FinancingStatus.Approved or FinancingStatus.Paid;

            string? bookingId = null;
            if (status == FinancingStatus.Paid)
            {
                var payout = new KassenBuchung
                {
                    CaseNumber = await caseNumbers.NextAsync(db, KassePrefix, ct),
                    Account = KassenKonto.Gruengeld,
                    Kind = KassenBuchungArt.Auszahlung,
                    Amount = subsidy,
                    Reason = $"Finanzierung: {itemName}",
                    BookedById = DemoIdentity.AgentId,
                    Timestamp = DateTime.UtcNow.AddDays(-daysAgo + 3),
                };
                db.KassenBuchungen.Add(payout);
                bookingId = payout.Id;
                added++;
            }

            var request = new FinancingRequest
            {
                CaseNumber = await caseNumbers.NextAsync(db, FinancingPrefix, ct),
                AgentId = DemoIdentity.AgentId,
                Status = status,
                Justification = justification,
                RequestedGross = gross,
                RequestedSubsidy = subsidy,
                ApprovedSubsidy = decided ? subsidy : null,
                BudgetYear = decided ? DateTime.UtcNow.Year : null,
                BudgetMonth = decided ? DateTime.UtcNow.Month : null,
                DeciderName = decided ? DemoIdentity.Codename : null,
                DecidedAt = decided ? DateTime.UtcNow.AddDays(-daysAgo + 2) : null,
                DecisionNote = decided ? "In voller Höhe genehmigt." : null,
                PaidAt = status == FinancingStatus.Paid ? DateTime.UtcNow.AddDays(-daysAgo + 3) : null,
                PaidByName = status == FinancingStatus.Paid ? DemoIdentity.Codename : null,
                KassenBuchungId = bookingId,
            };
            db.FinancingRequests.Add(request);
            db.FinancingRequestLines.Add(new FinancingRequestLine
            {
                RequestId = request.Id,
                ItemId = item.Id,
                ItemName = item.Name,
                Category = item.Category,
                UnitPrice = item.UnitPrice,
                SubsidyPercent = item.SubsidyPercent,
                Quantity = quantity,
                ApprovedQuantity = decided ? quantity : null,
                Sorting = 0,
            });
            added += 2;
        }
        return added;
    }

    /// <summary>One abduction of the demo agent, with a leak and the records it compromised.</summary>
    private async Task<int> SeedAbductionAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions, CancellationToken ct)
    {
        if (await db.AgentAbductions.IgnoreQueryFilters().AnyAsync(ct))
        {
            return 0;
        }
        var faction = factions.Values.FirstOrDefault(f => !f.IsClassified && !f.IsStateFaction);
        if (faction is null)
        {
            return 0;
        }

        var abduction = new AgentAbduction
        {
            CaseNumber = await caseNumbers.NextAsync(db, AbductionPrefix, ct),
            VictimAgentId = DemoIdentity.AgentId,
            PerpetratorType = nameof(Faction),
            PerpetratorId = faction.Id,
            Timestamp = DateTime.UtcNow.AddDays(-27),
            ReleasedAt = DateTime.UtcNow.AddDays(-27).AddHours(6),
            Location = "Lagerhalle, Elysian Island",
            TruthSerum = true,
            InformationLeaked = true,
            LeakCategories = LeakCategory.Safehouses | LeakCategory.Operations,
            LeakSeverity = LeakSeverity.High,
            Outcome = AbductionOutcome.Rescued,
            Notes = "Befreiung durch die TRU nach sechs Stunden. Der Agent wurde unter Wahrheitsserum befragt; "
                + "nach eigener Aussage wurden zwei Deckadressen und eine laufende Maßnahme preisgegeben.",
        };
        db.AgentAbductions.Add(abduction);
        var added = 1;

        var target = people.Values.FirstOrDefault(p => !p.IsClassified && !p.IsDeleted);
        if (target is not null)
        {
            db.AbductionCompromises.Add(new AbductionCompromise
            {
                AbductionId = abduction.Id,
                TargetType = nameof(Person),
                TargetId = target.Id,
                Status = CompromiseStatus.Compromised,
                Note = "Deckadresse im Zusammenhang mit dieser Akte wurde preisgegeben.",
            });
            added++;
        }
        db.AbductionCompromises.Add(new AbductionCompromise
        {
            AbductionId = abduction.Id,
            TargetType = nameof(Faction),
            TargetId = faction.Id,
            Status = CompromiseStatus.Cleared,
            Note = "Zugangsdaten gewechselt, Einstufung zurückgenommen.",
            ClearedAt = DateTime.UtcNow.AddDays(-20),
            ClearedById = DemoIdentity.AgentId,
        });
        return added + 1;
    }

    private static async Task<int> SeedFeedbackAsync(AppDbContext db, List<TimeStamp> stamps, CancellationToken ct)
    {
        (FeedbackKind Kind, string Route, string? Tab, string Text, FeedbackStatus Status, string? Response, int DaysAgo)[] specs =
        [
            (FeedbackKind.Improvement, "/fahndung", "observationen",
                "Bitte einen Filter nach Aktualität in der Observationsliste ergänzen — bei über 200 Einträgen "
                + "findet man die frischen nicht mehr.",
                FeedbackStatus.Accepted, "Guter Vorschlag, kommt mit dem nächsten Update.", 12),
            (FeedbackKind.Bug, "/graph", null,
                "Der Beziehungsgraph verliert beim Wechsel in den Vollbildmodus das gespeicherte Layout.",
                FeedbackStatus.InProgress, "Nachgestellt, Ursache liegt im Resize-Handler. Wird behoben.", 8),
            (FeedbackKind.FeatureRequest, "/hinweise", null,
                "Es wäre hilfreich, einen Hinweis direkt aus dem Posteingang heraus einer bestehenden Observation "
                + "zuordnen zu können, statt erst die Akte zu öffnen.",
                FeedbackStatus.New, null, 3),
            (FeedbackKind.Complaint, "/statistik", null,
                "Der CSV-Export bricht bei Umlauten in Excel um. Bitte BOM prüfen.",
                FeedbackStatus.Done, "Der Export trägt jetzt ein UTF-8-BOM.", 30),
            (FeedbackKind.Improvement, "/kalender", null,
                "Im Kalender fehlt eine Wochenansicht mit Agentenspalten.",
                FeedbackStatus.Deferred, "Zurückgestellt, bis FullCalendar die Ressourcenansicht in der freien Version anbietet.", 44),
        ];

        var existing = (await db.Feedbacks.IgnoreQueryFilters().Select(f => f.Text).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var (kind, route, tab, text, status, response, daysAgo) in specs)
        {
            if (!existing.Add(text))
            {
                continue;
            }
            var created = DateTime.UtcNow.AddDays(-daysAgo);
            var entry = new FeedbackEntry
            {
                AgentId = DemoIdentity.AgentId,
                Kind = kind,
                PageRoute = route,
                PageTab = tab,
                Text = text,
                Status = status,
                Response = response,
                DeciderName = response is null ? null : DemoIdentity.Codename,
                DecidedAt = response is null ? null : created.AddDays(1),
            };
            db.Feedbacks.Add(entry);
            stamps.Add(new TimeStamp("Feedback", entry.Id, created));
            added++;
        }
        return added;
    }

    /// <summary>Informants with their meeting log; each is tied to an existing person record.</summary>
    private async Task<int> SeedInformantsAsync(
        AppDbContext db, Dictionary<string, Person> people, Dictionary<string, Faction> factions, CancellationToken ct)
    {
        var taken = (await db.Informants.IgnoreQueryFilters()
                .Where(i => i.PersonId != null).Select(i => i.PersonId!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (taken.Count > 0)
        {
            return 0;
        }

        var candidates = people.Values
            .Where(p => !p.IsClassified && !p.IsDeleted)
            .OrderBy(p => p.ThreatScore ?? 0)
            .Take(3)
            .ToList();
        var faction = factions.Values.FirstOrDefault(f => !f.IsClassified && !f.IsStateFaction);

        (InformantReliability Reliability, InformantStatus Status, string Description, string Contact, string[] Meetings)[] specs =
        [
            (InformantReliability.B, InformantStatus.Active,
                "Fahrer im Umfeld der Führungsebene; kennt Routen und Treffpunkte.",
                "Treffpunkt: Parkhaus Vinewood Blvd, Ebene 3",
                [
                    "Meldet eine geplante Waffenlieferung über den Hafen, Zeitfenster unklar.",
                    "Bestätigt die Lieferung; nennt zwei Fahrzeuge und eine Uhrzeit.",
                    "Keine neuen Erkenntnisse, wirkt angespannt. Kontaktfrequenz reduziert.",
                ]),
            (InformantReliability.C, InformantStatus.Active,
                "Barkeeper, hört Gespräche im Umfeld der Gruppierung mit.",
                "Kontakt über Prepaid-Nummer, Rückruf nur abends",
                [
                    "Berichtet von einem Streit zwischen zwei Untergruppen.",
                    "Nennt einen Namen, der bereits in der Akte steht — Angaben decken sich.",
                ]),
            (InformantReliability.D, InformantStatus.Burned,
                "Ehemaliges Mitglied; Quelle gilt seit dem Vorfall im Hafen als verbrannt.",
                "kein Kontakt mehr",
                [
                    "Letztes Treffen; Quelle berichtet von Verdacht innerhalb der Gruppierung.",
                ]),
        ];

        var added = 0;
        for (var i = 0; i < specs.Length && i < candidates.Count; i++)
        {
            var (reliability, status, description, contact, meetings) = specs[i];
            var person = candidates[i];
            var informant = new Informant
            {
                CaseNumber = await caseNumbers.NextAsync(db, InformantPrefix, ct),
                PersonId = person.Id,
                FactionId = faction?.Id,
                Description = description,
                ContactInfo = contact,
                Notes = "Angaben werden laufend mit der Funkaufklärung abgeglichen.",
                Reliability = reliability,
                Status = status,
                HandlerId = DemoIdentity.AgentId,
            };
            db.Informants.Add(informant);
            added++;

            var day = 40;
            foreach (var content in meetings)
            {
                db.InformantMeetings.Add(new InformantMeeting
                {
                    InformantId = informant.Id,
                    MeetingDate = DateTime.UtcNow.AddDays(-day),
                    Location = contact,
                    Content = content,
                });
                day -= 12;
                added++;
            }
        }
        return added;
    }

    /// <summary>Two past meetings with agenda and minutes plus one upcoming; reminders pre-stamped so the worker stays quiet.</summary>
    private async Task<int> SeedMeetingsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.Meetings.IgnoreQueryFilters().Select(m => m.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        (string Title, int DaysOffset, MeetingStatus Status, string[] Agenda, string? Minutes)[] specs =
        [
            ("Wochenbesprechung Lagebild (KW 34)", -14, MeetingStatus.Held,
                ["Lagebild Süden der Stadt", "Stand Hafenobservation", "Personalien und Abmeldungen", "Verschiedenes"],
                "Lagebild besprochen. Die Observation im Hafen wird um zwei Wochen verlängert; Zuständigkeit bleibt "
                + "bei der Ermittlungsführung. Für die Gefahrenlage wurde eine Anhebung auf „Erhöht\" beschlossen."),
            ("Wochenbesprechung Lagebild (KW 35)", -7, MeetingStatus.Held,
                ["Rückblick Zugriff Hafenviertel", "Auswertung Bürgerhinweise", "Kopfgeld-Budget", "Verschiedenes"],
                "Der Zugriff im Hafenviertel verlief ohne Zwischenfälle. Die Auswertung der Bürgerhinweise ergab eine "
                + "deutlich gestiegene Zahl verwertbarer Meldungen. Das Kopfgeld-Budget wird aus dem Nachtragshaushalt gedeckt."),
            ("Wochenbesprechung Lagebild (KW 36)", 3, MeetingStatus.Planned,
                ["Lagebild", "Stand der offenen Ausschreibungen", "Öffentlicher Bereich: Rückmeldungen", "Verschiedenes"],
                null),
        ];

        var added = 0;
        foreach (var (title, dayOffset, status, agenda, minutes) in specs)
        {
            if (!existing.Add(title))
            {
                continue;
            }
            var start = DateTime.UtcNow.AddDays(dayOffset).Date.AddHours(19);
            var meeting = new Meeting
            {
                CaseNumber = await caseNumbers.NextAsync(db, MeetingPrefix, ct),
                Title = title,
                Start = start,
                End = start.AddHours(1),
                Location = "Lagezentrum, NOOSE HQ",
                Status = status,
                MinutesHtml = minutes is null ? null : Paragraph(minutes),
                AttendanceClosedAt = status == MeetingStatus.Held ? start.AddHours(2) : null,
                // pre-stamped so the reminder worker does not fire for seeded rows
                ReminderDaySentAt = start.AddDays(-1),
                ReminderSoonSentAt = start.AddMinutes(-30),
            };
            db.Meetings.Add(meeting);
            added++;

            var order = 0;
            foreach (var item in agenda)
            {
                db.MeetingAgendaItems.Add(new MeetingAgendaItem
                {
                    MeetingId = meeting.Id,
                    Title = item,
                    Sorting = order++,
                });
                added++;
            }

            if (status == MeetingStatus.Held)
            {
                db.MeetingAttendances.Add(new MeetingAttendance
                {
                    MeetingId = meeting.Id,
                    AgentId = DemoIdentity.AgentId,
                    Status = MeetingAttendanceStatus.Present,
                    Origin = MeetingAbsenceOrigin.None,
                });
                added++;
            }
        }
        return added;
    }

    private static async Task<int> SeedAbsencesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Absences.IgnoreQueryFilters().AnyAsync(a => a.AgentId == DemoIdentity.AgentId, ct))
        {
            return 0;
        }

        (int FromOffset, int Days, AbsenceCategory Category, string Reason)[] specs =
        [
            (-40, 9, AbsenceCategory.Vacation, "Urlaub, in dringenden Fällen über die Leitstelle erreichbar."),
            (-16, 3, AbsenceCategory.Sick, "Krankgemeldet."),
            (5, 7, AbsenceCategory.RpBreak, "RP-Pause, danach wieder im regulären Dienst."),
        ];

        var added = 0;
        foreach (var (fromOffset, days, category, reason) in specs)
        {
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(fromOffset));
            db.Absences.Add(new Absence
            {
                AgentId = DemoIdentity.AgentId,
                FromDate = from,
                ToDate = from.AddDays(days - 1),
                Days = days,
                Category = category,
                Reason = reason,
                AcknowledgedAt = fromOffset < 0 ? DateTime.UtcNow.AddDays(fromOffset - 1) : null,
                AcknowledgedById = fromOffset < 0 ? DemoIdentity.AgentId : null,
                AcknowledgedByName = fromOffset < 0 ? DemoIdentity.Codename : null,
            });
            added++;
        }
        return added;
    }

    /// <summary>Milestone badges; the sweep worker would award them anyway, this makes the profile look lived-in from the first minute.</summary>
    private static async Task<int> SeedBadgesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.AgentBadges
                .Where(b => b.AgentId == DemoIdentity.AgentId)
                .Select(b => b.BadgeKey).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        (string Key, string Note, int DaysAgo)[] specs =
        [
            ("erste-akte", "Erste Akte angelegt.", 180),
            ("aktenfuchs", "25 Akten angelegt.", 120),
            ("netzwerker", "25 Verknüpfungen erstellt.", 74),
            ("analyst", "10 Einstufungen vorgenommen.", 51),
            ("beobachter", "20 Observationen dokumentiert.", 33),
        ];

        var added = 0;
        foreach (var (key, note, daysAgo) in specs)
        {
            if (!existing.Add(key))
            {
                continue;
            }
            db.AgentBadges.Add(new AgentBadge
            {
                AgentId = DemoIdentity.AgentId,
                BadgeKey = key,
                AwardedAt = DateTime.UtcNow.AddDays(-daysAgo),
                Note = note,
            });
            added++;
        }
        return added;
    }

    /// <summary>One decoration from the starter catalog, so the personnel file shows a certificate to print.</summary>
    private static async Task<int> SeedDecorationsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.DecorationAwards.AnyAsync(a => a.AgentId == DemoIdentity.AgentId, ct))
        {
            return 0;
        }
        var decoration = await db.Decorations.Where(d => d.IsActive).OrderBy(d => d.Order).FirstOrDefaultAsync(ct);
        if (decoration is null)
        {
            return 0;
        }
        db.DecorationAwards.Add(new DecorationAward
        {
            AgentId = DemoIdentity.AgentId,
            DecorationId = decoration.Id,
            AwardedOn = DateTime.Now.Date.AddDays(-12),
            Citation = decoration.DefaultCitation ?? "In Anerkennung besonderer Verdienste.",
            RecipientRank = Rank.Director,
            AwardedByName = "Behördenleitung",
        });
        return 1;
    }
}
