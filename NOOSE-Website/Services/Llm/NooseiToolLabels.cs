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
        "suche_akten" => "FIBI durchsucht die Akten …",
        "finde_akten" => "FIBI sichtet den Bestand …",
        "hole_kennzahlen" => "FIBI wertet Kennzahlen aus …",
        "lies_bereich" => "FIBI sieht einen Bereich durch …",
        "lies_akte" => "FIBI liest eine Akte …",
        "lies_akteninhalt" => "FIBI liest die Inhalte einer Akte …",
        "zeige_verbindungen" => "FIBI verfolgt Verbindungen …",
        "finde_verbindungsweg" => "FIBI sucht einen Verbindungsweg …",
        "lies_zeitstrahl" => "FIBI prüft den Verlauf …",
        "letzte_aenderungen" => "FIBI sieht die letzten Änderungen durch …",
        "loese_erwaehnung_auf" => "FIBI löst eine Erwähnung auf …",
        "hole_kurzbrief" => "FIBI holt einen Kurzbrief …",
        "lies_kalender" => "FIBI sieht den Kalender durch …",
        "erklaere_bedrohungsscore" => "FIBI schlüsselt einen Bedrohungs-Score auf …",
        "meine_akten" => "FIBI holt die Beobachtungsliste …",
        "schlage_nach" => "FIBI schlägt im Handbuch nach …",
        "liste_gesetzbuecher" => "FIBI sieht die Gesetzbücher durch …",
        "suche_gesetz" => "FIBI sucht in den Gesetzen …",
        "lies_gesetz" => "FIBI liest einen Paragrafen …",
        "vorschlag_wiedervorlage" => "FIBI bereitet eine Wiedervorlage vor …",
        "vorschlag_stichwort" => "FIBI bereitet Stichwörter vor …",
        "vorschlag_aufgabe" => "FIBI bereitet eine Aufgabe vor …",
        "vorschlag_beobachten" => "FIBI bereitet das Beobachten vor …",
        "vorschlag_verknuepfung" => "FIBI bereitet eine Verknüpfung vor …",
        _ => "FIBI arbeitet …",
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
        "vorschlag_wiedervorlage" => "Vorschlag Wiedervorlage",
        "vorschlag_stichwort" => "Vorschlag Stichwort",
        "vorschlag_aufgabe" => "Vorschlag Aufgabe",
        "vorschlag_beobachten" => "Vorschlag Beobachten",
        "vorschlag_verknuepfung" => "Vorschlag Verknüpfung",
        _ => toolName,
    };
}
