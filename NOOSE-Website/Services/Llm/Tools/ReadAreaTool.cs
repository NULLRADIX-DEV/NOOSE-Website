using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Reads the current state of an operating area — the till, the evidence room, the board, the roster,
/// counter-intelligence, own followups, training.</summary>
/// <remarks>
/// The counterpart to <c>finde_akten</c>, and the line between them is the shape of the question: that one
/// enumerates records by attribute, this one reports a standing state that is not a list of files at all.
/// <para>Every area hands the actor straight to its own service and reports whatever comes back. An area the
/// viewer has no right to reads exactly like an empty one — a distinct refusal would make the tool a rights
/// oracle over areas whose mere existence the schema already discloses.</para>
/// </remarks>
public sealed class ReadAreaTool(
    IDbContextFactory<AppDbContext> dbFactory,
    IKassenService treasury,
    IEvidenceService evidence,
    IAnnouncementService announcements,
    ICounterIntelService counterIntel,
    IFollowupService followups,
    ITrainingModuleService training,
    IPersonService people,
    ISystemSettingService settings,
    IDocumentTemplateService documentTemplates,
    IActivityTemplateService activityTemplates,
    IPersonnelTemplateService personnelTemplates,
    IDocTemplateService docTemplates,
    IKassenTemplateService kassenTemplates,
    IFinancingCatalogService financingCatalog,
    ITagService tags,
    IBewerbungTestService bewerbungTests,
    IBewerbungssperreService bewerbungssperren,
    IBewerbungTemplateService bewerbungTemplates,
    ITicketService tickets,
    IPressReleaseService press,
    IPublicPageService publicPages,
    IPublicWarningService publicWarnings,
    IPublicReportService publicReports,
    IPublicSituationService situation,
    INotificationService notifications) : INooseiTool
{
    private const string Treasury = "kasse";
    private const string EvidenceRoom = "asservatenkammer";
    private const string Board = "brett";
    private const string Roster = "personal";
    private const string CounterIntelligence = "gegenaufklaerung";
    private const string Followups = "wiedervorlagen";
    private const string Training = "ausbildung";
    private const string Wanted = "fahndung";
    private const string Templates = "vorlagen";
    private const string Recruiting = "bewerbungswesen";
    private const string Keywords = "stichworte";
    private const string Notifications = "benachrichtigungen";
    private const string Tickets = "tickets";
    private const string PublicVoice = "oeffentlichkeit";

    private static readonly string[] Areas =
        [Treasury, EvidenceRoom, Board, Roster, CounterIntelligence, Followups, Training, Wanted,
         Templates, Recruiting, Keywords, Notifications, Tickets, PublicVoice];

    public string Name => "lies_bereich";

    public string Description =>
        "Liest den aktuellen Stand eines Bereichs: Kassenstand und letzte Buchungen, Bestand der "
        + "Asservatenkammer, Schwarzes Brett, Personalbestand, Auffälligkeiten der Gegenaufklärung, eigene "
        + "offene Wiedervorlagen, Ausbildungsmodule, Fahndungsliste (wer steht drauf und warum), "
        + "Vorlagen und Kataloge, Bewerbungswesen (Tests, Sperren, Anschreiben), Stichworte mit Nutzung, "
        + "eigene Benachrichtigungen, Bürger-Tickets am Schalter der Führungsebene (tickets) und die "
        + "Außendarstellung der Behörde — Pressemitteilungen, Infoseiten, Warnungen, freigegebene Monatstexte "
        + "und die Gefahrenlage (oeffentlichkeit). "
        + "Für Fragen nach einzelnen Akten oder deren Anzahl nimm finde_akten.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["bereich"],
          "properties": {
            "bereich": { "type": "string", "enum": {{Json(Areas)}} },
            "max": { "type": "integer", "minimum": 1, "maximum": 40, "description": "Höchstzahl gelisteter Zeilen." }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var area = NooseiLimits.Text(arguments, "bereich");
        if (area is null || !Areas.Contains(area, StringComparer.OrdinalIgnoreCase))
        {
            return new NooseiToolResult(
                "Diesen Bereich gibt es nicht. Möglich sind: " + string.Join(", ", Areas) + ".", null, true);
        }

        var max = NooseiLimits.Count(arguments, "max", 15);
        var sb = new StringBuilder();
        try
        {
            switch (area.ToLowerInvariant())
            {
                case Treasury: await TreasuryAsync(sb, max, cancellationToken); break;
                case EvidenceRoom: await EvidenceAsync(sb, max, cancellationToken); break;
                case Board: await BoardAsync(sb, max, context.Actor, cancellationToken); break;
                case Roster: await RosterAsync(sb, max, context.Scope, cancellationToken); break;
                case CounterIntelligence: await CounterIntelAsync(sb, max, context.Actor, cancellationToken); break;
                case Followups: await FollowupsAsync(sb, max, context.Actor, cancellationToken); break;
                case Wanted: await WantedAsync(sb, max, context.Scope, cancellationToken); break;
                case Templates: await TemplatesAsync(sb, max, context.Scope, cancellationToken); break;
                case Recruiting: await RecruitingAsync(sb, max, context.Actor, cancellationToken); break;
                case Keywords: await KeywordsAsync(sb, max, context.Scope, cancellationToken); break;
                case Notifications: await NotificationsAsync(sb, max, context.Actor, cancellationToken); break;
                case Tickets: await TicketsAsync(sb, max, context.Actor, cancellationToken); break;
                case PublicVoice: await PublicVoiceAsync(sb, max, context.Actor, cancellationToken); break;
                default: await TrainingAsync(sb, max, cancellationToken); break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // identical to an empty area on purpose: see the remark on the class
            return new NooseiToolResult($"Zum Bereich {area} liegt dir nichts vor.");
        }

        return sb.Length == 0
            ? new NooseiToolResult($"Zum Bereich {area} liegt dir nichts vor.")
            : new NooseiToolResult(NooseiLimits.Clip(sb.ToString(), NooseiLimits.MaxContentResultChars));
    }

    private async Task TreasuryAsync(StringBuilder sb, int max, CancellationToken ct)
    {
        var summaries = await treasury.GetSummariesAsync(ct);
        sb.AppendLine("Kasse");
        foreach (var s in summaries)
        {
            sb.Append("• ").Append(KassenKontoDisplay.Name(s.Account)).Append(": ")
                .Append(s.Balance.ToString("N0")).Append(" $ | Buchungen: ").Append(s.Count);
            if (s.LastBookingAt is { } last)
            {
                sb.Append(" | zuletzt ").Append(Fmt(last));
            }
            sb.AppendLine();
        }

        foreach (var account in summaries.Select(s => s.Account))
        {
            var ledger = await treasury.GetLedgerAsync(account, ct);
            if (ledger.Count == 0)
            {
                continue;
            }
            sb.Append("— Letzte Buchungen ").Append(KassenKontoDisplay.Name(account))
                .Append(" (").Append(Math.Min(max, ledger.Count)).Append(" von ").Append(ledger.Count).AppendLine(") —");
            foreach (var b in ledger.Take(max))
            {
                sb.Append("• ").Append(Fmt(b.Buchung.Timestamp)).Append(" | ")
                    .Append(KassenBuchungArtDisplay.Name(b.Buchung.Kind)).Append(" | ")
                    .Append(b.Buchung.Amount.ToString("N0")).Append(" $");
                if (Free(b.Buchung.Reason) is { Length: > 0 } why)
                {
                    sb.Append(" | ").Append(why);
                }
                sb.AppendLine();
            }
        }
    }

    private async Task EvidenceAsync(StringBuilder sb, int max, CancellationToken ct)
    {
        var items = await evidence.GetItemsAsync(null, null, ct);
        if (items.Count == 0)
        {
            return;
        }
        var stocked = items.Where(i => i.OnHand != 0).ToList();
        sb.AppendLine("Asservatenkammer");
        sb.Append("Asservate insgesamt: ").Append(items.Count)
            .Append(" | davon mit Bestand: ").Append(stocked.Count).AppendLine();
        var shown = stocked.OrderByDescending(i => i.OnHand).Take(max).ToList();
        sb.Append("— Bestand (").Append(shown.Count).Append(" von ").Append(stocked.Count).AppendLine(") —");
        foreach (var i in shown)
        {
            sb.Append("• ").Append(Free(i.Item.Name)).Append(": ").Append(i.OnHand);
            if (Free(i.Item.Category) is { Length: > 0 } category)
            {
                sb.Append(" | Kategorie: ").Append(category);
            }
            sb.AppendLine();
        }
    }

    private async Task BoardAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        var rows = await announcements.GetBoardAsync(actor, ct);
        if (rows.Count == 0)
        {
            return;
        }
        sb.AppendLine("Schwarzes Brett");
        sb.Append("— Ankündigungen (").Append(Math.Min(max, rows.Count)).Append(" von ").Append(rows.Count).AppendLine(") —");
        foreach (var a in rows.Take(max))
        {
            sb.Append("• ").Append(Fmt(a.CreatedAt)).Append(a.Important ? " [wichtig] " : " ")
                .Append(Free(a.Title)).Append(" | Zielgruppe: ").Append(a.TargetDisplay);
            if (a.MustAcknowledge)
            {
                sb.Append(" | von dir noch nicht quittiert");
            }
            sb.AppendLine();
        }
    }

    private async Task RosterAsync(StringBuilder sb, int max, ViewerScope scope, CancellationToken ct)
    {
        // the roster is the personnel list, and that page is leadership-only
        if (!scope.MayClassifiedRead)
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // the one named rule the personnel page uses; filtering db.Users by hand here is how the search leaked
        var rows = await db.Users.AsNoTracking().OnlyWithPersonnelFile()
            .OrderBy(u => u.Rank == null).ThenByDescending(u => u.Rank).ThenBy(u => u.Codename)
            .Select(u => new { u.Codename, u.Rank, u.Division, u.Status, u.IsTRU, u.IsHRB })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return;
        }

        sb.AppendLine("Personalbestand");
        sb.Append("Personalakten: ").Append(rows.Count)
            .Append(" | aktiv: ").Append(rows.Count(r => r.Status == AgentStatus.Active)).AppendLine();
        sb.Append("— Agenten (").Append(Math.Min(max, rows.Count)).Append(" von ").Append(rows.Count).AppendLine(") —");
        foreach (var r in rows.Take(max))
        {
            var unit = DivisionRules.Of(r.Rank, r.Division, r.IsTRU);
            sb.Append("• ").Append(string.IsNullOrWhiteSpace(r.Codename) ? "(unbenannt)" : r.Codename)
                .Append(" | ").Append(r.Rank is { } rank ? RankDisplay.Name(rank, unit.Home) : "ohne Dienstgrad")
                .Append(" | ").Append(AgentStatusDisplay.Name(r.Status));
            if (!unit.IsNone) { sb.Append(" | Abteilung: ").Append(DivisionRules.Label(unit)); }
            if (r.IsHRB) { sb.Append(" | Administration Division"); }
            sb.AppendLine();
        }
    }

    private async Task CounterIntelAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        var overview = await counterIntel.GetOverviewAsync(actor, 30, ct);
        var flags = await counterIntel.GetFlagsAsync(actor, ct);

        sb.AppendLine("Gegenaufklärung");
        sb.Append("Zeitraum: letzte ").Append(overview.WindowDays).AppendLine(" Tage");
        sb.Append("Zugriffe: ").Append(overview.TotalAccesses)
            .Append(" | Agenten: ").Append(overview.DistinctAgents)
            .Append(" | Akten: ").Append(overview.DistinctRecords)
            .Append(" | außerhalb der Dienstzeit: ").Append(overview.OffHoursAccesses).AppendLine();

        if (flags.Count == 0)
        {
            sb.AppendLine("Keine Auffälligkeiten.");
            return;
        }
        sb.Append("— Auffälligkeiten (").Append(Math.Min(max, flags.Count)).Append(" von ").Append(flags.Count).AppendLine(") —");
        foreach (var f in flags.OrderByDescending(f => f.Severity).Take(max))
        {
            sb.Append("• ").Append(f.AgentName).Append(" | ").Append(CounterIntelSeverityDisplay.Name(f.Grade))
                .Append(" | ").Append(Free(f.Rule)).Append(": ").AppendLine(Free(f.Detail));
        }
    }

    private async Task FollowupsAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        var rows = await followups.GetMyDueAsync(actor, ct);
        if (rows.Count == 0)
        {
            return;
        }
        sb.AppendLine("Eigene Wiedervorlagen");
        sb.Append("— Fällig (").Append(Math.Min(max, rows.Count)).Append(" von ").Append(rows.Count).AppendLine(") —");
        foreach (var f in rows.Take(max))
        {
            sb.Append("• ").Append(Fmt(f.DueAt)).Append(" | ").Append(Free(f.Display));
            if (Free(f.Note) is { Length: > 0 } note)
            {
                sb.Append(" — ").Append(note);
            }
            sb.AppendLine();
        }
    }

    private async Task TrainingAsync(StringBuilder sb, int max, CancellationToken ct)
    {
        var modules = await training.GetActiveAsync(ct);
        if (modules.Count == 0)
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var completions = await db.AgentModuleCompletions.AsNoTracking()
            .GroupBy(c => c.ModuleId)
            .Select(g => new { ModuleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ModuleId, x => x.Count, ct);

        sb.AppendLine("Ausbildung");
        sb.Append("— Aktive Module (").Append(Math.Min(max, modules.Count)).Append(" von ").Append(modules.Count).AppendLine(") —");
        foreach (var m in modules.Take(max))
        {
            sb.Append("• ").Append(Free(m.Name))
                .Append(" | Abschlüsse: ").Append(completions.GetValueOrDefault(m.Id));
            if (Free(m.Description) is { Length: > 0 } description)
            {
                sb.Append(" | ").Append(description);
            }
            sb.AppendLine();
        }
    }

    /// <summary>The citizen ticket desk. Leadership only, and never the citizen behind a ticket.</summary>
    /// <remarks>
    /// The service holds the gate and throws for anyone else, which the caller turns into the same sentence an
    /// empty area produces. No citizen name: a stored assistant answer is a second place a name would live, and the
    /// desk is where it belongs.
    /// </remarks>
    private async Task TicketsAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        var open = await tickets.GetInboxAsync(TicketInboxScope.Offen, null, false, actor, cancellationToken: ct);
        var working = await tickets.GetInboxAsync(TicketInboxScope.Bearbeitung, null, false, actor, cancellationToken: ct);
        var waiting = await tickets.GetInboxAsync(TicketInboxScope.Wartet, null, false, actor, cancellationToken: ct);

        // the desk lists are capped, so these are the rows that came back, not the stock — the badge on /tickets is
        // the number that counts them
        sb.AppendLine("Bürger-Tickets");
        sb.Append("Gezeigt: offen ").Append(open.Count)
            .Append(" | in Bearbeitung ").Append(working.Count)
            .Append(" | wartet auf Bürger ").Append(waiting.Count).AppendLine();

        var rows = open.Concat(working).Concat(waiting)
            .OrderByDescending(t => t.LastActivityAt)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }
        sb.Append("— Laufende Tickets (").Append(Math.Min(max, rows.Count)).Append(" von ").Append(rows.Count)
            .AppendLine(") —");
        foreach (var t in rows.Take(max))
        {
            sb.Append("• ").Append(Free(t.Subject))
                .Append(" | ").Append(t.CaseNumber)
                .Append(" | ").Append(TicketStatusDisplay.Name(t.Status))
                .Append(" | letzte Aktivität: ").Append(Fmt(t.LastActivityAt.ToLocalTime()));
            if (t.HandlerCodename is { Length: > 0 } handler)
            {
                sb.Append(" | Bearbeiter: ").Append(handler);
            }
            if (t.AwaitingAnswer)
            {
                sb.Append(" | wartet auf Antwort");
            }
            sb.AppendLine();
        }
    }

    /// <summary>What the agency says publicly: press, editorial pages, warnings, monthly texts, the threat level.</summary>
    /// <remarks>
    /// One area over five surfaces rather than five areas: they share a gate, a budget and a question — "what have
    /// we said out there". Drafts are included; that is the point of an internal read. The four lists stand or fall
    /// together because one guard admits to all of them, and a throw reads as an empty area, as everywhere else in
    /// this file. Only the threat level degrades on its own: it answers to its own module switch and says so.
    /// </remarks>
    private async Task PublicVoiceAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        sb.AppendLine("Außendarstellung");

        // the level answers to its own module switch, so it is the one surface that can be silent on its own
        if (await situation.GetPublishedAsync(ct) is { } level)
        {
            sb.Append("Gefahrenlage: ").Append(PublicSituationLevelDisplay.Name(level.Level));
            if (level.Since is { } since)
            {
                sb.Append(" (seit ").Append(Fmt(since.ToLocalTime())).Append(')');
            }
            if (level.Previous is { } before)
            {
                sb.Append(" | zuvor: ").Append(PublicSituationLevelDisplay.Name(before));
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Gefahrenlage: nicht veröffentlicht");
        }

        Section(sb, "Pressemitteilungen", max, await press.GetAllAsync(actor, ct),
            m => $"{Free(m.Title)} | {m.CaseNumber ?? "ohne Aktenzeichen"} | {PressReleaseStatusDisplay.Name(m.Status)}"
                + Published(m.PublishedAt));

        Section(sb, "Infoseiten", max, await publicPages.GetAllAsync(actor, ct),
            p => $"{Free(p.Title)} | /info/{p.Slug} | {PublicPageStatusDisplay.Name(p.Status)}"
                + Published(p.PublishedAt));

        Section(sb, "Warnungen", max, await publicWarnings.GetAllAsync(actor, ct),
            w => $"{Free(w.Title)} | {PublicWarningStatusDisplay.Name(w.Status)}"
                + (w.ValidUntil is { } until ? $" | gültig bis {Fmt(until.ToLocalTime())}" : string.Empty)
                + (w.IsExpired ? " | abgelaufen" : string.Empty));

        Section(sb, "Freigegebene Monatstexte", max, await publicReports.GetAllAsync(actor, ct),
            r => $"{ReportPeriod.Label(r.Year, r.Month)} | {Free(r.Title)} | "
                + PublicReportStatusDisplay.Name(r.Status) + Published(r.PublishedAt));
    }

    private static string Published(DateTime? when)
        => when is { } value ? " | veröffentlicht: " + Fmt(value.ToLocalTime()) : string.Empty;

    /// <summary>One capped block of an area, with the total named beside the shown count.</summary>
    private static void Section<T>(StringBuilder sb, string heading, int max, IReadOnlyList<T> rows,
        Func<T, string> line)
    {
        if (rows.Count == 0)
        {
            return;
        }
        sb.Append("— ").Append(heading).Append(" (").Append(Math.Min(max, rows.Count))
            .Append(" von ").Append(rows.Count).AppendLine(") —");
        foreach (var row in rows.Take(max))
        {
            sb.Append("• ").AppendLine(line(row));
        }
    }

    private async Task WantedAsync(StringBuilder sb, int max, ViewerScope scope, CancellationToken ct)
    {
        // same rule and threshold as the /fahndung page, via the shared helper and the scope-filtered list
        var threshold = (await settings.GetAsync(ct)).WantedBoardMinHazard;
        var board = (await people.GetListAsync(scope, cancellationToken: ct))
            .Where(p => WantedBoard.IsOnBoard(p, threshold))
            .OrderByDescending(p => p.IsWanted)
            .ThenByDescending(p => p.ThreatScore ?? -1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (board.Count == 0)
        {
            return;
        }

        sb.AppendLine("Fahndung");
        sb.Append("Schwelle: ab Gefahrenstufe ").Append(HazardLevelLogic.Name(threshold))
            .Append(" | auf der Liste: ").Append(board.Count)
            .Append(" | davon manuell ausgeschrieben: ").Append(board.Count(p => p.IsWanted)).AppendLine();
        sb.Append("— Personen (").Append(Math.Min(max, board.Count)).Append(" von ").Append(board.Count).AppendLine(") —");
        foreach (var p in board.Take(max))
        {
            sb.Append("• ").Append(Free(p.Name))
                .Append(" | Aktenzeichen: ").Append(string.IsNullOrWhiteSpace(p.CaseNumber) ? "—" : p.CaseNumber)
                .Append(" | Einstufung: ").Append(ClassificationDisplay.Name(p.Classification))
                .Append(" | Grund: ").Append(WantedBoard.Reason(p, threshold));
            if (p.ThreatScore is { } score)
            {
                sb.Append(" | Bedrohungs-Score: ").Append(score);
            }
            sb.AppendLine();
        }
    }

    private async Task TemplatesAsync(StringBuilder sb, int max, ViewerScope scope, CancellationToken ct)
    {
        // the template/catalog management pages are leadership-gated in /einstellungen; a plain agent, who cannot
        // open them in the app, reads this area as empty rather than as a config dump
        if (!scope.MayClassifiedRead)
        {
            return;
        }
        // the library page hides the recruiting-category rows; they belong to the HRB-gated Bewerbungswesen area
        var docs = (await documentTemplates.GetAllAsync(ct))
            .Where(t => !string.Equals(t.Category, RecruitingSeeder.TemplateCategory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var acts = await activityTemplates.GetAllAsync(ct);
        var pers = await personnelTemplates.GetAllAsync(ct);
        var lib = await docTemplates.GetAllAsync(ct);
        var kas = await kassenTemplates.GetAllAsync(ct);
        var fin = await financingCatalog.GetAllAsync(ct);
        if (docs.Count + acts.Count + pers.Count + lib.Count + kas.Count + fin.Count == 0)
        {
            return;
        }

        sb.AppendLine("Vorlagen & Kataloge");
        Section(sb, "Dokument-Vorlagen", max, docs.Count, docs.Select(t => Label(t.Name, t.IsActive)));
        Section(sb, "Aktivitäts-Vorlagen", max, acts.Count, acts.Select(t => Label(t.Name, t.IsActive)));
        Section(sb, "Personal-Vorlagen", max, pers.Count,
            pers.Select(t => PersonnelTemplateKindDisplay.Name(t.Kind) + ": " + Label(t.Name, t.IsActive)));
        Section(sb, "Personen-Dok-Vorlagen", max, lib.Count, lib.Select(t => Label(t.Name, t.IsActive)));
        Section(sb, "Kassen-Vorlagen", max, kas.Count,
            kas.Select(t => $"{Label(t.Name, t.IsActive)} | {KassenBuchungArtDisplay.Name(t.Kind)} {t.Amount:N0} $"));
        Section(sb, "Finanzierungs-Katalog", max, fin.Count, fin.Select(t => Label(t.Name, t.IsActive)));
    }

    private async Task RecruitingAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        // every read here is RequireHrbOrLeadership; a plain agent trips the throw and the area reads as empty
        var tests = await bewerbungTests.GetTestsAsync(actor, ct);
        var bans = await bewerbungssperren.ListActiveAsync(actor, ct);
        var templates = await bewerbungTemplates.ListAsync(actor, ct);
        if (tests.Count + bans.Count + templates.Count == 0)
        {
            return;
        }

        sb.AppendLine("Bewerbungswesen");
        Section(sb, "Tests", max, tests.Count, tests.Select(t => Label(t.Title, t.IsActive)));
        Section(sb, "Aktive Sperren", max, bans.Count, bans.Select(b =>
            (b.IsBlacklist ? "[Blacklist] " : "")
                + Free(string.IsNullOrWhiteSpace(b.ApplicantName) ? b.AgentId : b.ApplicantName)
                + (b.BannedUntil is { } until ? " bis " + Fmt(until) : "")
                + (Free(b.Reason) is { Length: > 0 } why ? " — " + why : "")));
        Section(sb, "Anschreiben-Vorlagen", max, templates.Count, templates.Select(t => Label(t.Name, t.IsActive)));
    }

    private async Task KeywordsAsync(StringBuilder sb, int max, ViewerScope scope, CancellationToken ct)
    {
        // usage counts aggregate across records the viewer may not open; the Stichworte admin page is leadership-gated,
        // so keep the global list to leadership. Per-record tags stay readable via the lies_akteninhalt stichworte section
        if (!scope.MayClassifiedRead)
        {
            return;
        }
        var rows = (await tags.GetWithUsageAsync(ct))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }
        sb.AppendLine("Stichworte");
        Section(sb, "Stichworte mit Nutzung", max, rows.Count, rows.Select(t => $"{Free(t.Tag.Name)} ({t.Count})"));
    }

    private async Task NotificationsAsync(StringBuilder sb, int max, ClaimsPrincipal actor, CancellationToken ct)
    {
        // always the asking agent's own, by construction of the service — never anyone else's
        var rows = await notifications.GetOwnAsync(actor, Math.Max(max, 20), ct);
        if (rows.Count == 0)
        {
            return;
        }
        // GetOwnAsync returns only the most-recent page, so its count is not the grand total; take the exact unread
        // figure from the counter instead of deriving it from the page
        var unread = await notifications.GetUnreadCountAsync(actor, ct);
        sb.AppendLine("Benachrichtigungen");
        sb.Append("Ungelesen: ").Append(unread).AppendLine();
        sb.Append("— Neueste (").Append(Math.Min(max, rows.Count)).AppendLine(") —");
        foreach (var n in rows.Take(max))
        {
            sb.Append("• ").Append(Fmt(n.CreatedAt)).Append(n.ReadAt is null ? " [ungelesen] " : " ")
                .Append(NotificationTypeDisplay.Name(n.Type)).Append(": ").AppendLine(Free(n.Title));
        }
    }

    /// <summary>One capped subsection with its "n von m" count; skipped entirely when the source is empty.</summary>
    private static void Section(StringBuilder sb, string heading, int max, int total, IEnumerable<string> rows)
    {
        if (total == 0)
        {
            return;
        }
        sb.Append("— ").Append(heading).Append(" (").Append(Math.Min(max, total))
            .Append(" von ").Append(total).AppendLine(") —");
        foreach (var row in rows.Take(max))
        {
            sb.Append("• ").AppendLine(row);
        }
    }

    private static string Label(string name, bool active) => Free(name) + (active ? "" : " (inaktiv)");

    private static string Free(string? text) => MentionParser.Strip(text).Trim();

    private static string Fmt(DateTime when) => when.ToString("dd.MM.yyyy HH:mm");

    private static string Json(IEnumerable<string> values)
        => "[" + string.Join(",", values.Select(v => "\"" + v + "\"")) + "]";
}
