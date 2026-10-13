using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter five: the everyday running of the office.</summary>
internal static class DutyChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-dienstbetrieb", "dienstbetrieb", "Dienstbetrieb",
        "Aufgaben, Kalender, Besprechungen, Abmeldungen, Brett und Benachrichtigungen.", "EventNote",
        [
            new Article("art-aufgaben", "aufgaben", "Das Aufgaben-Board",
                "To-dos der Dienststelle als Tafel - sichtbar für alle.",
                """
                <p>Das <strong>Aufgaben-Board</strong> zeigt die To-dos der Dienststelle in Spalten:
                <em>Offen</em>, <em>In Arbeit</em>, <em>Erledigt</em>, <em>Verworfen</em>. Du verschiebst eine
                Karte per Ziehen von einer Spalte in die nächste.</p>
                <p>Eine Aufgabe hat einen Zuständigen, eine Frist und eine Priorität. Wird die Frist fällig,
                bekommt der Zuständige eine Benachrichtigung.</p>
                <p>Normalerweise sehen <strong>alle</strong> Agenten alle Aufgaben - das ist der Sinn einer
                Tafel. Wenn eine Aufgabe das nicht verträgt, setzt du den Schalter
                <strong>eingeschränkt</strong>: dann sehen sie nur die Zugeteilten, der Ersteller und die
                Führung.</p>
                """,
                NavKey: "aufgaben"),

            new Article("art-kalender", "kalender", "Kalender",
                "Termine der Behörde in der Monatsansicht.",
                """
                <p>Der <strong>Kalender</strong> zeigt die Termine der Dienststelle - Einsätze, Besprechungen,
                Schulungen, Fristen. Du legst einen Termin direkt im Kalender an, indem du auf einen Tag
                klickst.</p>
                <p>Jeder Termin hat eine <strong>Kategorie</strong> (die die Farbe bestimmt), eine
                Sichtbarkeitsstufe und einen Status. Termine, die aus anderen Bereichen kommen - Operationen,
                Besprechungen, Wiedervorlagen - erscheinen automatisch mit.</p>
                <p>Achte auf die Sichtbarkeit. Ein Termin, der die ganze Dienststelle betrifft, sollte auch für
                sie sichtbar sein; ein Zugriff, der noch geheim ist, nicht.</p>
                """,
                NavKey: "kalender"),

            new Article("art-besprechungen", "besprechungen", "Besprechungen",
                "Tagesordnung vorher, Protokoll nachher - und wer was wann sieht.",
                """
                <p>Eine <strong>Besprechung</strong> ist mehr als ein Termin: sie hat eine
                <strong>Tagesordnung</strong>, eine Anwesenheitsliste und hinterher ein
                <strong>Protokoll</strong>.</p>
                <p>Vorher trägst du die Punkte ein, über die gesprochen werden soll - jeder Teilnehmer kann
                sich darauf vorbereiten. Hinterher hältst du fest, was beschlossen wurde.</p>
                <p>Eine Besonderheit bei der Sichtbarkeit: Tagesordnung und Protokoll sind
                <strong>zwei Stunden nach dem Ende für jeden internen Agenten lesbar</strong>, unabhängig vom
                Dienstgrad. Vorher sehen sie nur die Eingeladenen. Partnerkonten sehen sie nie.</p>
                <p>Wer fehlt, kann sich abmelden - die Abmeldung erscheint direkt in der
                Anwesenheitsliste.</p>
                """,
                NavKey: "besprechungen"),

            new Article("art-abmeldungen", "abmeldungen", "Abmeldungen",
                "Abwesenheit ankündigen, damit niemand auf dich wartet.",
                """
                <p>Eine <strong>Abmeldung</strong> meldet, dass du für eine Zeit nicht verfügbar bist -
                Urlaub, Prüfungen, Krankheit, was auch immer. Du gibst Zeitraum, Kategorie und optional einen
                Grund an.</p>
                <p>Das ist keine Formalität. Wer abgemeldet ist, trägt beim Zuweisen einer <em>Aufgabe</em>,
                eines <em>Termins</em> oder einer <em>Wiedervorlage</em> den Zusatz <em>abgemeldet bis …</em> -
                im Anlegen-Formular ebenso wie später im Abschnitt <em>Beteiligte</em> beziehungsweise
                <em>Teilnehmer</em>. Gefragt wird nach dem Tag, um den es geht: der Fälligkeit, dem Beginn. Steht
                dort noch kein Datum, gilt der heutige Tag. Wählbar bleibst du trotzdem - die Seite warnt, sie
                entscheidet nicht. In der Anwesenheitsliste von Besprechungen taucht die Abmeldung ebenfalls
                auf.</p>
                <p>Melde dich lieber zu oft ab als zu selten. Eine Abmeldung kostet nichts; ein Agent, auf den
                eine Woche lang gewartet wird, kostet die Dienststelle Zeit.</p>
                """,
                NavKey: "abmeldungen"),

            new Article("art-brett", "schwarzes-brett", "Das Schwarze Brett",
                "Ankündigungen - und die, die du bestätigen musst.",
                """
                <p>Am <strong>Schwarzen Brett</strong> stehen behördliche Ankündigungen: Dienstanweisungen,
                Hinweise, Termine, Personalien. Jede Ankündigung richtet sich an eine bestimmte Gruppe - alle,
                nur die Führung, nur eine Einheit.</p>
                <p>Manche Ankündigungen sind <strong>quittierungspflichtig</strong>. Dann steht ein Knopf
                <em>Zur Kenntnis genommen</em> darunter, und die Zahl am Menüeintrag bleibt stehen, bis du
                geklickt hast. Wer angekündigt hat, sieht, wer schon quittiert hat.</p>
                <p>Das ist kein Schikane-Werkzeug, sondern ein Nachweis: Bei einer Dienstanweisung muss die
                Führung belegen können, dass sie jeden erreicht hat.</p>
                """,
                NavKey: "brett"),

            new Article("art-aktivitaeten", "dienst-aktivitaeten", "Dienst-Aktivitäten",
                "Was du selbst über deinen Dienst festhältst.",
                """
                <p>Eine <strong>Dienst-Aktivität</strong> ist ein kurzer Eintrag, den du über dich selbst
                führst: Streife gefahren, Vernehmung geführt, Akte aufgearbeitet. Datum, Art, ein paar
                Sätze.</p>
                <p>Diese Einträge sind <strong>für alle sichtbar</strong>. Sie sind das, woran die Dienststelle
                ablesen kann, wer woran gearbeitet hat.</p>
                <p>Schreib sie zeitnah. Eine Woche später weiß niemand mehr, was am Dienstag war. Für den
                schnellen Eintrag zwischendurch gibt es den Plus-Knopf in der Kopfzeile.</p>
                """,
                NavKey: "aktivitaeten"),

            new Article("art-bestenliste", "bestenliste", "Bestenliste",
                "Das Ranking - und was es wirklich misst.",
                """
                <p>Die <strong>Bestenliste</strong> stellt die Agenten nach dokumentierter Ermittlungsarbeit
                gegenüber. Gezählt werden sechs Dinge: angelegte Akten, geführte Doks, gesetzte Verknüpfungen,
                vergebene Einstufungen, protokollierte Observationen und abgeschlossene Vorgänge - über Woche,
                Monat oder insgesamt. Aufgaben und Dienst-Aktivitäten zählen ausdrücklich <em>nicht</em>.</p>
                <p>Sie misst <strong>Dokumentation</strong>, nicht Verdienst. Wer viel tut und nichts
                aufschreibt, steht unten; das ist kein Fehler der Liste, sondern ihre Aussage.</p>
                <p>Die Führungsränge stehen in einer eigenen Wertung. Sonst würde die Liste ausschließlich
                abbilden, wer am längsten dabei ist.</p>
                """,
                NavKey: "bestenliste"),

            new Article("art-benachrichtigungen", "benachrichtigungen", "Benachrichtigungen",
                "Die Glocke - was dort landet und warum.",
                """
                <p>Oben rechts sitzt die <strong>Glocke</strong>. Dort landet alles, was dich persönlich
                betrifft: eine Erwähnung in einem Kommentar, eine fällige Wiedervorlage, eine Änderung an einer
                Akte, der du folgst, eine Entscheidung über deinen Antrag.</p>
                <p>Jede Benachrichtigung führt mit einem Klick genau dorthin, wo etwas passiert ist. Gelesene
                verschwinden aus der Zahl, nicht aus der Liste.</p>
                <p>Die Glocke zeigt nur die zwanzig neuesten. Alles davor steht unter <em>Alle
                Benachrichtigungen</em> ganz unten in der Glocke: dort kannst du nach Art und Zeitraum filtern,
                nur die ungelesenen zeigen und seitenweise zurückblättern.</p>
                <p>Hast du etwas versehentlich angeklickt, holst du den Merker dort zurück: <em>Als ungelesen
                markieren</em>. Die Meldung zählt dann wieder in der Glocke, und unter <em>Nur ungelesene</em>
                findest du sie im Posteingang auch dann, wenn sie älter als die zwanzig neuesten ist. Solange eine
                Meldung zu einer beobachteten Akte ungelesen ist, kommt zu dieser Akte keine zweite dazu - die
                offene sagt ja schon, dass sich dort etwas getan hat.</p>
                <p>Was die Glocke klingeln lässt, stellst du in deinem <em>Profil</em> unter <em>Meine
                Benachrichtigungen</em> ein - je Art ein Schalter. Eine ausgeschaltete Art geht nicht verloren:
                sie landet gelesen im Posteingang, nur die Zahl an der Glocke bleibt still. Mit <em>Ruhe während
                meiner Abmeldung</em> gilt das für alle Arten, solange eine Abmeldung den heutigen Tag abdeckt.
                Erwähnungen und Meldungen zu deinem Konto kommen immer an.</p>
                <p>Ein Teil davon geht zusätzlich nach <strong>Discord</strong>. Was dort ankommt, ist bewusst
                inhaltsarm - eine Zeile, die sagt, dass es etwas gibt, nicht was es ist.</p>
                <p>Erwähnungen, Aufgaben und fällige Wiedervorlagen pingen dich dort persönlich. Wann, legst du im
                selben Abschnitt des Profils fest: <em>Sofort</em>, <em>Gesammelt</em> oder <em>Nie</em>.
                Gesammeltes kommt einmal am Tag um 18 Uhr als <em>Tagesüberblick</em> - eine Zeile mit deinem
                Ping und der Zahl der Meldungen, die du bis dahin noch nicht gelesen hast. Was du vorher in der
                Glocke liest, pingt dich also gar nicht mehr. Eine Art, die du für die Glocke ausgeschaltet hast,
                kommt schon gelesen an und fehlt deshalb auch im Tagesüberblick. Ankündigungen, Besprechungen und
                andere Rundrufe pingen eine ganze Rolle; die schaltest du in Discord selbst stumm.</p>
                """,
                NavKey: "benachrichtigungen",
                Steps:
                [
                    new Step("Inbox", "Alle Benachrichtigungen öffnen",
                        "Ganz unten in der Glocke - oder im Menü unter Mein Dienst."),
                    new Step("FilterAlt", "Eingrenzen",
                        "Nach Art, Zeitraum oder nur ungelesene. Den Filter kannst du dir als Ansicht merken."),
                    new Step("MarkEmailUnread", "Merker zurückholen",
                        "Der Knopf am Zeilenende markiert wieder als ungelesen, ohne die Akte zu öffnen."),
                ]),

            new Article("art-schnellerfassung", "schnellerfassung", "Schnell etwas festhalten",
                "Die Wege, die weniger als eine Minute brauchen.",
                """
                <p>Vieles muss nur festgehalten, nicht ausgearbeitet werden. Dafür gibt es die kurzen Wege:</p>
                <ul>
                <li><strong>Strg+K</strong> öffnet überall die Schnellsuche - der schnellste Weg zu einer
                Akte, ohne die aktuelle Seite zu verlassen.</li>
                <li>Ein <strong>Kommentar</strong> an der Akte ist immer richtig, wenn du dir nicht sicher
                bist, wohin etwas gehört.</li>
                <li>Ein <strong>Bild</strong> fügst du mit Strg+V direkt in ein Textfeld ein; es landet an der
                Akte, nicht bei dir.</li>
                <li>Eine <strong>Wiedervorlage</strong> kostet zehn Sekunden und verhindert, dass etwas
                vergessen wird.</li>
                </ul>
                <p>Die Regel dahinter: lieber eine unvollständige Notiz heute als ein perfekter Bericht, den
                nie jemand schreibt.</p>
                """),

            new Article("art-feedback", "feedback", "Feedback zur Seite",
                "Wenn etwas fehlt oder nicht funktioniert.",
                """
                <p>Unter <em>Feedback</em> im Bereich <em>Mein Dienst</em> meldest du, was an dieser Seite
                nicht stimmt oder fehlt: ein Fehler, ein Wunsch, eine Verständnisfrage.</p>
                <p>Schreib dazu, <strong>was du tun wolltest</strong> und was stattdessen passiert ist. Das ist
                der Unterschied zwischen einer Meldung, die sich beheben lässt, und einer, die zurückkommt.</p>
                <p>Wenn es um einen Fehler geht: die Fassung, mit der du arbeitest, steht oben auf der Seite
                <em>Neuerungen</em>. Nenn sie mit.</p>
                """,
                NavKey: "feedback"),
        ]);
}
