using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter seven: the tools on top of the records.</summary>
internal static class ToolsChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-werkzeuge", "werkzeuge", "Werkzeuge",
        "NOOSEI, Statistik, Nachweis, Gegenaufklärung, Gesetzbuch, Druck und Einstellungen.", "Handyman",
        [
            new Article("art-noosei", "noosei", "NOOSEI",
                "Die Behörden-KI: was sie kann, was sie nicht darf.",
                """
                <p><strong>NOOSEI</strong> ist die KI der Behörde. Du kannst sie in normaler Sprache nach der
                Aktenlage fragen - „Was wissen wir über die Vagos?“, „Welche Fahndungen laufen seit mehr als
                vier Wochen?“ - und sie schlägt selbst in den Akten nach.</p>
                <p>Zwei Regeln solltest du kennen:</p>
                <ul>
                <li><strong>NOOSEI sieht genau so viel wie du.</strong> Eine Verschlusssache, die du nicht
                lesen darfst, kommt in ihrer Antwort nicht vor - nicht einmal als Andeutung. Für sie sieht die
                Akte aus, als gäbe es sie nicht.</li>
                <li><strong>NOOSEI schreibt nichts.</strong> Sie liest, fasst zusammen und formuliert Entwürfe.
                Eine Akte ändert immer ein Mensch.</li>
                </ul>
                <p>Unter jeder Antwort stehen die <strong>Quellen</strong>: die Akten, in die sie geschaut hat.
                Klick sie an und prüf nach. Eine Antwort ohne Quelle ist eine Formulierung, kein Befund.</p>
                <p>An Akten findest du außerdem den <strong>NOOSEI-Kurzbrief</strong>: eine kurze
                Zusammenfassung der Akte auf Knopfdruck - praktisch, wenn du eine fremde Akte zum ersten Mal
                öffnest.</p>
                <p>Partnerbehörden, für die NOOSEI freigeschaltet ist, bekommen eine eigene
                <strong>Rechtsauskunft</strong>. Sie beantwortet nur Fragen zu den Gesetzbüchern, zitiert die
                Paragrafen und kann keine Akte lesen - auch keine freigegebene.</p>
                """,
                DiagramKey: "noosei-grenzen",
                NavKey: "ki",
                Steps:
                [
                    new Step("Chat", "Frage stellen",
                        "Ganze Sätze. Je konkreter die Frage, desto brauchbarer die Antwort."),
                    new Step("ManageSearch", "Werkzeugspur ansehen",
                        "Unter der Antwort steht, worin NOOSEI nachgeschlagen hat."),
                    new Step("FactCheck", "Quellen prüfen",
                        "Die Quellen-Chips führen direkt in die Akte. Nimm nichts ungeprüft in einen Bericht."),
                    new Step("DataUsage", "Aufs Kontingent achten",
                        "Jede Anfrage kostet Kontingent. Es füllt sich wöchentlich wieder auf."),
                ]),

            new Article("art-kontingent", "kontingent", "Das Kontingent",
                "Warum NOOSEI nicht unbegrenzt antwortet.",
                """
                <p>Jeder Agent hat ein wöchentliches <strong>Kontingent</strong> für NOOSEI. Jede Anfrage
                verbraucht davon - lange Fragen und Antworten mehr als kurze. Ist es aufgebraucht, wartest du
                bis zur nächsten Woche oder bittest die Führung um mehr.</p>
                <p>Das Kontingent wird in Punkten gerechnet, nicht in Geld. Was die Behörde tatsächlich
                bezahlt, sieht nur, wer die KI betreibt.</p>
                <p>Der praktische Rat: Stell eine gute Frage statt fünf ungefährer. Und wenn du nur wissen
                willst, ob es eine Akte gibt - dafür ist die Suche schneller und kostet nichts.</p>
                """),

            new Article("art-statistik", "statistik", "Statistik",
                "Zahlen über den eigenen Bestand.",
                """
                <p>Die <strong>Statistik</strong> wertet den Bestand aus: wie viele Akten welcher Art, wie sich
                Einstufungen verteilen, wie viel in welchem Zeitraum angelegt wurde, welche Fraktionen am
                stärksten belastet sind.</p>
                <p>Alle Auswertungen sind <strong>rechtegefiltert</strong> - du siehst nur Zahlen über Akten,
                die du auch öffnen dürftest. Zwei Agenten können dieselbe Auswertung mit unterschiedlichen
                Zahlen sehen; das ist kein Fehler.</p>
                <p>Jede Auswertung lässt sich als Tabelle herunterladen oder drucken.</p>
                """,
                NavKey: "statistik"),

            new Article("art-lageberichte", "lageberichte", "Lageberichte",
                "Der monatliche Bericht, den die Seite selbst schreibt.",
                """
                <p>Einmal im Monat erzeugt die Seite selbst einen <strong>Lagebericht</strong>: was sich an
                Einstufungen verändert hat, welche Fraktionen auffällig wurden, wie viele Fahndungen liefen und
                ausgingen.</p>
                <p>Er ist ein Ausgangspunkt, kein fertiger Bericht. Die Führung liest ihn, ergänzt die
                Einschätzung und kann daraus eine Veröffentlichung machen.</p>
                <p>Lageberichte findest du bei der Statistik.</p>
                """),

            new Article("art-nachweis", "nachweis", "Nachweis: Chronik und Protokolle",
                "Wer hat wann was getan - und wer hat nachgesehen.",
                """
                <p>Unter <em>Nachweis</em> liegen drei verschiedene Blicke auf dasselbe:</p>
                <ul>
                <li>Die <strong>Chronik</strong> ist der Tagesverlauf der Behörde: was ist heute passiert,
                über alle Akten hinweg.</li>
                <li>Das <strong>Änderungsprotokoll</strong> zeigt jede Änderung an jeder Akte - mit altem und
                neuem Wert, Zeitpunkt und Agent.</li>
                <li>Das <strong>Zugriffsprotokoll</strong> zeigt, wer welche Akte geöffnet hat.</li>
                </ul>
                <p>Das ist keine Überwachung der Kollegen, sondern die Grundlage dafür, dass man einer Akte
                überhaupt glauben kann. Jede Akte hat außerdem ihren eigenen <strong>Zeitstrahl</strong>, der
                dasselbe für genau diese Akte zeigt.</p>
                <p>Der Nachweis ist für <strong>jeden internen Agenten</strong> lesbar. Einzelne besonders
                heikle Inhalte - Ticket-Texte, Hinweise - erscheinen deshalb dort bewusst nicht im Wortlaut:
                wer etwas geändert hat, steht drin, was drinstand nicht.</p>
                """,
                NavKey: "chronik"),

            new Article("art-gegenaufklaerung", "gegenaufklaerung", "Gegenaufklärung",
                "Eigene Regeln dafür, was auffällig ist.",
                """
                <p>Die <strong>Gegenaufklärung</strong> beobachtet das Verhalten innerhalb der Seite: wer
                ungewöhnlich viel liest, wer nachts in Akten schaut, die ihn nichts angehen, wer kurz vor dem
                Ausscheiden auffällig aktiv wird.</p>
                <p>Was „auffällig“ heißt, ist nicht fest eingebaut, sondern ein <strong>Regel-Baukasten</strong>:
                die Führung baut eigene Regeln aus Bedingungen - Aktionsart, Menge, Zeitraum, betroffener
                Bereich. Innerhalb einer Kategorie gilt „oder“, zwischen Kategorien „und“.</p>
                <p>Eine ausgelöste Regel ist ein <strong>Anlass zum Hinsehen</strong>, kein Vorwurf. Es gibt
                harmlose Gründe für fast jedes Muster.</p>
                """),

            new Article("art-gesetze", "gesetzbuch", "Die Gesetzbücher",
                "Paragrafen nachschlagen - und wie sie nach außen kommen.",
                """
                <p>Unter <em>Gesetze</em> stehen die <strong>Gesetzbücher</strong>, nach denen die Behörde
                arbeitet - zuerst als Karten, eine je Buch. Ein Klick auf ein Buch zeigt alle Paragrafen,
                geordnet nach den Abschnitten des Buchs. Einen Paragrafen klappst du mit einem Klick auf und
                liest ihn direkt dort; <em>Akte öffnen</em> bringt dich zu seiner eigenen Seite mit Verknüpfungen
                und Druckansicht.</p>
                <p>Das Suchfeld oben durchsucht alle Bücher auf einmal, das Suchfeld im Buch nur dieses eine.
                Ein Treffer öffnet das Buch und klappt den Paragrafen gleich auf.</p>
                <p>Die Führung legt Bücher und Paragrafen an. Der Gesetzestext ist ein formatierter Text:
                Absätze, Aufzählungen und Hervorhebungen bleiben so stehen, wie du sie schreibst. Ein
                gelöschter Paragraf landet im <em>Papierkorb</em> und lässt sich von dort zurückholen.</p>
                <p>Einzelne Auszüge lassen sich <strong>für die Öffentlichkeit freigeben</strong>. Bürger sehen
                dann genau diesen Auszug - nicht das ganze Buch.</p>
                <p>Begriffe aus dem Gesetzbuch stehen bewusst <em>nicht</em> im Glossar dieses Handbuchs. Eine
                zweite Fassung derselben Texte würde auseinanderlaufen; wenn du einen Rechtsbegriff suchst,
                such ihn dort.</p>
                """,
                NavKey: "gesetze"),

            new Article("art-druck", "druckansichten", "Druckansichten",
                "Eine Akte auf Papier - für Gericht, Partner oder Besprechung.",
                """
                <p>Fast jede Akte hat eine <strong>Druckansicht</strong>. Sie zeigt dieselben Inhalte ohne
                Menü, ohne Knöpfe, auf eine Seite gebracht - und wird über die Druckfunktion des Browsers
                ausgegeben, auch als PDF.</p>
                <p>Die Druckansicht ist <strong>ebenfalls rechtegefiltert</strong>: was du auf dem Bildschirm
                nicht siehst, steht auch nicht auf dem Ausdruck. Ein Ausdruck ist damit nie mehr als das, was
                du ohnehin lesen durftest - aber er verlässt das Haus. Überleg dir, wer ihn danach in der Hand
                hält.</p>
                """),

            new Article("art-einstellungen", "einstellungen", "Einstellungen",
                "Wo die Führung die Seite selbst einrichtet.",
                """
                <p>Unter <em>Einstellungen</em> liegt alles, was an dieser Seite eingestellt werden kann -
                nach Abschnitten gegliedert: System und Aussehen, Stichworte, Zusatzfelder, Vorlagen,
                Aktualitäts-Schwellen, Bedrohungs-Score, öffentliche Module, Partner, Einladungen, Protokolle,
                NOOSEI.</p>
                <p>Drei davon greifen weit: der <strong>Wartungsmodus</strong> (alle außer Admins sehen nur
                einen Hinweis), der <strong>Not-Aus</strong> für den öffentlichen Bereich und die
                <strong>Schwellen des Bedrohungs-Scores</strong>, die jede Zahl im ganzen Bestand
                verschieben.</p>
                <p>Die Einstellungen sind der Führung vorbehalten.</p>
                """,
                NavKey: "einstellungen"),

            new Article("art-neuerungen", "neuerungen", "Neuerungen",
                "Was sich zuletzt geändert hat - in Alltagssprache.",
                """
                <p>Unter <em>Neuerungen</em> steht, was an dieser Seite gemacht wurde: nach Fassungen
                gegliedert, jede Zeile ein Satz, jede Art farbig gekennzeichnet - <em>Neu</em>,
                <em>Verbessert</em>, <em>Behoben</em>.</p>
                <p>Oben steht die Fassung, mit der du gerade arbeitest. Nenn sie, wenn du einen Fehler
                meldest.</p>
                <p>Ist seit deinem letzten Besuch etwas dazugekommen, öffnet sich beim ersten Aufruf danach
                einmal ein kleines Fenster mit den neuen Zeilen. <em>Alle Neuerungen ansehen</em> bringt dich
                hierher; schließt du es, ist es weg - bis zum nächsten Update.</p>
                """,
                NavKey: "neuerungen"),

            new Article("art-handbuch-pflegen", "handbuch-pflegen", "Handbuch und Glossar pflegen",
                "Für Führung und HRB: wie du hier etwas änderst.",
                """
                <p>Dieses Handbuch ist nicht in Stein gemeißelt. <strong>Führung und HRB</strong> können
                Kapitel, Artikel und Glossarbegriffe anlegen und bearbeiten - direkt auf dieser Seite.</p>
                <p>Ein Artikel hat zwei getrennte Textfelder: die <strong>Anleitung</strong> und den Kasten
                <strong>„Im Rollenspiel bedeutet das …“</strong>. Halte sie auseinander. So lassen sich eure
                RP-Regeln ändern, ohne die Bedienungsanleitung anzufassen.</p>
                <p>Zwei Dinge sind bewusst keine freien Texte:</p>
                <ul>
                <li><strong>Diagramme</strong> werden nur ausgewählt, nicht gezeichnet. Sie liegen im Programm
                und veralten damit nicht.</li>
                <li><strong>Schritt-Karten</strong> sind eigene Zeilen. Sie behalten ihre Form, egal was
                jemand am Text darüber ändert.</li>
                </ul>
                <p>Wichtig: Sobald du einen mitgelieferten Artikel speicherst, gehört er
                <strong>dir</strong>. Nachgelieferte Fassungen überschreiben ihn nie wieder. Das gilt genauso
                für gelöschte Artikel - was im Papierkorb liegt, kommt nicht von selbst zurück.</p>
                """),
            // deliberately no NavKey: "handbuch" belongs to "Hilfe finden" in the first chapter. A key
            // claimed twice means the help button resolves to whichever article sorts first, and the
            // other is never reachable through it.
        ]);
}
