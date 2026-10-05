using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter three: the work itself - cases, operations, surveillance, analysis.</summary>
internal static class InvestigationChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-ermitteln", "ermitteln", "Ermitteln",
        "Vorgänge, Operationen, Taskforces, Observationen, Doks und die Auswertung.", "Radar",
        [
            new Article("art-ebenen", "die-vier-ebenen", "Vorgang, Operation, Taskforce - was wofür",
                "Vier Ebenen, die oft verwechselt werden. Ein Bild schafft Klarheit.",
                """
                <p>Vier Aktenarten klingen ähnlich und meinen Verschiedenes. Der Unterschied ist die
                <strong>Größe des Ausschnitts</strong>:</p>
                <ul>
                <li><strong>Personen- und Fraktionsakte</strong> - was über ein Subjekt bekannt ist. Sie lebt
                dauerhaft.</li>
                <li><strong>Vorgang</strong> - eine Ermittlung, die mehrere Akten zusammenfasst. Sie hat einen
                Anfang und ein Ende.</li>
                <li><strong>Operation</strong> - ein konkreter Einsatz mit Ort, Zeit und Ergebnis. Sie dauert
                Stunden, nicht Wochen.</li>
                <li><strong>Taskforce</strong> - eine Mannschaft, nicht ein Sachverhalt. Sie ist die Antwort
                auf „wer arbeitet daran“.</li>
                </ul>
                <p>Faustregel: Ein <em>Sachverhalt</em> ist ein Vorgang, ein <em>Termin</em> ist eine
                Operation, eine <em>Gruppe Menschen</em> ist eine Taskforce.</p>
                """,
                DiagramKey: "akten-ebenen"),

            new Article("art-vorgaenge", "vorgaenge", "Vorgänge",
                "Die Ermittlungsakte, die mehrere andere Akten bündelt.",
                """
                <p>Ein <strong>Vorgang</strong> ist die übergeordnete Ermittlungsakte. Er hat einen Status -
                <em>Angelegt</em>, <em>Aktiv</em>, <em>Ruhend</em>, <em>Abgeschlossen</em>,
                <em>Archiviert</em> -, beteiligte Agenten und im Abschnitt <em>Inhalt</em> alles, was dazu
                gehört.</p>
                <p>Alles, was du an einen Vorgang verknüpfst - Personen, Fraktionen, Dokumente, Operationen -
                erscheint gebündelt im Abschnitt <em>Inhalt</em>, nach Aktenart gruppiert. Das ist der Punkt
                des Vorgangs: eine Stelle, an der der ganze Fall steht.</p>
                <p>Setze den Status <em>Ruhend</em> statt abzuschließen, wenn ihr auf etwas wartet. Ein
                abgeschlossener Vorgang, der wieder aufgemacht wird, verliert seinen Abschlusszeitpunkt.</p>
                """,
                NavKey: "vorgaenge"),

            new Article("art-operationen", "operationen", "Operationen",
                "Der Einsatzbericht: Ort, Zeit, Beteiligte, Ergebnis.",
                """
                <p>Eine <strong>Operation</strong> ist ein einzelner Einsatz: Zugriff, Durchsuchung,
                Beobachtungsfahrt. Sie hat Ort und Zeit, die beteiligten Agenten und einen Status von
                <em>Geplant</em> über <em>Laufend</em> zu <em>Abgeschlossen</em> oder
                <em>Abgebrochen</em>.</p>
                <p>Leg die Operation <strong>vorher</strong> an, wenn es geht. Dann steht der Auftrag fest,
                bevor jemand losfährt, und hinterher fehlt nur noch das Ergebnis.</p>
                <p>Verknüpfe sie mit dem Vorgang, zu dem sie gehört. Eine Operation, die an keinem Vorgang
                hängt, ist hinterher nicht mehr auffindbar.</p>
                """,
                NavKey: "operationen",
                Steps:
                [
                    new Step("AddCircle", "Operation anlegen",
                        "Operationen → Neu. Titel, Ort und geplante Zeit reichen für den Anfang."),
                    new Step("Group", "Agenten zuteilen",
                        "Nur zugeteilte Agenten erscheinen später im Bericht als beteiligt."),
                    new Step("PlayArrow", "Auf laufend setzen",
                        "Damit sieht die Dienststelle, dass gerade etwas passiert."),
                    new Step("Description", "Ergebnis nachtragen",
                        "Was ist passiert, was wurde sichergestellt, wer wurde angetroffen. Ohne Nachtrag war die Operation umsonst."),
                ]),

            new Article("art-taskforces", "taskforces", "Taskforces",
                "Eine Einheit mit Auftrag, Genehmigung und eigenem Chat.",
                """
                <p>Eine <strong>Taskforce</strong> ist eine Mannschaft für eine bestimmte Aufgabe. Sie wird
                beantragt und muss von der Führung <strong>genehmigt</strong> werden - der Status geht von
                <em>Beantragt</em> zu <em>Aktiv</em>, <em>Abgelehnt</em> oder <em>Aufgelöst</em>.</p>
                <p>Sie hat einen eigenen <strong>Chat</strong>, in dem nur die zugeteilten Agenten schreiben.
                Und sie ist die einzige Akte, die andere Agenten <em>ausschließt</em>: Wer nicht zugeteilt
                ist, sieht die Taskforce gar nicht - weder in der Liste noch in der Suche.</p>
                <p>Der <strong>Umfang</strong> unterscheidet innerbehördlich von überbehördlich. Eine
                überbehördliche Taskforce ist die Form, in der mit LSPD, DoJ oder LSMD zusammengearbeitet
                wird; Partnerkonten können dort mitlesen und schreiben, aber die Agentenliste bleibt
                intern.</p>
                """,
                NavKey: "taskforces"),

            new Article("art-observationen", "observationen", "Observationen",
                "Beobachtung festhalten: wer, wann, wo, was gesehen.",
                """
                <p>Eine <strong>Observation</strong> hält eine Beobachtung fest - Zeitpunkt, Ort, was
                beobachtet wurde und wer beobachtet hat. Sie hängt an der Personenakte im Abschnitt
                <em>Überwachung</em>.</p>
                <p>Schreib auf, was du <strong>gesehen</strong> hast, nicht was du daraus schließt. „Traf sich
                um 21:40 vor dem Lager mit zwei Unbekannten“ ist eine Observation. „Übergabe von Ware“ ist
                eine Schlussfolgerung und gehört in den Kommentar.</p>
                <p>Observationen zählen in den Bedrohungs-Score ein - allerdings mit abnehmendem Gewicht: eine
                Beobachtung von gestern wiegt schwerer als eine vom Frühjahr.</p>
                """),

            new Article("art-doks", "doks", "Personen-Doks",
                "Das Protokoll einer Vernehmung oder Maßnahme.",
                """
                <p>Ein <strong>Dok</strong> ist das Protokoll einer Maßnahme an einer Person: Vernehmung,
                Zugriff, Befragung. Es steht im Abschnitt <em>Doks</em> ihrer Akte und hält fest, was
                geschehen ist und wie es ausging.</p>
                <p>Der <strong>Ausgang</strong> kennt vier Werte: <em>Läuft noch</em>, <em>Offiziell
                entlassen</em>, <em>Amnestie-Spritze</em> und <em>Erschossen</em>. Er ist keine Formalie - er
                entscheidet mit darüber, wie schwer die Person im Bedrohungs-Score wiegt.</p>
                <p>Ein Dok kann eingestuft werden wie jedes andere Dokument. Denk daran, dass ein eingestuftes
                Dok für alle anderen <em>nicht existiert</em> - auch nicht als Lücke.</p>
                """),

            new Article("art-informanten", "informanten", "Informanten",
                "V-Personen führen - und warum es hier keine Decknamen gibt.",
                """
                <p>Ein <strong>Informant</strong> (V-Person) ist eine Quelle aus dem Milieu. Die Akte hält
                fest, wer die Person ist, welcher Agent sie führt, und protokolliert jedes Treffen mit Datum
                und Inhalt.</p>
                <p>Anders als man erwarten würde gibt es <strong>keinen Decknamen und keine zweite
                Geheimhaltungsstufe</strong>. Ein Informant ist an seine Personenakte gekoppelt, und jeder
                interne Agent darf mit jeder V-Personen-Akte arbeiten. Partnerkonten sehen davon
                nichts.</p>
                <p>Führe die Treffen zeitnah nach. Der Wert eines Informanten liegt im Verlauf - eine einzelne
                Meldung ohne Vorgeschichte lässt sich nicht einordnen.</p>
                """,
                NavKey: "informanten"),

            new Article("art-entfuehrungen", "entfuehrungen", "Entführungen",
                "Wenn ein eigener Agent in fremde Hände gerät.",
                """
                <p>Der Bereich <strong>Entführungen</strong> behandelt einen einzigen Fall: ein NOOSE-Agent
                wurde entführt. Festgehalten wird, wer die Täter sind, wie lange es dauerte und - das ist der
                eigentliche Zweck - <strong>welche Informationen abgeflossen sind</strong>.</p>
                <p>Zu jeder Entführung lassen sich die Akten benennen, die als kompromittiert gelten müssen.
                Sie werden markiert, damit jeder, der sie später öffnet, weiß, dass die Gegenseite ihren
                Inhalt womöglich kennt.</p>
                <p>Melde eine Entführung auch dann, wenn scheinbar nichts passiert ist. Die Einschätzung, was
                abgeflossen ist, trifft nicht der Betroffene.</p>
                """,
                NavKey: "entfuehrungen"),

            new Article("art-asservate", "asservatenkammer", "Asservatenkammer",
                "Ein- und Auslagern von Gegenständen mit Bestand und Besitzer.",
                """
                <p>Die <strong>Asservatenkammer</strong> führt Buch über sichergestellte Gegenstände. Jede
                Bewegung ist eine Buchung: <em>Einlagerung</em> oder <em>Herausnahme</em>, mit Menge, Datum
                und dem Agenten, der sie vorgenommen hat.</p>
                <p><strong>Einlagern darf jeder Agent, eine Herausnahme bucht erst ein Senior Special
                Agent.</strong> Was hereinkommt, trägst du also selbst ein; was hinausgeht, buchst du ab diesem
                Dienstgrad selbst, darunter lässt du es buchen. Eine Buchung nachträglich ändern oder löschen
                bleibt Sache der Führung.</p>
                <p>Der Bestand ergibt sich aus den Buchungen, er lässt sich nicht direkt setzen. Soll ein
                Posten auf null, geschieht das über eine Herausnahme - und wenn der Bestand rechnerisch negativ
                würde, gleicht eine Korrektur-Einlagerung vorher aus.</p>
                <p>Jeder Gegenstand kann einem <strong>Besitzer</strong> zugeordnet werden - einer Person oder
                einer Fraktion. Dort taucht er dann im Abschnitt <em>Asservate</em> auf.</p>
                """,
                NavKey: "asservatenkammer"),

            new Article("art-graph", "beziehungsgraph", "Beziehungsgraph",
                "Verbindungen als Netz statt als Liste.",
                """
                <p>Der <strong>Beziehungsgraph</strong> zeichnet Akten als Punkte und ihre Verbindungen als
                Linien. Du kannst ihn von jeder Akte aus öffnen und Schritt für Schritt erweitern.</p>
                <p>Er zeigt drei Sorten Verbindung zusammen: <strong>Mitgliedschaften</strong> (Person in
                Fraktion, Partei, Personengruppe), <strong>Beziehungen zwischen Personen</strong> und die
                <strong>Verknüpfungen</strong>, die jemand von Hand gesetzt hat.</p>
                <p>Der Graph ist ein Suchwerkzeug, keine Dokumentation. Was du dort entdeckst, gehört als
                Verknüpfung mit Begründung in die Akte - sonst ist die Erkenntnis mit dem Schließen des
                Fensters weg.</p>
                """,
                NavKey: "graph"),

            new Article("art-score", "bedrohungs-score", "Der Bedrohungs-Score",
                "Die Zahl zwischen 0 und 100 - was sie misst und was nicht.",
                """
                <p>Jede Person und jede Fraktion hat einen <strong>Bedrohungs-Score</strong> (auch
                EHK-Score): eine Zahl von 0 bis 100, die das System selbst berechnet. Daneben steht eine
                <strong>Konfidenz</strong> - wie gut die Datenlage ist, auf der die Zahl beruht.</p>
                <p>Die Zahl entsteht aus dem, was in der Akte steht: Aktivitäten, Doks und Observationen
                (frische wiegen schwerer als alte), Größe und Struktur einer Fraktion, Konflikte und
                Bündnisse, die Stellung im Beziehungsnetz. Die <strong>Einstufung</strong> setzt zusätzlich
                einen Sockel: ein Verdachtsfall startet nie unter 50, gesichert staatsgefährdend nie unter
                75.</p>
                <p>Aus dem Score wird die <strong>Gefährdungsstufe</strong>: bis 24 niedrig, bis 49 mittel,
                bis 74 hoch, ab 75 kritisch.</p>
                <p>Neben der Stufe steht in den Listen, im Lagezentrum und bei deinen beobachteten Akten eine
                kleine <strong>Verlaufskurve</strong> der letzten Bewertungen. Sie beantwortet, was die nackte
                Zahl offenlässt: bewegt sich der Wert - und wohin? Fahre mit der Maus darüber, dann nennt sie
                dir Anfang, Ende und die Differenz. Wurde ein Score erst einmal berechnet, gibt es noch keinen
                Verlauf; dann steht dort ein Strich.</p>
                <p>Zwei Dinge solltest du wissen. Erstens: Eine niedrige Konfidenz heißt nicht „ungefährlich“,
                sondern „wir wissen zu wenig“. Zweitens: Der Score misst, was <em>dokumentiert</em> ist. Eine
                gefährliche Fraktion mit leerer Akte hat eine niedrige Zahl - das ist ein Befund über uns,
                nicht über sie.</p>
                """,
                DiagramKey: "ehk-score"),

            new Article("art-ermittlungshinweise", "ermittlungshinweise", "Ermittlungshinweise",
                "Was dem System an deinem Bestand auffällt.",
                """
                <p><strong>Ermittlungshinweise</strong> sind Beobachtungen, die das System selbst am Bestand
                macht: zwei Personen tauchen auffällig oft gemeinsam auf, eine Einstufung passt nicht mehr zur
                Aktenlage, eine Fraktionsakte ist seit Monaten unberührt.</p>
                <p>Das sind <strong>Vorschläge, keine Feststellungen</strong>. Jeder Hinweis nennt, worauf er
                sich stützt, und verlinkt die betroffenen Akten. Was du daraus machst, entscheidest du.</p>
                <p>Geh die Liste gelegentlich durch. Sie findet vor allem das, was niemand sucht, weil es
                zwischen zwei Zuständigkeiten liegt.</p>
                """,
                NavKey: "hinweise"),

            new Article("art-dokumente", "dokumenten-bibliothek", "Dokumenten-Bibliothek",
                "Die zentrale Ablage - und die Vorlagen dahinter.",
                """
                <p>Die <strong>Dokumenten-Bibliothek</strong> ist die Ablage für alles Schriftliche, das nicht
                an einer einzelnen Akte hängt: Berichte, Vermerke, Anordnungen, Konzepte.</p>
                <p>Ein Dokument wird im Editor geschrieben - mit Formatierung, Tabellen und eingefügten
                Bildern. Beim Anlegen kannst du eine <strong>Vorlage</strong> wählen; die Platzhalter darin
                werden beim Einsetzen automatisch gefüllt (Name, Aktenzeichen, Datum, Uhrzeit, dein Codename,
                dein Dienstgrad).</p>
                <p>Jedes Dokument trägt eine <strong>VS-Stufe</strong>. Sie entscheidet allein darüber, wer es
                sieht - Verknüpfungen und Suche richten sich danach.</p>
                <p>Hier liegt auch die <strong>Dienstverordnung</strong> selbst. Das Kapitel
                <em>Dienstvorschrift</em> in diesem Handbuch ist nur ihre Kurzfassung - im Zweifel gilt das
                Dokument.</p>
                """,
                NavKey: "dokumente"),

            new Article("art-funkplan", "funkplan", "Funkplan",
                "Wer auf welchem Kanal funkt - und zu wem eine aufgeschnappte Frequenz gehört.",
                """
                <p>Der <strong>Funkplan</strong> beantwortet zwei Fragen. Von oben nach unten gelesen: auf
                welchem Kanal erreiche ich wen. Rückwärts gelesen: wem gehört die Frequenz, die ich eben
                aufgeschnappt habe.</p>
                <p>Er zeigt vier Blöcke - <em>Eigene Kanäle</em>, <em>Partnerbehörden</em>, <em>Sonstige</em>
                und die <em>Fraktionen</em>. Die ersten drei pflegst du hier über <em>Neuer Kanal</em>. Die
                Fraktionsfrequenzen stehen <strong>nicht</strong> hier, sondern werden direkt aus den
                Fraktionsakten gelesen: geändert wird eine Fraktionsfrequenz im Feld <em>Funk</em> der
                jeweiligen Akte, und der Funkplan zieht das sofort nach. So gibt es je Frequenz nur eine
                Stelle, an der sie steht.</p>
                <p>Das Suchfeld oben durchsucht alle Blöcke gleichzeitig. <strong>Komma und Punkt sind
                gleichwertig</strong> - wer <em>411,7</em> tippt, findet <em>411.7</em>. Dasselbe gilt für die
                große Suche und für Strg+K: eine eingetippte Frequenz führt dich zum Kanal oder zur
                Fraktionsakte, ohne dass du den Deep-Scan einschalten musst.</p>
                <p>Zwei Kanäle siehst du unter Umständen nicht. Ein Kanal, der an eine <strong>Taskforce</strong>
                gebunden ist, erscheint nur bei den ihr zugeteilten Agenten - so, wie die Taskforce selbst.
                Und ein Kanal mit dem Schloss-Zeichen ist <strong>Verschlusssache</strong>: den sieht und
                ändert nur die Führung.</p>
                """,
                RoleplayHtml:
                """
                <p>Ein Kanal im Plan heißt nicht, dass er frei ist. Wer auf einer fremden Frequenz mithört,
                tut das im Spiel auf eigenes Risiko - und wer dort sendet, gibt sich zu erkennen.</p>
                <p>Eine Frequenz, die du im Einsatz aufschnappst, gehört in die Akte, bevor du sie hier
                einträgst: der Funkplan ist ein Verzeichnis, kein Ermittlungsergebnis.</p>
                """,
                NavKey: "funk",
                Steps:
                [
                    new Step("Search", "Frequenz eintippen",
                        "Tippe die Zahl ins Suchfeld - mit Komma oder Punkt, beides findet dasselbe."),
                    new Step("Add", "Kanal anlegen",
                        "Über Neuer Kanal: Frequenz, Bezeichnung und Bereich. Alles Weitere ist optional."),
                    new Step("Groups2", "Taskforce binden",
                        "Wählst du eine Taskforce, sehen den Kanal nur deren Agenten."),
                    new Step("Lock", "Verschlusssache setzen",
                        "Nur die Führung kann den Haken setzen - und nur sie sieht den Kanal danach."),
                ]),
        ]);
}
