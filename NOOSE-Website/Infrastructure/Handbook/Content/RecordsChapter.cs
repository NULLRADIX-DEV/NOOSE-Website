using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter two: the records themselves and everything that hangs off them.</summary>
internal static class RecordsChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-akten", "akten-fuehren", "Akten führen",
        "Personen, Fraktionen, Einstufungen, Verknüpfungen und der Papierkorb.", "FolderShared",
        [
            new Article("art-personenakte", "personenakte", "Die Personenakte",
                "Die wichtigste Akte der Behörde - und wie sie aufgebaut ist.",
                """
                <p>Zu jeder Person, mit der das FIB zu tun hat, gibt es <strong>genau eine Akte</strong>.
                Alles, was über sie bekannt ist, läuft dort zusammen: Vernehmungen, Observationen, Fotos,
                Fraktionszugehörigkeit, Fahndung, Kommentare.</p>
                <p>Eine neue Akte legst du über <em>Personen → Neu</em> an. Pflicht ist nur der Name; das
                <strong>Aktenzeichen</strong> - die feste Nummer der Akte - vergibt das System selbst.</p>
                <p>Die Akte ist in <strong>Abschnitte</strong> unterteilt, die links in einer Leiste stehen:
                <em>Akte</em> (Steckbrief, Einstufung, Gefährdung, Fotos), <em>Ermittlung</em> (Doks,
                Überwachung, Verknüpfungen, Zugehörigkeit), <em>Anhänge</em> (Quellen, Fotos) und
                <em>Verlauf</em> (Kommentare, Wiedervorlagen, Zeitstrahl). Du siehst nur die Abschnitte, für
                die deine Rechte reichen.</p>
                <p>Lege lieber <strong>eine</strong> Akte gut an als drei halbe. Wenn du unsicher bist, ob es
                die Person schon gibt, such zuerst - die Suche findet auch ähnlich klingende Schreibweisen.</p>
                <p>Die Seite passt beim Anlegen zusätzlich auf. Gibt es schon eine Akte mit
                <strong>gleichem Namen</strong>, einem Namen, der <strong>gleich klingt</strong> oder nur
                <strong>vertippt</strong> ist, oder mit derselben <strong>Telefonnummer</strong>, fragt sie vor dem
                Speichern nach - auch über die Aliase und archivierte Akten. Ein Klick auf einen Treffer öffnet ihn
                in einem neuen Tab, deine Eingaben bleiben stehen. Ist es dieselbe Person, brichst du ab; sonst
                legst du mit <em>Trotzdem anlegen</em> an. Dieselbe Frage kommt, wenn du eine neue Person direkt
                als Mitglied einer Fraktion, Gruppe oder Partei einträgst.</p>
                <p>Ist es doch passiert, führt die <strong>Führung</strong> zwei Akten zusammen: auf der Liste
                <em>Personen</em> mit <em>Zusammenführen</em> oder direkt aus den <em>Ermittlungshinweisen</em>.
                Alles aus der doppelten Akte - Doks, Observationen, Fotos, Steckbrief, Verknüpfungen, Kommentare,
                Mitgliedschaften und Verlauf - wandert in die bleibende. Die doppelte landet im Papierkorb, ihr Name
                bleibt als Alias stehen. Einstufung und Lebensstatus der bleibenden Akte ändern sich dabei
                nicht.</p>
                """,
                DiagramKey: "personenakte",
                NavKey: "personen",
                Steps:
                [
                    new Step("Search", "Zuerst suchen",
                        "Tippe den Namen in die Suche. Vielleicht gibt es die Akte längst - eine zweite Akte zur selben Person ist der häufigste Fehler."),
                    new Step("PersonAdd", "Akte anlegen",
                        "Personen → Neu. Name reicht; alles andere kannst du später ergänzen. Warnt die Seite vor einer Dublette, sieh dir den Treffer erst an."),
                    new Step("ContactPage", "Steckbrief füllen",
                        "Aliase, Telefonnummer, Fahrzeuge, Waffen - das ist der Teil, den andere am häufigsten brauchen."),
                    new Step("Shield", "Einstufung setzen",
                        "Erst wenn es einen Anlass gibt. Eine Einstufung ohne Grund verzerrt später jede Auswertung."),
                    new Step("Link", "Verknüpfen",
                        "Fraktion, Vorgang, andere Personen. Eine Akte ohne Verbindungen ist eine Sackgasse."),
                ]),

            new Article("art-steckbrief", "steckbrief", "Der Steckbrief",
                "Aliase, Telefon, Fahrzeuge, Waffen - die Merkmale, nach denen gesucht wird.",
                """
                <p>Der <strong>Steckbrief</strong> ist der Abschnitt der Personenakte, in dem die harten
                Merkmale stehen: Aliase und Spitznamen, Telefonnummer, Fahrzeuge mit Kennzeichen, bekannte
                Waffen, Wohnsitz.</p>
                <p>Diese Felder sind kein Beiwerk. Nach genau diesen Angaben wird gesucht, wenn jemand nur ein
                Kennzeichen oder einen Spitznamen hat. Ein eingetragenes Alias findet die Akte auch dann, wenn
                niemand den echten Namen kennt.</p>
                <p>Trag ein, was du <strong>belegen</strong> kannst, und hänge den Beleg als Quelle an. Eine
                Vermutung gehört in einen Kommentar, nicht in den Steckbrief.</p>
                """),

            new Article("art-aktenzeichen", "aktenzeichen", "Aktenzeichen",
                "Woher die Nummer kommt und wozu sie gut ist.",
                """
                <p>Jede Akte bekommt beim Anlegen ein <strong>Aktenzeichen</strong> - eine feste,
                menschenlesbare Nummer wie <em>FIB-P-2026-0001</em>. Sie besteht aus der Behörde, einem
                Buchstaben für die Aktenart, dem Jahr und einer laufenden Nummer.</p>
                <p>Das Aktenzeichen vergibt das System selbst und es ändert sich nie. Genau deshalb ist es das
                Richtige, wenn du im Funk, in einem Bericht oder gegenüber einer Partnerbehörde eine bestimmte
                Akte benennen willst: Namen sind mehrdeutig, Aktenzeichen nicht.</p>
                """),

            new Article("art-einstufung", "einstufung", "Einstufungen",
                "Prüffall, Verdachtsfall, gesichert staatsgefährdend - und was daran hängt.",
                """
                <p>Die <strong>Einstufung</strong> sagt, wie ernst die Behörde eine Person oder Fraktion nimmt.
                Es gibt drei Stufen, und sie bauen aufeinander auf:</p>
                <ul>
                <li><strong>Prüffall</strong> - es gibt Anhaltspunkte, mehr nicht. Wir schauen hin.</li>
                <li><strong>Verdachtsfall</strong> - der Verdacht ist belegt genug, um aktiv zu ermitteln.</li>
                <li><strong>Gesichert staatsgefährdend</strong> - die höchste Stufe. Die Bewertung ist
                abgeschlossen.</li>
                </ul>
                <p>Eine Einstufung zu setzen ist eine <strong>Entscheidung</strong>, keine Notiz. Jede Änderung
                wird mit Begründung und Datum festgehalten und ist im Abschnitt <em>Einstufung</em> als
                Verlauf nachlesbar. Die höchste Stufe darf erst ab Senior Special Agent vergeben werden. Wer
                eine Einstufung <strong>herabsetzt</strong>, muss das begründen - mehr dazu unter
                <em>Begründungspflicht</em>.</p>
                <p>Die Einstufung hebt außerdem den <strong>Bedrohungs-Score</strong> auf einen festen Sockel.
                Eine leichtfertige Einstufung färbt also die ganze Lageübersicht ein.</p>
                """,
                DiagramKey: "einstufung"),

            new Article("art-verschlusssachen", "verschlusssachen", "Verschlusssachen",
                "Was „VS“ bedeutet und wer damit was sieht.",
                """
                <p>Eine <strong>Verschlusssache</strong> - kurz VS - ist ein Inhalt, den nicht jeder sehen
                darf. Es gibt vier Stufen:</p>
                <ul>
                <li><strong>Offen</strong> - alle internen Agenten.</li>
                <li><strong>Führung</strong> - erst ab Section Chief.</li>
                <li><strong>HRT</strong> - Konten der Abteilung HRT, dazu die Führung.</li>
                <li><strong>Administration</strong> - Konten mit dem Kennzeichen Administration Division, dazu
                die Führung.</li>
                </ul>
                <p>Die beiden Fach-Stufen schließen die Führung also <strong>nicht</strong> aus: wer ohnehin
                Verschlusssachen lesen darf, sieht auch HRT- und Administration-Material. Sie grenzen nach unten
                ab, nicht nach oben.</p>
                <p>Die Stufe wirkt <strong>überall</strong>, nicht nur auf der Akte selbst: ein VS-Dokument
                erscheint auch in der Suche nicht, taucht in keiner Liste auf und wird von FIBI nicht
                erwähnt. Für dich sieht es so aus, als gäbe es das Dokument nicht - und das ist Absicht.</p>
                <p>Stufe hoch heißt nicht „sicherer“. Was niemand findet, hilft auch niemandem. Stufe eine
                Sache nur ein, wenn es einen Grund gibt, den du benennen kannst. Umgekehrt gilt dasselbe: wer
                die Verschlusssache einer Akte aufhebt oder sie für mehr Agenten öffnet, gibt einen Grund an.</p>
                <p>Verwechsle diese vier Stufen nicht mit den <strong>Sicherheitsfreigaben</strong> der
                Dienstverordnung. Die VS-Stufen der Seite sind Sichtbarkeiten, keine Freigabestufen: Sie richten
                sich nach Dienstgrad <em>und</em> Abteilung, und die Seite rechnet keine Freigabe in eine VS-Stufe
                um. Welche Freigaben es gibt und wem sie zustehen, regelt die <em>Dienstverordnung</em> (Menü
                <em>Wissen</em>).</p>
                """,
                DiagramKey: "vs-stufen"),

            new Article("art-begruendungspflicht", "begruendungspflicht", "Begründungspflicht",
                "Vier Änderungen an einer Akte gehen nur mit einem Grund.",
                """
                <p>Manche Änderungen sind nicht nur eine Korrektur, sondern eine <strong>Entscheidung</strong>,
                die später jemand nachvollziehen muss. Vier davon verlangen deshalb einen Grund:</p>
                <ul>
                <li>eine <strong>Einstufung herabsetzen</strong>, etwa vom Verdachtsfall zum Prüffall oder
                zurück auf <em>Unbekannt</em>,</li>
                <li>eine <strong>Verschlusssache lockern</strong> - ganz aufheben oder für mehr Agenten öffnen,
                zum Beispiel von <em>Führung</em> auf <em>HRT</em>,</li>
                <li>eine <strong>Akte löschen</strong> - Person, Fraktion, Personengruppe, Partei, Vorgang,
                Operation oder Taskforce,</li>
                <li>eine <strong>Fahndung aufheben</strong>, die an der Personenakte ausgeschrieben ist.</li>
                </ul>
                <p>Die Seite fragt dich dann in einem Fenster nach dem Grund. Ohne Grund wird nichts geändert.
                Bei der Einstufung schreibst du ihn wie gewohnt in das Feld <em>Begründung</em>.</p>
                <p>Der Grund steht danach als eigene Zeile <em>Begründung</em> im <strong>Zeitstrahl</strong>
                der Akte und im Änderungsprotokoll unter <em>Nachweis</em>, direkt neben der Änderung, die er
                erklärt. Partnerbehörden sehen den Grund nicht, auch nicht im Verlauf der Einstufung.</p>
                <p>Eine Akte enger stellen, höher einstufen oder zur Fahndung ausschreiben braucht keinen
                eigenen Grund. Eine öffentliche Ausschreibung zurückzuziehen verlangt ohnehin einen Grund.</p>
                """,
                """
                <p>Die Dienstaufsicht fragt bei einer Herabstufung nicht „was“, sondern „warum“. Schreib den Grund
                so, dass ihn jemand versteht, der die Akte nicht kennt: „Hinweise haben sich nicht bestätigt,
                siehe Dok vom 12.10.“ hilft, „erledigt“ hilft niemandem.</p>
                """),

            new Article("art-fraktionen", "fraktionen", "Fraktionen",
                "Die Akte einer organisierten Gruppe: Mitglieder, Ränge, Bestände, Beziehungen.",
                """
                <p>Eine <strong>Fraktion</strong> ist eine organisierte Gruppe mit eigener Struktur - Ränge,
                Führung, oft ein Anwesen. Ihre Akte funktioniert wie eine Personenakte, hat aber vier eigene
                Abschnitte: <em>Mitglieder</em>, <em>Bestände</em>, <em>Aktivitäten</em> und
                <em>Beziehungen</em> zu anderen Fraktionen.</p>
                <p>Mitglieder werden mit ihrer Personenakte verknüpft, nicht als Namen eingetippt. Dadurch
                sieht man von beiden Seiten dasselbe: die Person zeigt ihre Fraktion, die Fraktion ihre
                Mitglieder.</p>
                <p>Beim Hinzufügen lässt sich eine bestehende Person gleich <strong>aus ihrer bisherigen
                Fraktion entfernen</strong> - praktisch beim Wechsel zwischen Gruppierungen. Ohne diese
                Auswahl bleibt sie in beiden Fraktionen. Vor der Bestätigung zeigt die Liste, in welchen
                Fraktionen die Person derzeit steht.</p>
                <p>Vier dieser Bereiche haben eine <strong>eigene Aktualitäts-Ampel</strong>. Wer die
                Mitgliederliste pflegt, setzt nur die Mitglieder-Ampel zurück - Stammdaten anzufassen zählt
                nicht als „gepflegt“.</p>
                <p>Eine <strong>Drogenroute gehört immer nur einer Fraktion</strong>. Trägst du eine Route ein,
                die schon eine andere Fraktion hält, fragt die Seite vor dem Speichern nach. Bestätigst du, geht
                die Route über: bei der anderen Fraktion verschwindet sie, und beide Akten vermerken den Wechsel in
                ihrem Verlauf. Groß- und Kleinschreibung zählen dabei nicht - „Kokain Nord“ und „kokain nord“ sind
                dieselbe Route. Eine Fraktion im Papierkorb oder im Archiv hält keine Route; holt man sie zurück
                und eine andere Fraktion hat die Route inzwischen, fällt sie bei der zurückgeholten weg.</p>
                """,
                NavKey: "fraktionen"),

            new Article("art-personengruppen", "personengruppen", "Personengruppen",
                "Der lose Zusammenschluss - und warum er im Zweifel die richtige Wahl ist.",
                """
                <p>Eine <strong>Personengruppe</strong> ist ein loser Zusammenschluss ohne feste Struktur -
                eine Clique, eine Zelle, eine Gruppe, die zusammen auftritt, aber keine Ränge kennt. Nimm sie,
                wenn du Leute zusammenfassen willst, ohne eine Organisation zu behaupten, die es nicht gibt.</p>
                <p>Sie hat Mitglieder wie eine Fraktion, aber keine Ränge, keine Bestände und keine
                Beziehungen zu anderen Gruppen. Genau das ist ihr Zweck: weniger Behauptung.</p>
                <p>Im Zweifel: Personengruppe. Sie lässt sich später immer noch zur Fraktion ausbauen, während
                eine vorschnelle Fraktionsakte eine Struktur suggeriert, die niemand belegt hat.</p>
                """,
                NavKey: "personengruppen"),

            new Article("art-parteien", "parteien", "Parteien",
                "Die politische Organisation - getrennt von den Fraktionen geführt.",
                """
                <p>Eine <strong>Partei</strong> ist eine politische Organisation mit Mitgliedern und
                Ausrichtung. Sie wird getrennt von den Fraktionen geführt, weil politische Arbeit anders
                bewertet wird als organisierte Kriminalität.</p>
                <p>Die Akte funktioniert wie die einer Personengruppe: Mitglieder werden mit ihrer
                Personenakte verknüpft, alles andere - Einstufung, Verknüpfungen, Kommentare - ist dasselbe
                wie überall.</p>
                """,
                NavKey: "parteien"),

            new Article("art-verknuepfungen", "verknuepfungen", "Akten verknüpfen",
                "Warum eine Verknüpfung immer in beide Richtungen sichtbar ist.",
                """
                <p>Fast jede Akte hat einen Abschnitt <em>Verknüpfungen</em>. Dort stellst du eine Verbindung
                zu einer anderen Akte her - egal welcher Art: Person zu Fraktion, Vorgang zu Dokument,
                Operation zu Person.</p>
                <p>Eine Verknüpfung ist <strong>immer beidseitig</strong>. Du legst sie einmal an, und sie
                erscheint auf beiden Akten. Du musst sie also nie doppelt pflegen - und du kannst dich darauf
                verlassen, dass auf der Gegenseite dasselbe steht.</p>
                <p>Zu jeder Verknüpfung gehört eine <strong>Art</strong> (etwa „gehört zu“, „steht in
                Verbindung mit“) und möglichst eine kurze Notiz, warum. Eine Verknüpfung ohne Begründung ist
                in drei Wochen nicht mehr nachvollziehbar.</p>
                """,
                DiagramKey: "verknuepfung"),

            new Article("art-quellen", "quellen", "Quellen anhängen",
                "Der Beleg zur Behauptung - Bild, Datei, Link oder Zitat.",
                """
                <p>Der Abschnitt <em>Quellen</em> nimmt Belege auf: einen Screenshot, ein Dokument, einen Link
                oder ein wörtliches Zitat. Jede Quelle bekommt eine Art und eine kurze Beschreibung.</p>
                <p>Die Faustregel dieser Behörde: <strong>was in der Akte steht, soll belegbar sein</strong>.
                Eine Beobachtung ohne Quelle ist ein Gerücht - und im Zweifelsfall genau das, was vor Gericht
                zusammenbricht.</p>
                <p>Quellen hängen an der Akte, nicht an dir. Wer die Akte sehen darf, sieht auch die Quellen.
                Umgekehrt gilt: eine Quelle an einer Verschlusssache ist selbst eine.</p>
                """),

            new Article("art-kommentare", "kommentare", "Kommentare und Erwähnungen",
                "Die laufende Unterhaltung an einer Akte - und wie du jemanden hinzuziehst.",
                """
                <p>Jede Akte hat einen Abschnitt <em>Kommentare</em>. Er ist der richtige Ort für alles, was
                Einschätzung ist und nicht Tatsache: Vermutungen, offene Fragen, Absprachen.</p>
                <p>Mit einem <strong>@</strong> ziehst du jemanden dazu. Du bekommst eine Auswahlliste, wählst
                den Agenten - und er bekommt eine Benachrichtigung mit Link auf genau diese Akte. Genauso lässt
                sich eine <strong>andere Akte</strong> erwähnen; sie erscheint dann als anklickbarer Verweis
                mitten im Text.</p>
                <p>Ein Bild kannst du direkt mit <strong>Strg+V</strong> einfügen. Es wird an der Trägerakte
                abgelegt, nicht bei dir - wer die Akte nicht sehen darf, sieht auch das Bild nicht.</p>
                <p>Du musst die Akte dafür nicht einmal öffnen: Der <strong>Plus-Knopf</strong> oben in der
                Kopfzeile nimmt unter <em>Vermerk</em> denselben Text entgegen, hängt ihn an die Akte, die du
                dort auswählst - und lässt dich auf der Seite stehen, auf der du gerade arbeitest.</p>
                """),

            new Article("art-stichworte", "stichworte-und-zusatzfelder", "Stichworte und Zusatzfelder",
                "Zwei Wege, eine Akte über die vorgesehenen Felder hinaus zu beschreiben.",
                """
                <p><strong>Stichworte</strong> (Tags) sind freie Schlagworte, die du an jede Akte hängen
                kannst. Sie sind dafür da, quer durch den Bestand zu filtern: alle Akten zu einem Einsatz, zu
                einem Stadtteil, zu einem Thema.</p>
                <p>Damit daraus kein Wildwuchs wird, pflegt die Führung die Liste der Stichworte unter
                <em>Einstellungen</em>. Nimm ein vorhandenes, bevor du ein neues erfindest - zwei Stichworte
                für dieselbe Sache trennen genau die Akten, die zusammengehören.</p>
                <p><strong>Zusatzfelder</strong> sind etwas anderes: eigene, von der Führung definierte Felder,
                die an jeder Akte einer Art erscheinen. Sie sind die Antwort auf „wir bräuchten hier noch ein
                Feld für …“, ohne dass die Seite umgebaut werden muss.</p>
                """),

            new Article("art-wiedervorlagen", "wiedervorlagen", "Wiedervorlagen",
                "Die Erinnerung an einer Akte - damit nichts liegen bleibt.",
                """
                <p>Eine <strong>Wiedervorlage</strong> ist eine Erinnerung, die an einer Akte hängt: „am 14.
                nochmal prüfen, ob sich die Fraktionslage geändert hat“. Du setzt Datum, Text und wer
                zuständig ist.</p>
                <p>Wird sie fällig, bekommt der Zuständige eine Benachrichtigung - und die Wiedervorlage steht
                auf seinem Lagezentrum, bis sie erledigt ist. Sie verschwindet nicht von selbst.</p>
                <p>Alle Wiedervorlagen, für die du zuständig bist, stehen unter <em>Meine Wiedervorlagen</em> im
                Bereich <em>Mein Dienst</em>, sortiert nach <strong>überfällig</strong>, <strong>heute</strong>,
                <strong>diese Woche</strong> (bis Sonntag) und <strong>später</strong>. Dort verschiebst du eine mit
                einem Klick um <em>+1 Tag</em> oder <em>+1 Woche</em> oder hakst sie mit <em>Erledigt</em> ab, ohne
                die Akte zu öffnen. Ist sie schon überfällig, zählt das Verschieben ab heute: <em>+1 Tag</em> heißt
                dann morgen.</p>
                <p>Hängt eine deiner Wiedervorlagen an einer Akte, die du nicht sehen darfst, zählt die Seite sie
                nur mit, ohne Akte und ohne Notiz. Die Führung kann sie auf jemand anderen umhängen.</p>
                <p>Bleibt eine Wiedervorlage sieben Tage überfällig, erfährt die Führung davon, einmal je
                Fälligkeitsdatum. Verschiebst du sie, beginnt die Frist neu.</p>
                <p>Nutze sie großzügig. Eine Akte, die auf etwas wartet, ohne dass jemand daran erinnert wird,
                wartet für immer.</p>
                """,
                NavKey: "wiedervorlagen"),

            new Article("art-watchlist", "beobachtete-akten", "Beobachtete Akten",
                "Einer Akte folgen und gesagt bekommen, wenn sich etwas ändert.",
                """
                <p>Über das Stern-Symbol setzt du eine Akte auf deine <strong>Beobachtungsliste</strong>. Ab
                dann wirst du benachrichtigt, wenn sich an ihr etwas ändert - ein neuer Kommentar, eine neue
                Einstufung, ein neues Dok.</p>
                <p>Unter <em>Beobachtete Akten</em> stehen alle, denen du folgst, mit dem Datum der letzten
                Änderung. Das ist der schnellste Weg zu „was ist bei meinen Sachen passiert, während ich weg
                war“.</p>
                <p>Die Liste ist deine allein. Niemand sieht, welchen Akten du folgst.</p>
                """,
                NavKey: "watchlist"),

            new Article("art-akte-teilen", "akte-in-discord-teilen", "Eine Akte in Discord teilen",
                "Kollegen gezielt auf eine Akte stoßen, ohne die Adresse von Hand zu kopieren.",
                """
                <p>Auf Personen, Fraktionen, Personengruppen, Parteien, Vorgängen und Operationen steht oben das
                Papierflieger-Symbol <em>In Discord teilen</em>. Damit postest du einen kurzen Hinweis auf die Akte
                in einen Discord-Kanal, den die Führung dafür eingerichtet hat. Auch eine gesuchte Person teilst du
                so, von ihrer Personenakte aus.</p>
                <p>Im Beitrag stehen nur Art, Name und Aktenzeichen der Akte, dein <strong>Hinweis</strong> - ein
                Satz, höchstens 300 Zeichen - und ein Link. Der Link öffnet die Akte erst nach der Anmeldung und
                zeigt jedem Leser nur, was seine Rechte hergeben. Gepingt wird niemand, und dein Name steht nicht im
                Beitrag.</p>
                <p><strong>Verschlusssachen lassen sich nie teilen</strong>, auch nicht von der Führung. Bei einer
                VS-Akte ist das Symbol gesperrt: in einem Kanal lesen auch Leute mit, die die Akte nicht sehen
                dürfen, und schon der Name kann zu viel sein.</p>
                <p>Teilen darf jeder interne Agent mit Schreibrecht, höchstens fünfmal in zehn Minuten. Jedes Teilen
                steht im Zugriffsprotokoll. Fehlt das Symbol, hat die Führung noch keinen Kanal eingerichtet oder
                Discord ist ausgeschaltet.</p>
                <p>Die Kanäle legt die Führung unter <em>Einstellungen → Discord-Benachrichtigungen</em> an, mit
                einem Namen und der Webhook-URL des Discord-Kanals. Eine gespeicherte URL zeigt die Seite danach
                nicht mehr an; wer sie ersetzen will, trägt einfach eine neue ein.</p>
                """,
                Steps:
                [
                    new Step("Send", "Symbol anklicken",
                        "Oben auf der Akte: In Discord teilen. Bei einer Verschlusssache ist es gesperrt."),
                    new Step("Forum", "Kanal wählen",
                        "Zur Wahl stehen nur die Kanäle, die die Führung eingerichtet hat."),
                    new Step("EditNote", "Hinweis schreiben",
                        "Ein Satz, worauf die Kollegen achten sollen. Er ist freiwillig."),
                ]),

            new Article("art-akten-export", "akten-exportieren", "Listen und Akten herunterladen",
                "Eine Liste als CSV, eine ganze Akte als Dossier - und warum beides protokolliert wird.",
                """
                <p>Auf den Listen der <em>Personen</em>, <em>Fraktionen</em>, <em>Personengruppen</em>,
                <em>Parteien</em>, <em>Vorgänge</em> und <em>Operationen</em> steht oben rechts der Knopf
                <em>CSV</em>. Er lädt die Liste <strong>genau so</strong> herunter, wie du sie gerade siehst: mit
                deinen Filtern, in deiner Sortierung, mit den Spalten der Liste. Die Datei öffnest du in Excel oder
                Google Tabellen. Herunterladen darf jeder interne Agent; in der Datei steht nie mehr als auf deinem
                Bildschirm.</p>
                <p>Die <strong>Führung</strong> und die Nur-Lese-Aufsicht laden außerdem jede dieser Akten als
                <strong>Dossier</strong> herunter,
                über das Download-Symbol oben auf der Akte. Das Dossier ist eine Textdatei (<em>.md</em>) mit allem,
                was die Akte hergibt - Steckbrief, Einstufung, Doks, Verknüpfungen und so weiter -, aber nur so weit,
                wie die Rechte dessen reichen, der es herunterlädt. Im Kopf stehen, wer es wann exportiert hat, ob die
                Akte eine Verschlusssache ist, und der Satz, dass es nur mit Freigabe der Führung weitergegeben werden
                darf.</p>
                <p>Jeder Download steht im <strong>Zugriffsprotokoll</strong>: bei einer Liste die Art und die Zahl
                der Zeilen, beim Dossier die Akte. Eine Datei verlässt das Haus und lässt sich nicht zurückholen.
                Lade nur herunter, was du wirklich außerhalb der Seite brauchst - für alles andere gibt es die
                <em>Druckansicht</em> und den Link auf die Akte.</p>
                """,
                Steps:
                [
                    new Step("FilterList", "Liste eingrenzen",
                        "Filter und Sortierung einstellen. Die Datei enthält genau die Zeilen, die du siehst."),
                    new Step("Download", "CSV herunterladen",
                        "Oben rechts auf der Liste. Jeder Download steht im Zugriffsprotokoll."),
                    new Step("FileDownload", "Dossier (Führung)",
                        "Das Download-Symbol oben auf der Akte lädt sie ganz als Textdatei herunter."),
                ]),

            new Article("art-neu-seit-besuch", "neu-seit-besuch", "Was ist neu an dieser Akte?",
                "Was andere eingetragen haben, seit du zuletzt drin warst.",
                """
                <p>Öffnest du eine Akte, die du schon kennst, steht über den Abschnitten eine Zeile wie
                <em>„Seit deinem letzten Besuch am 16.09. um 14:20: Einstufung geändert, 1 Dok und 3
                Kommentare“</em>. Sie fasst zusammen, was <strong>andere</strong> seitdem eingetragen haben.
                Was du selbst geschrieben hast, zählt nicht mit - das kennst du ja.</p>
                <p>Ein Klick auf <em>Im Zeitstrahl ansehen</em> springt in den <strong>Zeitstrahl</strong> der
                Akte. Dort trägt jeder dieser Einträge ein <em>Neu</em>, und der Knopf <em>Neu</em> ganz vorn
                in der Filterleiste blendet alles andere aus.</p>
                <p>Ein <strong>Besuch</strong> ist nicht jeder Seitenaufruf. Wer die Akte öffnet, einer
                Verknüpfung folgt und zurückkommt, ist noch beim selben Besuch - die Markierungen bleiben
                stehen, auch nach dem Neuladen. Erst nach einer halben Stunde Pause beginnt ein neuer.</p>
                <p>Es zählt, <strong>wann</strong> etwas eingetragen wurde, nicht wann es passiert ist. Eine
                Observation, die jemand heute für letzte Woche nachträgt, steht im Zeitstrahl weit unten - und
                ist trotzdem neu.</p>
                <p>Die Zeile gibt es an Personen, Fraktionen, Parteien, Personengruppen, Vorgängen, Operationen
                und Taskforces. Beim ersten Öffnen einer Akte erscheint sie nicht, und auch nicht, wenn nichts
                dazugekommen ist.</p>
                """,
                Steps:
                [
                    new Step("FiberNew", "Zeile lesen",
                        "Oben auf der Akte steht, was andere seit deinem letzten Besuch eingetragen haben."),
                    new Step("Timeline", "In den Zeitstrahl springen",
                        "Im Zeitstrahl ansehen öffnet den Abschnitt. Jeder neue Eintrag trägt ein Neu."),
                    new Step("FilterList", "Nur Neues zeigen",
                        "Der Knopf Neu vorn in der Filterleiste blendet alles andere aus."),
                ]),

            new Article("art-aktualitaet", "aktualitaet", "Die Aktualitäts-Ampel",
                "Grün, gelb, rot - wie alt die Angaben einer Akte sind.",
                """
                <p>An Fraktionsakten steht eine <strong>Ampel</strong>: grün heißt aktuell, gelb heißt „wird
                langsam alt“, rot heißt veraltet. Sie misst nicht, wie gut die Akte ist, sondern nur, wann
                zuletzt jemand nachgesehen hat.</p>
                <p>Wichtig: Die Ampel hängt an <strong>vier getrennten Stempeln</strong> - Mitglieder,
                Bestände, Aktivitäten und Doks. Der älteste davon bestimmt die Farbe. Am Namen der Fraktion zu
                schrauben setzt die Ampel <em>nicht</em> zurück, und das ist Absicht: sonst würde eine
                Rechtschreibkorrektur eine drei Monate alte Mitgliederliste als frisch ausweisen.</p>
                <p>Eine rote Ampel ist kein Vorwurf, sondern eine Arbeitsanweisung.</p>
                """),

            new Article("art-papierkorb", "papierkorb", "Der Papierkorb",
                "Gelöschtes ist nicht weg - und wie du es zurückholst.",
                """
                <p>Wenn du eine Akte löschst, wird sie nicht vernichtet. Sie wandert in den
                <strong>Papierkorb</strong> und verschwindet nur aus Listen, Suche und Verknüpfungen. Der
                Inhalt bleibt erhalten. Beim Löschen fragt die Seite nach einem <strong>Grund</strong>; er
                steht danach im Zeitstrahl der Akte.</p>
                <p>Unter <em>Papierkorb</em> liegen die gelöschten Einträge <strong>aller</strong> Aktenarten
                an einer Stelle, nach Art gruppiert. Ein Klick auf <em>Wiederherstellen</em> holt eine Akte
                zurück, als wäre nichts gewesen.</p>
                <p>Deshalb ist Löschen kein Drama - aber auch keine Lösung für „ich will das nicht mehr
                sehen“. Dafür gibt es das <strong>Archiv</strong>.</p>
                """,
                NavKey: "papierkorb"),

            new Article("art-archiv", "akten-archivieren", "Akten archivieren",
                "Erledigtes aus dem Weg räumen, ohne es zu löschen.",
                """
                <p>Eine Akte, die niemand mehr braucht, muss nicht gelöscht werden. Du kannst sie
                <strong>archivieren</strong>: sie verschwindet aus den Listen, aus den Auswahlfeldern und aus
                der normalen Suche - bleibt aber vollständig erhalten und lesbar.</p>
                <p>Den Knopf <em>Archivieren</em> findest du auf der Akte selbst, neben <em>Löschen</em>. Du
                kannst einen Grund angeben; er steht danach oben auf der Akte und im Nachweis. Zurückholen darf
                sie jeder, der schreiben darf - ein Klick auf <em>Zurückholen</em> in dem Hinweisband.</p>
                <p><strong>Archiv ist nicht Papierkorb.</strong> Im Papierkorb liegt Gelöschtes, das nur die
                Führung zurückholt. Das Archiv ist der ruhende Bestand: die Akte ist da, sie steht nur nicht
                mehr im Weg. Verknüpfungen, Erwähnungen und der Beziehungsgraph zeigen sie weiterhin an - im
                Graph blass, damit man sieht, woran man ist.</p>
                <p>Willst du eine archivierte Akte sehen, stell in der Liste den Filter <em>Archiv</em> auf
                <em>Mit Archiv</em> oder <em>Nur Archiv</em>; in der Suche gibt es den Schalter
                <em>Archiv einschließen</em>. Eine archivierte Akte bleibt bearbeitbar, zählt aber nicht mehr
                in Statistik und Aktualitäts-Ampel mit.</p>
                """,
                Steps:
                [
                    new Step("Archive", "Archivieren",
                        "Auf der Akte, neben Löschen. Der Grund ist freiwillig, hilft aber dem Nächsten."),
                    new Step("FilterList", "Wiederfinden",
                        "In der Liste den Filter Archiv umstellen, oder in der Suche den Schalter setzen."),
                    new Step("Unarchive", "Zurückholen",
                        "Ein Klick im Hinweisband oben auf der Akte. Sie steht sofort wieder in allen Listen."),
                ]),
        ]);
}
