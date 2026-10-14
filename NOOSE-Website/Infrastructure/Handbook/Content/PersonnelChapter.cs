using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter six: people, ranks, recruiting and money.</summary>
internal static class PersonnelChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-personal", "personal-und-fuehrung", "Personal & Führung",
        "Personalakte, Beförderung, Ausbildung, Bewerbungen, Partner und Kasse.", "Groups",
        [
            new Article("art-personalakte", "personalakte", "Die Personalakte",
                "Was über einen Agenten geführt wird - und wer es sieht.",
                """
                <p>Zu jedem Agenten gehört eine <strong>Personalakte</strong>. Darin stehen der Verlauf seiner
                Dienstgrade, Notizen der Führung, Beförderungen, abgehakte Ausbildungsmodule und - falls es
                dazu kam - das Ausscheiden.</p>
                <p>Die Personalakte ist etwas anderes als die Personenakte. Die Personenakte beschreibt jemanden,
                über den wir ermitteln; die Personalakte beschreibt einen von uns.</p>
                <p>Der Abschnitt <em>Vermerke</em> ist dabei mehr als ein Notizzettel: Hier werden
                <strong>Belobigungen</strong> festgehalten - und die <strong>Disziplinarmaßnahmen</strong> nach
                §3.7 der Dienstverordnung. Ein Verweis nach Stufe 1 ist genau das: ein neutraler oder negativer
                Vermerk in dieser Akte. Welche Stufe ab welchem Dienstgrad verhängt werden darf, steht unter
                <em>Dienstvorschrift → Sanktionsstufen</em>.</p>
                <p>Sie ist der Führung vorbehalten. Deinen eigenen Stand siehst du in deinem Profil.</p>
                """,
                NavKey: "personal"),

            new Article("art-inaktivitaet", "inaktivitaets-radar", "Das Inaktivitäts-Radar",
                "Wer seit Wochen kein Lebenszeichen gibt - und was die Führung dann tut.",
                """
                <p>Unter <em>Personal</em> hat die Führung einen Reiter <em>Inaktiv</em>. Er listet Agenten, von
                denen die Seite seit einer Weile nichts gesehen hat. Als <strong>Lebenszeichen</strong> zählt
                alles, was du hier tust: etwas schreiben, eine Akte öffnen oder die Seite überhaupt aufrufen.
                Das jüngste davon steht in der Liste; fährst du mit der Maus über das Datum, siehst du alle drei
                einzeln.</p>
                <p><strong>Abgemeldete Tage zählen nicht.</strong> Wer zwei Wochen still war und davon zehn Tage
                abgemeldet, kommt auf vier Tage ohne Lebenszeichen. Läuft gerade eine Abmeldung, steht in der
                Zeile <em>abgemeldet bis …</em>. Ein frisch freigegebenes, entsperrtes oder wieder aufgenommenes
                Konto zählt erst ab diesem Tag.</p>
                <p>Die Liste ist nach Dauer gestuft, und jede Stufe bringt ihren Knopf mit:</p>
                <ul>
                <li><strong>Nachfassen</strong> (Grundwert ab 14 Tagen): Der Agent bekommt eine Benachrichtigung
                und - wenn der Kanal dafür eingerichtet ist - einen Discord-Ping mit der Bitte, sich zu melden
                oder eine Abmeldung einzutragen. Diese
                Nachricht lässt sich nicht stummschalten. Die Zeile merkt sich, wer wann nachgefasst hat, und
                ein zweites Nachfassen geht frühestens nach 24 Stunden - so schreibt niemand doppelt.</li>
                <li><strong>Vermerk</strong> (ab 30 Tagen): Ein vorausgefüllter Eintrag in der Personalakte, Art
                <em>Disziplinarisch</em>. Text und Art lassen sich vor dem Eintragen ändern.</li>
                <li><strong>Kündigung prüfen</strong> (ab 60 Tagen): Der übliche Kündigungsdialog, der Grund ist
                schon ausgefüllt.</li>
                </ul>
                <p>Die drei Schwellen stellt die Führung unter <em>Einstellungen → Inaktivitäts-Radar</em> ein.
                Das Radar entscheidet nichts von selbst: es meldet sich nicht in der Glocke und kündigt niemanden.
                Sobald der Agent wieder ein Lebenszeichen gibt, verschwindet er aus der Liste.</p>
                <p>Die Seite sieht nur, was auf der Seite passiert. Wer im Spiel aktiv ist, aber nie hier
                hereinschaut, steht trotzdem im Radar - deshalb ist Nachfassen der erste Schritt, nicht der
                Vermerk. Wer gerade abgemeldet ist, bekommt keine Bitte um Rückmeldung.</p>
                <p>Seitenbesuche zählt die Seite erst seit dem Update, mit dem das Radar kam. Vorher hinterließ wer
                nur Listen oder das Brett las gar keine Spur. Steht ein Agent nur mit einer älteren Spur im Radar,
                ist die Zeile deshalb <em>vorläufig</em>: bis die Besuche so lange gezählt wurden wie die erste
                Schwelle, bietet das Radar dort nur <em>Nachfassen</em> an.</p>
                """),

            new Article("art-befoerderung", "befoerderung", "Beförderung",
                "Vom Antrag bis zum neuen Dienstgrad.",
                """
                <p>Eine <strong>Beförderung</strong> beginnt als Antrag: jemand aus der Führung schlägt einen
                Agenten für den nächsten Dienstgrad vor und begründet es. Der Antrag steht dann zur
                Entscheidung.</p>
                <p>Entscheiden darf erst ab <strong>Deputy Director</strong>. Das ist bewusst eine höhere
                Schwelle als „Führung“ - wer befördert, verändert dauerhaft die Rechte eines Kontos.</p>
                <p>Mit der Entscheidung wird der Dienstgrad gesetzt und im Verlauf festgehalten. Der Betroffene
                wird einmal abgemeldet und muss sich neu anmelden, damit die neuen Rechte greifen.</p>
                <p>Die Seite prüft <strong>nicht</strong>, ob der Zielgrad der nächsthöhere ist - sie lässt auch
                einen Sprung oder eine Herabstufung zu. Das ist Absicht: nach §2.4.4 darf die Behördenleitung
                jeden Agenten in jeden Dienstgrad setzen.</p>
                <p>Was die Dienstverordnung für den regulären Weg verlangt - Ausbildungen, Aktivität,
                Zugehörigkeit zu TRU oder HRB, eine abgeschlossene Infiltration - steht unter
                <em>Dienstvorschrift → Beförderungskriterien</em>. Diese Voraussetzungen prüft ein Mensch, nicht
                die Seite; die Begründung des Antrags ist der Ort, an dem sie belegt werden.</p>
                """,
                DiagramKey: "befoerderung"),

            new Article("art-ehrungen", "ehrungen-und-urkunden", "Ehrungen und Urkunden",
                "Ehrenzeichen verleihen, bekanntgeben, aberkennen und als Urkunde drucken.",
                """
                <p>Die Führung kann einem Agenten ein <strong>Ehrenzeichen</strong> verleihen - etwa eine
                Verdienst- oder Tapferkeitsmedaille. Welche es gibt, legt die Führung unter
                <em>Einstellungen → Ehrenzeichen</em> fest. Dort steht zu jedem auch ein Standardtext, der beim
                Verleihen schon eingetragen ist.</p>
                <p>Verliehen wird in der Personalakte, Reiter <em>Auszeichnungen</em>, über
                <em>Ehrenzeichen verleihen</em>: Ehrenzeichen wählen, Tag der Verleihung und Verleihungstext
                eintragen. Drei Schalter entscheiden über die Bekanntgabe: ein Eintrag am Schwarzen Brett, ein
                Beitrag im Discord-Kanal für Ehrungen, der den Geehrten erwähnt, und eine Meldung in seiner Glocke.
                Genannt wird immer nur der Codename. Dasselbe Ehrenzeichen kann ein Agent mehrfach bekommen; sich
                selbst kann niemand etwas verleihen.</p>
                <p>Verliehene Ehrenzeichen erscheinen als kleine <strong>Plaketten</strong> in der Personalakte, im
                eigenen Profil, im Organigramm, in der Bestenliste und einige Wochen lang im Lagezentrum. Auf dem
                öffentlichen Führungsprofil stehen nur die, die die Führung dort ausdrücklich angehakt hat.</p>
                <p>Eine Ehrung lässt sich korrigieren - dann ändern sich auch der Eintrag am Schwarzen Brett und das
                Führungsprofil - oder <strong>aberkennen</strong>. Eine aberkannte Ehrung bleibt mit dem Grund
                durchgestrichen und ausgegraut in der Personalakte stehen, verschwindet aber aus Profil, Organigramm,
                Bestenliste, Lagezentrum und vom Führungsprofil, und ihr Eintrag am Schwarzen Brett wandert in den
                Papierkorb. Über die eigenen Ehrungen entscheidet immer jemand anderes.</p>
                <p>Zu jeder Ehrung gibt es eine <strong>Urkunde</strong> im Querformat, ebenso zu jeder Ernennung
                und Beförderung im Dienstgrad-Verlauf. Drucken dürfen die Führung und der Geehrte selbst. Vor dem
                Druck wählst du, ob der Codename oder der Klarname darauf steht. Unterschrieben ist sie von der
                Behördenleitung, die unter <em>Einstellungen → Ehrenzeichen</em> eingetragen ist; eine
                Ehrungs-Urkunde behält die Unterschrift vom Tag der Verleihung.</p>
                """,
                RoleplayHtml: """
                <p>Eine Ehrung ist ein feierlicher Moment. Trag sie hier ein - mit dem Tag der Übergabe als Datum -,
                druck die Urkunde aus und übergib Medaille und Urkunde dann im Spiel, bei einem Antreten oder einer
                Besprechung.</p>
                """),

            new Article("art-ausbildung", "ausbildungsmodule", "Ausbildungsmodule",
                "Die Abhak-Liste der Einarbeitung.",
                """
                <p>In der Personalakte steht eine Liste von <strong>Ausbildungsmodulen</strong> - Schulungen,
                Unterweisungen, Nachweise, die jeder Agent durchlaufen soll. Jedes Modul wird einzeln
                abgehakt, mit Datum und dem Namen dessen, der es abgenommen hat.</p>
                <p>Abhaken dürfen die <strong>Führung</strong> und das <strong>HRB</strong>. Das Kennzeichen HRB
                setzt keinen Führungsrang voraus - ein Special Agent mit diesem Kennzeichen darf es also auch.
                Wer ein Modul irrtümlich abgehakt hat, kann den Haken wieder entfernen; der Vorgang steht im
                Protokoll.</p>
                <p>Die <strong>Liste der Module</strong> selbst ist etwas anderes: sie anzulegen, zu ändern oder
                zu löschen bleibt der Führung vorbehalten. Ein gelöschtes Modul nimmt die Haken aller Agenten mit.</p>
                <p>Die Liste wird <strong>leer ausgeliefert</strong>. Welche Module es gibt, entscheidet die
                Führung - die Dienstverordnung nennt in §2.3 Grundeinweisung, Tarnausbildung, „(Erweiterte)
                Verhörmethoden“, die Drohnenausbildung sowie die Ausbildungen für TRU und HRB. Was davon
                Voraussetzung für welche Beförderung ist, steht unter
                <em>Dienstvorschrift → Aus- und Fortbildungen</em>.</p>
                <p>Für den Agenten selbst ist die Liste vor allem eine Orientierung: sie sagt, was von ihm
                erwartet wird, bevor ihn jemand danach fragt.</p>
                """),

            new Article("art-agenten-verwalten", "agenten-verwalten", "Konten freigeben und verwalten",
                "Wer hereinkommt, mit welchem Dienstgrad und welchen Kennzeichen.",
                """
                <p>Unter <em>Agenten-Verwaltung</em> werden neue Konten freigegeben. Ein frisch angemeldetes
                Konto steht auf <strong>ausstehend</strong> und sieht nichts, bis jemand es freigibt und dabei
                Dienstgrad und Kennzeichen setzt.</p>
                <p>Hier werden auch die <strong>Kennzeichen</strong> vergeben: Admin, TRU, HRB und die
                Nur-Lese-Aufsicht. Sie sind keine Dienstgrade - ein Deputy Director muss kein HRB sein, und ein
                Special Agent kann es sein.</p>
                <p>Für <strong>TRU und HRB</strong> gilt dabei eine Schwelle aus der Dienstverordnung: sie
                werden erst ab dem Dienstgrad <strong>Special Agent</strong> vergeben (§2.4.1). Bei einem Junior
                Agent sind die beiden Schalter deshalb gesperrt. Wird jemand unter diese Schwelle herabgestuft,
                nimmt die Seite ihm die Kennzeichen ab und schreibt das ins Protokoll.</p>
                <p>Jede Änderung an Dienstgrad, Kennzeichen oder Status meldet den Betroffenen einmal ab. Das
                ist kein Fehler: die Rechte stecken in seiner Anmeldung, und sie werden erst beim nächsten
                Anmelden neu ausgestellt.</p>
                """,
                NavKey: "admin.agenten"),

            new Article("art-bewerbungen", "bewerbungen", "Bewerbungen",
                "Der Weg von außen in die Behörde.",
                """
                <p>Wer zur NOOSE will, bewirbt sich über den öffentlichen Bereich. Die Bewerbung durchläuft
                feste Stufen: <em>Eingereicht</em> → <em>Sicherheitsüberprüfung</em> → <em>Test</em> →
                <em>Vorstellungsgespräch</em> → <em>Angenommen</em> oder <em>Abgelehnt</em>.</p>
                <p>Bearbeitet wird sie von der <strong>HRB</strong> und der Führung. Es gibt einen
                Schriftwechsel mit dem Bewerber; er sieht dabei nie den Namen des Agenten, der ihm schreibt -
                die Anschreiben sind so gebaut, dass der Name automatisch geschwärzt wird.</p>
                <p>Der Bewerber sieht außerdem einen <strong>vergröberten Status</strong>: Test und
                Vorstellungsgespräch erscheinen ihm als eine Stufe. Sonst wüsste er nach dem Weiterrücken,
                dass er den Test bestanden hat, bevor die Entscheidung gefallen ist.</p>
                """,
                DiagramKey: "bewerbung-ablauf",
                NavKey: "bewerbungen",
                Steps:
                [
                    new Step("Inbox", "Bewerbung sichten",
                        "Neue Bewerbungen stehen im Eingang. Lies zuerst, ob eine Sperre am Discord-Konto liegt."),
                    new Step("Security", "Sicherheitsüberprüfung",
                        "Gibt es eine Personenakte? Gibt es Verbindungen, die gegen eine Einstellung sprechen?"),
                    new Step("Quiz", "Test freigeben",
                        "Der Eignungstest läuft mit Zeitlimit. Die Uhr startet, wenn der Bewerber auf „Test starten“ klickt."),
                    new Step("RecordVoice", "Gespräch führen",
                        "Termin über den Schriftwechsel abstimmen. Der Bewerber sieht weiter nur „Test“."),
                    new Step("HowToReg", "Entscheiden",
                        "Annehmen legt das Konto an. Ablehnen und Schließen sperren das Discord-Konto für 14 Tage."),
                ]),

            new Article("art-eignungstest", "eignungstest", "Der Eignungstest",
                "Fragen, Zeitlimit und was beim Zurücksetzen passiert.",
                """
                <p>Der <strong>Eignungstest</strong> ist ein Fragebogen, den ein Bewerber im Rahmen seiner
                Bewerbung ausfüllt. Die Fragen pflegt die HRB; es gibt Auswahlfragen und Freitextfragen.</p>
                <p>Der Test hat eine <strong>Bearbeitungszeit</strong>. Sie beginnt in dem Moment, in dem der
                Bewerber die Fragen zum ersten Mal abruft - nicht, wenn du ihn freigibst. Braucht jemand aus
                gutem Grund länger, kannst du <strong>Zusatzminuten</strong> gewähren, ohne den Test neu zu
                starten.</p>
                <p>Ein <strong>Zurücksetzen</strong> löscht die bisherigen Antworten und startet die Zeit neu.
                Es gibt pro Bewerbung nur einen laufenden Versuch - sei also sicher, bevor du zurücksetzt.</p>
                """),

            new Article("art-sperren", "bewerbungssperren", "Bewerbungssperren",
                "Wer 14 Tage draußen bleibt - und warum automatisch.",
                """
                <p>Drei Dinge lösen eine <strong>Sperre von 14 Tagen</strong> aus: eine abgelehnte Bewerbung,
                eine geschlossene Bewerbung und eine nicht bestandene Sicherheitsüberprüfung. Die Sperre wird
                automatisch gesetzt, niemand muss daran denken.</p>
                <p>Sie hängt am <strong>Discord-Konto</strong>, nicht am Namen. Ein neuer Name mit demselben
                Konto kommt also nicht durch.</p>
                <p>Daneben gibt es die <strong>Blacklist</strong>: eine dauerhafte Sperre, die von Hand gesetzt
                wird. Sie ist für die Fälle gedacht, in denen es nicht um Eignung geht, sondern um etwas
                Grundsätzliches.</p>
                """),

            new Article("art-partner", "partner-freigaben", "Partnerbehörden",
                "LSPD, DoJ, LSMD und Parlament - und was sie sehen dürfen.",
                """
                <p>Konten von <strong>Partnerbehörden</strong> sind keine NOOSE-Agenten. Sie sehen
                standardmäßig <strong>nichts</strong> - und das ändert sich nur durch ausdrückliche
                Freigaben.</p>
                <p>Eine Akte wird auf zwei Wegen freigegeben:</p>
                <ul>
                <li>Die <strong>Einzelfreigabe</strong> gibt eine bestimmte Akte für eine Behörde oder ein
                einzelnes Konto frei.</li>
                <li>Die <strong>Regelfreigabe</strong> gibt alle passenden Akten eines Typs frei - auch die,
                die erst später angelegt werden. Beim Parlament sind das zum Beispiel alle Badfraks und
                deren aktuelle Mitglieder.</li>
                </ul>
                <p>Die <strong>Typ-Erlaubnis</strong> je Rang schränkt danach nur noch ein; sie gibt nichts
                frei. Verschlusssachen sieht ein Partner nie.</p>
                <p>Unter <em>Einstellungen → Partnerbehörden</em> steht außerdem, welche Inhalte eine Behörde
                <strong>nie</strong> sieht - zum Beispiel Doks und Befragungen. Das schlägt jede Freigabe,
                auch eine Einzelfreigabe mit „Ganze Akte“.</p>
                <p>Je Behörde lassen sich dort auch <strong>Funktionen</strong> freischalten: die
                NOOSEI-Rechtsauskunft, Beziehungsgraph, Funkplan, Lageberichte und Anfragen. Die Rechtsauskunft
                kennt nur die Gesetzbücher; ihr Wochenkontingent je Konto legt der KI-Eigner fest. Graph und Funkplan zeigen einem Partner nur, was für ihn
                freigegeben ist; Lageberichte bekommt er ohne Doks, ohne Namen von Personen und ohne Finanzen.</p>
                <p>Über <em>Freigaben anfragen</em> bittet eine Behörde in eigenen Worten um weitere Akten. Die
                Anfrage landet im Eingang unter <em>Anfragen von Partnern</em>. Wer sie genehmigt, wählt die passende
                Akte aus und gibt sie damit frei; die Behörde bekommt Bescheid.</p>
                <p>Partner dürfen wenig schreiben: Dokumente, Quellen, Beiträge im Taskforce-Chat und Anfragen. Alles
                andere ist für sie nur zu lesen.</p>
                """,
                DiagramKey: "partner-freigabe",
                NavKey: "admin.freigaben"),

            new Article("art-organigramm", "organigramm", "Organigramm",
                "Wer über wem steht - und wer es nach außen sieht.",
                """
                <p>Das <strong>Organigramm</strong> zeigt den Aufbau der Behörde nach Dienstgrad und Einheit.
                Intern zeigt es alle aktiven Agenten mit ihrem Codenamen.</p>
                <p>Es gibt zusätzlich ein <strong>öffentliches</strong> Organigramm. Darin erscheint nur, wer
                dafür einzeln freigegeben wurde - in aller Regel ausschließlich die Führung. Ein Agent kommt
                nie automatisch hinein.</p>
                """,
                NavKey: "organigramm"),

            new Article("art-kasse", "kasse", "Die Kasse",
                "Zwei Konten, drei Buchungsarten, ein Beleg.",
                """
                <p>Die <strong>Kasse</strong> führt zwei getrennte Konten: <strong>Schwarzgeld</strong> und
                <strong>Grüngeld</strong>. Jede Bewegung ist eine Buchung mit Betrag, Grund und Datum.</p>
                <p>Es gibt drei Arten: <em>Einzahlung</em>, <em>Auszahlung</em> und <em>Korrektur</em>. Die
                Korrektur setzt den Stand auf einen Wert - sie ist für die Fälle da, in denen die Buchhaltung
                auseinandergelaufen ist, und gehört in die Hand der Führung.</p>
                <p>Der Kontostand ergibt sich aus den Buchungen. Du kannst ihn nicht direkt ändern, und das ist
                der Punkt: jede Zahl lässt sich bis zu ihrer Buchung zurückverfolgen.</p>
                """,
                NavKey: "kasse"),

            new Article("art-finanzierungen", "finanzierungen", "Finanzierungen",
                "Geld für die eigene Ausrüstung beantragen.",
                """
                <p>Eine <strong>Finanzierung</strong> ist ein Antrag auf Geld aus der Kasse - für Ausrüstung,
                Fahrzeuge, einen Einsatz. Du stellst ihn selbst; die Führung entscheidet.</p>
                <p>Jeder Agent hat ein <strong>Monatsbudget</strong>. Ein genehmigter Antrag reserviert davon,
                die Auszahlung bucht es ab. Deinen Stand siehst du auf derselben Seite.</p>
                <p>Was die Fraktionskasse in der Regel übernimmt, legt §8.0 der Dienstverordnung fest - vom
                Jailbreak-Handy über Dienstwaffen bis zu den chemischen Produkten, jeweils mit Höchstmengen. Die
                vollständige Liste steht unter
                <em>Ausrüstung &amp; Einrichtungen → Erstattungsfähige Dienstmittel</em>. Für laufende
                Infiltrationen und große Einsätze kann die Führungsebene darüber hinausgehen; das ist dann im
                Einzelfall zu dokumentieren.</p>
                <p>Begründe den Antrag so, dass er ohne Rückfrage entschieden werden kann. Ein Antrag ohne
                Zweck bleibt liegen.</p>
                """,
                NavKey: "finanzierungen"),

            new Article("art-ausscheiden", "ausscheiden", "Ausscheiden aus der Behörde",
                "Kündigung, Sperrung und was mit den Akten passiert.",
                """
                <p>Wenn ein Agent die Behörde verlässt, wird sein Konto nicht gelöscht, sondern
                <strong>beendet</strong>. Der Grund und das Datum stehen in der Personalakte.</p>
                <p>Das Konto verliert damit jeden Zugriff, verschwindet aus allen Auswahllisten - bleibt aber
                in den Protokollen und an seinen Akten sichtbar. Das muss so sein: sonst wäre die
                Nachvollziehbarkeit von Monaten Arbeit weg.</p>
                <p>Kommt jemand zurück, meldet er sich mit demselben Discord-Konto an und findet seine
                Personalakte vor.</p>
                """),
        ]);
}
