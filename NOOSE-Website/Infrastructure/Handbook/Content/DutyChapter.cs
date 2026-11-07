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

            new Article("art-kalender-abo", "kalender-abo", "Kalender-Abo für das Handy",
                "Dein Kalender als privater Link für Google, Apple oder Outlook - und was dabei die Seite verlässt.",
                """
                <p>Mit dem <strong>Kalender-Abo</strong> stehen deine Termine auch auf dem Handy. Du legst den Link
                in deinem <em>Profil</em> unter <em>Kalender-Abo</em> mit <em>Link erstellen</em> an und trägst ihn in
                deiner Kalender-App als Kalender per Link ein (bei Google <em>Per URL</em>, bei Apple
                <em>Abonnierten Kalender hinzufügen</em>). Die App holt die Einträge dann selbst ab, meist einmal die
                Stunde.</p>
                <p>Im Abo stehen deine Termine, die Besprechungen, deine fälligen Aufgaben, deine Wiedervorlagen,
                deine Abmeldungen und die Operationen, soweit du sie auch im <em>Kalender</em> siehst, von 30 Tagen
                zurück bis 180 Tage voraus. Abgesagte oder erledigte Einträge fehlen. Observationen, Personen-Doks,
                Fraktions-Aktivitäten und die Abmeldungen anderer stehen nie darin.</p>
                <p><strong>Der Link ist ein Schlüssel.</strong> Wer ihn kennt, sieht deinen Kalender, ohne sich
                anzumelden. Er steht nur im Moment des Erstellens auf der Seite; gespeichert wird er nicht, nur ein
                Fingerabdruck davon. Ist er verloren oder weitergegeben, erzeugst du mit <em>Neuen Link erzeugen</em>
                einen neuen, der alte hört sofort auf zu gelten. <em>Widerrufen</em> beendet das Abo ganz. Wirst du
                gesperrt oder verlässt du die Behörde, hört der Link von selbst auf zu gelten. Jeder Abruf landet,
                höchstens einmal je halbe Stunde, im Zugriffsprotokoll.</p>
                <p><strong>Das Abo verlässt die Seite.</strong> Deine Kalender-App speichert die Einträge bei ihrem
                Anbieter, zum Beispiel Google oder Apple, und das FIB kann sie dort nicht mehr zurückholen. Deshalb
                heißen Operationen und Wiedervorlagen im Abo nur <em>Operation</em> und <em>Wiedervorlage</em>, ohne
                Namen und ohne Akte. Termine, Besprechungen und Aufgaben tragen ihren Titel: schreibe dort nichts
                hinein, was nicht auf ein Handy gehört, oder schalte <em>Titel im Abo neutral halten</em> ein, dann
                steht nur <em>Termin</em>, <em>Besprechung</em> und <em>Aufgabe fällig</em> in der App. Teamleitungen
                und Partnerbehörden haben kein Abo.</p>
                """,
                RoleplayHtml: """
                <p>Im Rollenspiel ist das dein Diensthandy: es erinnert dich an die Besprechung, auch wenn du gerade
                nicht auf der Seite bist. Was auf dem Handy steht, kann auch jemand lesen, der es in die Hand
                bekommt, also keine Klarnamen oder Ziele in Titel schreiben.</p>
                """),

            new Article("art-besprechungen", "besprechungen", "Besprechungen",
                "Tagesordnung vorher, Protokoll nachher - und wer was wann sieht.",
                """
                <p>Eine <strong>Besprechung</strong> ist mehr als ein Termin: sie hat eine
                <strong>Tagesordnung</strong>, eine Anwesenheitsliste und hinterher ein
                <strong>Protokoll</strong>.</p>
                <p>Vorher trägst du die Punkte ein, über die gesprochen werden soll - jeder Teilnehmer kann
                sich darauf vorbereiten. Hinterher hältst du fest, was beschlossen wurde.</p>
                <p>Wird zu einem Punkt etwas beschlossen, legt die Führung dort mit <em>Maßnahme</em> eine
                <strong>Aufgabe</strong> oder eine <strong>Wiedervorlage</strong> an, mit Zuständigem und Frist.
                Der Punkt zeigt danach dauerhaft ihren Stand: offen, überfällig oder erledigt. Lass den Punkt offen,
                wenn er in der nächsten Besprechung wieder auf den Tisch soll; er wandert dann samt Maßnahmen mit.</p>
                <p>Eine Besonderheit bei der Sichtbarkeit: Tagesordnung und Protokoll sind
                <strong>zwei Stunden nach dem Ende für jeden internen Agenten lesbar</strong>, unabhängig vom
                Dienstgrad. Vorher sehen sie nur die Eingeladenen. Partnerkonten sehen sie nie.</p>
                <p>Wer fehlt, kann sich abmelden - die Abmeldung erscheint direkt in der
                Anwesenheitsliste.</p>
                """,
                NavKey: "besprechungen"),

            new Article("art-praesentationen", "praesentationen", "Präsentationen",
                "Folien anlegen und live vorführen - für alle, eine Gruppe oder Eingeladene.",
                """
                <p>Unter <em>Präsentationen</em> legt jeder interne Agent mit Schreibrecht Folien an. Jede Folie
                folgt einer <strong>Vorlage</strong>: Titelfolie, Titel und Text, Text mit Bild links oder rechts,
                nur Bild oder Abschnitt. Zu jeder Folie kannst du <strong>Sprechernotizen</strong> schreiben, die nur
                du beim Vortrag siehst.</p>
                <p>Liegt die Präsentation schon in <strong>Google Slides</strong>, holst du sie mit <em>Aus Google
                Slides importieren</em>: per Freigabe-Link (in Google „Jeder mit dem Link“), über die
                <strong>Google-Anmeldung</strong> (auch für Präsentationen, die nicht freigegeben sind; das FIB liest
                nur die eine Datei, die du im Google-Fenster wählst) oder als hochgeladene PDF-Datei (<em>Datei →
                Herunterladen → PDF-Dokument</em>). Jede Seite wird eine Folie und sieht aus
                wie in Google, auch im Vollbild am Beamer; Animationen, Übergänge und eingebettete Videos gehen dabei
                verloren. Im Editor fügst du so auch Folien in eine bestehende Präsentation ein. Unter
                <em>Google-Importe</em> holst du eine geänderte Fassung mit <em>Aktualisieren</em>: Jede Folie behält
                Platz und Notizen ihrer Seitennummer, neue Seiten kommen dahinter. Den Inhalt einer Google-Folie
                änderst du in Google, hier pflegst du nur ihre Notizen.</p>
                <p>Wer eine Präsentation sieht, legst du beim Anlegen fest: <strong>öffentlich</strong> für alle
                internen Agenten, <strong>geschlossen</strong> für eine Gruppe (HRT, Administration Division, eine Taskforce oder ab einem
                Dienstgrad) oder <strong>privat</strong> nur für die Eingeladenen. Schließen kannst du sie nur für eine
                Gruppe, zu der du selbst gehörst. Du und die Führung sehen deine Präsentationen immer.</p>
                <p>Mit <em>Vorführen</em> startest du den Vortrag. Wer die Präsentation sehen darf und gerade online
                ist, bekommt oben auf der Seite einen Hinweis und eine Benachrichtigung in der Glocke und kann mit
                einem Klick zuschauen. Du blätterst mit den Pfeiltasten, siehst die nächste Folie, deine Notizen und
                wer zuschaut. Im Modus <em>Alle folgen mir</em> sehen die Zuschauer immer deine Folie; im Modus
                <em>Frei blättern</em> dürfen sie vor- und zurückblättern und springen mit einem Knopf zurück zum
                Vortrag.</p>
                <p>Willst du jemanden dabeihaben, der die Präsentation sonst nicht sieht, lädst du ihn zur
                <strong>Vorführung</strong> ein: schon vor dem Start unter <em>Zusätzlich einladen</em> oder während
                des Vortrags rechts unter <em>Zu dieser Vorführung eingeladen</em>. Eingeladene bekommen den Hinweis
                und die Glocke und schauen live zu wie alle anderen, aber nur die Folien dieser Vorführung (im Modus
                <em>Frei blättern</em> also alle): keine Notizen, keine Ansicht der Präsentation, keinen Druck,
                keinen Editor, und in der Suche taucht sie für sie nicht auf. Die Einladung gilt nur für diese eine Vorführung und endet mit ihr; über das
                Kreuz an einem Namen nimmst du sie vorher zurück.</p>
                <p>Schließt du die Vortragsseite, endet die Vorführung nach zwei Minuten von selbst. Bearbeiten,
                vorführen und löschen dürfen nur, wer die Präsentation angelegt hat, und die Führung.</p>
                <p>Unter den Folien steht <em>Verknüpfte Akten</em>. Mit <em>Akte verknüpfen</em> hängst du die
                Präsentation an eine andere Akte, etwa einen Vorgang, eine Person oder eine Besprechung; das darf
                jeder mit Schreibrecht, der die Präsentation sieht. Umgekehrt wählst du an einem Vorgang, einer
                Person, einer Aufgabe, einem Gesetz oder einer Besprechung beim Verknüpfen den Aktentyp
                <em>Präsentation</em>. Wer die Präsentation nicht sehen darf, findet die Verknüpfung weder in der
                Liste noch im Zeitstrahl. Die Suche findet Präsentationen über Titel, Beschreibung und Folientitel,
                <em>Strg+K</em> über den Titel.</p>
                """,
                NavKey: "praesentationen"),

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
                <p>Abgemeldete Tage zählen auch im <em>Inaktivitäts-Radar</em> der Führung nicht mit. Wer
                wochenlang kein Lebenszeichen gibt und nicht abgemeldet ist, landet dort - und bekommt zuerst eine
                Nachricht mit der Bitte, sich zu melden.</p>
                """,
                NavKey: "abmeldungen"),

            new Article("art-brett", "schwarzes-brett", "Das Schwarze Brett",
                "Ankündigungen - und die, die du bestätigen musst.",
                """
                <p>Am <strong>Schwarzen Brett</strong> stehen behördliche Ankündigungen: Hinweise, Termine,
                Personalien. Jede Ankündigung richtet sich an eine bestimmte Gruppe - alle, nur die Führung, nur
                eine Einheit. Verbindliche Dienstanweisungen mit Nummer und Fassung stehen nicht hier, sondern
                unter <em>Dienstanweisungen</em>.</p>
                <p>Manche Ankündigungen sind <strong>quittierungspflichtig</strong>. Dann steht ein Knopf
                <em>Kenntnis nehmen</em> darunter, und die Zahl am Menüeintrag bleibt stehen, bis du
                geklickt hast. Wer angekündigt hat, sieht, wer schon quittiert hat.</p>
                <p>Meist gibt es dazu eine <strong>Frist</strong>, sie steht neben dem Knopf. Hast du bis dahin nicht
                quittiert, erinnert dich die Seite einmal - in der Glocke und, wenn eingerichtet, per Discord-Ping.
                Bei Ankündigungen an eine Taskforce bleibt es bei der Glocke: Ein gemeinsamer Ping im Kanal würde
                zeigen, wer in der Taskforce ist.
                Die Führung sieht oben am Brett, bei welchen Ankündigungen die Frist abgelaufen ist und wer noch
                fehlt. Wer die Zielgruppe inzwischen verlassen hat, etwa eine Taskforce, schuldet die Quittierung
                nicht mehr.</p>
                <p>Das ist kein Schikane-Werkzeug, sondern ein Nachweis: Die Führung kann belegen, dass eine
                wichtige Ankündigung jeden erreicht hat.</p>
                """,
                NavKey: "brett"),

            new Article("art-dienstanweisungen", "dienstanweisungen", "Dienstanweisungen",
                "Hausrecht mit Nummer, Fassung und Kenntnisnahme.",
                """
                <p>Eine <strong>Dienstanweisung</strong> ist Hausrecht: Sie regelt, wie das FIB intern arbeitet.
                Was draußen gilt, steht im <em>Gesetzbuch</em>. Jede Dienstanweisung hat eine feste
                <strong>Nummer</strong> (DA-01, DA-02 und so weiter), einen Titel und auf Wunsch ein Sachgebiet.
                Die Nummer bleibt ihr, auch wenn sie aufgehoben oder gelöscht wird. Vergeben wird sie nie ein
                zweites Mal.</p>
                <p>Eine Dienstanweisung kann <strong>Fassungen</strong> haben. Jede trägt ein <em>gültig ab</em>
                und eine kurze Änderungsnotiz und lässt sich nach dem Speichern nicht mehr ändern. Liegt das Datum
                in der Zukunft, gilt bis dahin die vorige Fassung, und du kannst die neue schon vorab zur
                Kenntnis nehmen. Ältere Fassungen liest du im Abschnitt <em>Fassungen</em>. Mit
                <em>Fassungen vergleichen</em> siehst du Wort für Wort, was sich geändert hat.</p>
                <p>Wer eine Dienstanweisung sehen darf, muss sie zur Kenntnis nehmen. Du liest sie und klickst
                <em>Zur Kenntnis genommen</em>. Was du noch offen hast, zeigt die Zahl am Menüeintrag
                <em>Dienstanweisungen</em>, und in <em>Meine Schicht</em> steht es unter <em>Zu quittieren</em>.
                Kommt eine Dienstanweisung neu dazu, klingelt außerdem die Glocke; einen Discord-Ping gibt es
                dafür nicht. Wer später ins FIB kommt oder die nötige Stufe später bekommt, schuldet die
                Kenntnisnahme sofort. Wer die Stufe verliert, schuldet sie nicht mehr.</p>
                <p>Eine neue Fassung verlangt meist, dass du sie erneut zur Kenntnis nimmst. Eine
                <strong>Schreibkorrektur</strong> verlangt es nicht: Dann gilt deine frühere Kenntnisnahme
                weiter, und es klingelt nichts. Ob eine Fassung Kenntnisnahme verlangt, steht im Abschnitt
                <em>Fassungen</em> als <em>Kenntnisnahme nötig</em>.</p>
                <p>Jede Dienstanweisung trägt eine <strong>VS-Stufe</strong>, genau wie ein Dokument in der
                Bibliothek. Sie entscheidet darüber, wer die Dienstanweisung sieht und wer sie zur
                Kenntnis nehmen muss. In Liste, Suche und Verknüpfungen taucht sie für alle ohne die Stufe
                nicht auf. Erwähnst du eine eingestufte Dienstanweisung mit @, sehen alle unterhalb der
                Führung dort und auf dem Zeitstrahl nur <em>Verschlusssache</em>. Wer nur lesen darf, etwa die Nur-Lese-Aufsicht, schuldet nichts und
                kann nichts quittieren.</p>
                <p>Die Führung legt Dienstanweisungen über <em>Neue Dienstanweisung</em> an, ändert sie über
                <em>Neue Fassung</em> und nimmt sie mit <em>Aufheben</em> zurück. Dafür braucht sie eine
                Begründung. Eine aufgehobene Dienstanweisung schuldet niemand mehr, sie bleibt aber unter
                <em>Aufgehoben</em> lesbar, und ihre Nummer bleibt vergeben. Im Abschnitt <em>Kenntnisnahme</em>
                sehen die Führung und die Nur-Lese-Aufsicht, wer sie schon zur Kenntnis genommen hat und wer
                noch fehlt.</p>
                <p>Unter <em>Verknüpfungen</em> hängst du eine Dienstanweisung mit <em>Akte verknüpfen</em> an
                eine andere Akte, etwa einen Vorgang oder eine Anordnung. Umgekehrt wählst du an einer Akte oder
                Anordnung beim Verknüpfen den Aktentyp <em>Dienstanweisung</em>. Im Text eines Kommentars oder
                Dokuments zitierst du sie mit <strong>@</strong>.</p>
                """,
                NavKey: "dienstanweisungen",
                Steps:
                [
                    new Step("Description", "Lesen",
                        "Die Dienstanweisung öffnen. Ist eine neuere Fassung angekündigt, weist ein Hinweis darauf hin."),
                    new Step("DoneOutline", "Zur Kenntnis nehmen",
                        "Der Knopf steht oben und verschwindet, sobald du die Fassung quittiert hast."),
                    new Step("Compare", "Fassungen vergleichen",
                        "Im Abschnitt Fassungen zwei Fassungen wählen und die Änderungen ansehen."),
                ]),

            new Article("art-aktivitaeten", "dienst-aktivitaeten", "Dienst-Aktivitäten (Archiv)",
                "Die früheren Einsatzvermerke - neue Einsätze sind Operationen.",
                """
                <p>Bis November 2026 hielten wir Einsätze als <strong>Dienst-Aktivität</strong> fest: Titel,
                Art, Datum und ein Aktivitätsvermerk nach Vorlage. Neue Aktivitäten gibt es nicht mehr. Ein
                Einsatz ist jetzt eine <em>Operation</em> mit Einsatztagebuch, Beteiligten und Einsatzbericht
                (siehe <em>Operationen</em>).</p>
                <p>Die alten Einträge bleiben unter <em>Aktivitäten (Archiv)</em> für alle lesbar, an der
                Fraktion sichtbar und zählen weiter für ihren Bedrohungs-Score. Bearbeiten darf sie wie bisher
                nur, wer sie geschrieben hat, oder die Führung.</p>
                <p>Für den schnellen Eintrag zwischendurch nimmst du den Plus-Knopf in der Kopfzeile, Reiter
                <em>Einsatz</em>.</p>
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
                Erwähnungen, Meldungen zu deinem Konto und die Bitte der Führung um eine Rückmeldung kommen immer
                an.</p>
                <p>Ein Teil davon geht zusätzlich nach <strong>Discord</strong>. Was dort ankommt, ist bewusst
                inhaltsarm - eine Zeile, die sagt, dass es etwas gibt, nicht was es ist.</p>
                <p>Erwähnungen, Aufgaben, Einladungen zu einem Termin, fällige Wiedervorlagen und überfällige
                Quittierungen pingen dich dort persönlich - bei einem Termin also nur die Eingeladenen, nicht die
                ganze Behörde. Wann, legst du im selben Abschnitt des Profils fest: <em>Sofort</em>, <em>Gesammelt</em>
                oder <em>Nie</em>.
                Gesammeltes kommt einmal am Tag um 18 Uhr als <em>Tagesüberblick</em> - eine Zeile mit deinem
                Ping und der Zahl der Meldungen, die du bis dahin noch nicht gelesen hast. Was du vorher in der
                Glocke liest, pingt dich also gar nicht mehr. Eine Art, die du für die Glocke ausgeschaltet hast,
                kommt schon gelesen an und fehlt deshalb auch im Tagesüberblick. Ankündigungen, Besprechungen und
                andere Rundrufe pingen eine ganze Rolle; die schaltest du in Discord selbst stumm. Bittet dich die
                Führung um eine Rückmeldung, weil sie lange nichts von dir gesehen hat, pingt das immer sofort -
                abgemeldet bekommst du diese Bitte gar nicht erst.</p>
                <p>Schaltet ein Admin <em>Persönliche Pings privat zustellen</em> ein, kommen diese persönlichen Pings
                nicht mehr als Erwähnung in einem Kanal, den alle sehen, sondern als <strong>Direktnachricht vom
                Bot</strong> - dem Konto, mit dem die Seite in Discord schreibt. Es ist derselbe kurze Satz mit Link,
                nie der Titel einer Akte. Deine Wahl <em>Sofort</em>, <em>Gesammelt</em> oder <em>Nie</em> gilt davor
                unverändert, und der Tagesüberblick kommt dann ebenfalls als Direktnachricht. Rundrufe an eine Rolle
                bleiben im Kanal.</p>
                <p>Dafür muss Discord dem Bot erlauben, dir zu schreiben. Hast du in den Privatsphäre-Einstellungen des
                Servers Direktnachrichten gesperrt, fällt der Ping nur für dich auf den Kanal zurück, sofern dafür
                einer eingerichtet ist, und dein Profil sagt dir, dass Discord keine Direktnachrichten zulässt.
                Erlaubst du sie wieder, kommt der nächste Ping wieder privat.</p>
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
