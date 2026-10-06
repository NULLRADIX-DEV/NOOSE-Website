using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Actor and derived scope for one turn. Built fresh from the live principal, never rehydrated from storage —
/// a conversation resumed after a demotion is answered at the new scope.</summary>
public sealed record NooseiToolContext(ClaimsPrincipal Actor, ViewerScope Scope)
{
    public static NooseiToolContext From(ClaimsPrincipal actor) => new(actor, ViewerScope.From(actor));
}

/// <summary>German plain-text result of one tool call, already clipped, plus what it touched.</summary>
public sealed record NooseiToolResult(string Text, IReadOnlyList<LlmContextRef>? Refs = null, bool IsError = false)
{
    /// <summary>Deliberately identical for "does not exist" and "you may not see it" — anything else turns
    /// every tool into an existence oracle for classified records.</summary>
    public static NooseiToolResult NotFound()
        => new("Akte nicht gefunden oder für dich nicht sichtbar.", null, true);

    public static NooseiToolResult Empty(string what) => new($"Keine {what} gefunden.");
}

/// <summary>One read capability NOOSEI may call. Every implementation filters by the actor's own scope.</summary>
public interface INooseiTool
{
    string Name { get; }

    string Description { get; }

    JsonElement ParameterSchema { get; }

    Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>Caps every tool honours, so a single result cannot blow the context or the token budget.</summary>
public static class NooseiLimits
{
    public const int MaxToolResultChars = 6_000;
    public const int MaxCompactRecordChars = 2_500;

    /// <summary>Budget of the content tools, which exist precisely to deliver what the record budget cuts off.</summary>
    public const int MaxContentResultChars = 8_000;

    public const int MaxRowsPerTool = 40;
    public const int MaxSnippetChars = 160;

    /// <summary>Trim a tool result to its budget.</summary>
    public static string Clip(string text, int max = MaxToolResultChars)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= max ? text
            : text[..max] + "\n[…] (gekürzt)";

    /// <summary>Reads a string argument; missing, wrong-typed or blank all yield null.</summary>
    public static string? Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

    /// <summary>Reads a bounded integer argument.</summary>
    public static int Count(JsonElement args, string name, int fallback, int max = MaxRowsPerTool)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? Math.Clamp(number, 1, max)
                : fallback;

    public static IReadOnlyList<string> Strings(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!.Trim())
            .ToList();
    }

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>All tools NOOSEI may offer, resolved from DI.</summary>
public sealed class NooseiToolRegistry
{
    private readonly Dictionary<string, INooseiTool> _byName;

    private readonly IReadOnlyList<LlmToolDefinition> _legal;

    public NooseiToolRegistry(IEnumerable<INooseiTool> tools)
    {
        _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Definitions = Build(t => !LegalToolGate.ToolNames.Contains(t.Name));
        _legal = Build(t => LegalToolGate.ToolNames.Contains(t.Name));
    }

    private List<LlmToolDefinition> Build(Func<INooseiTool, bool> include)
        => _byName.Values
            .Where(include)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new LlmToolDefinition(t.Name, t.Description, t.ParameterSchema))
            .ToList();

    /// <summary>The agency chat's tools. Stable order: the tool block is the cached prompt prefix, and reordering it would cost cache hits.</summary>
    public IReadOnlyList<LlmToolDefinition> Definitions { get; }

    /// <summary>The tools of one chat mode; computed once, so each mode keeps its own stable prefix.</summary>
    public IReadOnlyList<LlmToolDefinition> DefinitionsFor(NooseiChatMode mode)
        => mode == NooseiChatMode.Legal ? _legal : Definitions;

    public INooseiTool? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>A tool of this mode only: a name the mode was never offered is unknown, even when the model invents it.</summary>
    public INooseiTool? Find(string name, NooseiChatMode mode)
        => (mode == NooseiChatMode.Legal) == LegalToolGate.ToolNames.Contains(name) ? Find(name) : null;
}

/// <summary>What NOOSEI may do with a record type.</summary>
/// <remarks>Four axes, not one flag, because the tools genuinely differ in reach. With a single list of "known
/// types" the narrowest tool decided for all of them, so the offering stood at eight kinds while the search, the
/// chronicle and the law module already carried more.</remarks>
[Flags]
public enum NooseiUse
{
    /// <summary>Nameable in a result, nothing else.</summary>
    None = 0,

    /// <summary>Openable as a record — <c>lies_akte</c>, <c>hole_kurzbrief</c>, <c>lies_zeitstrahl</c> and both
    /// graph tools. Requires a branch in <see cref="DossierContextBuilder" />.</summary>
    Read = 1,

    /// <summary>Enumerable by attribute in <c>finde_akten</c>. Requires a scope-filtered list service.</summary>
    List = 2,

    /// <summary>A category <c>suche_akten</c> may narrow to; <see cref="ISearchService" /> emits a group for it.</summary>
    Search = 4,

    /// <summary>A type <c>letzte_aenderungen</c> may filter on; the chronicle collects it.</summary>
    Chronicle = 8,
}

/// <summary>One record type as the model names it, and what the tools may do with it.</summary>
public sealed record NooseiRecordType(string German, string Clr, string Plural, NooseiUse Use = NooseiUse.None);

/// <summary>Maps the German record type names the model sees to the CLR names the services expect, and decides
/// which tool accepts which type.</summary>
/// <remarks>
/// German name, plural and the searchable set are not decided here — they come from <see cref="SearchCatalog" />,
/// which already carries a row per category. Two label tables over the same categories drift, and a drifted label
/// reaches the model as a record kind that does not exist. The searchable set is likewise the catalog's
/// <see cref="SearchTraits.Assistant" /> flag rather than a copy of it: derived, the two cannot disagree, which is
/// stronger than the drift test that used to compare them.
/// </remarks>
public static class NooseiRecordTypes
{
    /// <summary>What a tool may do with a type beyond searching it.</summary>
    /// <remarks>Only these three flags live here, because each is a promise about a service this file cannot see:
    /// <see cref="NooseiUse.Read" /> needs a branch in <see cref="DossierContextBuilder" /> and an arm in
    /// <see cref="Visibility.IsRecordVisibleAsync" />, <see cref="NooseiUse.List" /> a branch in the filter tool,
    /// <see cref="NooseiUse.Chronicle" /> collection by the chronicle. A drift test guards each.</remarks>
    private static readonly Dictionary<string, NooseiUse> Uses = new(StringComparer.Ordinal)
    {
        [nameof(Person)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Faction)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(PersonGroup)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Party)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Operation)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Case)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Law)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Taskforce)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Document)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,

        // duty, personnel, ledger and administration: openable through DossierContextBuilder.Operations.cs
        [nameof(Job)] = NooseiUse.Read | NooseiUse.List | NooseiUse.Chronicle,
        [nameof(Meeting)] = NooseiUse.Read | NooseiUse.List,
        [nameof(Bewerbung)] = NooseiUse.Read | NooseiUse.List,
        [nameof(Informant)] = NooseiUse.Read | NooseiUse.List,
        [nameof(EvidenceItem)] = NooseiUse.Read | NooseiUse.List,
        [nameof(FinancingRequest)] = NooseiUse.Read | NooseiUse.List,
        [nameof(Announcement)] = NooseiUse.Read | NooseiUse.List,
        [nameof(Absence)] = NooseiUse.Read | NooseiUse.List,
        [nameof(LibraryFile)] = NooseiUse.Read | NooseiUse.List,

        // readable but not enumerable: no scope-filtered list, or an area/kalender already enumerates them
        [nameof(Appointment)] = NooseiUse.Read,     // lies_kalender lists what is coming up
        [nameof(Agent)] = NooseiUse.Read,           // the roster is lies_bereich personal
        [nameof(EvidenceEntry)] = NooseiUse.Read,   // lies_bereich asservatenkammer
        [nameof(KassenBuchung)] = NooseiUse.Read,   // lies_bereich kasse
        [nameof(Request)] = NooseiUse.Read,         // no scope-filtered list; open one by id only

        // readable and enumerable by attribute — each has a scope-/actor-filtered list or is visible to every
        // internal agent; the matching branch lives in FilterRecordsTool
        [nameof(AgentAbduction)] = NooseiUse.Read | NooseiUse.List,
        [nameof(SituationReport)] = NooseiUse.Read | NooseiUse.List,
        [nameof(AgentActivity)] = NooseiUse.Read | NooseiUse.List,
        [nameof(TrainingModule)] = NooseiUse.Read | NooseiUse.List,
        [nameof(CounterIntelRule)] = NooseiUse.Read | NooseiUse.List,
        [nameof(Data.Entities.Feedback.Feedback)] = NooseiUse.Read | NooseiUse.List,

        // The one type of the public area the assistant opens as a record. It has its own case number, its own
        // page and a manual link to the file it was taken over into, so "which tips came in about X" is a question
        // about content that only Read can answer. Deliberately no List: the filter tool's whole vocabulary
        // (classification, threat score, life status) is record-shaped and says nothing about a tip. Deliberately
        // no Chronicle: it is a closed list of ten record kinds that deliberately excludes tips and tickets.
        [nameof(Hinweis)] = NooseiUse.Read,
    };

    /// <summary>Every category, in catalog order, with the capabilities decided above.</summary>
    /// <remarks>Order is load-bearing: it decides the order inside every schema enum, and the tool block is the
    /// cached prompt prefix. It is the catalog's order, so appending a category there appends it here too.</remarks>
    private static readonly NooseiRecordType[] All = SearchCatalog.Categories
        .Select(c => new NooseiRecordType(c.German, c.Clr, c.Plural,
            Uses.GetValueOrDefault(c.Clr) | (c.Has(SearchTraits.Assistant) ? NooseiUse.Search : NooseiUse.None)))
        .ToArray();

    private static readonly Dictionary<string, NooseiRecordType> ByGerman =
        All.ToDictionary(t => t.German, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, NooseiRecordType> ByClr =
        All.ToDictionary(t => t.Clr, StringComparer.Ordinal);

    /// <summary>The types a capability was configured for, so a test can catch a key naming no category at all.</summary>
    public static IReadOnlyCollection<string> ConfiguredClrs => Uses.Keys;

    /// <summary>Search categories that carry no <see cref="NooseiUse.Read" /> yet are still reachable — through a
    /// content section of <c>lies_akteninhalt</c>, an operating area of <c>lies_bereich</c>, or a personal tool —
    /// each mapped to the path that reaches it.</summary>
    /// <remarks>With the Read set and <see cref="NotAssistantReadable" /> this decides every catalog category. The
    /// coverage test fails on any category that is neither Read-capable, listed here, nor excluded — that is the
    /// guard that keeps the assistant reading everything the asking agent can.</remarks>
    public static readonly IReadOnlyDictionary<string, string> ReachableWithoutRead =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Comment"] = "kommentare (lies_akteninhalt)",
            ["Source"] = "quellen (lies_akteninhalt)",
            ["Followup"] = "wiedervorlagen (lies_akteninhalt)",
            ["Link"] = "verknuepfungen (lies_akteninhalt)",
            ["CustomFieldValue"] = "zusatzfelder (lies_akteninhalt)",
            ["PersonDoc"] = "doks (lies_akteninhalt)",
            ["Observation"] = "observationen (lies_akteninhalt)",
            ["TaskforceMessage"] = "chat (lies_akteninhalt)",
            ["MeetingAgendaItem"] = "tagesordnung (lies_akteninhalt)",
            ["BewerbungMessage"] = "nachrichten (lies_akteninhalt)",
            ["AgentNote"] = "vermerke (lies_akteninhalt)",
            ["InformantMeeting"] = "treffen (lies_akteninhalt)",
            ["AccessLog"] = "zugriffe (lies_akteninhalt)",
            ["Tag"] = "stichworte (lies_akteninhalt / lies_bereich)",
            ["WatchlistEntry"] = "meine_akten",
            ["Notification"] = "benachrichtigungen (lies_bereich)",
            ["FinancingItem"] = "vorlagen (lies_bereich)",
            ["DocumentTemplate"] = "vorlagen / bewerbungswesen (lies_bereich)",
            ["ActivityTemplate"] = "vorlagen (lies_bereich)",
            ["PersonnelTemplate"] = "vorlagen (lies_bereich)",
            ["DocTemplate"] = "vorlagen (lies_bereich)",
            ["KassenBuchungVorlage"] = "vorlagen (lies_bereich)",
            ["BewerbungTest"] = "bewerbungswesen (lies_bereich)",
            ["Bewerbungssperre"] = "bewerbungswesen (lies_bereich)",
            ["AuditLog"] = "lies_zeitstrahl / letzte_aenderungen",

            // public area: reachable through the section of the file they were published from, or through the
            // operating area that reports the agency's outward voice. None of them is opened as a record —
            // Read would cost a dossier branch and a visibility arm apiece, and a section discharges all of them.
            ["OeffentlicheFahndung"] = "oeffentlich (lies_akteninhalt)",
            ["OeffentlichesFraktionsprofil"] = "oeffentlich (lies_akteninhalt)",
            ["FahndungEinspruch"] = "oeffentlich (lies_akteninhalt)",
            ["Ticket"] = "tickets (lies_bereich)",
            ["Pressemitteilung"] = "oeffentlichkeit (lies_bereich)",
            ["OeffentlicheSeite"] = "oeffentlichkeit (lies_bereich)",
            ["OeffentlicheWarnung"] = "oeffentlichkeit (lies_bereich)",
            ["OeffentlicherLagebericht"] = "oeffentlichkeit (lies_bereich)",

            // the handbook is not a record and is not opened as one: an article is an answer to a question, and
            // the tool that answers questions reads the body, the walkthrough and the glossary in one pass
            ["HandbookArticle"] = "handbuch (schlage_nach)",
            ["GlossaryTerm"] = "glossar (schlage_nach)",
        };

    /// <summary>Search categories NOOSEI deliberately does not read as a record, each with the reason.</summary>
    /// <remarks>The counterpart to <see cref="SearchCatalog" />'s NotSearchable: personal UI state, the assistant's
    /// own chats, and cost/operating meta behind the AI-owner axis.</remarks>
    public static readonly IReadOnlyDictionary<string, string> NotAssistantReadable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NooseiConversation"] = "eigene KI-Chats; sie im Chat zu lesen wäre zirkulär",
            ["SavedSearch"] = "reine UI-Voreinstellung ohne Ermittlungsinhalt",
            ["GraphCanvasLayout"] = "reine UI-Voreinstellung ohne Ermittlungsinhalt",
            ["TextSnippet"] = "private Schreibhilfe eines Agenten, kein Ermittlungsinhalt",
            ["LlmRequestLog"] = "NOOSEI-Kosten- und Betriebsmeta hinter der KI-Eigner-Achse",
            // searchable, but not openable: the hit already carries everything the row holds
            ["RadioChannel"] = "die Trefferzeile ist schon der ganze Kanal — Frequenz und Bezeichnung; "
                + "ein Aktenaufruf hätte nichts hinzuzufügen",
        };

    /// <summary>The enum values offered where a type must be openable as a record.</summary>
    public static readonly string EnumJson = Json(NooseiUse.Read);

    /// <summary>Types <c>finde_akten</c> can enumerate by attribute.</summary>
    public static readonly string ListableEnumJson = Json(NooseiUse.List);

    /// <summary>Categories <c>suche_akten</c> can narrow to.</summary>
    public static readonly string SearchableEnumJson = Json(NooseiUse.Search);

    /// <summary>Types <c>letzte_aenderungen</c> can filter on.</summary>
    public static readonly string ChronicleEnumJson = Json(NooseiUse.Chronicle);

    /// <summary>CLR name for a German type name; null when the name is unknown.</summary>
    public static string? Clr(string? german) => Find(german)?.Clr;

    /// <summary>CLR name only when the type also carries <paramref name="required" />.</summary>
    /// <remarks>Every tool that takes a type argument goes through this overload. A schema enum is a hint, not a
    /// guarantee — and a type that slips past it reaches a service that never gated it:
    /// <see cref="Visibility.IsRecordVisibleAsync" /> answers "visible" for every type it does not know.</remarks>
    public static string? Clr(string? german, NooseiUse required)
        => Find(german) is { } type && type.Use.HasFlag(required) ? type.Clr : null;

    /// <summary>German name of a type; never the CLR name, so no English label ever reaches the model.</summary>
    public static string German(string clr)
        => ByClr.TryGetValue(clr, out var type) ? type.German : "Eintrag";

    /// <summary>German plural of a record type, for count sentences.</summary>
    public static string Plural(string clr)
        => ByClr.TryGetValue(clr, out var type) ? type.Plural : German(clr);

    /// <summary>True when <c>lies_akte</c> accepts this type. A hit of any other type must not carry an id.</summary>
    public static bool IsReadable(string clr) => Can(clr, NooseiUse.Read);

    public static bool Can(string? clr, NooseiUse use)
        => clr is not null && ByClr.TryGetValue(clr, out var type) && type.Use.HasFlag(use);

    /// <summary>German names carrying a capability, for schema enums and for saying what a tool does accept.</summary>
    public static IReadOnlyList<string> Names(NooseiUse use)
        => All.Where(t => t.Use.HasFlag(use)).Select(t => t.German).ToList();

    private static NooseiRecordType? Find(string? german)
        => german is not null && ByGerman.TryGetValue(german, out var type) ? type : null;

    private static string Json(NooseiUse use)
        => "[" + string.Join(",", Names(use).Select(n => "\"" + n + "\"")) + "]";
}
