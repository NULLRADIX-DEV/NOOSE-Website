using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter seven: the workspace of the Administration Division.</summary>
internal static class AdministrationChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-administration", "administration-division", "Administration Division",
        "Der Arbeitsbereich für Personal, Disziplin, Presse und Parlament.", "AssuredWorkload",
        [
            new Article("art-administration", "arbeitsbereich-administration", "Der Arbeitsbereich der Administration",
                "Chat, Informationen und wer zur Administration gehört.",
                """
                <p>Die <strong>Administration Division</strong> hat einen eigenen Arbeitsbereich: im Menü links unter
                <em>Leitung</em> der Eintrag <em>Administration</em>. Ihn sehen die Mitglieder der Administration, das
                Board und die Admins. Alle anderen sehen den Eintrag gar nicht.</p>
                <p>Die Leiste auf der Seite ist nach Aufgaben gegliedert, jeder Eintrag ist eine eigene Seite. Unter
                <em>Allgemeines</em> findest du den <strong>internen Chat</strong> der Abteilung, die <em>Informationen</em>
                (Zuständigkeiten, Abläufe, Ansprechpartner, frei bearbeitbar, darunter Verweise auf Dokumente und
                Links), die <em>Zugeteilten Agents</em> und die <em>Personalbeziehungen</em>. Jede Liste hat oben ein
                Suchfeld. Neue Nachrichten im Chat zeigt
                der Eintrag <em>Administration</em> im Menü als Zahl, und die Glocke meldet sich einmal, bis du den Chat wieder gelesen hast.</p>
                <p>Unter <em>Verlauf</em> stehen wie in jeder Akte <em>Zeitstrahl</em>, <em>Wiedervorlagen</em> und
                <em>Kommentare</em>. Der Zeitstrahl sammelt Versetzungen, Dienstzeugnisse, Beziehungen,
                Ehrungsvorschläge und parlamentarische Anfragen. Disziplinarverfahren haben ihren eigenen.</p>
                <p><strong>Personalbeziehungen</strong> sind Ehen, Partnerschaften und enge Verwandtschaft unter
                Agents. Wer mit jemandem in Beziehung steht, darf ihn weder befördern noch beurteilen, ihm keine
                Ehrung verleihen und kein Verfahren gegen ihn führen. Die Seite lehnt das ab, und die Sache geht an
                jemand anderen in der Administration.</p>
                """,
                NavKey: "administration",
                Steps:
                [
                    new Step("AssuredWorkload", "Bereich öffnen",
                        "Im Menü unter Leitung auf Administration, dann in der Leiste der Seite Chat."),
                    new Step("Forum", "Chat lesen",
                        "Neue Nachrichten stehen unter der Linie Neu seit deinem letzten Besuch."),
                    new Step("Favorite", "Beziehung erfassen",
                        "Zwei Agents wählen, Art und Datum eintragen. Ab dann gelten beide als befangen."),
                ]),

            new Article("art-administration-personalgewinnung", "personalgewinnung", "Personalgewinnung",
                "Die laufenden Sicherheitsüberprüfungen auf einen Blick.",
                """
                <p>Unter <em>Sicherheitsüberprüfungen</em> stehen alle Bewerbungen, die gerade in der
                <strong>Sicherheitsüberprüfung</strong> sind, und die Ergebnisse des letzten Monats. Ein Klick öffnet
                die Bewerbung. Dort wird geprüft und entschieden, wie bisher im Bewerbungswesen.</p>
                """,
                NavKey: "administration.sicherheitspruefungen"),

            new Article("art-administration-personalwesen", "personalwesen", "Personalwesen",
                "Beurteilungen, Beförderungen, Versetzungen, Auszeichnungen und Dienstzeugnisse.",
                """
                <p><em>Beurteilungen</em> zeigt je Agent die letzte Note. Markiert ist, wer seit mehr als zwei Wochen
                keine Beurteilung hat oder zuletzt mangelhaft oder ungenügend beurteilt wurde.</p>
                <p><em>Beförderungen</em> listet alle offenen Anträge. Stellen kann die Administration einen Antrag in
                der Personalakte, entscheiden bleibt beim Section Chief und beim Board. Läuft gegen den Agenten ein
                Disziplinarverfahren, ist die Zeile markiert.</p>
                <p>Eine <strong>Versetzung</strong> beantragt der Agent selbst in seiner Personalakte (Reiter
                <em>Administration</em>), die Führung oder die Administration. Die Administration hält die
                <strong>Anhörung</strong> fest, danach genehmigt oder lehnt das Board ab. Mit der Genehmigung wechselt
                die Abteilung. Jeder Abteilungswechsel steht im Versetzungsverlauf.</p>
                <p><em>Auszeichnungen</em> sammelt die <strong>Vorschläge</strong>. Jeder Agent kann einen Kollegen
                für ein Ehrenzeichen vorschlagen. Die Administration verleiht oder lehnt ab.</p>
                <p>Unter <em>Schusswaffengebrauch</em> stehen alle Meldungen der Agents. In den Personalakten sieht die
                Administration wie die Führung die Dienstaufsicht, die Kommentare und unter <em>Personal</em> die
                Gekündigten.</p>
                <p>Ein <strong>Dienstzeugnis</strong> stellt die Administration beim Ausscheiden, bei einer Versetzung
                oder auf Anfrage aus. Die Gesamtnote ist aus den Beurteilungen vorbelegt. Der Druck enthält
                Dienstgrade, Verwendungen und Auszeichnungen bis zum Ausstellungstag. Der Agent sieht seine
                Zeugnisse in der eigenen Personalakte.</p>
                """,
                NavKey: "administration.beurteilungen",
                Steps:
                [
                    new Step("SwapHoriz", "Versetzung beantragen",
                        "Zielabteilung wählen und begründen."),
                    new Step("RecordVoiceOver", "Anhörung festhalten",
                        "Die Administration notiert, was der Agent dazu sagt."),
                    new Step("TaskAlt", "Entscheiden",
                        "Das Board genehmigt oder lehnt ab. Mit der Genehmigung wechselt die Abteilung."),
                ]),

            new Article("art-administration-disziplinar", "disziplinarwesen", "Disziplinarwesen",
                "Ermittlungsverfahren einleiten, aussetzen und mit Einstellung oder Verfügung abschließen.",
                """
                <p>Ein <strong>Disziplinarverfahren</strong> leitet die Administration unter
                <em>Ermittlungsverfahren</em> ein: Agent wählen, Anlass beschreiben. Bei Trainees und Agents in der
                Probezeit reicht die <strong>vereinfachte Anhörung</strong>. Das Verfahren bekommt ein Aktenzeichen
                und eine eigene Akte mit Ermittlungsnotizen, Quellen, Wiedervorlagen und Zeitstrahl.</p>
                <p>Läuft wegen derselben Sache ein Strafverfahren, lässt sich das Verfahren <em>aussetzen</em>. Eine
                vorläufige Suspendierung wird am Verfahren vermerkt. Solange es läuft, ist eine Beförderung gesperrt.</p>
                <p>Abgeschlossen wird es auf zwei Wegen: <em>Einstellen</em> mit Begründung oder eine
                <strong>Disziplinarverfügung</strong> mit Maßnahme, Sachverhalt, Begründung und Belehrung. Beides
                lässt sich drucken.</p>
                <p>Der betroffene Agent sieht das laufende Verfahren nicht. Nach dem Abschluss bekommt er eine
                Benachrichtigung und findet das Ergebnis in seiner Personalakte. Verfahren gegen Mitglieder der
                Administration führt nur das Board, und wer mit dem Agenten in Beziehung steht, sieht das Verfahren
                nicht.</p>
                <p>Eine Maßnahme wie eine Degradierung setzt die Seite nicht selbst um. Das geschieht wie bisher in der
                Agenten-Verwaltung.</p>
                """,
                NavKey: "administration.ermittlungsverfahren",
                Steps:
                [
                    new Step("Gavel", "Verfahren einleiten",
                        "Agent und Anlass angeben. Bei Trainees ist die vereinfachte Anhörung vorgeschlagen."),
                    new Step("Comment", "Ermitteln",
                        "Notizen, Quellen und Wiedervorlagen an der Verfahrensakte."),
                    new Step("TaskAlt", "Abschließen",
                        "Einstellen oder Verfügung erlassen. Der Agent wird benachrichtigt."),
                ]),

            new Article("art-administration-oeffentlichkeit", "oeffentlichkeit-und-parlament", "Öffentlichkeit und Parlament",
                "Pressemitteilungen und parlamentarische Anfragen.",
                """
                <p>Unter <em>Öffentlichkeitsarbeit</em> schreibt und veröffentlicht die Administration
                <strong>Pressemitteilungen</strong>, genau wie die Führung unter <em>Einstellungen</em>.</p>
                <p><strong>Parlamentarische Anfragen</strong> stellt das Parlament selbst über sein Partnerkonto,
                oder die Administration erfasst sie. Jede Anfrage hat eine Frist von sieben Tagen, überschrittene
                Fristen sind rot. Wer sie bearbeitet, übernimmt sie, schreibt eine Antwort und gibt sie frei. Erst
                dann sieht das Parlament die Antwort.</p>
                <p>Unter <em>Bürger-Tickets</em> liest die Administration alle Anliegen und Beschwerden von Bürgern
                mit. Interne Notizen kann sie dort schreiben, dem Bürger antwortet weiter die Führung.</p>
                """,
                NavKey: "administration.presse"),

            new Article("art-parlament-anfragen", "parlamentarische-anfragen", "Parlamentarische Anfragen stellen",
                "Für Partnerkonten des Parlaments: Anfragen an das FIB und ihre Antworten.",
                """
                <p>Als Konto des <strong>Parlaments</strong> findest du im Menü <em>Parlamentarische Anfragen</em>.
                Dort stellst du eine Anfrage mit Titel und Text. Die Administration Division hat sieben Tage Zeit für
                die Antwort. Sobald sie freigegeben ist, bekommst du eine Benachrichtigung und liest sie auf derselben
                Seite.</p>
                """),
        ]);
}
