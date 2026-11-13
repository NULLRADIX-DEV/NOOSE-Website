using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>How the gateway must answer before the handler runs (Discord wants an answer within three seconds).</summary>
public enum BotAnswerMode
{
    /// <summary>Defer a new message, then fill it.</summary>
    Reply,

    /// <summary>Defer an edit of the clicked message, then edit it.</summary>
    Update,

    /// <summary>Open a modal right away; the handler must not touch the database.</summary>
    Modal,
}

/// <summary>Maps commands, buttons, menus and modals to the command modules.</summary>
public sealed class DiscordBotRouter(
    RecordCommands recordCommands,
    KnowledgeCommands knowledge,
    PersonalCommands personal,
    LeadershipCommands leadership,
    CaptureCommands capture,
    ITestLockService testLocks)
{
    /// <summary>Pages behind a command; a running test that locks the page locks the command too.</summary>
    private static readonly IReadOnlyDictionary<string, string> CommandPages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dvo"] = "/dienstverordnung",
        ["gesetz"] = "/gesetze",
        ["funk"] = "/funk",
        ["fahndung"] = "/fahndung",
        ["kopfgeld"] = "/fahndung",
        ["fraktion"] = "/fraktionen",
        ["route"] = "/fraktionen",
    };

    /// <summary>Buttons and menus that show what one of those pages shows.</summary>
    private static string? ComponentPage(string action, string[] args) => action switch
    {
        "dvo" or "ackr" => "/dienstverordnung",
        "card" => args.Arg(0) == nameof(Faction) ? "/fraktionen" : "/personen",
        _ => null,
    };

    /// <summary>The refusal when a running test locks this page, else null.</summary>
    private async Task<string?> LockedAsync(string? page, BotCall call)
    {
        if (page is null || await testLocks.GetActiveAsync(call.User) is not { } locked || !locked.Blocks(page))
        {
            return null;
        }
        return $"Gesperrt, solange du den Test „{locked.TestTitle}“ ablegst.";
    }

    /// <summary>Commands whose card may go to the channel with teilen:true.</summary>
    public static readonly IReadOnlySet<string> Shareable = new HashSet<string>(StringComparer.Ordinal)
    {
        "akte", "fahndung", "kopfgeld", "suche", "fraktion", "route", "gesetz", "dvo", "lage", "funk", "agent",
    };

    public static BotAnswerMode ModeForComponent(string action, string[] args) => action switch
    {
        "note" or "fu" or "habs" or "fibi" => BotAnswerMode.Modal,
        "s" or "sc" or "jd" or "fd" or "ack" or "pj" or "pf" or "dates" => BotAnswerMode.Update,
        // from the list the list is redrawn, from a /dvo card a short confirmation
        "ackr" => args.Arg(2) == "l" ? BotAnswerMode.Update : BotAnswerMode.Reply,
        _ => BotAnswerMode.Reply,
    };

    // ---------------------------------------------------------------- slash commands

    public async Task<BotReply> SlashAsync(string name, BotCall call)
        => await LockedAsync(CommandPages.GetValueOrDefault(name), call) is { } refusal
            ? BotReply.Error(refusal)
            : await SlashCoreAsync(name, call);

    private Task<BotReply> SlashCoreAsync(string name, BotCall call) => name switch
    {
        "akte" => recordCommands.AkteAsync(call),
        "fahndung" => recordCommands.FahndungAsync(call),
        "kopfgeld" => recordCommands.KopfgeldAsync(call),
        "suche" => recordCommands.SucheAsync(call),
        "fraktion" => recordCommands.FraktionAsync(call),
        "route" => recordCommands.RouteAsync(call),
        "gesetz" => knowledge.GesetzAsync(call),
        "dvo" => knowledge.DvoAsync(call),
        "fibi" => knowledge.FibiAsync(call),
        "lage" => knowledge.LageAsync(call),
        "funk" => knowledge.FunkAsync(call),
        "fib" => personal.HubAsync(call),
        "abmeldung" => personal.AbmeldungAsync(call),
        "termine" => personal.TermineAsync(call),
        "quittieren" => personal.QuittierenAsync(call),
        "aufgaben" => personal.AufgabenAsync(call),
        "wiedervorlagen" => personal.WiedervorlagenAsync(call),
        "agent" => leadership.AgentAsync(call),
        "abgemeldet" => leadership.AbgemeldetAsync(call),
        "offen" => leadership.OffenAsync(call),
        "inaktiv" => leadership.InaktivAsync(call),
        "notiz" => capture.NotizAsync(call),
        "wiedervorlage" => capture.WiedervorlageAsync(call),
        "aufgabe" => capture.AufgabeAsync(call),
        "schusswaffe" => capture.SchusswaffeAsync(call),
        "alarm" => capture.AlarmAsync(call),
        _ => Task.FromResult(BotReply.Error("Diesen Befehl kenne ich nicht.")),
    };

    public async Task<BotReply> AutocompleteAsync(string name, BotCall call)
        => await LockedAsync(CommandPages.GetValueOrDefault(name), call) is not null
            ? BotReply.Autocomplete([])
            : await AutocompleteCoreAsync(name, call);

    private Task<BotReply> AutocompleteCoreAsync(string name, BotCall call) => (name, call.Options.Focused) switch
    {
        ("akte", _) => recordCommands.AkteSuggestAsync(call),
        ("fahndung", _) => recordCommands.PersonSuggestAsync(call, "name"),
        ("fraktion", _) => recordCommands.FactionSuggestAsync(call),
        ("route", _) => recordCommands.RouteSuggestAsync(call),
        ("gesetz", _) => knowledge.GesetzSuggestAsync(call),
        ("dvo", _) => knowledge.DvoSuggestAsync(call),
        ("funk", _) => knowledge.FunkSuggestAsync(call),
        ("agent", _) => leadership.AgentSuggestAsync(call, "codename"),
        ("aufgabe", "agent") => leadership.AgentSuggestAsync(call, "agent"),
        ("notiz" or "wiedervorlage", _) => capture.AkteSuggestAsync(call),
        _ => Task.FromResult(BotReply.Autocomplete([])),
    };

    // ---------------------------------------------------------------- context menus

    public Task<BotReply> UserCommandAsync(ulong targetUserId, BotCall call) => leadership.ProfileOfDiscordUserAsync(targetUserId, call);

    /// <summary>Opens the modal; reads nothing.</summary>
    public BotReply MessageCommand(CaptureCommands.AttachState state) => capture.AttachStart(state);

    // ---------------------------------------------------------------- buttons and menus

    /// <summary>Modal-opening buttons; no database, no await.</summary>
    public static BotReply ModalFor(string action, string[] args) => action switch
    {
        "note" => BotReply.OpenModal(CaptureCommands.NoteModal(args.Arg(0), args.Arg(1))),
        "fu" => BotReply.OpenModal(CaptureCommands.FollowupModal(args.Arg(0), args.Arg(1))),
        "habs" => BotReply.OpenModal(PersonalCommands.AbsenceModal()),
        "fibi" => BotReply.OpenModal(new NetCord.Rest.ModalProperties(BotIds.Make("mfibi", args.Arg(0)), "Nachfrage an FIBI")
        {
            new NetCord.Rest.LabelProperties("Deine Frage", new NetCord.Rest.TextInputProperties("frage", NetCord.Rest.TextInputStyle.Paragraph)
                .WithRequired(true).WithMaxLength(1500)),
        }),
        _ => BotReply.Error("Unbekannte Aktion."),
    };

    public async Task<BotReply> ComponentAsync(string action, string[] args, IReadOnlyList<string> selected, BotCall call)
        => await LockedAsync(ComponentPage(action, args), call) is { } refusal
            ? BotReply.Error(refusal)
            : await ComponentCoreAsync(action, args, selected, call);

    private Task<BotReply> ComponentCoreAsync(string action, string[] args, IReadOnlyList<string> selected, BotCall call)
    {
        var first = selected.Count > 0 ? selected[0] : null;
        return action switch
        {
            "s" => recordCommands.SearchPageAsync(args.Arg(0), Int(args.Arg(1)), Category(args.Arg(2)), call),
            "sc" => recordCommands.SearchPageAsync(args.Arg(0), 0, Category(first), call),
            "card" => args.Arg(0) == nameof(Faction)
                ? recordCommands.FactionCardAsync(args.Arg(1), call)
                : recordCommands.PersonCardAsync(args.Arg(1), call),
            "watch" => capture.WatchAsync(call, args.Arg(0), args.Arg(1)),
            "jd" => personal.JobDoneAsync(call, args.Arg(0), Int(args.Arg(1))),
            "pj" => personal.AufgabenAsync(call, Int(args.Arg(0))),
            "fd" => personal.FollowupDoneAsync(call, args.Arg(0), Int(args.Arg(1))),
            "pf" => personal.WiedervorlagenAsync(call, Int(args.Arg(0))),
            "ack" => personal.AcknowledgeAnnouncementAsync(call, args.Arg(0)),
            "ackr" => personal.AcknowledgeParagraphAsync(call, args.Arg(0), Int(args.Arg(1)), args.Arg(2) == "l"),
            "dates" => personal.TermineAsync(call, args.Arg(0)),
            "absend" => personal.EndAbsenceAsync(call),
            "hub" => args.Arg(0) switch
            {
                "jobs" => personal.AufgabenAsync(call),
                "fu" => personal.WiedervorlagenAsync(call),
                "ack" => personal.QuittierenAsync(call),
                _ => personal.TermineAsync(call, "heute"),
            },
            "al" => capture.AlarmAnswerAsync(call, args.Arg(0), args.Arg(1)),
            "dvo" => first is null ? Task.FromResult(BotReply.Error("Bitte einen Absatz wählen.")) : knowledge.DvoParagraphAsync(first, call),
            "pick" => capture.AttachPickAsync(call, args.Arg(0), first),
            _ => Task.FromResult(BotReply.Error("Unbekannte Aktion.")),
        };
    }

    // ---------------------------------------------------------------- modals

    public Task<BotReply> ModalAsync(string action, string[] args, IReadOnlyDictionary<string, string> fields, BotCall call)
    {
        string? Field(string key) => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return action switch
        {
            "mnote" => capture.NoteAsync(call, args.Arg(0), args.Arg(1), Field("text")),
            "mfu" => capture.FollowupAsync(call, args.Arg(0), args.Arg(1), Field("wann"), Field("notiz")),
            "mabs" => personal.CreateAbsenceAsync(call, Field("von"), Field("bis"), Field("art"), Field("grund")),
            "mfibi" => knowledge.FibiAsync(call, args.Arg(0), Field("frage")),
            "mattach" => capture.AttachSubmitAsync(call, args.Arg(0), Field("akte"), Field("anmerkung")),
            _ => Task.FromResult(BotReply.Error("Unbekannte Eingabe.")),
        };
    }

    private static int Int(string value) => int.TryParse(value, out var n) ? n : 0;

    private static string? Category(string? value) => string.IsNullOrEmpty(value) || value == "*" ? null : value;

    /// <summary>Kept for the record cards linked from a preview.</summary>
    public Task<BotReply> CardAsync(string type, string id, BotCall call)
        => type == nameof(Faction) ? recordCommands.FactionCardAsync(id, call)
            : type == nameof(Person) ? recordCommands.PersonCardAsync(id, call)
            : Task.FromResult(BotReply.Error("Dafür gibt es keine Karte."));
}
