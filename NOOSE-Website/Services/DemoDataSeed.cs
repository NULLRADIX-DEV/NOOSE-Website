using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Static, declarative dataset for the public demo instance: factions, people, memberships, relations and faction conflicts. Pure data — the apply/idempotency logic lives in DemoDataService.</summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class DemoDataSeed
{
    internal sealed record FactionSpec(
        string Name, string Kind, string Color, Classification Classification,
        int? ThreatScore, bool Classified, bool StateFaction,
        string? Estate, string? Targets, string? Description, string? Radio,
        int? EstimatedMembers, string[] Ranks, ActivitySpec[] Activities);

    internal sealed record ActivitySpec(string Title, string Kind, int DaysAgo, string? Location, string? Description);

    internal sealed record PersonSpec(
        string Name, Classification Classification, int? ThreatScore, LifeStatus LifeStatus, bool Classified,
        string Description, string[] Aliases, (string Number, string? Label)[] Phones,
        (string Designation, string? Plate)[] Vehicles, string[] Weapons, (string Place, string? Note)[] Locations);

    internal sealed record MemberSpec(string Person, string Faction, string Rank, bool IsLead);
    internal sealed record RelationSpec(string A, string B, RelationType Type, string? Note);
    internal sealed record FactionLinkSpec(string Source, string Target, LinkKind Kind, string Label);
    internal sealed record DocSpec(string Person, int DaysAgo, string Reason, string Faction, MeasureOutcome Outcome, bool TruthSerum, string? Info);
    internal sealed record ObsSpec(string Person, int StartDaysAgo, int? DurationHours, string Location, string Sighting, string? Result);

    // ---- Factions ----------------------------------------------------------

    public static readonly FactionSpec[] Factions =
    [
        new("Los Santos Vagos", "Streetgang", "#F9A825", Classification.SuspicionCase, 62, false, false,
            "Rancho, East Los Santos", "Kontrolle des Drogenhandels in East LS, Expansion nach Süden.",
            "Älteste Latino-Straßenbande der Stadt; straff hierarchisch, gewaltbereit.", "Funk 12.4",
            120,
            ["Boss", "Lieutenant", "Soldado", "Prospect"],
            [
                new("Razzia Rancho-Lagerhaus", "Durchsuchung", 9, "Rancho", "Waffen und Betäubungsmittel sichergestellt."),
                new("Schießerei mit Ballas", "Gewalt", 21, "Grove Street", "Revierkonflikt eskaliert, zwei Verletzte."),
            ]),
        new("Ballas", "Streetgang", "#6A1B9A", Classification.SuspicionCase, 58, false, false,
            "Davis, South Los Santos", "Drogenhandel, Kontrolle über Davis und Strawberry.",
            "Erzfeind der Families; lila Erkennungsfarbe.", "Funk 9.1",
            90,
            ["OG", "Shotcaller", "Member"],
            [
                new("Drive-by in Strawberry", "Gewalt", 14, "Strawberry", "Vergeltung für Vorfall an der Grove Street."),
            ]),
        new("Grove Street Families", "Streetgang", "#2E7D32", Classification.ReviewCase, 41, false, false,
            "Grove Street, Davis", "Rückgewinnung verlorener Reviere, Schutzgelderpressung.",
            "Traditionsbande rund um die Grove Street; im Wiederaufbau.", "Funk 7.7",
            70,
            ["Leader", "Lieutenant", "Member"],
            [
                new("Versammlung Grove Street", "Treffen", 3, "Grove Street", "Größeres Mitgliedertreffen beobachtet."),
            ]),
        new("Marabunta Grande", "Streetgang", "#00897B", Classification.SuspicionCase, 55, false, false,
            "El Rancho", "Menschenschmuggel, Drogenkurierdienste.",
            "Salvadorianisch geprägte Bande, brutale Initiationsriten.", "Funk 15.2",
            60,
            ["Lider", "Sicario", "Soldado"], []),
        new("The Lost MC", "Bikergang", "#455A64", Classification.SuspicionCase, 60, false, false,
            "Clubhaus Sandy Shores", "Waffen- und Drogenschmuggel über Blaine County.",
            "Outlaw-Motorradclub mit Wurzeln in Sandy Shores.", "Funk 4.4",
            45,
            ["President", "Vice President", "Member", "Prospect"],
            [
                new("Waffentransport abgefangen", "Schmuggel", 30, "Route 68", "Transport mit Sturmgewehren gestoppt."),
            ]),
        new("Cartel Madrazo", "Kartell", "#C62828", Classification.SecuredStateThreatening, 95, true, false,
            "Anwesen Vinewood Hills", "Kontrolle der überregionalen Kokainlogistik.",
            "Mächtigstes Kartell der Region; politische Verflechtungen vermutet.", null,
            200,
            ["Patrón", "Lugarteniente", "Sicario"],
            [
                new("Container-Lieferung Hafen", "Schmuggel", 18, "Hafen Los Santos", "Nächtliche Übernahme beobachtet."),
                new("Treffen mit Investoren", "Treffen", 33, "Vinewood Hills", "Mutmaßliche Geldwäsche-Absprache."),
            ]),
        new("Sinaloa Connection", "Kartell", "#E65100", Classification.SecuredStateThreatening, 84, true, false,
            "Grapeseed", "Aufbau eigener Schmuggelrouten, Verdrängung Madrazos.",
            "Aufstrebendes Konkurrenzkartell; im offenen Krieg mit Madrazo.", null,
            110,
            ["Jefe", "Teniente", "Halcón"], []),
        new("Bratva Los Santos", "Mafia", "#AD1457", Classification.SecuredStateThreatening, 92, true, false,
            "Lager 7, Hafen", "Waffenhandel, Schutzgeld, Geldwäsche über Scheinfirmen.",
            "Russische Mafia; diszipliniert und international vernetzt.", null,
            80,
            ["Pakhan", "Brigadier", "Boyevik", "Buchhalter"],
            [
                new("Container-Übernahme bei Nacht", "Schmuggel", 20, "Hafen, Lager 7", "Kennzeichen der Fahrzeuge notiert."),
            ]),
        new("Yakuza Kanto-kai", "Mafia", "#283593", Classification.SuspicionCase, 67, false, false,
            "Little Seoul", "Glücksspiel, Schutzgeld, Hafenrouten.",
            "Japanisch geprägte Organisation; Ehrenkodex, klare Hierarchie.", null,
            55,
            ["Oyabun", "Wakagashira", "Kobun"], []),
        new("Triaden Wah Ching", "Organisierte Kriminalität", "#FF8F00", Classification.SuspicionCase, 64, false, false,
            "Chinatown", "Schmuggel, illegale Spielhallen, Produktpiraterie.",
            "Chinesische Triade; konkurriert mit der Yakuza um Reviere.", null,
            65,
            ["Dragon Head", "Vanguard", "49er"], []),
        new("Rotten Brotherhood MC", "Bikergang", "#5D4037", Classification.ReviewCase, 38, false, false,
            "Paleto Bay", "Schmuggel kleinerer Mengen, Hehlerei.",
            "Kleinerer Motorradclub; Rivale der Lost MC.", "Funk 3.9",
            25,
            ["President", "Member"], []),
        new("Vinewood Syndikat", "Organisierte Kriminalität", "#37474F", Classification.SecuredStateThreatening, 82, true, false,
            "Bürohochhaus, Downtown", "Geldwäsche, Korruption, Immobilienbetrug.",
            "Weiße-Kragen-Netzwerk; finanziert und wäscht Geld für mehrere Fraktionen.", null,
            30,
            ["Vorstand", "Partner", "Mittelsmann"], []),
        new("Merryweather Security", "Söldnerfirma", "#1565C0", Classification.ReviewCase, 33, false, false,
            "Hangar, Los Santos Airport", "Private Sicherheits- und Söldnerdienste.",
            "Privater Militärdienstleister; Aufträge an Höchstbietende.", null,
            150,
            ["Director", "Operator"], []),
        new("Government Los Santos", "Staatsfraktion", "#0D47A1", Classification.Unknown, null, false, true,
            "City Hall", "Verwaltung und Aufsicht.",
            "Staatliche Verwaltung; als Staatsfraktion von der Aktualitäts-Ampel ausgenommen.", null,
            null,
            ["Verwaltung"], []),
    ];

    // ---- People ------------------------------------------------------------

    public static readonly PersonSpec[] People =
    [
        // Familie Rodriguez (Vagos)
        new("Carlos Rodriguez", Classification.SecuredStateThreatening, 90, LifeStatus.Alive, true,
            "Oberhaupt der Los Santos Vagos und der Familie Rodriguez. Gilt als Kopf der Drogenlogistik in East LS.",
            ["El Jefe", "Don Carlos"], [("555-0101", "Privat"), ("555-0188", "Geschäft")],
            [("Declasse Tornado", "VAGOS01"), ("Bravado Buffalo", "RANCHO9")],
            ["Pistole .50", "Micro-SMG"], [("Rancho, East LS", "Wohnsitz"), ("Vespucci Canals", "Treffpunkt")]),
        new("Hector Rodriguez", Classification.SuspicionCase, 70, LifeStatus.Alive, false,
            "Bruder von Carlos, operativer Lieutenant der Vagos.", ["Tío Hector"], [("555-0102", null)],
            [("Vapid Dominator", "HR-VAG2")], ["Pump Shotgun"], [("Rancho, East LS", null)]),
        new("Miguel Rodriguez", Classification.SuspicionCase, 55, LifeStatus.Alive, false,
            "Sohn von Carlos; jung, ehrgeizig, soll nachrücken.", ["Miguelito"], [("555-0103", null)],
            [("Karin Sultan", "MIG07")], [], []),
        new("Maria Rodriguez", Classification.SuspicionCase, 40, LifeStatus.Alive, false,
            "Ehefrau von Carlos; verwaltet mutmaßlich Teile der Finanzen.", ["Doña Maria"], [], [], [], [("Rancho, East LS", "Wohnsitz")]),
        new("Sofia Rodriguez", Classification.ReviewCase, 25, LifeStatus.Alive, false,
            "Tochter von Carlos; bislang nur als Mitwisserin geführt.", [], [], [], [], []),
        new("Ricardo Mendez", Classification.SuspicionCase, 60, LifeStatus.Alive, false,
            "Sicario der Vagos, mehrfach im Zusammenhang mit Gewalttaten.", ["Rico"], [("555-0110", null)],
            [], ["Combat Pistol"], []),

        // Ballas
        new("Kane Williams", Classification.SuspicionCase, 65, LifeStatus.Alive, false,
            "OG der Ballas; koordiniert den Straßenverkauf in Davis.", ["Big K"], [("555-0120", null)],
            [("Albany Buccaneer", "BALLA1")], ["MAC-10"], [("Davis", "Revier")]),
        new("DeShawn Williams", Classification.ReviewCase, 40, LifeStatus.Alive, false,
            "Bruder von Kane; Straßenverkäufer.", ["D"], [("555-0121", null)], [], [], []),
        new("Tyrell Banks", Classification.ReviewCase, 35, LifeStatus.Alive, false,
            "Läufer der Ballas.", [], [], [], [], []),

        // Grove Street Families
        new("Marcus Johnson", Classification.SuspicionCase, 60, LifeStatus.Alive, false,
            "Anführer der Grove Street Families; versucht alte Reviere zurückzugewinnen.", ["MJ"], [("555-0130", "Privat")],
            [("Bravado Bison", "GROVE1")], ["Pistol"], [("Grove Street, Davis", "Wohnsitz")]),
        new("Denise Johnson", Classification.ReviewCase, 30, LifeStatus.Alive, false,
            "Schwester von Marcus; Verbindungsperson zu Geschäftsleuten.", [], [("555-0131", null)], [], [], []),
        new("Tyrone Johnson", Classification.ReviewCase, 35, LifeStatus.Alive, false,
            "Cousin von Marcus; Mitglied der Families.", ["Ty"], [], [], [], []),
        new("Lamar Davis", Classification.SuspicionCase, 50, LifeStatus.Alive, false,
            "Enger Vertrauter von Marcus; Lieutenant der Families.", ["LD"], [("555-0132", null)],
            [("Emperor", null)], [], []),

        // Marabunta Grande
        new("Ernesto Vargas", Classification.SuspicionCase, 58, LifeStatus.Alive, false,
            "Lider der Marabunta Grande.", ["El Lobo"], [("555-0140", null)], [], ["Machete", "Pistol"], [("El Rancho", null)]),
        new("Pablo Cruz", Classification.ReviewCase, 33, LifeStatus.Alive, false,
            "Soldado der Marabunta.", [], [], [], [], []),

        // The Lost MC
        new("Johnny Klebitz", Classification.SuspicionCase, 68, LifeStatus.Alive, false,
            "Präsident der Lost MC; kontrolliert Schmuggel in Blaine County.", ["Johnny K"], [("555-0150", null)],
            [("Western Daemon", "LOST01")], ["Sawed-Off Shotgun"], [("Sandy Shores Clubhaus", "Wohnsitz")]),
        new("Terry Marsh", Classification.SuspicionCase, 50, LifeStatus.Alive, false,
            "Vice President der Lost MC; zuständig für Waffenbeschaffung.", [], [("555-0151", null)], [], [], []),
        new("Clay Simons", Classification.ReviewCase, 38, LifeStatus.Alive, false,
            "Mitglied der Lost MC.", [], [], [], [], []),

        // Cartel Madrazo
        new("Martin Madrazo", Classification.SecuredStateThreatening, 95, LifeStatus.Alive, true,
            "Patrón des Cartel Madrazo; höchste Bedrohungseinstufung. Vermutete Verbindungen in Verwaltung und Wirtschaft.",
            ["El Patrón"], [("555-0160", "abhörsicher")], [("Pegassi Toros", "MADRAZO")], [], [("Vinewood Hills, Anwesen", "Wohnsitz")]),
        new("Patricia Madrazo", Classification.SuspicionCase, 45, LifeStatus.Alive, false,
            "Ehefrau von Martin Madrazo.", [], [], [], [], [("Vinewood Hills, Anwesen", "Wohnsitz")]),
        new("Javier Madrazo", Classification.SecuredStateThreatening, 80, LifeStatus.Alive, true,
            "Sohn von Martin; Lugarteniente, operative rechte Hand.", ["Javi"], [("555-0161", null)],
            [("Enus Cognoscenti", "JAVI11")], ["Pistole .50"], []),
        new("Diego Salazar", Classification.SecuredStateThreatening, 85, LifeStatus.Alive, true,
            "Sicario des Cartel Madrazo; in mehrere Schießereien verwickelt.", ["La Sombra"], [("555-0162", null)],
            [], ["Carbine Rifle", "Combat Pistol"], [("Grapeseed", "Treffpunkt")]),

        // Sinaloa Connection
        new("Joaquin Reyes", Classification.SecuredStateThreatening, 84, LifeStatus.Alive, true,
            "Jefe der Sinaloa Connection; treibt den Kartellkrieg gegen Madrazo voran.", ["El Chapo"], [("555-0170", null)],
            [], [], [("Grapeseed", "Stützpunkt")]),
        new("Camila Reyes", Classification.SuspicionCase, 50, LifeStatus.Alive, false,
            "Schwester von Joaquin; Teniente der Sinaloa.", [], [("555-0171", null)], [], [], []),

        // Bratva
        new("Dimitri Volkov", Classification.SecuredStateThreatening, 92, LifeStatus.Alive, true,
            "Pakhan der Bratva Los Santos; international vernetzter Waffenhändler.", ["Dima"], [("555-0180", "abhörsicher")],
            [("Übermacht Oracle", "BRATVA1")], [], [("Hafen, Lager 7", "Stützpunkt")]),
        new("Yuri Volkov", Classification.SuspicionCase, 78, LifeStatus.Alive, false,
            "Bruder von Dimitri; Brigadier und Vollstrecker.", [], [("555-0181", null)], [], ["AK-47"], []),
        new("Anna Petrova", Classification.SuspicionCase, 55, LifeStatus.Alive, false,
            "Buchhalterin der Bratva; Schlüsselfigur der Geldwäsche.", ["Die Buchhalterin"], [("555-0182", null)],
            [("Benefactor Schafter", null)], [], [("Downtown", "Büro")]),

        // Yakuza
        new("Kenji Tanaka", Classification.SuspicionCase, 72, LifeStatus.Alive, false,
            "Oyabun der Yakuza Kanto-kai.", ["Tanaka-san"], [("555-0190", null)], [("Annis Elegy", "YAKUZA1")], [], [("Little Seoul", null)]),
        new("Haruto Sato", Classification.SuspicionCase, 58, LifeStatus.Alive, false,
            "Wakagashira der Yakuza.", [], [], [], [], []),

        // Triaden
        new("Wei Cheng", Classification.SuspicionCase, 70, LifeStatus.Alive, false,
            "Dragon Head der Triaden Wah Ching.", ["Onkel Wei"], [("555-0200", null)], [], [], [("Chinatown", null)]),
        new("Lin Cheng", Classification.ReviewCase, 33, LifeStatus.Alive, false,
            "Tochter von Wei Cheng; übernimmt zunehmend Geschäfte.", [], [("555-0201", null)], [], [], []),

        // Rotten Brotherhood
        new("Frank Doyle", Classification.ReviewCase, 38, LifeStatus.Alive, false,
            "Präsident der Rotten Brotherhood MC.", ["Doyle"], [], [("Western Sovereign", "ROTTEN1")], [], [("Paleto Bay", null)]),

        // Vinewood Syndikat
        new("Devin Weston", Classification.SecuredStateThreatening, 82, LifeStatus.Alive, true,
            "Vorstand des Vinewood Syndikats; Finanzier und Geldwäscher für mehrere Fraktionen.", ["Der Investor"], [("555-0210", "abhörsicher")],
            [("Truffade Adder", "DEVIN1")], [], [("Downtown, Bürohochhaus", "Büro")]),
        new("Solomon Richards", Classification.SuspicionCase, 48, LifeStatus.Alive, false,
            "Partner im Vinewood Syndikat; Frontmann für Immobiliengeschäfte.", [], [("555-0211", null)], [], [], []),

        // Merryweather
        new("Don Percival", Classification.ReviewCase, 33, LifeStatus.Alive, false,
            "Direktor von Merryweather Security.", [], [("555-0220", null)], [], [], [("Airport Hangar", "Stützpunkt")]),

        // Unabhängige / vernetzt
        new("Michael De Santa", Classification.SuspicionCase, 52, LifeStatus.Alive, false,
            "Ehemaliger Berufskrimineller; offiziell im Ruhestand, weiterhin vernetzt.", ["Michael Townley"], [("555-0230", null)],
            [("Obey Tailgater", "DESANTA")], [], [("Rockford Hills", "Wohnsitz")]),
        new("Trevor Philips", Classification.SecuredStateThreatening, 80, LifeStatus.Alive, true,
            "Unberechenbarer Schwerkrimineller; Waffen- und Drogengeschäfte in Blaine County.", ["TP"], [("555-0231", null)],
            [("Bravado Bodhi", "TPI001")], ["Sturmgewehr", "Sticky Bombs"], [("Sandy Shores Trailer", "Wohnsitz")]),
        new("Franklin Clinton", Classification.ReviewCase, 28, LifeStatus.Alive, false,
            "Aufsteiger mit Verbindungen zu mehreren Gruppen; gilt als wendig.", ["Frank"], [("555-0232", null)],
            [("Bravado Buffalo S", "FRANK1")], [], [("Vinewood Hills", "Wohnsitz")]),
        new("Lester Crest", Classification.SuspicionCase, 45, LifeStatus.Alive, false,
            "Hacker und Planer; arbeitet für wechselnde Auftraggeber.", ["Der Planer"], [("555-0233", "verschlüsselt")],
            [], [], [("Murrieta Heights", "Werkstatt")]),
        new("Brad Snider", Classification.ReviewCase, 20, LifeStatus.Dead, false,
            "Ehemaliger Komplize; bei einer Maßnahme verstorben.", [], [], [], [], []),
        new("Vinnie Moretti", Classification.SuspicionCase, 64, LifeStatus.Fugitive, false,
            "Flüchtig; per Haftbefehl gesucht. Letzter bekannter Aufenthalt unklar.", ["Ghost"], [("555-0240", null)],
            [], ["Pistol"], [("unbekannt", "auf der Flucht")]),
    ];

    // ---- Faction memberships ----------------------------------------------

    public static readonly MemberSpec[] Members =
    [
        new("Carlos Rodriguez", "Los Santos Vagos", "Boss", true),
        new("Hector Rodriguez", "Los Santos Vagos", "Lieutenant", true),
        new("Miguel Rodriguez", "Los Santos Vagos", "Soldado", false),
        new("Ricardo Mendez", "Los Santos Vagos", "Soldado", false),

        new("Kane Williams", "Ballas", "OG", true),
        new("DeShawn Williams", "Ballas", "Member", false),
        new("Tyrell Banks", "Ballas", "Member", false),

        new("Marcus Johnson", "Grove Street Families", "Leader", true),
        new("Lamar Davis", "Grove Street Families", "Lieutenant", false),
        new("Denise Johnson", "Grove Street Families", "Member", false),
        new("Tyrone Johnson", "Grove Street Families", "Member", false),

        new("Ernesto Vargas", "Marabunta Grande", "Lider", true),
        new("Pablo Cruz", "Marabunta Grande", "Soldado", false),

        new("Johnny Klebitz", "The Lost MC", "President", true),
        new("Terry Marsh", "The Lost MC", "Vice President", false),
        new("Clay Simons", "The Lost MC", "Member", false),

        new("Martin Madrazo", "Cartel Madrazo", "Patrón", true),
        new("Javier Madrazo", "Cartel Madrazo", "Lugarteniente", true),
        new("Diego Salazar", "Cartel Madrazo", "Sicario", false),

        new("Joaquin Reyes", "Sinaloa Connection", "Jefe", true),
        new("Camila Reyes", "Sinaloa Connection", "Teniente", false),

        new("Dimitri Volkov", "Bratva Los Santos", "Pakhan", true),
        new("Yuri Volkov", "Bratva Los Santos", "Brigadier", false),
        new("Anna Petrova", "Bratva Los Santos", "Buchhalter", false),

        new("Kenji Tanaka", "Yakuza Kanto-kai", "Oyabun", true),
        new("Haruto Sato", "Yakuza Kanto-kai", "Wakagashira", false),

        new("Wei Cheng", "Triaden Wah Ching", "Dragon Head", true),
        new("Lin Cheng", "Triaden Wah Ching", "49er", false),

        new("Frank Doyle", "Rotten Brotherhood MC", "President", true),

        new("Devin Weston", "Vinewood Syndikat", "Vorstand", true),
        new("Solomon Richards", "Vinewood Syndikat", "Partner", false),

        new("Don Percival", "Merryweather Security", "Director", true),
    ];

    // ---- Person-to-person relations ---------------------------------------

    public static readonly RelationSpec[] Relations =
    [
        // Familie Rodriguez
        new("Carlos Rodriguez", "Maria Rodriguez", RelationType.Family, "Ehepartner"),
        new("Carlos Rodriguez", "Miguel Rodriguez", RelationType.Family, "Sohn"),
        new("Carlos Rodriguez", "Sofia Rodriguez", RelationType.Family, "Tochter"),
        new("Carlos Rodriguez", "Hector Rodriguez", RelationType.Family, "Bruder"),
        new("Miguel Rodriguez", "Sofia Rodriguez", RelationType.Family, "Geschwister"),
        // Familie Johnson
        new("Marcus Johnson", "Denise Johnson", RelationType.Family, "Schwester"),
        new("Marcus Johnson", "Tyrone Johnson", RelationType.Family, "Cousin"),
        // Familie Williams
        new("Kane Williams", "DeShawn Williams", RelationType.Family, "Bruder"),
        // Familie Madrazo
        new("Martin Madrazo", "Patricia Madrazo", RelationType.Family, "Ehepartner"),
        new("Martin Madrazo", "Javier Madrazo", RelationType.Family, "Sohn"),
        // Familie Volkov
        new("Dimitri Volkov", "Yuri Volkov", RelationType.Family, "Bruder"),
        // Familie Cheng / Reyes
        new("Wei Cheng", "Lin Cheng", RelationType.Family, "Tochter"),
        new("Joaquin Reyes", "Camila Reyes", RelationType.Family, "Schwester"),

        // Freundschaften / Verbündete
        new("Marcus Johnson", "Lamar Davis", RelationType.Ally, "Enge Vertraute seit Jahren"),
        new("Lamar Davis", "Franklin Clinton", RelationType.Ally, "Jugendfreunde"),
        new("Franklin Clinton", "Michael De Santa", RelationType.Ally, "Partner"),
        new("Johnny Klebitz", "Terry Marsh", RelationType.Ally, null),
        new("Johnny Klebitz", "Clay Simons", RelationType.Ally, null),

        // Geschäftsbeziehungen (oft fraktionsübergreifend)
        new("Michael De Santa", "Trevor Philips", RelationType.BusinessPartner, "Alte Komplizen, Verhältnis instabil"),
        new("Michael De Santa", "Lester Crest", RelationType.BusinessPartner, "Planung gemeinsamer Vorhaben"),
        new("Franklin Clinton", "Lester Crest", RelationType.Known, null),
        new("Anna Petrova", "Devin Weston", RelationType.BusinessPartner, "Geldwäsche-Kanal"),
        new("Devin Weston", "Solomon Richards", RelationType.BusinessPartner, "Immobilienfront"),
        new("Diego Salazar", "Yuri Volkov", RelationType.BusinessPartner, "Waffenhandel"),
        new("Martin Madrazo", "Devin Weston", RelationType.BusinessPartner, "Investitionen / Geldwäsche"),
        new("Lester Crest", "Devin Weston", RelationType.Known, null),

        // Feindschaften
        new("Kane Williams", "Marcus Johnson", RelationType.Enemy, "Bandenkrieg Ballas/Families"),
        new("Carlos Rodriguez", "Marcus Johnson", RelationType.Enemy, "Revierkonflikt"),
        new("Carlos Rodriguez", "Kane Williams", RelationType.Enemy, null),
        new("Martin Madrazo", "Joaquin Reyes", RelationType.Enemy, "Kartellkrieg"),
        new("Dimitri Volkov", "Kenji Tanaka", RelationType.Enemy, "Streit um Hafenrouten"),
        new("Trevor Philips", "Martin Madrazo", RelationType.Enemy, "Offene Rechnung"),
        new("Ernesto Vargas", "Carlos Rodriguez", RelationType.Enemy, "Marabunta gegen Vagos"),
    ];

    // ---- Faction-to-faction conflicts and alliances -----------------------

    public static readonly FactionLinkSpec[] FactionLinks =
    [
        new("Los Santos Vagos", "Ballas", LinkKind.Conflict, "Revierkrieg"),
        new("Ballas", "Grove Street Families", LinkKind.Conflict, "Traditioneller Bandenkrieg"),
        new("Los Santos Vagos", "Grove Street Families", LinkKind.Conflict, "Drogenrevier"),
        new("Los Santos Vagos", "Marabunta Grande", LinkKind.Conflict, "Gebietsstreit"),
        new("The Lost MC", "Rotten Brotherhood MC", LinkKind.Conflict, "MC-Fehde"),
        new("Cartel Madrazo", "Sinaloa Connection", LinkKind.Conflict, "Kartellkrieg"),
        new("Bratva Los Santos", "Yakuza Kanto-kai", LinkKind.Conflict, "Hafenroute"),
        new("Yakuza Kanto-kai", "Triaden Wah Ching", LinkKind.Conflict, "Glücksspiel-Revier"),

        new("Cartel Madrazo", "Vinewood Syndikat", LinkKind.Alliance, "Finanzpartnerschaft"),
        new("Bratva Los Santos", "Vinewood Syndikat", LinkKind.Alliance, "Geldwäsche"),
        new("Cartel Madrazo", "Bratva Los Santos", LinkKind.Alliance, "Waffenlieferung"),
        new("Grove Street Families", "Marabunta Grande", LinkKind.Alliance, "Zweckbündnis gegen Ballas"),
    ];

    // ---- Person docs (interrogation / measure protocols) ------------------

    public static readonly DocSpec[] Docs =
    [
        new("Carlos Rodriguez", 12, "Verhör nach Razzia", "Los Santos Vagos", MeasureOutcome.RunningStill, true,
            "Bestätigte Waffenlieferungen aus Sandy Shores; Name eines Mittelsmanns genannt."),
        new("Diego Salazar", 25, "Befragung nach Schießerei", "Cartel Madrazo", MeasureOutcome.RunningStill, false,
            "Aussage verweigert, Anwalt eingeschaltet."),
        new("Yuri Volkov", 8, "Befragung", "Bratva Los Santos", MeasureOutcome.OfficiallyReleased, false,
            "Keine verwertbaren Aussagen, offiziell entlassen."),
        new("Trevor Philips", 40, "Verhör", "—", MeasureOutcome.RunningStill, true,
            "Unkooperativ, widersprüchliche Angaben."),
        new("Brad Snider", 60, "Letzte Maßnahme", "The Lost MC", MeasureOutcome.Injection, false,
            "Spritze verabreicht; Person verstorben."),
    ];

    // ---- Observations ------------------------------------------------------

    public static readonly ObsSpec[] Observations =
    [
        new("Trevor Philips", 5, 3, "Sandy Shores Trailer", "Treffen mit unbekannten Käufern, Übergabe von Kisten.", "Fotomaterial gesichert."),
        new("Martin Madrazo", 15, null, "Vinewood Hills, Anwesen Madrazo", "Mehrere Luxusfahrzeuge, bewaffnete Wachen, Lieferung per Van.", null),
        new("Marcus Johnson", 3, 2, "Grove Street", "Versammlung mit ca. 12 Personen, vereinzelt Waffen sichtbar.", "Observation abgebrochen, Enttarnung drohte."),
        new("Dimitri Volkov", 20, 4, "Hafen, Lager 7", "Container-Übernahme bei Nacht.", "Kennzeichen notiert."),
    ];

    // ---- Drug routes -------------------------------------------------------

    internal sealed record DrugRouteSpec(
        string Designation, string Faction, string Substance, string Description, string? Findings,
        (DrugRouteStationKind Kind, string Location, string? Description)[] Stations,
        (string Person, string Role)[] People);

    public static readonly DrugRouteSpec[] DrugRoutes =
    [
        new("Rancho-Route", "Los Santos Vagos", "Kokain",
            "Kokain kommt per Boot nach Paleto Bay, wird in Sandy Shores gestreckt und über ein Lagerhaus in Rancho in East LS verkauft.",
            "<p>Die Vagos fahren die Ware nachts in zwei Fahrzeugen, ein Vorausfahrzeug sichert die Strecke ab.</p>",
            [
                (DrugRouteStationKind.Handover, "Steg in Paleto Bay", "Übernahme vom Boot, meist zwischen 2 und 4 Uhr."),
                (DrugRouteStationKind.Processing, "Trailer in Sandy Shores", "Strecken und Abpacken in kleine Einheiten."),
                (DrugRouteStationKind.Storage, "Lagerhaus Rancho", "Zwischenlager, bei der Razzia teilweise ausgehoben."),
                (DrugRouteStationKind.Sale, "Straßenecken East LS", "Verkauf durch Soldados in kleinen Mengen."),
            ],
            [
                ("Hector Rodriguez", "Organisator"),
                ("Miguel Rodriguez", "Fahrer"),
                ("Ricardo Mendez", "Verkäufer"),
            ]),
    ];
}
