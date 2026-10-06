namespace NOOSE_Website.Services;

/// <summary>German wording for the tools, in the two places an agent ever sees one: the live step trace while an
/// answer is being built, and the used-tools list underneath it.</summary>
/// <remarks>One file for both, keyed the same way, because they are the same list — kept apart they drift, and a
/// tool added to one of them shows up as a raw identifier in the other.</remarks>
public static class NooseiToolLabels
{
    /// <summary>What NOOSEI is doing right now, as a full sentence.</summary>
    public static string Progress(string toolName) => toolName switch
    {
        "suche_akten" => "NOOSEI durchsucht die Akten …",
        "finde_akten" => "NOOSEI sichtet den Bestand …",
        "hole_kennzahlen" => "NOOSEI wertet Kennzahlen aus …",
        "lies_bereich" => "NOOSEI sieht einen Bereich durch …",
        "lies_akte" => "NOOSEI liest eine Akte …",
        "lies_akteninhalt" => "NOOSEI liest die Inhalte einer Akte …",
        "zeige_verbindungen" => "NOOSEI verfolgt Verbindungen …",
        "finde_verbindungsweg" => "NOOSEI sucht einen Verbindungsweg …",
        "lies_zeitstrahl" => "NOOSEI prüft den Verlauf …",
        "letzte_aenderungen" => "NOOSEI sieht die letzten Änderungen durch …",
        "loese_erwaehnung_auf" => "NOOSEI löst eine Erwähnung auf …",
        "hole_kurzbrief" => "NOOSEI holt einen Kurzbrief …",
        "lies_kalender" => "NOOSEI sieht den Kalender durch …",
        "erklaere_bedrohungsscore" => "NOOSEI schlüsselt einen Bedrohungs-Score auf …",
        "meine_akten" => "NOOSEI holt die Beobachtungsliste …",
        "schlage_nach" => "NOOSEI schlägt im Handbuch nach …",
        "liste_gesetzbuecher" => "NOOSEI sieht die Gesetzbücher durch …",
        "suche_gesetz" => "NOOSEI sucht in den Gesetzen …",
        "lies_gesetz" => "NOOSEI liest einen Paragrafen …",
        _ => "NOOSEI arbeitet …",
    };

    /// <summary>Short name for the used-tools list; falls back to the identifier so a new tool is visible, not silent.</summary>
    public static string Label(string toolName) => toolName switch
    {
        "suche_akten" => "Aktensuche",
        "finde_akten" => "Bestandssichtung",
        "hole_kennzahlen" => "Kennzahlen",
        "lies_bereich" => "Bereich",
        "lies_akte" => "Akte gelesen",
        "lies_akteninhalt" => "Akteninhalt",
        "zeige_verbindungen" => "Verbindungen",
        "finde_verbindungsweg" => "Verbindungsweg",
        "lies_zeitstrahl" => "Zeitstrahl",
        "letzte_aenderungen" => "Letzte Änderungen",
        "loese_erwaehnung_auf" => "Erwähnung aufgelöst",
        "hole_kurzbrief" => "Kurzbrief",
        "lies_kalender" => "Kalender",
        "erklaere_bedrohungsscore" => "Bedrohungs-Score",
        "meine_akten" => "Beobachtungsliste",
        "schlage_nach" => "Handbuch",
        "liste_gesetzbuecher" => "Gesetzbücher",
        "suche_gesetz" => "Gesetzessuche",
        "lies_gesetz" => "Paragraf gelesen",
        _ => toolName,
    };
}
