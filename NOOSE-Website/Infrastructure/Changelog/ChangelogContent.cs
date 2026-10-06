using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure.Changelog;

/// <summary>The changelog as it ships with the code; the seeder writes it into the database.</summary>
/// <remarks>
/// Written for the person using the site, not for the person who built it. Three rules held here:
/// no technology in a line (no table, no service, no framework), one short sentence per line, and nothing at all for
/// a change nobody outside the repository can notice - renames, tests, documentation and refactors are absent on
/// purpose rather than summarised.
/// <para>
/// A version is authored here, not read from the build counter: BuildNumber.txt is gitignored and grows on every
/// build, so it is unknown while these lines are written and was never recorded for the past at all. The build number
/// is stamped later by the seeder, onto the newest release that has none.
/// </para>
/// <para>
/// Raise <see cref="Revision"/> when an existing line is reworded. The seeder then rewrites rows that nobody has
/// edited and leaves edited ones alone; a new line needs no bump because it is recognised by its missing key.
/// </para>
/// </remarks>
public static class ChangelogContent
{
    /// <summary>Revision of the shipped text. Raise it after rewording an existing line.</summary>
    /// <remarks>
    /// Raised to 2 when six lines moved into release 2.2.00. The move itself is carried by their
    /// <c>LegacyKey</c>, but that pass rewrites only the key and the release - the sort order stays behind,
    /// and a line that kept its number from a release of seventy would sit above every line of a release of
    /// six. Only the revision opens the update branch that writes the order with them.
    /// Raised to 4 when 2.2.07 and 2.2.08 were cut to fit the 300-character title column; longer, they failed
    /// the whole seeding batch on MySQL.
    /// </remarks>
    public const int Revision = 4;

    /// <param name="Key">Stable handle; renaming one orphans the old row and creates a second.</param>
    /// <param name="LegacyKey">The key this line shipped under before it moved into another release.</param>
    public sealed record SeededEntry(
        string Key, ChangelogKind Kind, string Title, string? Area, string? LegacyKey = null);

    public sealed record SeededRelease(
        string Version, DateTime Date, string Title, SeededEntry[] Entries, string? LegacyVersion = null);

    private static SeededEntry Neu(string key, string title, string area, string? legacyKey = null)
        => new(key, ChangelogKind.Neu, title, area, legacyKey);

    private static SeededEntry Besser(string key, string title, string area, string? legacyKey = null)
        => new(key, ChangelogKind.Verbessert, title, area, legacyKey);

    private static SeededEntry Fix(string key, string title, string area, string? legacyKey = null)
        => new(key, ChangelogKind.Behoben, title, area, legacyKey);

    public static readonly IReadOnlyList<SeededRelease> Releases =
    [
        new("0.1.00", new DateTime(2026, 6, 12), "Der Anfang: Akten, Suche und Lagezentrum",
        [
            Neu("0.1.00-personenakten", "Personenakten mit Steckbrief, Fotos und Protokollen.", "Personen"),
            Neu("0.1.01-gruppierungen", "Fraktionen, Parteien und Personengruppen als eigene Akten.", "Akten"),
            Neu("0.1.02-vorgaenge", "Vorgänge und Operationen bündeln zusammengehörige Ermittlungen.", "Ermittlung"),
            Neu("0.1.03-taskforces", "Taskforces mit eigenem Chat.", "Taskforces"),
            Neu("0.1.04-dashboard", "Lagezentrum mit den wichtigsten Zahlen auf einen Blick.", "Lagezentrum"),
            Neu("0.1.05-suche", "Suche über den gesamten Aktenbestand.", "Suche"),
            Neu("0.1.06-aufgaben", "Aufgaben und Schwarzes Brett für den Dienstbetrieb.", "Dienstbetrieb"),
            Neu("0.1.07-kalender", "Kalender für Termine der Behörde.", "Kalender"),
            Neu("0.1.08-druck", "Jede Akte lässt sich als Druckansicht ausgeben.", "Druck"),
            Besser("0.1.09-tabellen", "Alle Listen lassen sich sortieren, Datumsangaben sind einheitlich deutsch.", "Bedienung"),
        ], "0.1"),

        new("0.2.00", new DateTime(2026, 6, 16), "Verschlusssachen, Ausbildung und Partnerbehörden",
        [
            Neu("0.2.00-vs", "Verschlusssachen: Dokumente nur für die Führung, für TRU oder für HRB.", "Dokumente"),
            Neu("0.2.01-ausbildung", "Ausbildungsmodule in der Personalakte.", "Personal"),
            Neu("0.2.02-gesetze", "Gesetzbuch zum Nachschlagen.", "Gesetze"),
            Neu("0.2.03-graph", "Beziehungsgraph zeigt, wer mit wem zu tun hat.", "Analyse"),
            Neu("0.2.04-score", "Bedrohungs-Score schätzt die Gefährdung je Person und Fraktion.", "Analyse"),
            Neu("0.2.05-partner", "Zugänge für LSPD, DoJ und LSMD, mit Freigabe je Akte.", "Partner"),
            Besser("0.2.06-fraktionsraenge", "Fraktionsränge lassen sich umbenennen und in der Reihenfolge ändern.", "Fraktionen"),
        ], "0.2"),

        new("0.3.00", new DateTime(2026, 6, 26), "Bewerbungen, Demo-Modus und ein besserer Chat",
        [
            Neu("0.3.00-bewerbungen", "Bewerbungen werden auf der Seite eingereicht und bearbeitet.", "Bewerbungen"),
            Neu("0.3.01-demo", "Demo-Modus zum Vorführen der Seite ohne echte Daten.", "Betrieb"),
            Neu("0.3.02-dok-stichworte", "Dokumente lassen sich mit Stichworten versehen.", "Dokumente"),
            Besser("0.3.03-chat", "Der Taskforce-Chat wurde überarbeitet.", "Taskforces"),
            Besser("0.3.04-navigation", "Das Menü wurde aufgeräumt und reagiert schneller.", "Bedienung"),
            Fix("0.3.05-doks-loeschen", "Protokolle an einer Person lassen sich jetzt auch löschen.", "Personen"),
        ], "0.3"),

        new("0.4.00", new DateTime(2026, 7, 3), "Bilder, Verknüpfungen und eigene Felder",
        [
            Neu("0.4.00-bilder", "Mehrere Bilder auf einmal hochladen, Vollbild-Ansicht per Klick.", "Bilder"),
            Neu("0.4.01-verknuepfungen", "Verknüpfungen zwischen Akten an einer Stelle gesammelt.", "Akten"),
            Neu("0.4.02-customfelder", "Eigene Felder je Aktentyp, von der Führung festgelegt.", "Konfiguration"),
            Neu("0.4.03-vorlagen", "Vorlagen für Dokumente und Protokolle.", "Vorlagen"),
            Neu("0.4.04-wartung", "Wartungsmodus und eigene Akzentfarben der Behörde.", "Betrieb"),
            Besser("0.4.05-partner-dokumente", "Partnerbehörden können eigene Dokumente anlegen und bearbeiten.", "Partner"),
            Besser("0.4.06-vs-eigene", "TRU und HRB dürfen ihre eigenen Verschlusssachen bearbeiten.", "Dokumente"),
        ], "0.4"),

        new("0.5.00", new DateTime(2026, 7, 16), "Fahndung, Nachweis und Discord",
        [
            Neu("0.5.00-fahndungsbrett", "Fahndungsbrett mit allen ausgeschriebenen Personen.", "Fahndung"),
            Neu("0.5.01-nachweis", "Nachweis: wer hat wann was geändert und eingesehen.", "Nachweis"),
            Neu("0.5.02-discord", "Meldungen gehen automatisch in die passenden Discord-Kanäle.", "Discord"),
            Neu("0.5.03-ankuendigungen", "Ankündigungen mit Textgestaltung, Frist und Lesebestätigung.", "Brett"),
            Neu("0.5.04-anhaenge", "Anhänge lassen sich direkt in der Akte ansehen.", "Anhänge"),
            Besser("0.5.05-mitglieder", "Mitglieder einer Fraktion lassen sich in einem Rutsch pflegen.", "Fraktionen"),
            Besser("0.5.06-suche-relevanz", "Die Suche zeigt Passenderes zuerst.", "Suche"),
        ], "0.5"),

        new("0.6.00", new DateTime(2026, 7, 28), "Besprechungen, Abmeldungen und Kündigungen",
        [
            Neu("0.6.00-besprechungen", "Besprechungen mit Tagesordnung und Protokoll.", "Besprechungen"),
            Neu("0.6.01-abmeldungen", "Abmeldungen zeigen, wer wann nicht erreichbar ist.", "Abmeldungen"),
            Neu("0.6.02-kuendigung", "Kündigungen laufen als eigener, nachvollziehbarer Ablauf.", "Personal"),
            Neu("0.6.03-vermerke", "Vermerke in der Personalakte, mit Vorlagen.", "Personal"),
            Neu("0.6.04-erwaehnungen", "Wer in einem Text erwähnt wird, bekommt eine Benachrichtigung.", "Benachrichtigungen"),
            Besser("0.6.05-bewerbung-status", "Der Status einer Bewerbung lässt sich nachträglich ändern.", "Bewerbungen"),
        ], "0.6"),

        new("0.7.00", new DateTime(2026, 8, 2), "Neues Erscheinungsbild",
        [
            Besser("0.7.00-redesign", "Die Seite hat ein neues Erscheinungsbild bekommen.", "Bedienung"),
            Neu("0.7.01-bilder-text", "Bilder lassen sich direkt in Texte einfügen.", "Texte"),
            Neu("0.7.02-wertelisten", "Auswahllisten wie Status und Kategorien sind selbst pflegbar.", "Konfiguration"),
        ], "0.7"),

        new("0.8.00", new DateTime(2026, 8, 8), "Asservatenkammer, Kasse und NOOSEI",
        [
            Neu("0.8.00-asservate", "Asservatenkammer für Beweismittel, mit Ein- und Auslagerung.", "Asservate"),
            Neu("0.8.01-kasse", "Kasse der Behörde mit Buchungen und Belegen.", "Kasse"),
            Neu("0.8.02-finanzierung", "Finanzierungsanträge mit Genehmigungsweg.", "Finanzierung"),
            Neu("0.8.03-entfuehrungen", "Entführungen als eigene Akte.", "Ermittlung"),
            Neu("0.8.04-noosei", "NOOSEI beantwortet Fragen zum Aktenbestand.", "NOOSEI"),
            Neu("0.8.05-bestenliste", "Bestenliste der Agenten.", "Bestenliste"),
            Neu("0.8.06-feedback", "Feedback-Seite für Rückmeldungen zur Website.", "Feedback"),
            Besser("0.8.07-kasse-offen", "Kasse und Asservatenkammer stehen allen Agenten offen.", "Berechtigungen"),
            Besser("0.8.08-statistik", "Die Statistik wurde um viele Auswertungen erweitert.", "Statistik"),
        ], "0.8"),

        new("0.9.00", new DateTime(2026, 8, 12), "Die neue Suche und @-Erwähnungen",
        [
            Besser("0.9.00-suche-neu", "Die Suche wurde neu gebaut und findet jetzt auch bei Tippfehlern.", "Suche"),
            Neu("0.9.01-mentions", "@-Erwähnungen in allen Textfeldern, mit Sprung zur Akte.", "Texte"),
            Neu("0.9.02-informanten", "Informanten als eigene Akte.", "Ermittlung"),
            Neu("0.9.03-chronik", "Chronik als Tages-Feed statt langer Liste.", "Chronik"),
            Besser("0.9.04-nachweis-filter", "Im Nachweis lassen sich auch Teamleitungen und Partner filtern.", "Nachweis"),
        ], "0.9"),

        new("1.0.00", new DateTime(2026, 8, 25), "Der öffentliche Bereich geht online",
        [
            Neu("1.0.00-startseite", "Öffentliche Startseite mit Karriere-Bereich.", "Öffentlich"),
            Neu("1.0.01-gesucht", "Öffentliche Fahndung: Bürger sehen gesuchte Personen.", "Fahndung"),
            Neu("1.0.02-kopfgeld", "Kopfgeld auf ausgeschriebene Personen.", "Fahndung"),
            Neu("1.0.03-buergerkonto", "Bürgerkonten mit eigenem Bereich.", "Bürger"),
            Neu("1.0.04-hinweise", "Bürger können Hinweise einreichen.", "Hinweise"),
            Neu("1.0.05-tickets", "Tickets: Bürger schreiben der Führung.", "Tickets"),
            Neu("1.0.06-presse", "Presse, Warnungen und Lageberichte für die Öffentlichkeit.", "Öffentlich"),
            Neu("1.0.07-gefahrenlage", "Gefahrenlage-Ampel auf der öffentlichen Seite.", "Öffentlich"),
            Neu("1.0.08-oeff-suche", "Öffentliche Suche über die freigegebenen Inhalte.", "Öffentlich"),
            Neu("1.0.09-organisationen", "Organisationsprofile bekannter Gruppierungen.", "Öffentlich"),
        ], "1.0"),

        new("1.1.00", new DateTime(2026, 9, 2), "Belohnungen, Einspruch und ein gehärteter Außenbereich",
        [
            Neu("1.1.00-belohnung", "Belohnungen für nützliche Hinweise, mit Beleg.", "Hinweise"),
            Neu("1.1.01-einspruch", "Einspruch gegen eine Ausschreibung.", "Fahndung"),
            Neu("1.1.02-galerien", "Fotogalerien für Personengruppen und Parteien.", "Akten"),
            Besser("1.1.03-buerger-nav", "Der Bürgerbereich hat eine eigene Navigation bekommen.", "Bürger"),
            Fix("1.1.04-kpi", "Die Zahlen auf den öffentlichen Seiten stimmen jetzt.", "Öffentlich"),
            Fix("1.1.05-anmeldung", "Die Anmeldeseite lässt sich nicht mehr von außen blockieren.", "Anmeldung"),
            Fix("1.1.06-gesperrt", "Gesperrte Bürger bekommen eine ehrliche Auskunft statt einer leeren Seite.", "Bürger"),
            Fix("1.1.07-module-aus", "Abgeschaltete Bereiche antworten sauber, statt halb zu laden.", "Öffentlich"),
        ], "1.1"),

        new("1.2.00", new DateTime(2026, 9, 4), "Ergreifungsmeldung, FAQ und Eignungstests mit Zeitlimit",
        [
            Neu("1.2.00-organigramm", "Öffentliches Organigramm der Führung.", "Öffentlich"),
            Neu("1.2.01-ergreifung", "Ergreifungsmeldung: Bürger melden, dass sie jemanden gestellt haben.", "Fahndung"),
            Neu("1.2.02-faq", "Häufige Fragen auf einer eigenen Seite.", "Öffentlich"),
            Neu("1.2.03-test-zeit", "Eignungstests mit Bearbeitungszeit.", "Bewerbungen"),
            Neu("1.2.04-ticket-beteiligte", "An einem Ticket beteiligte Agenten und ein interner Nebenstrang.", "Tickets"),
            Neu("1.2.05-buerger-name", "Bürger vergeben ihren Namen einmal selbst.", "Bürger"),
            Besser("1.2.06-nachricht-bearbeiten", "Bürger können ihre Nachricht nachträglich bearbeiten.", "Bürger"),
            Besser("1.2.07-gefahr-manuell", "Die Gefahrenlage lässt sich von Hand setzen.", "Öffentlich"),
        ], "1.2"),

        new("1.3.00", new DateTime(2026, 9, 10), "Feinschliff",
        [
            Besser("1.3.00-kopfzeile", "Die Kopfzeile wurde aufgeräumt.", "Bedienung"),
            Besser("1.3.01-betraege", "Die Übersicht zeigt die Beträge mit an.", "Kasse"),
            Fix("1.3.02-belohnung-gebucht", "Eine Belohnung gilt erst als ausgezahlt, wenn sie gebucht ist.", "Hinweise"),
            Fix("1.3.03-test-antworten", "Antworten auf einen Test werden nicht mehr mit veralteten Angaben gespeichert.", "Bewerbungen"),
        ], "1.3"),

        new("2.0.00", new DateTime(2026, 9, 12), "Handbuch, Glossar und diese Seite hier",
        [
            Neu("2.0.00-neuerungen", "Diese Seite: hier steht ab jetzt, was sich geändert hat.", "Bedienung"),
            Neu("2.0.01-neuerungen-hinweis", "Nach dem Anmelden weist eine Karte auf Neuerungen hin.", "Bedienung"),
            Neu("2.0.02-handbuch", "Handbuch mit Anleitungen zu jeder Seite und jedem Ablauf.", "Handbuch"),
            Neu("2.0.03-glossar", "Glossar: über 140 Fachwörter in je einem Satz erklärt.", "Handbuch"),
            Neu("2.0.04-schaubilder", "Schaubilder zu Fahndung, Bewerbung, Bürgerhinweis und Rechten.", "Handbuch"),
            Neu("2.0.05-handbuch-suche", "Eigenes Suchfeld im Handbuch, das Artikel und Begriffe zugleich findet.", "Handbuch"),
            Neu("2.0.06-handbuch-pflege", "Führung und HRB können Handbuch und Glossar selbst bearbeiten.", "Handbuch"),
            Neu("2.0.07-module-hrb", "Das HRB kann Ausbildungsmodule jetzt selbst abhaken.", "Personal"),
            Neu("2.0.08-hilfe-knopf", "Ein Fragezeichen in der Kopfzeile führt zur Anleitung für diese Seite.", "Handbuch"),
            Neu("2.0.09-erklaerblasen", "Fachwörter in Texten erklären sich selbst, wenn du mit der Maus darüberfährst.", "Handbuch"),
            Neu("2.0.10-erklaerblasen-aus", "Die Worterklärungen lassen sich unter „Menü anpassen“ abschalten.", "Bedienung"),
            Neu("2.0.11-einarbeitung", "Eine Karte im Lagezentrum zeigt neuen Agenten die ersten sechs Schritte.", "Bedienung"),
            Neu("2.0.12-einarbeitung-stand", "Führung und HRB sehen in der Personalakte, wie weit die Einarbeitung ist.", "Personal"),
            Neu("2.0.13-noosei-handbuch", "NOOSEI beantwortet Fragen zur Bedienung aus dem Handbuch, mit Quellenangabe.", "Handbuch"),
            Neu("2.0.14-suche-handbuch", "Die Suche findet jetzt auch Handbuch-Artikel und Glossarbegriffe.", "Suche"),
        ], "1.4"),

        new("2.1.00", new DateTime(2026, 9, 14), "Geteilte Links sehen nach Behörde aus",
        [
            Neu("2.1.00-link-vorschau", "Ein Link auf eine öffentliche Seite erscheint im Discord als Karte mit "
                + "Titel, Kurztext und Bild.", "Öffentlich"),
            Neu("2.1.01-fahndung-vorschau", "Ein geteilter Fahndungslink zeigt Name, Vorwurf und das Fahndungsfoto.",
                "Fahndung"),
            Besser("2.1.02-dienstnummer", "Die Dienstnummer wird aus den freien römischen Nummern gewählt.", "Personal"),
            Fix("2.1.03-bewerbung-terminauswahl", "Die Termin-Auswahl in Bewerbungsnachrichten wird nicht mehr abgeschnitten.",
                "Bewerbungen"),
            Besser("2.1.04-verknuepfen", "Beim Verknüpfen sieht das Fenster überall gleich aus und schlägt schon "
                + "Verknüpftes nicht mehr vor.", "Bedienung"),
            Fix("2.1.05-tagesordnung-notiz", "Die erste Zeile einer Notiz in der Tagesordnung liegt nicht mehr unter "
                + "der Formatierungsleiste.", "Besprechungen"),
            Neu("2.1.06-editor-struktur", "Der Texteditor hat jetzt Checklisten, Einzüge und Ausrichtung, und über "
                + "den Schrägstrich am Zeilenanfang lassen sich Überschriften und Listen einfügen.", "Bedienung"),
            Neu("2.1.07-editor-komfort", "Lange Texte zeigen Wortzahl und Gliederung und lassen sich im Vollbild "
                + "durchsuchen und ersetzen.", "Bedienung"),
            Neu("2.1.08-editor-entwurf", "Ein nicht gespeicherter Text geht nicht mehr verloren: Beim nächsten "
                + "Öffnen bietet der Editor ihn zur Wiederherstellung an.", "Bedienung"),
            Neu("2.1.09-editor-bilder", "Bilder im Text lassen sich jetzt in der Größe anpassen, ausrichten "
                + "und mit einer Beschriftung versehen – ein Klick auf das Bild öffnet die Einstellungen.", "Bedienung"),
            Neu("2.1.10-noosei-kontingent", "Das NOOSEI-Wochenkontingent lässt sich für alle Dienstgrade zugleich "
                + "anheben, ohne die Regeln je Dienstgrad anzufassen.", "NOOSEI"),
            Neu("2.1.11-editor-einfuegen", "Eingefügtes aus Word oder einer Webseite kommt jetzt aufgeräumt an, und "
                + "mit Strg+Shift+V fügt man nur den reinen Text ein.", "Bedienung"),
            Neu("2.1.12-editor-struktur", "Hinweis-, Warnungs- und Info-Kästen, Trennlinien und ein "
                + "Inhaltsverzeichnis lassen sich jetzt direkt im Text setzen.", "Bedienung"),
            Neu("2.1.13-editor-komfort", "Der Editor zeigt, wann der Entwurf zuletzt gesichert wurde, hat Knöpfe "
                + "für Rückgängig und Wiederholen, und Strg+S speichert.", "Bedienung"),
            Neu("2.1.14-editor-auswahl", "Über markiertem Text schwebt jetzt eine kleine Leiste mit den "
                + "wichtigsten Formaten, und eingefügte Bilder werden automatisch verkleinert, damit alles "
                + "schnell bleibt.", "Bedienung"),
            Fix("2.1.15-editor-ersetzen", "„Alle ersetzen\" trifft jetzt auch in Texten mit Bildern oder "
                + "Erwähnungen die richtige Stelle und lässt Erwähnungen unangetastet.", "Bedienung"),
            Fix("2.1.16-editor-inhaltsverzeichnis", "Die Einträge des Inhaltsverzeichnisses springen jetzt "
                + "wirklich zur Überschrift – auch in Texten ohne Bild und in Handbuch-Artikeln.", "Bedienung"),
            Fix("2.1.17-editor-entwurf", "Nach dem Speichern wird ein Entwurf nicht mehr beim nächsten Öffnen "
                + "erneut angeboten – betraf Ankündigungen, Handbuch, Glossar, Beförderungen und Fragen.", "Bedienung"),
            Fix("2.1.18-editor-bilder", "Zwei Bilder in derselben Zeile gehen beim Speichern nicht mehr "
                + "verloren.", "Bedienung"),
            Fix("2.1.19-noosei-kontingent", "Ein individuelles Kontingent lässt sich bestätigen, ohne dass sich "
                + "der Wert dabei jedes Mal vervielfacht.", "NOOSEI"),
            Fix("2.1.20-editor-codeblock", "In einem Codeblock bleiben Raute, Strich und Schrägstrich stehen, "
                + "statt die Zeile umzuformatieren, und „Text\" löst jetzt auch Zitat und Codeblock auf.", "Bedienung"),
            Fix("2.1.21-editor-gliederung", "Das eingefügte Inhaltsverzeichnis behält seine Ebenen.", "Bedienung"),
            Fix("2.1.22-glossar-ausgeblendet", "Ein ausgeblendeter Glossarbegriff ist für die Redaktion wieder "
                + "sichtbar und lässt sich zurückholen; sein Artikel-Link geht beim Speichern nicht mehr "
                + "verloren.", "Handbuch"),
            Fix("2.1.23-handbuch-tempo", "Das Handbuch lädt für Lesende deutlich weniger im Hintergrund.", "Handbuch"),
            Fix("2.1.24-entfuehrung-auswahl", "Die Auswahl „Kompromittierte Akte\" bietet nur noch echte Akten "
                + "an – Handbuch-Artikel und Glossarbegriffe standen fälschlich mit drin.", "Akten"),
            Fix("2.1.25-erste-schritte", "Der Punkt „Menü angepasst\" in den ersten Schritten verweist jetzt "
                + "auf den Knopf „Navigation anpassen\" oben im Menü, statt ins Leere zu führen.", "Bedienung"),
            Fix("2.1.26-rechtstexte", "Geteilte Links auf Datenschutz und Nutzungsbedingungen zeigen jetzt eine "
                + "Vorschaukarte statt einer nackten Adresse.", "Öffentlich"),
            Fix("2.1.27-glossar-wortmitte", "Eine Worterklärung erscheint nicht mehr mitten in einem längeren "
                + "Wort, wenn ein Teil davon fett oder kursiv gesetzt ist.", "Handbuch"),
            Fix("2.1.28-glossar-link", "Der „mehr dazu\"-Link eines Glossarbegriffs führt nicht mehr auf einen "
                + "Artikel, dessen Kapitel ausgeblendet ist.", "Handbuch"),
            Fix("2.1.29-handbuch-asservate", "Der Artikel zur Asservatenkammer sagt jetzt richtig, dass "
                + "einlagern jeder darf und eine Herausnahme nur die Führung bucht.", "Handbuch"),
            Fix("2.1.30-menue-zurueck", "Eine gerade geänderte Menü-Einstellung springt nicht mehr kurzzeitig "
                + "auf den alten Stand zurück.", "Bedienung"),
            Fix("2.1.31-neuerungen-fassung", "Die Hinweiskarte nennt nur noch eine Fassung, die auf dieser "
                + "Seite auch zu finden ist.", "Bedienung"),
            Besser("2.1.32-fraktion-wechsel", "Beim Hinzufügen zu einer Fraktion lässt sich eine Person "
                + "gleich aus ihrer bisherigen Fraktion entfernen.", "Fraktionen"),
            Fix("2.1.33-word-einfuegen", "Beim Einfügen aus Word geht kein Inhalt mehr verloren - vorher "
                + "verschwanden ganze Absätze und Rasterblöcke.", "Bedienung"),
            Fix("2.1.34-word-listen", "Eine nummerierte Liste aus Word bleibt nummeriert und behält ihre "
                + "Einrückung.", "Bedienung"),
            Fix("2.1.35-inhaltsverzeichnis", "Das eingefügte Inhaltsverzeichnis führt jetzt auch in Presse, "
                + "Warnungen, Lageberichten und Vorlagen zu den Überschriften.", "Bedienung"),
            Fix("2.1.36-trennlinie", "Ein Text, der nur aus einer Trennlinie besteht, geht beim Speichern "
                + "nicht mehr verloren.", "Bedienung"),
            Fix("2.1.37-auswahl-fett", "Der Fett-Knopf über einer gemischt formatierten Auswahl setzt die "
                + "Fettung, statt sie zu entfernen.", "Bedienung"),
            Fix("2.1.38-listen-nummern", "Eingerückte nummerierte Listen zählen in der Leseansicht und im "
                + "Ausdruck richtig durch.", "Bedienung"),
            Fix("2.1.39-druck-schrift", "Gedruckte Akten zeigen ihren Fließtext wieder in Schwarz statt "
                + "beinahe weiß.", "Druck"),
            Fix("2.1.40-editor-schmal", "Suchen und Ersetzen im Texteditor passt auch auf ein schmales "
                + "Fenster.", "Bedienung"),
            Fix("2.1.41-entwurf-abmelden", "Beim Abmelden werden die im Browser zwischengespeicherten "
                + "Entwürfe entfernt.", "Bedienung"),
            Fix("2.1.42-entwurf-wegklicken", "Ein Entwurf wird auch dann gesichert, wenn direkt nach dem "
                + "letzten Wort weggeklickt wird.", "Bedienung"),
            Fix("2.1.43-tagesordnung-notiz", "Beim Wechsel zwischen zwei Tagesordnungspunkten geht eine "
                + "getippte Notiz nicht mehr verloren.", "Besprechungen"),
            Fix("2.1.44-handbuch-ausgeblendet", "Ein ausgeblendetes Kapitel und ein ausgeblendeter Artikel "
                + "bleiben für die Redaktion sichtbar und lassen sich zurückholen.", "Handbuch"),
            Fix("2.1.45-handbuch-suchtreffer", "Ein Glossartreffer aus der Suche öffnet sich auch dann, wenn "
                + "das Handbuch schon offen ist; die Adresse bleibt danach sauber.", "Handbuch"),
            Fix("2.1.46-handbuch-menuepunkt", "Zwei Handbuch-Artikel können nicht mehr denselben Menüpunkt "
                + "beanspruchen — der „?\"-Knopf zeigt sonst auf den falschen.", "Handbuch"),
            Fix("2.1.47-handbuch-adresse", "Die Adresse eines Kapitels wird beim Speichern nicht mehr "
                + "stillschweigend gekürzt.", "Handbuch"),
            Fix("2.1.48-handbuch-redaktion", "Das Handbuch lädt für die Redaktion spürbar schneller.", "Handbuch"),
            Fix("2.1.49-handbuch-start", "Ein selbst angelegter Artikel oder Begriff bringt die Seite nach "
                + "einem Update nicht mehr zum Stillstand.", "Handbuch"),
            Fix("2.1.50-glossar-am-bild", "Eine Worterklärung fällt nicht mehr aus, wenn direkt daneben ein "
                + "Bild steht.", "Handbuch"),
            Fix("2.1.51-glossar-schalter", "Der Schalter „Fachwörter erklären\" wirkt sofort, nicht erst nach "
                + "einem Seitenwechsel.", "Bedienung"),
            Fix("2.1.52-neuerungen-karte", "Die Hinweiskarte verschwindet, sobald die Seite mit den Neuerungen "
                + "geöffnet wurde.", "Bedienung"),
            Fix("2.1.53-nur-lesen-knoepfe", "Wer nur lesen darf, bekommt keine Knöpfe mehr angeboten, die "
                + "beim Klick ohnehin abgelehnt werden.", "Bedienung"),
            Fix("2.1.54-verknuepfen-dublette", "Die Personenakte schlägt beim Verknüpfen keine Akte mehr vor, "
                + "die schon verknüpft ist.", "Akten"),
            Fix("2.1.55-entfuehrung-typen", "Auch beim Anlegen und Bearbeiten einer Entführung lassen sich "
                + "nur echte Akten als kompromittiert vermerken.", "Akten"),
            Fix("2.1.56-noosei-kontingent-parallel", "Zwei gleichzeitig gestellte NOOSEI-Fragen überziehen das "
                + "Wochenkontingent nicht mehr.", "NOOSEI"),
            Fix("2.1.57-noosei-handbuch", "NOOSEI sagt zuverlässig, wenn im Handbuch nichts zu einer Frage "
                + "steht, statt leer zu antworten.", "NOOSEI"),
            Fix("2.1.58-bilder-aufraeumen", "Ein ersetztes oder gelöschtes Bild belegt keinen Speicherplatz "
                + "mehr.", "Bedienung"),
            Fix("2.1.59-doppelte-eingabe", "Legen zwei Redakteure gleichzeitig dieselbe Fassung oder Adresse "
                + "an, erscheint eine verständliche Meldung statt einer technischen.", "Bedienung"),
            Fix("2.1.60-einfuegen-ersetzt", "Eingefügter Text landet an der Schreibmarke, statt den bereits "
                + "geschriebenen Text zu ersetzen.", "Bedienung"),
            Besser("2.1.61-werkzeugleiste", "Die Knöpfe des Texteditors und der Hinweis im leeren Feld sind "
                + "wieder gut lesbar.", "Bedienung"),
            Neu("2.1.62-dienstvorschrift", "Das Handbuch hat zwei neue Kapitel: die Dienstverordnung in "
                + "Kurzform und die Listen zu Ausrüstung, Funk, Fahrzeugen und Einrichtungen.", "Handbuch"),
            Besser("2.1.63-freigaben-erklaert", "Das Handbuch erklärt jetzt, wie die Sicherheitsfreigaben der "
                + "Dienstverordnung und die Verschlusssachen-Stufen der Seite zusammenhängen.", "Handbuch"),
            Fix("2.1.64-kennzeichen-rang", "TRU und HRB lassen sich nur noch ab dem Dienstgrad Special Agent "
                + "vergeben, wie es die Dienstverordnung vorsieht. Wird jemand darunter herabgestuft, "
                + "verliert er die Zugehörigkeit automatisch.", "Personal"),
        ], "1.5"),

        new("2.2.00", new DateTime(2026, 9, 22), "Archiv, Funkplan und kurze Wege",
        [
            Neu("2.2.00-archiv", "Personen, Fraktionen, Gruppen, Parteien, Vorgänge, Operationen und "
                + "Taskforces lassen sich archivieren: sie verschwinden aus Listen und Suche, bleiben aber "
                + "lesbar und sind mit einem Klick wieder da.", "Akten", "2.1.65-archiv"),
            Neu("2.2.01-score-verlauf", "Neben der Gefährdung steht jetzt eine kleine Verlaufskurve – in den "
                + "Listen der Personen und Fraktionen, im Lagezentrum und bei den beobachteten Akten. Du "
                + "siehst damit auf einen Blick, ob ein Wert steigt, fällt oder sich nicht bewegt.",
                "Akten", "2.1.66-score-verlauf"),
            Neu("2.2.02-funkplan", "Es gibt einen Funkplan: wer auf welchem Kanal funkt – eigene Kanäle, "
                + "Partnerbehörden und die Frequenzen der Fraktionen an einer Stelle. Tippst du eine "
                + "aufgeschnappte Frequenz in die Suche, bekommst du heraus, zu wem sie gehört; Komma und "
                + "Punkt sind dabei gleichwertig.", "Ermittlung", "2.1.67-funkplan"),
            Neu("2.2.03-abwesenheit", "Wer abgemeldet ist, steht jetzt mit dem Hinweis „abgemeldet bis …“ in "
                + "den Auswahllisten für Aufgaben, Termine und Wiedervorlagen – und zwar für den Tag, den du "
                + "im Formular eingetragen hast. Wählen kannst du ihn trotzdem; du weißt es nur vorher.",
                "Dienstbetrieb", "2.1.68-abwesenheit"),
            Neu("2.2.04-kurzbefehle", "Es gibt Tastenkürzel: „?“ zeigt sie alle, „g“ und ein "
                + "Buchstabe springen in einen Bereich, „n“ legt auf einer Liste einen neuen Eintrag an "
                + "und „e“ öffnet die Akte zum Bearbeiten. Solange du in ein Feld schreibst, passiert "
                + "nichts davon.", "Bedienung", "2.1.69-kurzbefehle"),
            Neu("2.2.05-schnellerfassung", "Der Plus-Knopf oben in der Kopfzeile kann mehr als neue Akten "
                + "anlegen: Du hängst darüber auch einen Vermerk an eine gesuchte Akte oder hältst eine "
                + "Dienst-Aktivität fest – und bleibst dabei auf der Seite, auf der du gerade arbeitest.",
                "Bedienung", "2.1.70-schnellerfassung"),
            Neu("2.2.06-textbausteine", "Du kannst dir eigene Textbausteine anlegen: Sätze, die du immer "
                + "wieder schreibst, holst du mit einem Schrägstrich in jedes Textfeld. Platzhalter wie "
                + "Name, Aktenzeichen und Datum füllen sich dabei von selbst.", "Bedienung"),
            Neu("2.2.07-ansichten", "Auf den Listen der Personen, Fraktionen, Vorgänge und Operationen und "
                + "auf dem Aufgaben-Board merkst du dir eine gefilterte Auswahl unter einem Namen – sie steht "
                + "dann unter den Favoriten und im Schnellzugriff. Das Aufgaben-Board behält seine Filter "
                + "jetzt auch, wenn du die Seite neu lädst.", "Bedienung"),
            Neu("2.2.08-entwuerfe", "Was du in ein Dok, eine Observation, einen Vermerk, den Taskforce-Chat oder "
                + "die Beschreibung einer Akte tippst, hebt dein Browser jetzt mit auf. Reißt die Verbindung ab "
                + "oder schließt du einen Dialog aus Versehen, bietet dir das Feld den Text beim nächsten Öffnen "
                + "wieder an.", "Bedienung"),
            Neu("2.2.09-neu-seit-besuch", "Öffnest du eine Akte wieder, steht oben, was andere seit deinem "
                + "letzten Besuch eingetragen haben – etwa „Einstufung geändert, 1 Dok und 3 Kommentare“. Im "
                + "Zeitstrahl trägt jeder dieser Einträge ein „Neu“, und ein Klick filtert auf genau sie. Was "
                + "du selbst eingetragen hast, zählt nicht mit.", "Akten"),
            Besser("2.2.10-neuerungen-fenster", "Nach einem Update zeigt dir die Seite beim ersten Aufruf in einem "
                + "kleinen Fenster, was sich geändert hat – mit einem Knopf zu allen Neuerungen. Bisher stand dort "
                + "nur eine Karte mit der Anzahl, und die hat manche Neuerung übersehen.", "Bedienung"),
            Neu("2.2.11-mehrfachauswahl", "In der Suche wählst du über „Auswählen“ mehrere Akten auf einmal aus – "
                + "auch über mehrere Suchbegriffe hinweg – und verknüpfst sie in einem Schritt mit einem Vorgang, "
                + "gibst ihnen ein Stichwort oder beobachtest sie alle.", "Bedienung"),
            Neu("2.2.12-posteingang", "Unten in der Glocke führt „Alle Benachrichtigungen“ auf eine eigene Seite: "
                + "dort findest du auch ältere Meldungen als die zwanzig neuesten, filterst nach Art und Zeitraum und "
                + "holst eine versehentlich angeklickte Meldung mit „Als ungelesen markieren“ zurück.", "Bedienung",
                "2.2.11-posteingang"),
            Neu("2.2.13-routen", "Eine Drogenroute gehört jetzt immer nur einer Fraktion. Trägst du eine Route ein, "
                + "die schon eine andere Fraktion hält, fragt die Seite nach – bestätigst du, geht sie beim Speichern "
                + "über, und beide Akten vermerken den Wechsel.", "Akten"),
            Besser("2.2.14-asservat-herausnahme", "Etwas aus der Asservatenkammer herausnehmen und die Buchung "
                + "dazu anlegen darfst du jetzt schon ab dem Dienstgrad Senior Special Agent, nicht erst als "
                + "Führung.", "Asservate"),
            Neu("2.2.15-gesetzbuecher", "Unter Gesetze siehst du zuerst die Gesetzbücher. Ein Klick öffnet das "
                + "Buch mit allen Paragrafen nach Abschnitten, und jeden Paragrafen klappst du direkt dort auf.",
                "Gesetze"),
            Besser("2.2.16-gesetzestext-formatiert", "Gesetzestexte lassen sich jetzt formatieren – mit Absätzen, "
                + "Aufzählungen, Fett und Kursiv –, und bestehende Texte behalten ihre Zeilen.", "Gesetze"),
            Fix("2.2.17-zeilenumbrueche", "Zeilenumbrüche in Texten bleiben nach dem Speichern sichtbar, statt "
                + "zu einer Zeile zusammenzulaufen.", "Bedienung"),
        ]),
    ];
}
