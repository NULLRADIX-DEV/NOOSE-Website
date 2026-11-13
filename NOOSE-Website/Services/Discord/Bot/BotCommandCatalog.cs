using NetCord;
using NetCord.Rest;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Every command the bot registers; names are the routing keys.</summary>
public static class BotCommandCatalog
{
    public const string AgentProfile = "Agentenprofil";
    public const string AttachToRecord = "An Akte hängen";

    private static readonly InteractionContextType[] Where = [InteractionContextType.Guild, InteractionContextType.BotDMChannel];
    private static readonly ApplicationIntegrationType[] Install = [ApplicationIntegrationType.GuildInstall];

    public static IReadOnlyList<ApplicationCommandProperties> All() =>
    [
        // records
        Slash("akte", "Personen- oder Fraktionsakte als Karte",
            Str("suche", "Name oder Aktenzeichen", required: true, autocomplete: true), Share()),
        Slash("fahndung", "Fahndungstafel oder Fahndungsstand einer Person",
            Str("name", "Name oder Aktenzeichen (leer = ganze Tafel)", autocomplete: true), Share()),
        Slash("kopfgeld", "Die höchsten offenen Kopfgelder", Share()),
        Slash("suche", "Suche über alle Akten, die du sehen darfst",
            Str("text", "Wonach gesucht wird", required: true, max: 200), Share()),
        Slash("fraktion", "Fraktionsakte mit Bedrohung, Mitgliedern und Routen",
            Str("name", "Name oder Aktenzeichen", required: true, autocomplete: true), Share()),
        Slash("route", "Wer hält diese Drogenroute?",
            Str("name", "Bezeichnung der Route", required: true, autocomplete: true), Share()),

        // knowledge
        Slash("gesetz", "Paragraf aus den Gesetzbüchern",
            Str("buch", "Gesetzbuch, z. B. StGB", required: true, autocomplete: true),
            Str("paragraf", "Paragraf, z. B. 12", autocomplete: true), Share()),
        Slash("dvo", "Abschnitt der Dienstverordnung",
            Str("stichwort", "Stichwort oder Abschnitt", required: true, autocomplete: true), Share()),
        Slash("fibi", "Frage an FIBI, mit deinen Rechten und deinem Kontingent",
            Str("frage", "Deine Frage", required: true, max: 1500)),
        Slash("lage", "Aktuelle Gefahrenlage (DEFCON)", Share()),
        Slash("funk", "Funkplan und Frequenzen",
            Str("kanal", "Kanal oder Frequenz (leer = ganzer Plan)", autocomplete: true), Share()),

        // me
        Slash("fib", "Deine Dienst-Zentrale: Aufgaben, Wiedervorlagen, Quittierungen, Termine"),
        new SlashCommandProperties("abmeldung", "Abmeldung eintragen oder beenden")
        {
            Contexts = Where, IntegrationTypes = Install,
            Options =
            [
                Sub("neu", "Neue Abmeldung",
                    Str("von", "Erster Tag: heute, morgen, 20.11.", required: true),
                    Str("bis", "Letzter Tag: morgen, +3, 24.11.", required: true),
                    Choice("art", "Art der Abmeldung", ("Urlaub", "Vacation"), ("Arbeit", "Work"), ("Krank", "Sick"),
                        ("RP-Pause", "RpBreak"), ("Sonstiges", "Misc")),
                    Str("grund", "Grund (optional)", max: 300)),
                Sub("beenden", "Laufende Abmeldung heute beenden"),
            ],
        },
        Slash("termine", "Deine Termine und Besprechungen",
            Choice("zeitraum", "Zeitraum", ("Heute", "heute"), ("Diese Woche", "woche"), ("Nächste 14 Tage", "14"))),
        Slash("quittieren", "Offene Kenntnisnahmen quittieren"),
        Slash("aufgaben", "Deine offenen Aufgaben"),
        Slash("wiedervorlagen", "Deine fälligen Wiedervorlagen"),

        // leadership
        Slash("agent", "Agentenprofil: Dienstgrad, Abteilung, Abmeldung, Ehrungen",
            Str("codename", "Codename", required: true, autocomplete: true), Share()),
        Slash("abgemeldet", "Wer ist heute und diese Woche abgemeldet?"),
        Slash("offen", "Führung: offene Anträge, Hinweise, Tickets, Bewerbungen"),
        Slash("inaktiv", "Führung: Inaktivitäts-Radar"),

        // capture
        Slash("notiz", "Kommentar an eine Akte hängen",
            Str("akte", "Akte (Name oder Aktenzeichen)", required: true, autocomplete: true),
            Str("text", "Kommentar", required: true, max: 1800)),
        Slash("wiedervorlage", "Wiedervorlage an einer Akte setzen",
            Str("akte", "Akte (Name oder Aktenzeichen)", required: true, autocomplete: true),
            Str("wann", "Wann: morgen, +7, 20.11. 18:00", required: true),
            Str("notiz", "Notiz (optional)", max: 500)),
        Slash("aufgabe", "Aufgabe an einen Agenten vergeben",
            Str("agent", "Codename", required: true, autocomplete: true),
            Str("titel", "Titel", required: true, max: 200),
            Str("faellig", "Fällig: morgen, 20.11."),
            Str("beschreibung", "Beschreibung (optional)", max: 1500)),
        Slash("schusswaffe", "Schusswaffengebrauch melden",
            Str("begruendung", "Kurze Begründung", required: true, max: 500)),
        Slash("alarm", "Einsatzalarm auslösen",
            Str("lage", "Lage", required: true, max: 200),
            Str("ort", "Ort", max: 200),
            Choice("abteilung", "Nur eine Abteilung rufen", ("CID", "Cid"), ("CTD", "Ctd"), ("HRT", "Hrt")),
            Str("grund", "Grund (bei Gefahr im Verzug Pflicht)", max: 500)),

        // context menus
        new UserCommandProperties(AgentProfile) { Contexts = Where, IntegrationTypes = Install },
        new MessageCommandProperties(AttachToRecord) { Contexts = Where, IntegrationTypes = Install },
    ];

    public static IEnumerable<string> SlashNames() => All().OfType<SlashCommandProperties>().Select(c => c.Name);

    private static SlashCommandProperties Slash(string name, string description, params ApplicationCommandOptionProperties[] options)
        => new(name, description) { Contexts = Where, IntegrationTypes = Install, Options = options };

    private static ApplicationCommandOptionProperties Sub(string name, string description, params ApplicationCommandOptionProperties[] options)
        => new(ApplicationCommandOptionType.SubCommand, name, description) { Options = options };

    private static ApplicationCommandOptionProperties Str(string name, string description, bool required = false,
        bool autocomplete = false, int? max = null)
        => new(ApplicationCommandOptionType.String, name, description)
        {
            Required = required,
            Autocomplete = autocomplete ? true : null,
            MaxLength = max,
        };

    private static ApplicationCommandOptionProperties Choice(string name, string description, params (string Label, string Value)[] choices)
        => new(ApplicationCommandOptionType.String, name, description)
        {
            Choices = choices.Select(c => new ApplicationCommandOptionChoiceProperties(c.Label, c.Value)).ToList(),
        };

    private static ApplicationCommandOptionProperties Share()
        => new(ApplicationCommandOptionType.Boolean, "teilen", "Karte für alle im Kanal sichtbar posten");
}
