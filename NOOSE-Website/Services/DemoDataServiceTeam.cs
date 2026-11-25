using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Decorations;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Demo colleagues and their personnel files.</summary>
public partial class DemoDataService
{
    private const string ApplicationPrefix = "B";

    private sealed record ColleagueSpec(
        string Id, string Codename, string RealName, string? Badge, Rank? Rank, Division? Division, bool IsHrb,
        AgentStatus Status, int RegisteredDaysAgo, int? LastSeenDaysAgo, string? PublicPhone, string? DiscordName = null);

    private static readonly ColleagueSpec[] Colleagues =
    [
        new("demo-agent-falke", "Falke", "Eleanor Whitfield", "II", Rank.DeputyDirector, Division.Cid, false, AgentStatus.Active, 410, 0, "555-0101"),
        new("demo-agent-greif", "Greif", "Raymond Ashford", "III", Rank.AssistantDirector, Division.Ctd, false, AgentStatus.Active, 380, 1, "555-0102"),
        new("demo-agent-kranich", "Kranich", "Nadia Brennan", "IV", Rank.SectionChief, Division.Cid, true, AgentStatus.Active, 300, 0, "555-0103"),
        new("demo-agent-wolf", "Wolf", "Elias Moreno", "V", Rank.SectionChief, Division.Hrt, false, AgentStatus.Active, 290, 2, "555-0104"),
        new("demo-agent-luchs", "Luchs", "Sasha Kim", "VI", Rank.SectionChief, Division.Ctd, false, AgentStatus.Active, 260, 1, "555-0105"),
        new("demo-agent-habicht", "Habicht", "Owen Gallagher", "VII", Rank.SpecialAgentInCharge, Division.Cid, false, AgentStatus.Active, 220, 0, null),
        new("demo-agent-fuchs", "Fuchs", "Clara Lindqvist", "VIII", Rank.SeniorSpecialAgent, Division.Cid, true, AgentStatus.Active, 200, 0, null),
        new("demo-agent-baer", "Bär", "Jonah Petrakis", "IX", Rank.SeniorSpecialAgent, Division.Hrt, false, AgentStatus.Active, 180, 3, null),
        new("demo-agent-eule", "Eule", "Mira Okafor", "X", Rank.SpecialAgent, Division.Ctd, false, AgentStatus.Active, 140, 1, null),
        new("demo-agent-dachs", "Dachs", "Theo Vance", "XI", Rank.SpecialAgent, Division.Hrt, false, AgentStatus.Active, 120, 0, null),
        new("demo-agent-marder", "Marder", "Ines Novak", "XII", Rank.SpecialAgent, Division.Cid, false, AgentStatus.Active, 95, 19, null),
        new("demo-agent-specht", "Specht", "Leon Hartley", "XIII", Rank.Trainee, Division.Cid, false, AgentStatus.Active, 24, 0, null),
        new("demo-agent-reiher", "Reiher", "Ava Castellano", "XIV", Rank.Trainee, Division.Cid, false, AgentStatus.Active, 12, 1, null),
        new("demo-agent-moewe", "Möwe", "Grant Fielding", null, Rank.SpecialAgent, Division.Cid, false, AgentStatus.Terminated, 230, 40, null),
        new("demo-agent-otter", string.Empty, string.Empty, null, null, null, false, AgentStatus.Pending, 2, null, null, "otter.ls"),
    ];

    private const string Falke = "demo-agent-falke";
    private const string Greif = "demo-agent-greif";
    private const string Kranich = "demo-agent-kranich";
    private const string Wolf = "demo-agent-wolf";
    private const string Luchs = "demo-agent-luchs";
    private const string Habicht = "demo-agent-habicht";
    private const string Fuchs = "demo-agent-fuchs";
    private const string Baer = "demo-agent-baer";
    private const string Eule = "demo-agent-eule";
    private const string Dachs = "demo-agent-dachs";
    private const string Marder = "demo-agent-marder";
    private const string Specht = "demo-agent-specht";
    private const string Reiher = "demo-agent-reiher";
    private const string Moewe = "demo-agent-moewe";

    /// <summary>Codename of a seeded agent.</summary>
    private static string CodenameOf(string agentId)
        => agentId == DemoIdentity.AgentId
            ? DemoIdentity.Codename
            : Colleagues.FirstOrDefault(c => c.Id == agentId)?.Codename ?? DemoIdentity.Codename;

    /// <summary>Active colleagues with a rank.</summary>
    private static IEnumerable<ColleagueSpec> ActiveColleagues
        => Colleagues.Where(c => c.Status == AgentStatus.Active && c.Rank is not null);

    /// <summary>Create colleague accounts outside the transaction.</summary>
    private async Task<int> EnsureColleaguesAsync()
    {
        var added = 0;
        foreach (var spec in Colleagues)
        {
            if (await userManager.FindByIdAsync(spec.Id) is not null)
            {
                continue;
            }
            var registered = DateTime.UtcNow.AddDays(-spec.RegisteredDaysAgo);
            var agent = new Agent
            {
                Id = spec.Id,
                UserName = spec.Id,
                DiscordId = spec.Id,
                DiscordUsername = spec.DiscordName ?? spec.Codename.ToLowerInvariant(),
                Codename = spec.Codename,
                RealName = string.IsNullOrEmpty(spec.RealName) ? null : spec.RealName,
                BadgeNumber = spec.Badge,
                Status = spec.Status,
                Rank = spec.Rank,
                IsHRB = spec.IsHrb,
                RegisteredAt = registered,
                ReleasedAt = spec.Rank is null ? null : registered.AddDays(1),
                ReleasedById = spec.Rank is null ? null : DemoIdentity.AgentId,
            };
            if (spec.Division is { } division)
            {
                DivisionRules.Apply(agent, division);
            }
            if (spec.Status == AgentStatus.Terminated)
            {
                agent.TerminatedAt = DateTime.UtcNow.AddDays(-38);
                agent.TerminatedById = Falke;
                agent.TerminatedByName = "Falke";
                agent.TerminationReason = "Auf eigenen Wunsch ausgeschieden, Wechsel zum LSPD.";
            }
            var result = await userManager.CreateAsync(agent);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Demo-Agent {spec.Id} konnte nicht angelegt werden: "
                    + string.Join("; ", result.Errors.Select(e => e.Description)));
            }
            added++;
        }
        return added;
    }

    /// <summary>Seed last-seen stamps and the public roster.</summary>
    private static async Task<int> SeedColleagueStampsAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var spec in Colleagues.Where(c => c.LastSeenDaysAgo is not null))
        {
            var seen = DateTime.UtcNow.AddDays(-spec.LastSeenDaysAgo!.Value).AddHours(-2);
            await db.Users.Where(u => u.Id == spec.Id && u.LastSeenAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, seen), ct);
        }

        var visible = ActiveColleagues.Where(c => c.Rank >= Rank.SpecialAgent).Select(c => c.Id).ToList();
        await db.Users.Where(u => visible.Contains(u.Id) && u.HiddenFromPublicRoster)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.HiddenFromPublicRoster, false), ct);

        var withPhone = (await db.OeffentlicheTelefonnummern.Select(p => p.AgentId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var spec in Colleagues.Where(c => c.PublicPhone is not null && !withPhone.Contains(c.Id)))
        {
            db.OeffentlicheTelefonnummern.Add(new OeffentlicheTelefonnummer { AgentId = spec.Id, PhoneNumber = spec.PublicPhone! });
            added++;
        }
        return added;
    }

    /// <summary>Seed rank history up to the current rank.</summary>
    private static async Task<int> SeedRankHistoryAsync(AppDbContext db, CancellationToken ct)
    {
        var withHistory = (await db.AgentRankHistories.Select(h => h.AgentId).Distinct().ToListAsync(ct)).ToHashSet();
        var added = 0;

        var specs = Colleagues.Where(c => c.Rank is not null)
            .Select(c => (c.Id, Rank: c.Rank!.Value, c.RegisteredDaysAgo))
            .Append((DemoIdentity.AgentId, Rank.Director, 520));

        foreach (var (id, rank, registeredDaysAgo) in specs)
        {
            if (withHistory.Contains(id))
            {
                continue;
            }
            var steps = (int)rank;
            Rank? previous = null;
            for (var r = 1; r <= steps; r++)
            {
                // spread over tenure
                var daysAgo = registeredDaysAgo - 1 - (registeredDaysAgo - 5) * (r - 1) / Math.Max(1, steps);
                db.AgentRankHistories.Add(new AgentRankHistory
                {
                    AgentId = id,
                    Alt = previous,
                    New = (Rank)r,
                    Timestamp = DateTime.UtcNow.AddDays(-daysAgo),
                    ActorName = id == Falke || id == DemoIdentity.AgentId ? "Behördenleitung" : r <= 4 ? "Kranich" : "Demo",
                    Reason = r == 1 ? "Freigabe nach bestandener Sicherheitsüberprüfung" : "Beförderung nach Beurteilung",
                });
                previous = (Rank)r;
                added++;
            }
        }
        return added;
    }

    /// <summary>Seed notes, evaluations and promotion requests.</summary>
    private static async Task<int> SeedPersonnelFilesAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        var added = 0;

        (string Agent, AgentNoteKind Kind, int DaysAgo, string Text, string Author)[] notes =
        [
            (Habicht, AgentNoteKind.Commendation, 34, "Hervorragende Vorbereitung des Zugriffs auf der Elysian Island, Lagebild und Einsatzplan waren vollständig.", "Kranich"),
            (Fuchs, AgentNoteKind.Specialization, 80, "Zusatzqualifikation Vernehmungstechnik abgeschlossen.", "Kranich"),
            (Fuchs, AgentNoteKind.Department, 60, "Zusätzlich in der Administration Division eingesetzt.", "Falke"),
            (Wolf, AgentNoteKind.Commendation, 4, "Besonnene Einsatzführung bei der Geiselnahme in der Pacific Standard Bank, keine Verletzten.", "Falke"),
            (Dachs, AgentNoteKind.Training, 45, "Lehrgang Zugriff und Festnahme mit Auszeichnung bestanden.", "Wolf"),
            (Marder, AgentNoteKind.Information, 6, "Seit knapp drei Wochen ohne Rückmeldung, Nachfassen über Discord läuft.", "Kranich"),
            (Marder, AgentNoteKind.Disciplinary, 70, "Ermahnung wegen eines Funkspruchs mit Klarnamen im offenen Kanal.", "Kranich"),
            (Specht, AgentNoteKind.Training, 20, "Grundlehrgang Ermittlung begonnen, Ausbilderin ist Fuchs.", "Fuchs"),
            (Moewe, AgentNoteKind.Termination, 38, "Auf eigenen Wunsch ausgeschieden, Ausrüstung vollständig zurückgegeben.", "Falke"),
            (Eule, AgentNoteKind.Specialization, 50, "Schwerpunkt Finanzermittlungen, unterstützt die Taskforce Kartelle.", "Luchs"),
        ];
        var knownNotes = (await db.AgentNotes.IgnoreQueryFilters().Select(n => n.Text).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (agent, kind, daysAgo, text, author) in notes)
        {
            if (!knownNotes.Add(text))
            {
                continue;
            }
            db.AgentNotes.Add(new AgentNote
            {
                AgentId = agent,
                Kind = kind,
                EntryDate = DateTime.UtcNow.AddDays(-daysAgo),
                Text = text,
                AuthorName = author,
            });
            added++;
        }

        (string Agent, string Title, EvaluationGrade Grade, int DaysAgo, string Text, string Author)[] evaluations =
        [
            (Specht, "Beurteilung nach drei Wochen", EvaluationGrade.Good, 3,
                "Arbeitet sorgfältig und fragt nach, wenn etwas unklar ist. Im Funk noch zu ausführlich.", "Fuchs"),
            (Reiher, "Erste Beurteilung", EvaluationGrade.Satisfactory, 2,
                "Motiviert, kennt die Abläufe aber noch nicht sicher. Begleitung bei Vernehmungen weiter nötig.", "Fuchs"),
            (Eule, "Halbjahresbeurteilung", EvaluationGrade.VeryGood, 22,
                "Ihre Auswertung der Kontobewegungen hat den Verdacht gegen das Vinewood Syndikat erst belastbar gemacht.", "Luchs"),
            (Marder, "Halbjahresbeurteilung", EvaluationGrade.Poor, 30,
                "Unzuverlässig bei Terminen, Berichte kommen verspätet. Gespräch geführt, Ziele vereinbart.", "Kranich"),
            (Dachs, "Halbjahresbeurteilung", EvaluationGrade.Good, 26,
                "Sicher im Zugriff, gute Absprachen im Team. Schriftliche Berichte dürfen knapper werden.", "Wolf"),
        ];
        var knownEvaluations = (await db.AgentEvaluations.IgnoreQueryFilters().Select(e => e.AgentId + "|" + e.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (agent, title, grade, daysAgo, text, author) in evaluations)
        {
            if (!knownEvaluations.Add(agent + "|" + title))
            {
                continue;
            }
            db.AgentEvaluations.Add(new AgentEvaluation
            {
                AgentId = agent,
                Title = title,
                Grade = grade,
                EntryDate = DateTime.UtcNow.AddDays(-daysAgo),
                Text = text,
                AuthorName = author,
            });
            added++;
        }

        (string Agent, Rank Target, PromotionStatus Status, int DaysAgo, string Justification, string Requester, string? Note)[] promotions =
        [
            (Specht, Rank.SpecialAgent, PromotionStatus.Requested, 2,
                "Grundlehrgang fast abgeschlossen, Beurteilung gut. Freigabe zum Special Agent empfohlen.", "Fuchs", null),
            (Eule, Rank.SpecialAgent, PromotionStatus.Approved, 120,
                "Probezeit ohne Beanstandung abgeschlossen.", "Luchs", "Zugestimmt."),
            (Marder, Rank.SeniorSpecialAgent, PromotionStatus.Rejected, 28,
                "Langjährige Erfahrung im Streetgang-Milieu.", "Habicht", "Abgelehnt, die letzte Beurteilung ist negativ."),
        ];
        var withRequest = (await db.AgentPromotionRequests.Select(p => p.AgentId).ToListAsync(ct)).ToHashSet();
        foreach (var (agent, target, status, daysAgo, justification, requester, note) in promotions)
        {
            if (withRequest.Contains(agent))
            {
                continue;
            }
            var request = new AgentPromotionRequest
            {
                AgentId = agent,
                TargetRank = target,
                Justification = justification,
                Status = status,
                RequesterName = requester,
                DeciderName = status == PromotionStatus.Requested ? null : "Kranich",
                DecidedAt = status == PromotionStatus.Requested ? null : DateTime.UtcNow.AddDays(-daysAgo + 2),
                DecisionNote = note,
            };
            db.AgentPromotionRequests.Add(request);
            restamps.Add(new Restamp(typeof(AgentPromotionRequest), request.Id, DateTime.UtcNow.AddDays(-daysAgo), Colleague(requester)));
            added++;
        }
        return added;
    }

    private static string Colleague(string codename)
        => Colleagues.FirstOrDefault(c => c.Codename == codename)?.Id ?? DemoIdentity.AgentId;

    /// <summary>Seed training modules with completions.</summary>
    private static async Task<int> SeedTrainingAsync(AppDbContext db, CancellationToken ct)
    {
        (string Name, string Description, bool Mandatory, int Sort)[] modules =
        [
            ("Grundlehrgang Ermittlung", "Aktenführung, Beweissicherung, erste Vernehmung.", true, 10),
            ("Funk und Lagemeldung", "Rufnamen, Funkdisziplin und die kurze Lagemeldung.", true, 20),
            ("Umgang mit Verschlusssachen", "Einstufungen, Weitergabe und Vernichtung.", true, 30),
            ("Vernehmungstechnik", "Aufbau einer Vernehmung, Umgang mit Wahrheitsserum.", false, 40),
            ("Zugriff und Festnahme", "Taktisches Vorgehen im Team, Eigensicherung.", false, 50),
        ];
        var map = (await db.TrainingModules.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (name, description, mandatory, sort) in modules)
        {
            if (map.ContainsKey(name))
            {
                continue;
            }
            var module = new TrainingModule { Name = name, Description = description, IsMandatory = mandatory, Sorting = sort };
            db.TrainingModules.Add(module);
            map[name] = module;
            added++;
        }

        var done = (await db.AgentModuleCompletions.Select(c => c.AgentId + "|" + c.ModuleId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var spec in ActiveColleagues)
        {
            var count = spec.Rank switch
            {
                Rank.Trainee => spec.Id == Specht ? 2 : 1,
                Rank.SpecialAgent => 3,
                _ => spec.Division == Division.Hrt ? 5 : 4,
            };
            var index = 0;
            foreach (var (name, _, _, _) in modules.Take(count))
            {
                if (spec.Division != Division.Hrt && name == "Zugriff und Festnahme")
                {
                    continue;
                }
                var module = map[name];
                if (!done.Add(spec.Id + "|" + module.Id))
                {
                    continue;
                }
                db.AgentModuleCompletions.Add(new AgentModuleCompletion
                {
                    AgentId = spec.Id,
                    ModuleId = module.Id,
                    CompletedAt = DateTime.UtcNow.AddDays(-Math.Max(1, spec.RegisteredDaysAgo - 3 - index * 4)),
                    CompleterName = spec.Rank == Rank.Trainee ? "Fuchs" : "Kranich",
                });
                index++;
                added++;
            }
        }
        return added;
    }

    /// <summary>Seed firearm reports and a closed alarm.</summary>
    private static async Task<int> SeedFieldReportsAsync(AppDbContext db, CancellationToken ct)
    {
        var added = 0;
        (string Agent, int DaysAgo, string Reason)[] firearms =
        [
            (Dachs, 6, "Schusswechsel beim Zugriff im Hafenviertel, der Täter hat zuerst geschossen."),
            (Baer, 18, "Warnschuss bei der Verfolgung auf der Route 68, Fahrzeug hat danach angehalten."),
        ];
        var knownFirearms = (await db.FirearmUses.IgnoreQueryFilters().Select(f => f.Reason).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (agent, daysAgo, reason) in firearms)
        {
            if (!knownFirearms.Add(reason))
            {
                continue;
            }
            db.FirearmUses.Add(new FirearmUse { AgentId = agent, Timestamp = DateTime.UtcNow.AddDays(-daysAgo), Reason = reason });
            added++;
        }

        const string situation = "Geiselnahme in der Pacific Standard Bank";
        if (!await db.EmergencyAlarms.AnyAsync(a => a.Situation == situation, ct))
        {
            var triggered = DateTime.UtcNow.AddDays(-4).AddHours(-3);
            var alarm = new EmergencyAlarm
            {
                Situation = situation,
                Location = "Pacific Standard Bank, Vinewood",
                Target = Division.Hrt,
                IsEmergency = true,
                Reason = "Drei bewaffnete Täter, zwei Geiseln, LSPD hat abgesperrt.",
                TriggeredById = Wolf,
                TriggeredByName = "Wolf",
                TriggeredAt = triggered,
                Expiry = triggered.AddMinutes(30),
                EndedAt = triggered.AddMinutes(48),
                EndedById = Wolf,
            };
            db.EmergencyAlarms.Add(alarm);
            added++;
            (string Agent, EmergencyAlarmAnswer? Answer, int Minutes)[] answers =
            [
                (Baer, EmergencyAlarmAnswer.Coming, 2), (Dachs, EmergencyAlarmAnswer.Coming, 1), (DemoIdentity.AgentId, null, 0),
            ];
            foreach (var (agent, answer, minutes) in answers)
            {
                db.EmergencyAlarmRecipients.Add(new EmergencyAlarmRecipient
                {
                    AlarmId = alarm.Id,
                    AgentId = agent,
                    Answer = answer,
                    AnsweredAt = answer is null ? null : triggered.AddMinutes(minutes),
                });
                added++;
            }
        }
        return added;
    }

    /// <summary>Seed colleague absences and one decoration.</summary>
    private static async Task<int> SeedColleagueAbsencesAsync(AppDbContext db, CancellationToken ct)
    {
        var withAbsence = (await db.Absences.IgnoreQueryFilters().Select(a => a.AgentId).Distinct().ToListAsync(ct)).ToHashSet();
        (string Agent, int FromOffset, int Days, AbsenceCategory Category, string Reason)[] specs =
        [
            (Luchs, -2, 7, AbsenceCategory.Vacation, "Urlaub, Vertretung übernimmt Greif."),
            (Habicht, 6, 3, AbsenceCategory.Vacation, "Kurzurlaub."),
            (Baer, -9, 2, AbsenceCategory.Sick, "Krankgemeldet."),
            (Eule, 12, 5, AbsenceCategory.RpBreak, "RP-Pause."),
        ];
        var added = 0;
        foreach (var (agent, fromOffset, days, category, reason) in specs)
        {
            if (withAbsence.Contains(agent))
            {
                continue;
            }
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(fromOffset));
            db.Absences.Add(new Absence
            {
                AgentId = agent,
                FromDate = from,
                ToDate = from.AddDays(days - 1),
                Days = days,
                Category = category,
                Reason = reason,
                AcknowledgedAt = fromOffset < 3 ? DateTime.UtcNow.AddDays(Math.Min(fromOffset, 0) - 1) : null,
                AcknowledgedById = fromOffset < 3 ? Kranich : null,
                AcknowledgedByName = fromOffset < 3 ? "Kranich" : null,
            });
            added++;
        }

        if (!await db.DecorationAwards.AnyAsync(a => a.AgentId == Wolf, ct)
            && await db.Decorations.Where(d => d.IsActive).OrderByDescending(d => d.Order).FirstOrDefaultAsync(ct) is { } decoration)
        {
            db.DecorationAwards.Add(new DecorationAward
            {
                AgentId = Wolf,
                DecorationId = decoration.Id,
                AwardedOn = DateTime.Now.Date.AddDays(-2),
                Citation = "Für die besonnene Führung des Zugriffs in der Pacific Standard Bank, bei dem alle Geiseln unverletzt blieben.",
                RecipientRank = Rank.SectionChief,
                AwardedById = Falke,
                AwardedByName = "Falke",
                SignatoryName = "Eleanor Whitfield",
                SignatoryRank = Rank.DeputyDirector,
            });
            added++;
        }
        return added;
    }

    private sealed record ApplicantSpec(
        string Id, string DiscordName, string Name, int Age, string Employer, string Experience,
        BewerbungStatus Status, int SubmittedDaysAgo, string? AssignedTo, (bool FromApplicant, BewerbungMessageAudience Audience, string Text)[] Messages);

    private static readonly ApplicantSpec[] Applicants =
    [
        new("demo-bewerber-ward", "j.ward", "Julian Ward", 29, "Los Santos Police Department",
            "Vier Jahre Streifendienst, zuletzt in der Ermittlungsgruppe Drogen.",
            BewerbungStatus.Eingereicht, 1, null, []),
        new("demo-bewerber-sato", "emi.sato", "Emi Sato", 33, "San Andreas Medical Services",
            "Rettungsdienst, Notfallsanitäterin, Erfahrung mit Lagen mit vielen Verletzten.",
            BewerbungStatus.InSicherheitspruefung, 6, Fuchs,
            [
                (false, BewerbungMessageAudience.Bewerber, "Vielen Dank für Ihre Bewerbung. Wir führen jetzt die Sicherheitsüberprüfung durch und melden uns."),
                (true, BewerbungMessageAudience.Bewerber, "Danke! Brauchen Sie noch Unterlagen von mir?"),
                (false, BewerbungMessageAudience.Intern, "Referenz beim LSMD angefragt, Rückmeldung steht aus."),
            ]),
        new("demo-bewerber-grant", "d.grant", "Desmond Grant", 41, "Department of Justice",
            "Zwölf Jahre Staatsanwaltschaft, Schwerpunkt organisierte Kriminalität.",
            BewerbungStatus.ImVorstellungsgespraech, 14, Kranich,
            [
                (false, BewerbungMessageAudience.Bewerber, "Ihre Sicherheitsüberprüfung ist abgeschlossen. Wir laden Sie zu einem Gespräch ein."),
                (true, BewerbungMessageAudience.Bewerber, "Gern, der vorgeschlagene Termin passt mir."),
                (false, BewerbungMessageAudience.Intern, "Sehr erfahren, passt gut zur Taskforce Kartelle. Gespräch mit Luchs zusammen führen."),
            ]),
    ];

    /// <summary>Create applicant accounts outside the transaction.</summary>
    private async Task<int> EnsureApplicantAccountsAsync()
    {
        var added = 0;
        foreach (var spec in Applicants)
        {
            if (await userManager.FindByIdAsync(spec.Id) is not null)
            {
                continue;
            }
            var result = await userManager.CreateAsync(new Agent
            {
                Id = spec.Id,
                UserName = spec.Id,
                DiscordId = spec.Id,
                DiscordUsername = spec.DiscordName,
                Codename = string.Empty,
                Status = AgentStatus.Applicant,
                RegisteredAt = DateTime.UtcNow.AddDays(-spec.SubmittedDaysAgo - 1),
            });
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Demo-Bewerber {spec.Id} konnte nicht angelegt werden: "
                    + string.Join("; ", result.Errors.Select(e => e.Description)));
            }
            added++;
        }
        return added;
    }

    private async Task<int> SeedApplicationsAsync(AppDbContext db, List<Restamp> restamps, CancellationToken ct)
    {
        var known = (await db.Bewerbungen.IgnoreQueryFilters().Select(b => b.ApplicantUserId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var spec in Applicants)
        {
            if (known.Contains(spec.Id))
            {
                continue;
            }
            var submitted = DateTime.UtcNow.AddDays(-spec.SubmittedDaysAgo);
            var application = new Bewerbung
            {
                CaseNumber = await caseNumbers.NextAsync(db, ApplicationPrefix, ct),
                ApplicantUserId = spec.Id,
                Name = spec.Name,
                BirthDate = DateTime.UtcNow.Date.AddYears(-spec.Age).AddDays(-40),
                Employer = spec.Employer,
                PriorExperience = spec.Experience,
                CoverLetter = "Ich möchte meine Erfahrung dort einbringen, wo schwere Kriminalität bekämpft wird. "
                    + "Das FIB arbeitet behördenübergreifend und gründlich, genau so will ich arbeiten.",
                StreamConsent = true,
                Status = spec.Status,
                AssignedAgentId = spec.AssignedTo,
                AssignedAgentName = spec.AssignedTo is null ? null : CodenameOf(spec.AssignedTo),
                SecurityCheckPassed = spec.Status >= BewerbungStatus.ImTest ? true : null,
                SubmittedAt = submitted,
            };
            db.Bewerbungen.Add(application);
            restamps.Add(new Restamp(typeof(Bewerbung), application.Id, submitted, spec.Id));
            added++;

            var step = 1;
            foreach (var (fromApplicant, audience, text) in spec.Messages)
            {
                var message = new BewerbungMessage
                {
                    BewerbungId = application.Id,
                    Audience = audience,
                    Text = text,
                    AuthorIsApplicant = fromApplicant,
                    AuthorName = fromApplicant ? spec.Name : CodenameOf(spec.AssignedTo!),
                };
                db.BewerbungMessages.Add(message);
                restamps.Add(new Restamp(typeof(BewerbungMessage), message.Id,
                    submitted.AddHours(step++ * 9), fromApplicant ? spec.Id : spec.AssignedTo ?? DemoIdentity.AgentId));
                added++;
            }
        }
        return added;
    }
}
