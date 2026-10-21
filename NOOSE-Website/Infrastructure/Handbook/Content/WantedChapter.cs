using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter four: everything that leaves the building - wanted notices, citizens, press.</summary>
internal static class WantedChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-fahndung", "fahndung-und-oeffentlichkeit", "Fahndung & Öffentlichkeit",
        "Ausschreibung, Kopfgeld, Bürgerhinweise, Tickets, Einspruch und Presse.", "PersonSearch",
        [
            new Article("art-fahndung-grundlagen", "fahndung-grundlagen", "Was nach draußen geht",
                "Die wichtigste Regel des öffentlichen Bereichs - und warum sie so streng ist.",
                """
                <p>Die Seite hat zwei Hälften. Innen liegen die Akten. Außen liegt das, was jeder Bürger ohne
                Anmeldung sehen kann: die Fahndungsliste, Presseerklärungen, Warnungen, Gesetzesauszüge.</p>
                <p>Dazwischen steht eine einzige Regel: <strong>nach außen geht nur, was jemand ausdrücklich
                veröffentlicht hat</strong>. Es gibt keinen Weg, auf dem ein Akteninhalt „aus Versehen“ nach
                draußen rutscht. Eine öffentliche Ausschreibung ist eine <em>eigene Abschrift</em>, kein Blick
                in die Akte - sie trägt genau die Felder, die jemand hineingeschrieben hat.</p>
                <p>Deshalb musst du beim Veröffentlichen zweimal denken, aber danach nie mehr Angst haben:
                Änderst du später die Akte, ändert sich die Ausschreibung nicht von selbst.</p>
                """),

            new Article("art-fahndung-anlegen", "fahndung-ausschreiben", "Eine Fahndung ausschreiben",
                "Vom Entwurf zur Veröffentlichung - und wieder zurück.",
                """
                <p>Die öffentliche Ausschreibung legst du direkt an der Personenakte an, im Abschnitt
                <em>Öffentliche Fahndung</em>. Du füllst Vorwurf, Foto und Hinweise für die Bevölkerung - und
                bleibst zunächst im <strong>Entwurf</strong>.</p>
                <p>Wer den Dienstgrad <strong>Senior Special Agent</strong> oder höher hat, veröffentlicht
                selbst. Darunter stellst du stattdessen einen <strong>Antrag</strong>; er landet im Posteingang
                unter <em>Freigaben</em> und wird dort entschieden.</p>
                <p>Mit der ersten Veröffentlichung bekommt die Ausschreibung ein eigenes Aktenzeichen, das mit
                <em>FA</em> beginnt. Unter dieser Nummer meldet sich später jeder Bürger, der etwas dazu
                sagen will.</p>
                <p>Ist die Person gefasst, setzt du die Ausschreibung auf <strong>gefasst</strong>; war sie
                falsch, ziehst du sie <strong>zurück</strong>. Beides nimmt sie sofort von der öffentlichen
                Seite. Zurückgezogen behält sie Aktenzeichen und Inhalt - ein erneutes Veröffentlichen ist ein
                Klick.</p>
                """,
                DiagramKey: "fahndung-lebenszyklus",
                NavKey: "fahndung",
                Steps:
                [
                    new Step("ContactPage", "Steckbrief prüfen",
                        "Foto und Merkmale müssen stimmen, bevor etwas nach außen geht. Danach ändert sich die Ausschreibung nicht mehr mit der Akte."),
                    new Step("EditNote", "Entwurf schreiben",
                        "Vorwurf in einem Satz, verständlich für Bürger. Keine internen Kürzel, keine Aktenzeichen aus der Ermittlung."),
                    new Step("Send", "Veröffentlichen oder beantragen",
                        "Ab Senior Special Agent direkt. Darunter geht ein Antrag an die Freigaben."),
                    new Step("Public", "Draußen nachsehen",
                        "Die Ausschreibung steht unter ihrem FA-Aktenzeichen öffentlich. Genau das sehen Bürger."),
                    new Step("CheckCircle", "Abschließen",
                        "Gefasst oder zurückgezogen - beides nimmt sie sofort offline."),
                ]),

            new Article("art-sachfahndung", "sachfahndung", "Sachfahndung",
                "Wenn nicht eine Person gesucht wird, sondern ein Gegenstand.",
                """
                <p>Neben der Personenfahndung gibt es die <strong>Sachfahndung</strong>: ein gesuchtes
                Fahrzeug, eine Waffe, ein Gegenstand. Sie funktioniert genauso - Entwurf, Veröffentlichung,
                Abschluss - und erscheint auf derselben öffentlichen Seite.</p>
                <p>Der Unterschied liegt in der Beschreibung: hier zählen Kennzeichen, Farbe, Merkmale. Ein
                Foto hilft mehr als drei Absätze Text.</p>
                """),

            new Article("art-kopfgeld", "kopfgeld", "Kopfgeld",
                "Geld auf eine Fahndung setzen - und was davon öffentlich ist.",
                """
                <p>Auf eine veröffentlichte Fahndung kann <strong>Kopfgeld</strong> gesetzt werden: von der
                Behörde aus der Kasse, oder von einem Agenten aus eigenen Mitteln.</p>
                <p>Nach außen geht davon <strong>eine einzige Zahl</strong>: die Summe. Wer wie viel gestiftet
                hat, ist öffentlich nicht zu erfahren - und das ist keine Nachlässigkeit, sondern Absicht. Eine
                Aufschlüsselung wäre ein öffentliches Verzeichnis darüber, welcher Agent auf wen Geld
                gesetzt hat.</p>
                <p>In die öffentliche Summe zählen nur <strong>zugesagte und gesicherte</strong> Anteile. Ein
                Anteil, der noch beantragt ist, ist kein Geld - und würde nebenbei eine laufende interne
                Entscheidung verraten.</p>
                <p>Ab Senior Special Agent setzt du behördliches Geld selbst; darunter beantragst du es.</p>
                """),

            new Article("art-buergerhinweise", "buergerhinweise", "Bürgerhinweise",
                "Was passiert, wenn draußen jemand etwas meldet.",
                """
                <p>Bürger können über ein öffentliches Formular <strong>Hinweise</strong> abgeben - allgemein
                oder zu einer bestimmten Fahndung. Diese Hinweise landen im internen Eingang unter
                <em>Bürgerhinweise</em>.</p>
                <p>Jeder Hinweis durchläuft feste Zustände: <em>Neu</em> → <em>In Prüfung</em> →
                <em>Bestätigt</em> oder <em>Verworfen</em>. Braucht ihr eine Antwort vom Hinweisgeber, setzt du
                <em>Rückfrage</em> und schreibst ihm direkt - er bekommt sie in seinem Bürgerkonto.</p>
                <p>Übernimm einen Hinweis, bevor du daran arbeitest. Dann sieht jeder, dass er nicht liegen
                bleibt, und ihr schreibt dem Bürger nicht zu zweit.</p>
                <p>Unter dem Antwortfeld schreibt dir <em>NOOSEI-Entwurf</em> einen Vorschlag aus dem Hinweis und dem
                bisherigen Schriftwechsel, ohne interne Notizen. Lies den Entwurf, pass ihn an und schick ihn
                selbst ab.</p>
                """,
                DiagramKey: "hinweis-weg",
                NavKey: "buergerhinweise",
                Steps:
                [
                    new Step("Inbox", "Eingang sichten",
                        "Neue Hinweise stehen oben. Das Menü zeigt eine Zahl, solange etwas unbearbeitet ist."),
                    new Step("AssignmentInd", "Übernehmen",
                        "Damit bist du zuständig und niemand schreibt doppelt."),
                    new Step("QuestionAnswer", "Rückfragen",
                        "Der Bürger antwortet in seinem Konto. Schreib so, wie du es einem Fremden erklären würdest."),
                    new Step("Link", "Mit der Akte verknüpfen",
                        "Ein Hinweis ohne Verknüpfung ist in zwei Wochen nicht mehr auffindbar."),
                    new Step("Gavel", "Abschließen",
                        "Bestätigt, verworfen oder „führte zur Ergreifung“. Der letzte Wert ist die Grundlage für eine Belohnung."),
                ]),

            new Article("art-belohnung", "belohnung", "Belohnung",
                "Wenn ein Hinweis zum Erfolg geführt hat.",
                """
                <p>Hat ein Hinweis zur Ergreifung geführt, kann die Behörde eine <strong>Belohnung</strong>
                auszahlen. Der Weg dorthin ist der Status des Hinweises: erst <em>führte zur Ergreifung</em>,
                dann die Auszahlung.</p>
                <p>Die Auszahlung ist eine Buchung in der Kasse und bekommt einen Beleg. Sie ist damit
                nachvollziehbar, ohne dass irgendwo öffentlich steht, wer das Geld bekommen hat.</p>
                <p>Ein anonymer Hinweisgeber kann eine Belohnung annehmen, ohne seine Anonymität aufzugeben -
                die Abwicklung läuft über sein Bürgerkonto.</p>
                """),

            new Article("art-ergreifung", "ergreifungsmeldung", "Ergreifungsmeldung",
                "Ein Bürger meldet, dass er selbst jemanden gestellt hat.",
                """
                <p>Eine <strong>Ergreifungsmeldung</strong> ist ein Sonderfall des Bürgerhinweises: jemand
                meldet nicht, wo eine gesuchte Person ist, sondern dass er sie bereits gestellt hat.</p>
                <p>Sie kommt im selben Eingang an, ist aber als eigene Art gekennzeichnet - weil sie eilt.
                Prüfe zuerst, ob die Person tatsächlich ausgeschrieben ist, dann was mit ihr geschehen soll.</p>
                <p>Setze die Fahndung erst auf <em>gefasst</em>, wenn die Übergabe wirklich stattgefunden hat.
                Eine vorschnell geschlossene Ausschreibung ist von außen nicht mehr zu sehen - und niemand
                meldet dann noch etwas.</p>
                """),

            new Article("art-einspruch", "einspruch", "Einspruch gegen eine Fahndung",
                "Wenn ein Bürger sagt: Das bin ich nicht, oder das stimmt nicht.",
                """
                <p>Zu jeder öffentlichen Ausschreibung kann ein Bürger <strong>Einspruch</strong> einlegen. Er
                landet intern in einem eigenen Eingang und hat vier Zustände: <em>Neu</em>, <em>In
                Prüfung</em>, <em>Stattgegeben</em>, <em>Abgelehnt</em>.</p>
                <p>Ein Einspruch ist kein Ärgernis, sondern eine Kontrolle. Nimm ihn ernst: eine falsche
                Ausschreibung ist ein Schaden, den die Behörde selbst angerichtet hat. Wird ihm stattgegeben,
                zieh die Ausschreibung zurück.</p>
                <p>Der Bürger bekommt in jedem Fall eine Antwort. Formuliere sie so, dass sie ohne Kenntnis der
                Akte verständlich ist.</p>
                <p>Dabei hilft <em>NOOSEI-Entwurf</em> unter dem Begründungsfeld: Schreib deine Gründe in
                Stichpunkten hinein und wähle, ob du stattgeben oder ablehnen willst. NOOSEI formuliert daraus den
                Text, erfindet aber keine Gründe. Entscheiden tust du danach mit den Knöpfen darunter.</p>
                """),

            new Article("art-tickets", "buerger-tickets", "Bürger-Tickets",
                "Anliegen an die Führungsebene - mit Schriftwechsel.",
                """
                <p>Ein <strong>Ticket</strong> ist ein Anliegen eines Bürgers an die Führung: eine Beschwerde,
                eine Anfrage, ein Anliegen, das in kein anderes Formular passt. Es hat einen Verlauf wie ein
                Schriftwechsel - beide Seiten schreiben, bis es geschlossen wird.</p>
                <p>Tickets sind <strong>führungsvertraulich</strong>. Ihr Inhalt taucht deshalb bewusst nicht
                im Änderungsprotokoll auf; wer nicht beteiligt ist, sieht sie nicht.</p>
                <p>Bist du an einem Ticket beteiligt, findest du es unter <em>Meine Tickets</em> im Bereich
                <em>Mein Dienst</em> - auch ohne Führungsrang.</p>
                """,
                NavKey: "tickets"),

            new Article("art-presse", "presse-und-warnungen", "Presse und Warnungen",
                "Was die Behörde von sich aus sagt.",
                """
                <p>Zwei Wege führen von der NOOSE zur Öffentlichkeit, ohne dass jemand fragt:</p>
                <p>Eine <strong>Presseerklärung</strong> ist eine redaktionelle Mitteilung. Sie wird
                geschrieben, geprüft und dann veröffentlicht - mit Datum und ohne Verfasser.</p>
                <p>Eine <strong>Warnung</strong> ist dringender: eine Gefahr, vor der die Bevölkerung jetzt
                gewarnt werden soll. Sie erscheint hervorgehoben und hat ein Ende - eine Warnung, die drei
                Wochen steht, warnt niemanden mehr.</p>
                <p>Beides ist nicht zurückzunehmen, nur zu widerrufen. Was einmal draußen war, war draußen.</p>
                """),

            new Article("art-oeffentliche-lage", "oeffentliche-lage", "Gefahrenlage und öffentliche Zahlen",
                "Die Ampel nach außen - und was sie zeigt.",
                """
                <p>Die öffentliche Startseite trägt eine <strong>Gefahrenlage-Ampel</strong>: eine Stufe, die
                die Führung von Hand setzt und begründet. Sie ist eine Aussage der Behörde, keine berechnete
                Zahl.</p>
                <p>Daneben stehen <strong>öffentliche Zahlen</strong> - wie viele Fahndungen laufen, wie viele
                Hinweise eingegangen sind. Sie sind bewusst grob: aus ihnen darf sich nichts über einzelne
                Akten ableiten lassen.</p>
                <p>Ebenfalls öffentlich sind <strong>Organisationsprofile</strong>: das, was die Behörde über
                eine Gruppierung offen sagt. Ein Profil ist eine eigene, freigegebene Darstellung - nicht die
                Fraktionsakte.</p>
                """),

            new Article("art-module", "module-und-notaus", "Module und der Not-Aus",
                "Wie sich der öffentliche Bereich abschalten lässt.",
                """
                <p>Jeder Teil des öffentlichen Bereichs ist ein <strong>Modul</strong>, das sich einzeln
                abschalten lässt: Fahndung, Hinweise, Tickets, Presse, Warnungen. Ein abgeschaltetes Modul ist
                von außen einfach nicht da.</p>
                <p>Darüber steht der <strong>Not-Aus</strong>: ein einziger Schalter, der alles nach außen
                schließt. Er überstimmt jedes einzelne Modul, ohne die gespeicherte Einstellung zu ändern -
                nach dem Ausschalten ist alles wieder so, wie es vorher war.</p>
                <p>Eine Sache funktioniert auch bei geschlossenem Not-Aus weiter: das <strong>Zurückziehen</strong>
                einer Veröffentlichung. Veröffentlichen braucht ein laufendes Modul, Depublizieren nie - sonst
                machte der Not-Aus genau das unmöglich, wofür er da ist.</p>
                """),
        ]);
}
