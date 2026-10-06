using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter one: arriving, finding your way around, opening the first record.</summary>
internal static class GettingStartedChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-erste-schritte", "erste-schritte", "Erste Schritte",
        "Anmelden, zurechtfinden, das Menü einrichten und verstehen, wer was sehen darf.", "Start",
        [
            new Article("art-anmelden", "anmelden", "Anmelden und freigeschaltet werden",
                "Wie du hereinkommst - und warum es beim ersten Mal nicht sofort weitergeht.",
                """
                <p>Die Anmeldung läuft über <strong>Discord</strong>. Ein eigenes Passwort gibt es nicht:
                du klickst auf <em>Mit Discord anmelden</em>, bestätigst dort einmal, und bist zurück auf der
                Seite.</p>
                <p>Danach ist dein Konto zunächst <strong>ausstehend</strong>. Das ist kein Fehler. Jemand aus
                der Führung muss dich erst freigeben und dabei deinen Dienstgrad setzen. Bis dahin siehst du
                nur eine Hinweisseite.</p>
                <p>Wenn dein Dienstgrad oder deine Kennzeichen später geändert werden, wirst du einmal
                abgemeldet. Melde dich einfach neu an - das ist so gewollt, damit deine neuen Rechte auch
                wirklich greifen.</p>
                """,
                Steps:
                [
                    new Step("Login", "Mit Discord anmelden",
                        "Auf der Startseite auf „Mit Discord anmelden“ klicken und in Discord bestätigen."),
                    new Step("HourglassTop", "Freigabe abwarten",
                        "Dein Konto ist erst „ausstehend“. Melde dich bei der Führung, damit dich jemand freigibt."),
                    new Step("Badge", "Dienstgrad erhalten",
                        "Mit der Freigabe bekommst du Dienstgrad und Kennzeichen. Erst dann siehst du die Akten."),
                ]),

            new Article("art-oberflaeche", "oberflaeche", "Aufbau der Oberfläche",
                "Was wo liegt: Menüleiste, Bereiche, Kopfzeile, Glocke.",
                """
                <p>Links liegt eine schmale <strong>Symbolleiste</strong>. Jedes Symbol steht für einen
                Bereich - Akten, Ermittlung &amp; Wissen, Dienststelle, Mein Dienst, Verwaltung. Ein Klick
                darauf <em>wechselt nur die Liste daneben</em>; er springt nicht auf eine Seite.</p>
                <p>Daneben steht die eigentliche Liste der Seiten des gewählten Bereichs. Oben rechts findest
                du die Suche, die Glocke für Benachrichtigungen und dein Konto.</p>
                <p>Was du im Menü nicht siehst, darfst du meistens auch nicht öffnen: die Leiste zeigt nur,
                wofür deine Rechte reichen.</p>
                """,
                DiagramKey: "oberflaeche",
                NavKey: "dashboard"),

            new Article("art-suchen", "suchen", "Suchen und finden",
                "Die Suche über alles - und die Schnellsuche mit Strg+K.",
                """
                <p>Die Suche durchsucht den <strong>gesamten Bestand</strong>: Personen, Fraktionen, Vorgänge,
                Dokumente, Gesetze und mehr. Sie verzeiht Tippfehler und findet auch klangähnliche Namen -
                „Meier“ findet also auch „Mayer“.</p>
                <p>Für den schnellen Sprung brauchst du die Seite gar nicht: <strong>Strg+K</strong> öffnet
                überall ein Suchfeld. Tippen, mit den Pfeiltasten auswählen, Enter.</p>
                <p>Findest du nichts, liegt es oft nicht am Suchbegriff, sondern an den Rechten: Akten, die du
                nicht sehen darfst, tauchen in den Treffern gar nicht erst auf.</p>
                <p>Aus den Treffern heraus arbeitest du auch <strong>gleich weiter</strong>: <em>Auswählen</em> setzt
                Kästchen an die Akten. Hake an, was zusammengehört - auch über mehrere Suchen hinweg - und nutze die
                Leiste unten: <em>Verknüpfen</em> hängt alle an eine Akte, etwa einen Vorgang, <em>Stichwort</em> gibt
                allen dasselbe Stichwort, <em>Beobachten</em> nimmt sie auf deine Beobachtungsliste. Kommentare,
                Quellen und Protokolle lassen sich nicht auswählen, nur die Akten selbst.</p>
                """,
                NavKey: "suche",
                Steps:
                [
                    new Step("Search", "Strg+K drücken",
                        "Funktioniert auf jeder Seite, ohne die aktuelle Arbeit zu verlassen."),
                    new Step("Keyboard", "Tippen statt klicken",
                        "Schon nach wenigen Buchstaben erscheinen Treffer. Pfeiltasten wählen aus, Enter öffnet."),
                    new Step("FilterAlt", "Auf einen Typ eingrenzen",
                        "Auf der Suchseite grenzt die Leiste über den Treffern auf Personen, Fraktionen und so weiter ein."),
                    new Step("Checklist", "Mehrere Akten auf einmal",
                        "Über den Treffern „Auswählen“ drücken, anhaken und unten verknüpfen, verschlagworten oder beobachten."),
                ]),

            new Article("art-menue-anpassen", "menue-anpassen", "Das Menü anpassen",
                "Favoriten setzen, Einträge ausblenden, Startseite wählen.",
                """
                <p>Du musst nicht mit dem Menü leben, wie es ist. Über den Knopf <em>Navigation anpassen</em>
                oben im Menü (drei Schieberegler) kannst du <strong>Einträge ausblenden</strong>, die du nie brauchst, und die Reihenfolge ändern.</p>
                <p>Seiten und einzelne Akten lassen sich als <strong>Favorit</strong> anheften; sie stehen
                danach ganz oben. Und du kannst festlegen, welche Seite nach dem Anmelden zuerst erscheint -
                nicht jeder fängt sinnvollerweise im Lagezentrum an.</p>
                """,
                Steps:
                [
                    new Step("Tune", "Menü anpassen öffnen",
                        "Der Knopf „Navigation anpassen“ oben im Menü (drei Schieberegler) öffnet die Einstellungen der Navigation."),
                    new Step("VisibilityOff", "Unnötiges ausblenden",
                        "Ausgeblendete Einträge sind nicht gesperrt - du erreichst sie weiter über die Suche."),
                    new Step("PushPin", "Favoriten anheften",
                        "Das Stecknadel-Symbol an einer Akte oder Seite heftet sie oben an die Menüleiste."),
                ]),

            new Article("art-profil", "profil", "Dein Profil",
                "Codename, Foto und was andere von dir sehen.",
                """
                <p>Unter <em>Mein Profil</em> pflegst du deinen <strong>Codename</strong> und dein Foto. Der
                Codename ist der Name, unter dem dich alle anderen in der Behörde sehen - in Kommentaren, im
                Protokoll, in Auswahllisten.</p>
                <p>Dein <strong>Klarname</strong> ist etwas anderes. Ihn sieht nur die Führung. Das ist keine
                Kleinigkeit, sondern der Grund, warum die ganze Seite mit Codenamen arbeitet.</p>
                <p>Daneben steht deine <strong>Dienstnummer</strong> - eine römische Zahl von I bis XXV. Sie ist
                die interne Kennung, vor allem im Funk, und jede ist nur einmal vergeben. Ändern lässt sie die
                Führung; unterhalb davon stellst du einen Änderungswunsch.</p>
                """,
                NavKey: "profil"),

            new Article("art-rechte", "wer-darf-was", "Wer darf was",
                "Dienstgrad, Kennzeichen und warum beides zusammen zählt.",
                """
                <p>Deine Rechte ergeben sich aus <strong>zwei Dingen</strong>. Erstens dein
                <strong>Dienstgrad</strong> - von Junior Agent bis Director. Zweitens deine
                <strong>Kennzeichen</strong>: Admin, TRU, HRB.</p>
                <p>Ein Kennzeichen ist <strong>technisch</strong> nichts anderes als ein eigenes Feld am Konto;
                es entsteht nicht aus deinem Dienstgrad. Die Dienstverordnung zieht trotzdem eine Schwelle:
                <strong>TRU und HRB gibt es erst ab Special Agent</strong> (§2.4.1). Die Seite hält sich daran -
                darunter lässt sich das Kennzeichen gar nicht erst setzen.</p>
                <p>Ab <strong>Supervisory Special Agent</strong> zählst du zur <strong>Führung</strong>. Das
                ist die Schwelle, ab der Verschlusssachen, Klarnamen und die meisten Entscheidungen sichtbar
                werden. Das Kennzeichen <strong>Admin</strong> überspringt jede Rangprüfung - ein Admin gilt
                überall als Führung, unabhängig von seinem Dienstgrad.</p>
                <p>Es gibt zusätzlich Konten, die <strong>alles lesen, aber nichts schreiben</strong> dürfen -
                die Aufsicht. Und Partnerkonten von LSPD, DoJ, LSMD und Parlament, die nur freigegebene Akten
                sehen.</p>
                """,
                DiagramKey: "rechte-matrix"),

            new Article("art-hilfe-finden", "hilfe-finden", "Wo du Hilfe findest",
                "Handbuch, Glossar, Neuerungen und Feedback - was wofür da ist.",
                """
                <p>Dieses <strong>Handbuch</strong> erklärt die Bedienung. Oben steht ein Suchfeld, das
                gleichzeitig Artikel und Glossarbegriffe durchsucht - schneller als sich durch die Kapitel zu
                klicken.</p>
                <p>Das <strong>Glossar</strong> am Ende des Handbuchs erklärt jedes Fachwort in einem Satz.
                Wenn dir irgendwo ein Begriff begegnet, den du nicht kennst, schlag ihn dort nach, bevor du
                rätst.</p>
                <p>Im Lagezentrum steht für den Anfang eine Karte <strong>Erste Schritte</strong> mit sechs
                Punkten. Die haken sich von selbst ab, sobald du die jeweilige Sache einmal gemacht hast -
                niemand muss dich freigeben, und wenn alle sechs erledigt sind, verschwindet die Karte.</p>
                <p>Unter <em>Neuerungen</em> steht in Alltagssprache, was sich zuletzt geändert hat. Und wenn
                etwas nicht funktioniert oder dir fehlt: <em>Feedback</em> im Bereich <em>Mein Dienst</em>.
                Das landet direkt bei denen, die die Seite pflegen.</p>
                """,
                NavKey: "handbuch"),

            new Article("art-kurzbefehle", "tastenkuerzel", "Tastenkürzel",
                "Vier Tasten, die dir den Weg durch Menü und Maus sparen.",
                """
                <p>Neben <strong>Strg+K</strong>, dem Schnellzugriff, gibt es vier Tasten. Sie greifen überall -
                aber nur, solange du <em>nicht</em> gerade in ein Feld schreibst. Wer einen Namen tippt, tippt
                einen Namen; kein Kürzel fährt dazwischen.</p>
                <ul>
                <li><strong>?</strong> - zeigt diese Liste, jederzeit und mit den Sprungzielen, die du
                tatsächlich sehen darfst.</li>
                <li><strong>g</strong> und dann ein Buchstabe - springt in einen Bereich:
                <em>p</em> Personen, <em>f</em> Fraktionen, <em>v</em> Vorgänge, <em>o</em> Operationen,
                <em>a</em> Aufgaben, <em>k</em> Kalender, <em>t</em> Taskforces, <em>s</em> Suche,
                <em>b</em> Brett, <em>h</em> Handbuch, <em>w</em> Beobachtete, <em>d</em> Lagezentrum. Nach dem
                <em>g</em> hast du gut eine Sekunde für den zweiten Buchstaben.</li>
                <li><strong>n</strong> - legt an, was auf die Liste gehört, auf der du stehst. Auf
                <em>Personen</em> eine Personenakte, auf <em>Vorgänge</em> einen Vorgang.</li>
                <li><strong>e</strong> - öffnet die Akte, die du gerade offen hast, im Bearbeiten-Modus:
                Personen, Fraktionen, Gruppen, Parteien, Vorgänge, Operationen, Taskforces, Entführungen und
                V-Personen. Bei Besprechungen, Aufgaben, Terminen, Ankündigungen, Aktivitäten und Dokumenten
                passiert nichts - dort entscheidet nicht dein Schreibrecht allein, ob du bearbeiten darfst,
                und die Taste würde dich auf eine Absage schicken.</li>
                </ul>
                <p>Es gibt bewusst <strong>kein</strong> Kürzel fürs Folgen. Alle vier hier führen dich nur
                irgendwohin; keines ändert etwas an einer Akte. Ein verrutschter Finger soll nichts
                anrichten.</p>
                """),

            new Article("art-plus-knopf", "schnell-erfassen", "Schnell erfassen",
                "Der Plus-Knopf in der Kopfzeile - und was er außer neuen Akten noch kann.",
                """
                <p>Oben in der Kopfzeile sitzt ein <strong>Plus-Knopf</strong>. Er öffnet die
                <em>Schnellerfassung</em>, und die hat drei Reiter: <em>Neue Akte</em>, <em>Vermerk</em> und
                <em>Aktivität</em>.</p>
                <p>Unter <em>Neue Akte</em> wählst du den Aktentyp, tippst die Bezeichnung, fertig. Danach
                stehst du auf der neuen Akte und füllst sie in Ruhe aus.</p>
                <p>Unter <em>Vermerk</em> hängst du einen Kommentar an eine Akte, ohne sie zu öffnen. Erst
                suchst du die Akte und wählst sie aus, dann tippst du den Text. Diese Reihenfolge ist so
                gewollt: Jemanden mit dem <strong>@</strong> dazuziehen und ein Bild mit
                <strong>Strg+V</strong> einfügen geht genau wie im Abschnitt <em>Kommentare</em> der Akte -
                und dafür muss vorher feststehen, an welcher Akte du gerade schreibst.</p>
                <p>Erreichbar sind darüber Personen, Fraktionen, Personengruppen, Parteien, Vorgänge,
                Operationen, Taskforces, Aufgaben und Besprechungen. Was du nicht sehen darfst, findest du
                auch hier nicht.</p>
                <p>Eine Kante, über die du sonst stolperst: Hast du ein <strong>Bild eingefügt</strong>,
                lässt sich die Akte nicht mehr wechseln. Das Bild hängt in dem Moment schon an ihr.</p>
                <p>Unter <em>Aktivität</em> hältst du eine <strong>Dienst-Aktivität</strong> fest - einen
                kurzen Eintrag über deinen eigenen Dienst: Titel, Art, ein paar Sätze. Die Zeit steht schon
                auf jetzt, und du kannst eine Fraktion oder eine Personengruppe verknüpfen. Wer mehr Felder
                braucht, eine Vorlage oder formatierten Text, nimmt den Link
                <em>Ausführlich erfassen</em>.</p>
                <p>Vermerk und Aktivität nehmen dich <strong>nie von der Seite</strong>, auf der du gerade
                arbeitest. Genau dafür sind die beiden Reiter da.</p>
                """,
                Steps:
                [
                    new Step("Add", "Plus-Knopf öffnen",
                        "Der Knopf sitzt oben in der Kopfzeile und ist von jeder Seite aus erreichbar."),
                    new Step("Search", "Erst die Akte wählen",
                        "Im Reiter „Vermerk“ suchst du die Akte und wählst sie aus, bevor du tippst."),
                    new Step("Save", "Speichern und weiterarbeiten",
                        "Ein Hinweis nennt die Akte, an der dein Eintrag steht - und du bleibst, wo du warst."),
                ]),

            new Article("art-textbausteine", "textbausteine", "Textbausteine",
                "Sätze, die du ständig neu tippst - einmal hinterlegen, mit einem Schrägstrich abrufen.",
                """
                <p>Ein <strong>Textbaustein</strong> ist ein Stück Text, das du immer wieder schreibst -
                der Kopf eines Protokolls, die Formel „Observation ohne Feststellung“, der Satz, mit
                dem du einen Kontakt bestätigst. Du hinterlegst ihn einmal und holst ihn danach mit zwei
                Handgriffen.</p>
                <p>Angelegt werden Bausteine unter <em>Mein Profil</em> im Abschnitt
                <em>Textbausteine</em>. Sie gehören <strong>dir allein</strong>: niemand sonst sieht sie,
                niemand sonst kann sie ändern.</p>
                <p>Abgerufen wird ein Baustein mit einem <strong>Schrägstrich</strong>, gefolgt von den
                ersten Buchstaben seines Namens. In einem gewöhnlichen Feld - Vermerk an der Akte,
                Taskforce-Chat, Dok, Observation - tippst du ihn am Wortanfang ans Ende dessen, was schon
                dasteht. Im <strong>Text-Editor</strong> der Dokumente und Berichte stehen die Bausteine in
                demselben Menü wie Überschriften und Listen: Schrägstrich am <strong>Anfang einer Zeile</strong>.</p>
                <p>Ein Schrägstrich <strong>mitten in einem Wort</strong> tut nichts. „Vinewood/Ost“
                bleibt also einfach stehen - sonst klappte dir die Liste beim Tippen dauernd auf.</p>
                <p>In einem Baustein darfst du <strong>Platzhalter</strong> benutzen.
                <em>{{Name}}</em> und <em>{{Aktenzeichen}}</em> beziehen sich auf die Akte, an der du gerade
                schreibst; <em>{{Datum}}</em>, <em>{{Uhrzeit}}</em>, <em>{{Agent}}</em> und
                <em>{{Dienstgrad}}</em> auf dich und den Moment. Schreibst du in einem Feld ohne Akte, bleibt
                <em>{{Name}}</em> sichtbar stehen - dann gehört dort etwas hin, das die Seite nicht wissen
                kann. Im Text-Editor ist das immer so: er weiß nicht, an welcher Akte er hängt, also bleiben
                die beiden Akten-Platzhalter dort stehen und du setzt sie selbst ein.</p>
                <p>Verwechsle das nicht mit den <em>Vorlagen</em> unter Einstellungen: die pflegt die
                Führung für alle, Textbausteine pflegst du für dich.</p>
                """,
                Steps:
                [
                    new Step("ShortText", "Baustein anlegen",
                        "Unter „Mein Profil“ im Abschnitt „Textbausteine“ einen Namen und den Text eintragen."),
                    new Step("Keyboard", "Mit Schrägstrich holen",
                        "„/“ am Wortanfang tippen und weiterschreiben - die Liste grenzt sich mit jedem Buchstaben ein."),
                    new Step("AutoFixHigh", "Platzhalter füllen sich",
                        "Name und Aktenzeichen kommen aus der Akte, Datum, Uhrzeit und Dienstgrad von dir."),
                ]),

            new Article("art-listen-ansichten", "listen-ansichten", "Listen-Ansichten",
                "Eine gefilterte Liste unter einem Namen merken und mit einem Klick wieder öffnen.",
                """
                <p>Wer jeden Tag dieselbe Auswahl braucht - etwa alle <em>Verdachtsfälle</em>, deren Akte
                <em>rot</em> ist -, muss die Filter nicht jedes Mal neu einstellen. Eine
                <strong>Ansicht</strong> merkt sich die Liste zusammen mit ihren Filtern.</p>
                <p>Das geht auf den Listen der <em>Personen</em>, <em>Fraktionen</em>, <em>Vorgänge</em> und
                <em>Operationen</em> und auf dem <em>Aufgaben-Board</em>. Stell die Filter ein, öffne oben
                rechts <em>Ansichten</em> und wähle <em>Diese Ansicht merken</em>. Ohne gesetzten Filter ist
                der Eintrag abgeblendet - die ungefilterte Liste öffnet ohnehin jeder Klick im Menü.</p>
                <p>Gemerkte Ansichten findest du an drei Stellen: im selben Menü auf ihrer Liste, im Menü
                links unter den <em>Favoriten</em> in der Gruppe <em>Meine Ansichten</em> und im
                <strong>Schnellzugriff</strong> (Strg+K) mit dem Hinweis <em>Ansicht</em>. Ein Klick öffnet
                die Liste genau so, wie du sie gemerkt hast - auch dann, wenn du gerade schon auf ihr bist.</p>
                <p>Merkst du dir auf einer Liste eine Ansicht unter einem Namen, den <strong>diese Liste</strong>
                schon hat, <strong>ersetzt</strong> sie die alte; der Dialog sagt dir das vorher. Auf einer
                anderen Liste darf derselbe Name ruhig noch einmal vorkommen. Löschen kannst du eine Ansicht
                mit dem Papierkorb im Menü ihrer Liste oder über den Knopf <em>Navigation anpassen</em> oben
                im Menü links, Reiter <em>Ansichten</em>. Mehr als zwanzig Ansichten sind nicht vorgesehen.</p>
                <p>Ansichten gehören <strong>dir allein</strong>, so wie deine Favoriten. Wer eine Ansicht
                öffnet, sieht darin trotzdem nur, was er auch ohne sie sehen dürfte. Im öffentlichen
                Demo-Zugang gibt es keine Ansichten: dort teilen sich alle Besucher ein Konto.</p>
                """,
                Steps:
                [
                    new Step("FilterList", "Filter einstellen",
                        "Auf der Liste Einstufung, Aktualität, Status oder Suchbegriff wählen."),
                    new Step("BookmarkAdd", "Ansicht merken",
                        "Oben rechts „Ansichten“ öffnen, „Diese Ansicht merken“ wählen und einen Namen vergeben."),
                    new Step("Bolt", "Wieder öffnen",
                        "Unter den Favoriten im Menü oder mit Strg+K - die Liste steht sofort wieder so da."),
                ]),

            new Article("art-entwuerfe", "entwuerfe", "Nichts geht verloren",
                "Was du tippst, hebt dein Browser auf - auch wenn die Verbindung abreißt.",
                """
                <p>Die Seite hängt an einer ständigen Verbindung zum Server. Reißt sie ab - WLAN weg, Server
                neu gestartet, Rechner im Ruhezustand -, meldet sich oben der Hinweis zum Wiederverbinden. Was
                du bis dahin getippt hast, ist trotzdem nicht weg: jedes längere Textfeld legt deinen Text
                laufend in <strong>deinem Browser</strong> ab, als <strong>Entwurf</strong>.</p>
                <p>Öffnest du dasselbe Feld wieder - nach dem Neuladen, beim nächsten <em>Neues Dok</em>, am
                nächsten Tag -, steht darunter <em>Nicht gespeicherter Entwurf gefunden</em>. Mit
                <em>Wiederherstellen</em> steht der Text wieder im Feld, mit <em>Verwerfen</em> ist er weg.
                Tippst du einfach weiter, verschwindet der Hinweis: dann gilt, was du jetzt schreibst.</p>
                <p>Das gilt für den Text-Editor der Dokumente und Berichte und für die gewöhnlichen Textfelder:
                Doks, Observationen, Vermerke, Taskforce-Chat, Schnellerfassung und die Beschreibungen in den
                Formularen der Akten. Auch ein Dialog, den du aus Versehen mit Escape oder einem Klick daneben
                schließt, verliert nichts - <em>Abbrechen</em> übrigens auch nicht. Ein Entwurf verschwindet
                erst, wenn du <strong>erfolgreich speicherst</strong>. Schlägt das Speichern fehl, bleibt er
                stehen.</p>
                <p>Kurze Begründungen in Entscheidungsdialogen hebt die Seite bewusst nicht auf: ein Grund, der
                plötzlich unter einer anderen Entscheidung steht, wäre schlimmer als gar keiner.</p>
                <p>Entwürfe liegen <strong>nur auf diesem Gerät</strong> und nur für dich. Nach sieben Tagen
                fallen sie weg, beim <em>Abmelden</em> sofort alle. An einem fremden Rechner meldest du dich
                deshalb immer ab.</p>
                """,
                Steps:
                [
                    new Step("CloudOff", "Verbindung weg",
                        "Einfach weitertippen - das Feld hebt deinen Text im Browser auf."),
                    new Step("Restore", "Wiederherstellen",
                        "Beim nächsten Öffnen des Felds den Hinweis darunter anklicken."),
                    new Step("Save", "Speichern räumt auf",
                        "Erst ein erfolgreiches Speichern löscht den Entwurf."),
                ]),
        ]);
}
