using System.Security.Claims;
using System.Text;
using System.Text.Json;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Feedback;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Statistics;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Answers "which ones" and "how many" — the questions free-text search cannot.</summary>
/// <remarks>
/// Fans out over the per-type list services instead of querying EF directly. All six carry the identical
/// <c>GetListAsync(ViewerScope, ArchiveFilter, ct)</c> signature, so the visibility filtering is inherited from the canonical
/// read path rather than rewritten here, where it could be got wrong. It is also what the list pages do.
/// </remarks>
public sealed class FilterRecordsTool(
    IPersonService people,
    IFactionService factions,
    IPersonGroupService groups,
    IPartyService parties,
    IDrugRouteService drugRoutes,
    ICaseService cases,
    IOperationService operations,
    ILawService laws,
    ITaskforceService taskforces,
    IDocumentService documents,
    IMeetingService meetings,
    IJobService jobs,
    IInformantService informants,
    IEvidenceService evidence,
    ILibraryService library,
    IAbsenceService absences,
    IAnnouncementService announcements,
    IFinancingService financing,
    IFoodService food,
    IBewerbungService applications,
    ISystemSettingService settings,
    IAgentActivityService activities,
    IAbductionService abductions,
    ISituationReportService situationReports,
    ITrainingModuleService trainingModules,
    ICounterIntelRuleService counterIntelRules,
    IFeedbackService feedback) : INooseiTool
{
    /// <summary>How deep the paged list services are asked to go; the filter then runs over the whole page.</summary>
    private const int MaxRows = 200;

    public string Name => "finde_akten";

    public string Description =>
        "Findet Akten über Merkmale statt über Suchtext und zählt sie. Nutze es für Fragen der Form "
        + "welche alle und wie viele: alle Personen einer Einstufung, alle Personen auf der Fahndungsliste, "
        + "alle Fraktionen über einem Bedrohungs-Score, alle seit X Tagen geänderten Akten. "
        + "Für die Suche nach einem Namen nimm suche_akten.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["typ"],
          "properties": {
            "typ": { "type": "string", "enum": {{NooseiRecordTypes.ListableEnumJson}},
                     "description": "Aktentyp, der durchgesehen wird." },
            "einstufung": { "type": "string", "enum": ["Unbekannt","Prüffall","Verdachtsfall","Gesichert staatsgefährdend"],
                            "description": "Nur Akten mit dieser Einstufung." },
            "status": { "type": "string",
                        "description": "Nur Akten in diesem Status, deutsche Bezeichnung (Offen, Angenommen, Abgelehnt, Geplant …). Nur bei Typen, die einen Status führen." },
            "lebensstatus": { "type": "string", "enum": ["Lebend","Tot","Flüchtig"],
                              "description": "Nur bei typ=Person; berücksichtigt das Respawn-Fenster." },
            "nur_fahndung": { "type": "boolean",
                              "description": "Nur bei typ=Person: nur manuell zur Fahndung ausgeschriebene Personen." },
            "auf_fahndungsliste": { "type": "boolean",
                              "description": "Nur bei typ=Person: alle, die auf der Fahndungsseite stehen — manuell ausgeschrieben ODER Bedrohungs-Score ab der eingestellten Gefahrenstufe." },
            "score_min": { "type": "integer", "minimum": 0, "maximum": 100,
                           "description": "Nur Akten mit Bedrohungs-Score ab diesem Wert (Person und Fraktion)." },
            "score_max": { "type": "integer", "minimum": 0, "maximum": 100 },
            "geaendert_seit_tagen": { "type": "integer", "minimum": 1, "maximum": 3650,
                                      "description": "Nur Akten, die in diesem Zeitraum zuletzt geändert wurden." },
            "nur_verschlusssache": { "type": "boolean", "description": "Nur als Verschlusssache geführte Akten." },
            "max": { "type": "integer", "minimum": 1, "maximum": 40, "description": "Höchstzahl gelisteter Akten." },
            "nur_anzahl": { "type": "boolean", "description": "Nur die Anzahl liefern, ohne Einzelakten." }
          }
        }
        """);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var german = NooseiLimits.Text(arguments, "typ");
        var clr = NooseiRecordTypes.Clr(german, NooseiUse.List);
        if (clr is null)
        {
            return new NooseiToolResult(
                "Über Merkmale durchsehen lassen sich nur: "
                + string.Join(", ", NooseiRecordTypes.Names(NooseiUse.List)) + ".", null, true);
        }

        var rows = await LoadAsync(clr, context.Scope, context.Actor, cancellationToken);
        if (rows is null)
        {
            return new NooseiToolResult($"Für {german} steht keine Merkmalssuche zur Verfügung.", null, true);
        }

        // the wanted-board threshold is admin-set; read it (cached ~10 s) only when the board filter is actually
        // asked for, so auf_fahndungsliste means the same set the Fahndung page shows, not just the manual flag
        var wantsBoard = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("auf_fahndungsliste", out var board) && board.ValueKind == JsonValueKind.True;
        var boardThreshold = wantsBoard
            ? (await settings.GetAsync(cancellationToken)).WantedBoardMinHazard
            : HazardLevel.Critical;
        var filter = Filter.From(arguments, boardThreshold);
        var matches = rows.Where(filter.Matches)
            .OrderByDescending(r => r.Score ?? -1)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var headline = matches.Count == 1
            ? $"1 {NooseiRecordTypes.German(clr)} entspricht den Merkmalen{filter.Describe()}."
            : $"{matches.Count} {NooseiRecordTypes.Plural(clr)} entsprechen den Merkmalen{filter.Describe()}.";
        if (matches.Count == 0)
        {
            return new NooseiToolResult(headline);
        }

        var countOnly = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("nur_anzahl", out var only) && only.ValueKind == JsonValueKind.True;
        if (countOnly)
        {
            return new NooseiToolResult(headline);
        }

        var max = NooseiLimits.Count(arguments, "max", 15);
        var shown = matches.Take(max).ToList();
        var sb = new StringBuilder(headline).AppendLine();
        foreach (var row in shown)
        {
            sb.Append("• ").Append(row.Title)
                .Append(" | Aktenzeichen: ").Append(string.IsNullOrWhiteSpace(row.CaseNumber) ? "—" : row.CaseNumber);
            // a law has no classification at all; printing "Unbekannt" would read as one that was never set
            if (row.Classification is { } classification)
            {
                sb.Append(" | Einstufung: ").Append(ClassificationDisplay.Name(classification));
            }
            if (row.Score is { } score)
            {
                sb.Append(" | Bedrohungs-Score: ").Append(score);
            }
            if (row.Status is { Length: > 0 } state)
            {
                sb.Append(" | Status: ").Append(state);
            }
            if (row.Extra is { Length: > 0 } extra)
            {
                sb.Append(" | ").Append(extra);
            }
            if (row.Secrecy != DocumentClassification.None)
            {
                sb.Append(" | Verschlusssache");
            }
            sb.Append(" | id=").Append(row.Id).AppendLine();
        }
        if (matches.Count > shown.Count)
        {
            sb.Append("(… ").Append(matches.Count - shown.Count).AppendLine(" weitere)");
        }

        var refs = shown.Select(r => new LlmContextRef(clr, r.Id, r.Title)).ToList();
        return new NooseiToolResult(NooseiLimits.Clip(sb.ToString()), refs);
    }

    /// <summary>Loads one type through its own list service, which applies the viewer's scope.</summary>
    private async Task<List<Row>?> LoadAsync(
        string clr, ViewerScope scope, ClaimsPrincipal actor, CancellationToken cancellationToken) => clr switch
    {
        nameof(Data.Entities.People.Person) => (await people.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(p => new Row(p.Id, p.Name, p.CaseNumber, p.Classification, p.SecrecyLevel, p.ThreatScore,
                p.ModifiedAt, p.CreatedAt, Wanted(p), p.EffectiveLifeStatus, p.IsWanted))
            .ToList(),
        nameof(Data.Entities.Factions.Faction) => (await factions.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(f => new Row(f.Id, f.Name, f.CaseNumber, f.Classification, f.SecrecyLevel, f.ThreatScore,
                f.ModifiedAt, f.CreatedAt))
            .ToList(),
        nameof(Data.Entities.Groups.PersonGroup) => (await groups.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(g => new Row(g.Id, g.Name, g.CaseNumber, g.Classification, g.SecrecyLevel, null,
                g.ModifiedAt, g.CreatedAt))
            .ToList(),
        nameof(Data.Entities.Parties.Party) => (await parties.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(p => new Row(p.Id, p.Name, p.CaseNumber, p.Classification, p.SecrecyLevel, null,
                p.ModifiedAt, p.CreatedAt))
            .ToList(),
        nameof(Data.Entities.DrugRoutes.DrugRoute) => (await drugRoutes.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(r => new Row(r.Id, r.Designation, r.CaseNumber, r.Classification, r.SecrecyLevel, null,
                r.ModifiedAt, r.CreatedAt, string.IsNullOrWhiteSpace(r.Substance) ? null : "Substanz: " + r.Substance))
            .ToList(),
        nameof(Data.Entities.Cases.Case) => (await cases.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(c => new Row(c.Id, c.Title, c.CaseNumber, c.Classification, c.SecrecyLevel, null,
                c.ModifiedAt, c.CreatedAt, "Status: " + CaseStatusDisplay.Name(c.Status)))
            .ToList(),
        nameof(Data.Entities.Operations.Operation) => (await operations.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(o => new Row(o.Id, o.Title, o.CaseNumber, o.Classification, o.SecrecyLevel, null,
                o.ModifiedAt, o.CreatedAt, "Status: " + OperationStatusDisplay.Name(o.Status)))
            .ToList(),
        // the one stock without any classification: no case number, no secrecy, so both stay empty rather than
        // being filled with a default that reads like a value somebody set
        nameof(Data.Entities.Common.Law) => (await laws.GetListAsync(cancellationToken, scope.PartnerAgency, scope.MeId))
            .Select(l => new Row(l.Id, $"{l.Paragraph} {l.Title}".Trim(), string.Empty, null,
                DocumentClassification.None, null, l.ModifiedAt, l.CreatedAt,
                string.IsNullOrWhiteSpace(l.LawBook) ? null : "Gesetzbuch: " + l.LawBook))
            .ToList(),

        nameof(Data.Entities.Taskforces.Taskforce) => (await taskforces.GetListAsync(
                scope.MayAllTaskforces, scope.MeId, cancellationToken: cancellationToken, partnerAgency: scope.PartnerAgency))
            .Select(t => new Row(t.Id, t.Name, t.CaseNumber, null,
                t.IsClassified ? DocumentClassification.Leadership : DocumentClassification.None, null,
                t.ModifiedAt, t.CreatedAt, "Umfang: " + TaskforceScopeDisplay.Name(t.Scope),
                Status: TaskforceStatusDisplay.Name(t.Status)))
            .ToList(),
        nameof(Data.Entities.Common.Document) => (await documents.GetListAsync(
                scope.AsDocumentScope(), cancellationToken, scope.PartnerAgency, scope.MeId))
            .Select(d => new Row(d.Id, d.Title, string.Empty, null, d.Classification, null,
                d.Refreshed, d.Refreshed, string.IsNullOrWhiteSpace(d.Category) ? null : "Kategorie: " + d.Category))
            .ToList(),
        nameof(Data.Entities.Meetings.Meeting) => (await meetings.GetListAsync(
                scope.MeId, null, null, 0, MaxRows, cancellationToken))
            .Select(m => new Row(m.Id, m.Title, m.CaseNumber, null, DocumentClassification.None, null,
                m.Start, m.Start, $"Beginn: {m.Start:dd.MM.yyyy HH:mm} | Tagesordnungspunkte: {m.AgendaCount}",
                Status: MeetingStatusDisplay.Name(m.Status)))
            .ToList(),
        nameof(Data.Entities.Jobs.Job) => (await jobs.GetTeamBoardAsync(false, actor, cancellationToken))
            .Select(a => new Row(a.Id, a.Title, a.CaseNumber, null, DocumentClassification.None, null,
                a.DoneAt, a.DueDate ?? DateTime.UtcNow,
                a.AssignedCodenames.Count > 0 ? "Zugeteilt: " + string.Join(", ", a.AssignedCodenames) : null,
                Status: JobStatusDisplay.Name(a.Status)))
            .ToList(),
        // fail-closed by its own helper: a partner gets an empty roster, not a refusal
        nameof(Data.Entities.Informants.Informant) => (await informants.GetListAsync(actor, cancellationToken))
            .Select(i => new Row(i.Id, i.Name, i.CaseNumber, null, DocumentClassification.Leadership, null,
                null, DateTime.UtcNow,
                $"Zuverlässigkeit: {InformantEnumDisplay.Reliability(i.Reliability)}"
                    + (string.IsNullOrWhiteSpace(i.HandlerName) ? null : $" | Führung: {i.HandlerName}"),
                Status: InformantEnumDisplay.Status(i.Status)))
            .ToList(),
        nameof(Data.Entities.Evidence.EvidenceItem) => (await evidence.GetItemsAsync(null, null, cancellationToken))
            .Select(e => new Row(e.Item.Id, e.Item.Name, string.Empty, null, DocumentClassification.None, null,
                e.Item.ModifiedAt, e.Item.CreatedAt,
                $"Bestand: {e.OnHand}"
                    + (string.IsNullOrWhiteSpace(e.Item.Category) ? null : $" | Kategorie: {e.Item.Category}")))
            .ToList(),
        nameof(Data.Entities.Common.LibraryFile) => (await library.GetListAsync(scope.AsDocumentScope(), cancellationToken))
            .Select(f => new Row(f.Id, f.Title, string.Empty, null,
                RecordVisibility.LevelOf(f.IsClassified, f.IsTRUClassified, f.IsHRBClassified), null,
                f.ModifiedAt, f.CreatedAt, string.IsNullOrWhiteSpace(f.Category) ? null : "Kategorie: " + f.Category))
            .ToList(),
        // the roster tier already dropped the reason before this ever sees the row
        nameof(Data.Entities.Absences.Absence) => (await absences.GetListAsync(
                actor, AbsenceViewScope.All, null, null, cancellationToken))
            .Select(a => new Row(a.Id, $"{a.Codename} {a.FromDate:dd.MM.yyyy}–{a.ToDate:dd.MM.yyyy}", string.Empty,
                null, DocumentClassification.None, null, null, a.FromDate.ToDateTime(TimeOnly.MinValue),
                $"Tage: {a.Days}", Status: AbsenceCategoryDisplay.Name(a.Category)))
            .ToList(),
        nameof(Data.Entities.Announcements.Announcement) => (await announcements.GetBoardAsync(actor, cancellationToken))
            .Select(a => new Row(a.Id, a.Title, a.CaseNumber, null, DocumentClassification.None, null,
                a.CreatedAt, a.CreatedAt,
                (a.Important ? "Wichtig | " : null) + "Zielgruppe: " + a.TargetDisplay))
            .ToList(),
        nameof(Data.Entities.Financing.FinancingRequest) => (await financing.GetVisibleAsync(null, actor, cancellationToken))
            .Select(f => new Row(f.Request.Id, $"Antrag {f.AgentCodename}", f.Request.CaseNumber, null,
                DocumentClassification.None, null, f.Request.ModifiedAt, f.Request.CreatedAt,
                $"Zuschuss: {f.Request.RequestedSubsidy:N0} $",
                Status: FinancingStatusDisplay.Name(f.Request.Status)))
            .ToList(),
        nameof(Data.Entities.Food.FoodRequest) => (await food.GetVisibleAsync(null, actor, cancellationToken))
            .Select(f => new Row(f.Request.Id, $"Anforderung {f.AgentCodename}", f.Request.CaseNumber, null,
                DocumentClassification.None, null, f.Request.ModifiedAt, f.Request.CreatedAt,
                "Angefordert: " + FoodService.Summary(f.Request.FoodRequested, f.Request.DrinkRequested),
                Status: FoodRequestStatusDisplay.Name(f.Request.Status)))
            .ToList(),
        nameof(Data.Entities.Recruiting.Bewerbung) => (await ApplicationsAsync(actor, cancellationToken))
            .Select(b => new Row(b.Id, b.Name, b.CaseNumber, null, DocumentClassification.None, null,
                b.ModifiedAt, b.SubmittedAt,
                string.IsNullOrWhiteSpace(b.AssignedAgentName) ? null : "Zuständig: " + b.AssignedAgentName,
                Status: BewerbungStatusDisplay.Name(b.Status)))
            .ToList(),
        nameof(Data.Entities.Activities.AgentActivity) => (await activities.GetListAsync(scope, cancellationToken: cancellationToken))
            .Select(a => new Row(a.Id, a.Title, string.Empty, null, DocumentClassification.None, null,
                null, a.CreatedAt,
                $"Datum: {a.ActivityDate:dd.MM.yyyy}"
                    + (string.IsNullOrWhiteSpace(a.Kind) ? "" : " | Art: " + a.Kind)
                    + (string.IsNullOrWhiteSpace(a.OwnerName) ? "" : " | " + a.OwnerName)))
            .ToList(),
        // visible to every internal agent (its record gate is a bare existence check), so an unscoped list is consistent
        nameof(Data.Entities.Abductions.AgentAbduction) => (await abductions.GetListAsync(cancellationToken))
            .Select(a => new Row(a.Abduction.Id, "Entführung " + a.VictimCodename, a.Abduction.CaseNumber, null,
                DocumentClassification.None, null, a.Abduction.ModifiedAt, a.Abduction.CreatedAt,
                $"Zeitpunkt: {a.Abduction.Timestamp:dd.MM.yyyy}"
                    + (string.IsNullOrWhiteSpace(a.PerpetratorName) ? "" : " | Täter: " + a.PerpetratorName),
                Status: AbductionOutcomeDisplay.Name(a.Abduction.Outcome)))
            .ToList(),
        // leadership-only, exactly as the record gate is; a plain agent gets an empty list, never a refusal
        nameof(Data.Entities.Common.SituationReport) => scope.MayClassifiedRead
            ? (await situationReports.GetArchiveAsync(cancellationToken))
                .Select(r => new Row(r.Id, r.Title, string.Empty, null, DocumentClassification.Leadership, null,
                    null, r.GeneratedAt, $"Zeitraum: {r.Month:00}/{r.Year}"))
                .ToList()
            : [],
        nameof(Data.Entities.Personnel.TrainingModule) => (await trainingModules.GetActiveAsync(cancellationToken))
            .Select(m => new Row(m.Id, m.Name, string.Empty, null, DocumentClassification.None, null,
                m.ModifiedAt, m.CreatedAt,
                string.IsNullOrWhiteSpace(m.Description) ? null : Free(m.Description)))
            .ToList(),
        // both fail closed by their own helper: a stranger gets nothing, not a distinct refusal
        nameof(Data.Entities.CounterIntel.CounterIntelRule) => (await CounterIntelRulesAsync(actor, cancellationToken))
            .Select(r => new Row(r.Id, r.Name, string.Empty, null, DocumentClassification.Leadership, null,
                null, DateTime.UtcNow,
                (r.IsActive ? "aktiv" : "inaktiv") + " | Schwere: " + CounterIntelSeverityDisplay.Name(r.Severity)
                    + (string.IsNullOrWhiteSpace(r.Description) ? "" : " | " + Free(r.Description))))
            .ToList(),
        nameof(Data.Entities.Feedback.Feedback) => (await FeedbackAsync(actor, cancellationToken))
            .Select(f => new Row(f.Id, FeedbackKindDisplay.Name(f.Kind), string.Empty, null,
                DocumentClassification.None, null, null, f.CreatedAt,
                "von " + f.AgentCodename + ": " + Free(f.Text),
                Status: FeedbackStatusDisplay.Name(f.Status)))
            .ToList(),
        _ => null,
    };

    /// <summary>Counter-intel rules, or nothing for an agent who may not see them. Empty and refused read alike.</summary>
    private async Task<IReadOnlyList<CounterIntelRuleView>> CounterIntelRulesAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            return await counterIntelRules.GetAllAsync(actor, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every report for an internal agent, one's own otherwise — the two arms of feedback visibility.</summary>
    private async Task<IReadOnlyList<FeedbackRow>> FeedbackAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            return await feedback.GetInboxAsync(actor, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return await feedback.GetMyAsync(actor, cancellationToken);
        }
    }

    /// <summary>Applications, or nothing at all for an agent without recruiting access.</summary>
    /// <remarks>An empty roster and a refused one must read the same: the guard throws, and turning that into a
    /// distinct message would tell an ordinary agent that applications exist and are being kept from them.</remarks>
    private async Task<List<Data.Entities.Recruiting.Bewerbung>> ApplicationsAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            return await applications.ListAsync(actor, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Strips mention tokens so a raw @{Typ:Id} never reaches the model as free text.</summary>
    private static string Free(string? text) => MentionParser.Strip(text).Trim();

    /// <summary>The wanted note of a person, else their life status — one extra column, whichever says more.</summary>
    private static string Wanted(Data.Entities.People.Person person)
        => person.IsWanted
            ? "Zur Fahndung" + (string.IsNullOrWhiteSpace(person.WantedReason) ? string.Empty : ": " + person.WantedReason)
            : "Lebensstatus: " + LifeStatusDisplay.Name(person.EffectiveLifeStatus);

    /// <summary>One record reduced to what can be filtered on, whatever type it came from.</summary>
    private sealed record Row(
        string Id, string Title, string CaseNumber, Classification? Classification, DocumentClassification Secrecy,
        int? Score, DateTime? ModifiedAt, DateTime CreatedAt, string? Extra = null, LifeStatus? LifeStatus = null,
        bool IsWanted = false, string? Status = null)
    {
        /// <summary>When the record was last touched. The audit interceptor stamps <c>ModifiedAt</c> only on an
        /// update, so a record created yesterday and never edited still has none — falling through to
        /// <c>CreatedAt</c> is what keeps a "changed in the last N days" question from missing the newest files.</summary>
        public DateTime TouchedAt => ModifiedAt ?? CreatedAt;
    }

    private sealed record Filter(
        Classification? Classification, LifeStatus? LifeStatus, int? ScoreMin, int? ScoreMax,
        int? ChangedWithinDays, bool ClassifiedOnly, bool WantedOnly, bool OnWantedBoard,
        HazardLevel BoardThreshold, string? Status)
    {
        public static Filter From(JsonElement args, HazardLevel boardThreshold) => new(
            ParseLabel(NooseiLimits.Text(args, "einstufung"), ClassificationDisplay.All,
                ClassificationDisplay.Name, ClassificationDisplay.DefaultName),
            ParseLabel(NooseiLimits.Text(args, "lebensstatus"), LifeStatusDisplay.All,
                LifeStatusDisplay.Name, LifeStatusDisplay.DefaultName),
            Number(args, "score_min"),
            Number(args, "score_max"),
            Number(args, "geaendert_seit_tagen"),
            Flag(args, "nur_verschlusssache"),
            Flag(args, "nur_fahndung"),
            Flag(args, "auf_fahndungsliste"),
            boardThreshold,
            NooseiLimits.Text(args, "status"));

        public bool Matches(Row row)
            => (Classification is not { } c || row.Classification == c)
                && (LifeStatus is not { } l || row.LifeStatus == l)
                && (ScoreMin is not { } min || row.Score >= min)
                && (ScoreMax is not { } max || row.Score <= max)
                && (ChangedWithinDays is not { } days || row.TouchedAt >= DateTime.UtcNow.AddDays(-days))
                && (!ClassifiedOnly || row.Secrecy != DocumentClassification.None)
                && (!WantedOnly || row.IsWanted)
                // same rule the Fahndung page uses, via the shared helper, so the two never diverge
                && (!OnWantedBoard || WantedBoard.IsOnBoard(row.IsWanted, row.Score, BoardThreshold))
                // matched against the rendered German label, which is what the model was shown in the first place
                && (Status is not { Length: > 0 } state
                    || string.Equals(row.Status, state, StringComparison.OrdinalIgnoreCase));

        /// <summary>Repeats the applied filters, so a bare count cannot be read as a different question's answer.</summary>
        public string Describe()
        {
            var parts = new List<string>(6);
            if (Classification is { } c) { parts.Add("Einstufung " + ClassificationDisplay.Name(c)); }
            if (LifeStatus is { } l) { parts.Add("Lebensstatus " + LifeStatusDisplay.Name(l)); }
            if (ScoreMin is { } min) { parts.Add($"Score ab {min}"); }
            if (ScoreMax is { } max) { parts.Add($"Score bis {max}"); }
            if (ChangedWithinDays is { } days) { parts.Add($"in den letzten {days} Tagen angelegt oder geändert"); }
            if (ClassifiedOnly) { parts.Add("nur Verschlusssachen"); }
            if (WantedOnly) { parts.Add("nur zur Fahndung ausgeschrieben"); }
            if (OnWantedBoard) { parts.Add("auf der Fahndungsliste"); }
            if (Status is { Length: > 0 } state) { parts.Add("Status " + state); }
            return parts.Count == 0 ? " (ohne Einschränkung)" : " (" + string.Join(", ", parts) + ")";
        }

        private static bool Flag(JsonElement args, string name)
            => args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.True;

        private static int? Number(JsonElement args, string name)
            => args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number)
                    ? number
                    : null;

        /// <summary>Matches a German label back to its enum value. The live label is admin-overridable, so the
        /// code default and the raw member name are accepted too — otherwise a renamed label breaks the tool.</summary>
        private static T? ParseLabel<T>(string? text, IReadOnlyList<T> all, Func<T, string> label, Func<T, string> fallback)
            where T : struct, Enum
        {
            if (text is null) { return null; }
            foreach (var value in all)
            {
                if (text.Equals(label(value), StringComparison.OrdinalIgnoreCase)
                    || text.Equals(fallback(value), StringComparison.OrdinalIgnoreCase)
                    || text.Equals(value.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
            return null;
        }
    }
}
