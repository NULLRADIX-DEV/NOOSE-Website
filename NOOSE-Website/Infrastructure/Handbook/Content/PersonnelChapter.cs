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
                <strong>Belobigungen</strong> festgehalten - und die <strong>Disziplinarmaßnahmen</strong>. Ein
                Verweis ist hier ein Vermerk der Art <em>Negativer Vermerk</em>; die Bezeichnung lässt sich
                überschreiben, wenn <em>Verweis</em> oder <em>Ermahnung</em> genauer trifft. Welche Maßnahme wer
                verhängen darf, prüft die Seite nicht - das regelt die <em>Dienstverordnung</em> (Menü
                <em>Wissen</em>).</p>
                <p>Dienstliche <strong>Beurteilungen</strong> stehen nicht unter den Vermerken, sondern im eigenen
                Reiter <em>Beurteilungen</em>: ein Titel (vorbelegt mit <em>Beurteilung vom</em> und dem Datum),
                eine Note von 1 (sehr gut) bis 5 (ungenügend) und ein Text. Den Reiter sehen nur der Agent selbst,
                die Führung, die Administration Division und die Aufsicht; eintragen und löschen dürfen Führung und
                Administration Division. Liegt die letzte Beurteilung mehr als vier Wochen zurück, weist der Reiter
                darauf hin.</p>
                <p>Die Akte ist in <strong>Reiter</strong> gegliedert: <em>Dienstaufsicht</em> (vorn, für die
                Führung), <em>Dienstgrad-Verlauf</em>, <em>Vermerke</em>, <em>Beurteilungen</em>, <em>Beförderungen</em>,
                <em>Finanzierung</em>, <em>KI-Kontingent</em>, <em>Module</em>, <em>Auszeichnungen</em>,
                <em>Entführungen</em>, <em>Schusswaffengebrauch</em> und <em>Kommentare</em>. Oben auf der Akte
                stehen die Knöpfe für Dienstausweis, Druckansicht und - wenn jemand geht - <em>Arbeit
                übergeben</em>.</p>
                <p>Sie ist der Führung vorbehalten. Deinen eigenen Stand siehst du in deinem Profil.</p>
                """,
                NavKey: "personal"),

            new Article("art-dienstausweis", "dienstausweis", "Der Dienstausweis",
                "Der gedruckte Ausweis, was draufsteht und wie jemand von außen seine Echtheit prüft.",
                """
                <p>Jeder aktive Agent mit einer <strong>Dienstnummer</strong> von I bis XL hat einen
                <strong>Dienstausweis</strong>. Du druckst ihn in deinem <em>Profil</em> mit
                <em>Dienstausweis drucken</em>; die Führung und die Administration Division finden denselben Knopf
                oben in der <em>Personalakte</em> und drucken ihn für jeden Agenten. Teamleitungen, Partner und
                Konten ohne Dienstnummer haben keinen.</p>
                <p>Auf dem Ausweis stehen dein Lichtbild (das freigegebene Profilbild, sonst deine Initialen), dein
                Name, dein Dienstgrad, deine Abteilung, die Dienstnummer und ein <strong>QR-Code</strong>. Der Name
                ist dein Codename. Der Klarname steht nie darauf, auch nicht auf dem Ausweis, den die Führung für
                dich druckt. Der Ausweis gilt, solange du im Dienst bist; ein Ablaufdatum gibt es nicht.</p>
                <p>Scannt jemand den Code oder tippt die Adresse darunter ab, landet er auf der Prüfseite
                <em>/ausweis/Nummer</em>. Sie sagt nur drei Dinge: ob der Ausweis gültig ist, wie sein Inhaber
                heißt und welchen Rang er hat. Vergleiche beides mit dem Ausweis, den dir jemand zeigt. Ein Ausweis
                eines ausgeschiedenen Agenten gilt dort als ungültig, ohne Namen und Rang.</p>
                <p>Die Prüfseite ist <strong>standardmäßig abgeschaltet</strong>; die Führung schaltet das Modul
                <em>Dienstausweis-Prüfung</em> bei den Einstellungen des öffentlichen Bereichs ein. Sie
                antwortet außerdem nur für Agenten, die sich in ihrem Profil auf die öffentliche Mitarbeiterliste
                gesetzt haben; für alle anderen, gesperrte Konten eingeschlossen, sagt sie dasselbe wie bei einer
                erfundenen Nummer: gibt es nicht.</p>
                """,
                RoleplayHtml: """
                <p>Im Rollenspiel ist der Ausweis das, was du auf Zuruf zeigst. Wer ihn prüfen will, scannt den
                Code in der Spielwelt auf dem Handy; die Seite bestätigt, dass es dich gibt und dass du noch dabei
                bist.</p>
                """),

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
                schon ausgefüllt. Danach öffnet sich <em>Arbeit übergeben</em> (siehe <em>Ausscheiden aus der
                Behörde</em>).</li>
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

            new Article("art-dienstaufsicht", "dienstaufsicht", "Die Dienstaufsicht",
                "Ein Blatt mit allem, was vor einem Beförderungs-, Ermahnungs- oder Kündigungsgespräch zählt.",
                """
                <p>Der erste Reiter der Personalakte heißt <em>Dienstaufsicht</em>. Er sammelt auf einem Blatt, was
                sonst über ein halbes Dutzend Seiten verteilt ist, und stellt jede Zahl neben den
                <strong>Median</strong> aller aktiven Agenten - die Spalte <em>Team</em>. Oben wählst du den
                Zeitraum: <strong>30</strong>, <strong>90</strong> oder <strong>365 Tage</strong>.</p>
                <ul>
                <li><strong>Aktivität</strong> - das letzte Lebenszeichen, die Tage ohne eines (abgemeldete
                ausgenommen, wie im Inaktivitäts-Radar) und die Schreibleistung je Woche.</li>
                <li><strong>Wiedervorlagen</strong> - wie viele im Zeitraum pünktlich, verspätet oder gar nicht
                erledigt wurden.</li>
                <li><strong>Besprechungen</strong> - anwesend, abgemeldet, gefehlt. Es zählen nur Besprechungen,
                deren Anwesenheit abgeschlossen ist.</li>
                <li><strong>Abmeldungen, Punkte, Vermerke</strong> - Abmeldetage, Punkte wie in der Bestenliste,
                Abzeichen, Belobigungen und Disziplinarvermerke.</li>
                <li><strong>Gegenaufklärung</strong> - Zugriffe, verschiedene Akten, Zugriffe zwischen 22 und 6 Uhr
                und welche Regeln bei diesem Agenten anschlagen. Diesen Teil sieht nur die Führung, nicht die
                Nur-Lese-Aufsicht.</li>
                </ul>
                <p>Das Blatt urteilt nicht. Eine Zahl weit unter dem Team ist ein Grund, nachzufragen, kein Befund:
                wer viel im Einsatz ist, schreibt wenig. Für das Gespräch gibt es das Blatt auch auf Papier - die
                <em>Druckansicht</em> der Personalakte nimmt den gewählten Zeitraum mit.</p>
                <p>Die Dienstaufsicht sehen die Führung und die Nur-Lese-Aufsicht, bei jedem Agenten außer
                Bewerbern.</p>
                """),

            new Article("art-wiedervorlagen-rueckstand", "wiedervorlagen-rueckstand", "Der Wiedervorlagen-Rückstand",
                "Welche Wiedervorlagen in der Behörde liegen bleiben - und wie die Führung sie umhängt.",
                """
                <p>Unter <em>Wiedervorlagen-Rückstand</em> im Bereich <em>Verwaltung</em> sieht die Führung alle
                offenen <strong>Wiedervorlagen</strong> der Behörde, nicht nur die eigenen. Zuerst stehen nur die
                <strong>überfälligen</strong> da; schaltest du <em>Nur überfällige</em> aus, kommen die noch
                anstehenden dazu. Sortieren lässt sich nach <em>Zuständig</em>, nach <em>Alter</em> - wie lange sie
                schon überfällig ist - und nach <em>Akte</em>.</p>
                <p>Mit <em>Umhängen</em> gibst du eine Wiedervorlage einem anderen Agenten. Angeboten wird nur, wer
                gerade zugeteilt werden kann, und wer die Akte nicht sehen darf, wird abgelehnt. Der neue Zuständige
                bekommt eine Benachrichtigung. Steht in der Zeile <em>nicht zuteilbar</em>, ist der bisherige
                Zuständige gekündigt, gesperrt oder gar nicht mehr da - dann wartet die Wiedervorlage auf
                niemanden.</p>
                <p>Ist eine Wiedervorlage <strong>sieben Tage</strong> überfällig, bekommt die Führung eine
                Benachrichtigung. Es ist immer dieselbe Meldung, bis du sie gelesen hast, und sie nennt weder Akte
                noch Agent - das steht hier in der Liste, als <em>gemeldet am …</em>. Jede Wiedervorlage meldet sich
                nur einmal; erst ein neues Fälligkeitsdatum setzt das zurück. Im Profil lässt sich die Meldung
                stummschalten.</p>
                <p>Eine Akte, die du selbst nicht sehen darfst, bleibt auch hier unbenannt: Die Zeile zeigt nur
                Zuständigen und Datum, ohne Akte und Notiz, und lässt sich nicht umhängen. Die
                <strong>Nur-Lese-Aufsicht</strong> sieht die Liste, hängt aber nichts um.</p>
                """,
                NavKey: "wiedervorlagen.rueckstand"),

            new Article("art-befoerderung", "befoerderung", "Beförderung",
                "Vom Antrag bis zum neuen Dienstgrad.",
                """
                <p>Eine <strong>Beförderung</strong> beginnt als Antrag: jemand aus der Führung (ab Section
                Chief) schlägt einen Agenten für den nächsten Dienstgrad vor und begründet es. Der Antrag
                steht dann zur Entscheidung.</p>
                <p>Entscheiden darf erst ab <strong>Section Chief</strong>, und nur so weit, wie der Dienstgrad
                reicht: Das <strong>Board</strong> entscheidet für alle Abteilungen. Ein <strong>Section
                Chief</strong> entscheidet nur in seiner eigenen Abteilung, und nur über Dienstgrade bis zum
                Special Agent in Charge; wer Section Chief oder Assistant Director werden soll, den befördert
                nur das Board. Die Schwelle fällt mit der „Führung“ zusammen, bleibt aber an die eigene Abteilung
                gebunden - wer befördert, verändert dauerhaft die Rechte eines Kontos.</p>
                <p>Dieselbe Grenze gilt für jeden anderen Weg, einen Dienstgrad zu vergeben: ein neues Konto
                freigeben, einen Bewerber annehmen oder den Dienstgrad in der Agenten-Verwaltung ändern. Die
                Liste der offenen Anträge, der Zähler im Menü und die Knöpfe zeigen dir nur, was du auch
                entscheiden darfst: dem Section Chief die Anträge seiner Abteilung, dem Board alle.</p>
                <p>Mit der Entscheidung wird der Dienstgrad gesetzt und im Verlauf festgehalten. Der Betroffene
                wird einmal abgemeldet und muss sich neu anmelden, damit die neuen Rechte greifen.</p>
                <p>Die Seite prüft <strong>nicht</strong>, ob der Zielgrad der nächsthöhere ist - sie lässt auch
                einen Sprung oder eine Herabstufung zu. Das ist Absicht, denn die Behördenleitung darf einen
                Agenten auch außer der Reihe einstufen. Ein Section Chief darf das nur innerhalb der Grenze von
                oben.</p>
                <p>Welche Voraussetzungen für einen Dienstgrad gelten, regelt die <em>Dienstverordnung</em> (Menü
                <em>Wissen</em>). Diese Voraussetzungen prüft ein Mensch, nicht die Seite; die Begründung des
                Antrags ist der Ort, an dem sie belegt werden.</p>
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
                eigenen Profil, im Organigramm, in der Bestenliste und einige Wochen lang im Lagezentrum. Nach außen
                gehen sie nicht.</p>
                <p>Eine Ehrung lässt sich korrigieren - dann ändert sich auch der Eintrag am Schwarzen Brett - oder
                <strong>aberkennen</strong>. Eine aberkannte Ehrung bleibt mit dem Grund durchgestrichen und
                ausgegraut in der Personalakte stehen, verschwindet aber aus Profil, Organigramm, Bestenliste und
                Lagezentrum, und ihr Eintrag am Schwarzen Brett wandert in den Papierkorb. Über die eigenen Ehrungen
                entscheidet immer jemand anderes.</p>
                <p>Zu jeder Ehrung gibt es eine <strong>Urkunde</strong> im Querformat, ebenso zu jeder Ernennung
                und Beförderung im Dienstgrad-Verlauf. Drucken dürfen die Führung und der Geehrte selbst. Vor dem
                Druck wählst du, ob der Codename oder der Klarname darauf steht. Unterschrieben ist sie von dem
                Unterzeichner, den die Führung unter <em>Einstellungen → Ehrenzeichen</em> einträgt: einen für die
                ganze Behörde und auf Wunsch je einen für CID, CTD und HRT. Die Urkunde eines Agenten trägt die
                Unterschrift seiner Abteilung, und wo dort keiner steht, die der Behörde. Unterzeichner kann das
                Board sein oder in der eigenen Abteilung ein Section Chief. Eine Ehrungs-Urkunde behält die
                Unterschrift vom Tag der Verleihung.</p>
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
                <p>Abhaken dürfen die <strong>Führung</strong> und die <strong>Administration Division</strong>.
                Das Kennzeichen setzt keinen Führungsrang voraus - ein Special Agent mit diesem Kennzeichen darf
                es also auch.
                Wer ein Modul irrtümlich abgehakt hat, kann den Haken wieder entfernen; der Vorgang steht im
                Protokoll.</p>
                <p>Die <strong>Liste der Module</strong> selbst ist etwas anderes: sie anzulegen, zu ändern oder
                zu löschen bleibt der Führung vorbehalten. Ein gelöschtes Modul nimmt die Haken aller Agenten mit.</p>
                <p>Die Liste wird <strong>leer ausgeliefert</strong>. Welche Module es gibt, entscheidet die
                Führung, damit sie zur geltenden Fassung der <em>Dienstverordnung</em> passt (Menü
                <em>Wissen</em>). Dort steht auch, welche Ausbildung Voraussetzung wofür ist.</p>
                <p>Für den Agenten selbst ist die Liste vor allem eine Orientierung: sie sagt, was von ihm
                erwartet wird, bevor ihn jemand danach fragt.</p>
                """),

            new Article("art-einarbeitung", "einarbeitung-und-probezeit", "Einarbeitung und Probezeit",
                "Pate, Pflichtmodule und Probezeit-Ende für neue Agenten.",
                """
                <p>Wird ein neuer Agent freigegeben oder ein Bewerber hochgestuft, beginnt für ihn von selbst eine
                <strong>Einarbeitung</strong>. Sie besteht aus drei Dingen: den <strong>Pflichtmodulen</strong>,
                einem <strong>Paten</strong> und dem Ende der <strong>Probezeit</strong>.</p>
                <p>Welche Ausbildungsmodule Pflicht sind, legt die Führung in den Einstellungen unter
                <em>Module</em> fest; dort steht auch, wie viele Tage die Probezeit dauert (vorgegeben sind 14).
                Den Paten wählst du gleich bei der Freigabe aus, du kannst das Feld aber auch leer lassen und ihn
                später eintragen. Der Pate bekommt eine Benachrichtigung.</p>
                <p>In der Personalakte, Reiter <em>Module</em>, zeigt eine <strong>Ampel</strong>, wie es steht:
                grün, solange die erledigten Pflichtmodule mit der verstrichenen Zeit Schritt halten, gelb, wenn
                der Agent hinterherhängt, rot, wenn die Probezeit vorbei ist und noch Module offen sind.
                Pate und Frist ändern die Führung und die Administration Division.</p>
                <p>Drei Tage vor dem Ende und noch einmal am Ende erinnert die Glocke den Paten, den Section Chief
                der Abteilung und das Board. Abschließen darf nur die Führung: <em>Bestanden</em> oder
                <em>Nicht bestanden</em>, auf Wunsch mit einem Satz dazu. Das Ergebnis steht danach als Vermerk
                in der Personalakte.</p>
                <p>Agenten, die vor dieser Funktion freigegeben wurden, haben keine Einarbeitung. Für sie startest
                du sie bei Bedarf in der Personalakte von Hand.</p>
                """,
                RoleplayHtml: """
                <p>Der Pate ist der erste Ansprechpartner des Neuen: er nimmt ihn auf Streife mit, zeigt ihm die
                Abläufe und sagt der Führung vor dem Ende der Probezeit, wie er sich macht.</p>
                """),

            new Article("art-interne-tests", "interne-tests", "Interne Tests",
                "Tests für Agenten: zuweisen, ablegen, korrigieren.",
                """
                <p>Unter <em>Interne Tests</em> findest du die Tests, die dir die <strong>Administration
                Division</strong> oder die <strong>Führung</strong> zugewiesen hat. Es ist derselbe Baukasten wie beim Eignungstest der
                Bewerber: Auswahlfragen, Ja/Nein und Freitext, auf Wunsch mit Bearbeitungszeit.</p>
                <p>Hat ein Test eine Bearbeitungszeit, startet die Uhr erst, wenn du auf <em>Test jetzt
                starten</em> klickst. Sie läuft dann durch, auch wenn du das Fenster schließt; bei Ablauf wird
                abgegeben, was du eingetragen hast. Deine Antworten werden laufend gespeichert. Steht eine
                <strong>Frist</strong> dabei, wirst du einen Tag vorher und bei Überschreitung erinnert.</p>
                <p>Nach der Korrektur siehst du deine <strong>Punkte</strong>, den Prozentwert und ob du
                bestanden hast - nicht, welche Frage falsch war. Das Ergebnis steht als Vermerk in deiner
                Personalakte. Ist mit dem Test ein <strong>Ausbildungsmodul</strong> verknüpft, wird es beim
                Bestehen automatisch abgehakt.</p>
                <p>Administration Division und Führung weisen Tests einzelnen Agenten oder einer Gruppe zu (HRT,
                Administration Division, eine Taskforce oder ab einem Dienstgrad). Wer den Test schon offen hat, wird übersprungen. Nach einem nicht
                bestandenen Test kannst du ihn <em>erneut zuweisen</em>; der alte Versuch bleibt sichtbar.
                Den Katalog teilen sich interne Tests und Eignungstests - die <em>Verwendung</em> am Test legt
                fest, wohin er vergeben werden darf.</p>
                """,
                NavKey: "tests"),

            new Article("art-agenten-verwalten", "agenten-verwalten", "Konten freigeben und verwalten",
                "Wer hereinkommt, mit welchem Dienstgrad und welchen Kennzeichen.",
                """
                <p>Unter <em>Agenten-Verwaltung</em> werden neue Konten freigegeben. Ein frisch angemeldetes
                Konto steht auf <strong>ausstehend</strong> und sieht nichts, bis jemand es freigibt und dabei
                Dienstgrad und Abteilung setzt.</p>
                <p>Wer freigeben darf, hängt vom Dienstgrad ab: das Board und Admins geben jeden Dienstgrad in
                jede Abteilung frei, ein Section Chief nur in die eigene Abteilung und nur unterhalb von Section
                Chief.</p>
                <p>Hier werden auch die <strong>Abteilung</strong> und die <strong>Kennzeichen</strong> vergeben.
                Die Abteilung - CID, CTD oder HRT - ändern bei einem bestehenden Agenten nur das Board und
                Admins; das Board selbst ergibt sich aus dem Dienstgrad. Kennzeichen sind Admin, die
                Administration Division und die Nur-Lese-Aufsicht. Sie sind keine Dienstgrade - ein Deputy
                Director muss nicht in der Administration Division sein, und ein Special Agent kann es sein.</p>
                <p>Für <strong>HRT und Administration Division</strong> gilt dabei eine Schwelle: beides lässt
                sich erst ab dem Dienstgrad <strong>Special Agent</strong> setzen. Bei einem Trainee sind die
                beiden Auswahlen deshalb gesperrt. Wird jemand unter diese Schwelle
                herabgestuft, wechselt er aus dem HRT in die CID und verliert die Administration Division; das
                kommt ins Protokoll.</p>
                <p>Die Abteilungen haben <strong>Platzgrenzen</strong>: Board 4, HRT 9, CID 21, CTD 6. Ist eine
                Einheit voll, fragt die Seite ausdrücklich nach, bevor jemand darüber hinaus eingeteilt wird.</p>
                <p>Jede Änderung an Dienstgrad, Kennzeichen oder Status meldet den Betroffenen einmal ab. Das
                ist kein Fehler: die Rechte stecken in seiner Anmeldung, und sie werden erst beim nächsten
                Anmelden neu ausgestellt.</p>
                """,
                NavKey: "admin.agenten"),

            new Article("art-bewerbungen", "bewerbungen", "Bewerbungen",
                "Der Weg von außen in die Behörde.",
                """
                <p>Wer zum FIB will, bewirbt sich über den öffentlichen Bereich. Die Bewerbung durchläuft
                feste Stufen: <em>Eingereicht</em> → <em>Sicherheitsüberprüfung</em> → <em>Test</em> →
                <em>Vorstellungsgespräch</em> → <em>Angenommen</em> oder <em>Abgelehnt</em>.</p>
                <p>Abschicken kann eine Bewerbung nur, wer auf dem Discord-Server des FIB ist; wer es nicht ist, sieht
                im Formular den Einladungslink, den Admins unter <em>Einstellungen → Discord-Direktnachrichten</em>
                pflegen. Antwortet Discord nicht, geht die Bewerbung trotzdem durch.</p>
                <p>Bearbeitet wird sie von der <strong>Administration Division</strong> und der Führung. Es gibt einen
                Schriftwechsel mit dem Bewerber; er sieht dabei nie den Namen des Agenten, der ihm schreibt -
                die Anschreiben sind so gebaut, dass der Name automatisch geschwärzt wird.</p>
                <p>Der Bewerber sieht außerdem einen <strong>vergröberten Status</strong>: Test und
                Vorstellungsgespräch erscheinen ihm als eine Stufe. Sonst wüsste er nach dem Weiterrücken,
                dass er den Test bestanden hat, bevor die Entscheidung gefallen ist.</p>
                <p>Eine angenommene oder abgelehnte Bewerbung holst du mit <em>Wieder öffnen</em> zurück und wählst
                dabei den Schritt, in dem sie weiterläuft. War sie abgelehnt, fällt die 14-Tage-Sperre weg. Die alte
                Entscheidung bleibt im internen Thread stehen. In der Übersicht findest du jede Bewerbung über die
                Suche nach Name, Aktenzeichen, Bearbeiter oder Arbeitgeber.</p>
                <p>Ganz oben in den Stammdaten steht, ob der Bewerber einem <strong>Stream</strong> seiner Bewerbung
                zugestimmt hat. Steht dort <em>Nicht streamen</em>, bleibt die Bearbeitung, auch das Gespräch, aus dem
                Stream heraus. Ältere Bewerbungen zeigen <em>keine Angabe</em>; dann fragst du im Schriftwechsel nach.</p>
                <p>Ist <em>Bewerber per Direktnachricht informieren</em> eingeschaltet (<em>Einstellungen →
                Discord-Direktnachrichten</em>), schreibt der Bot dem Bewerber kurz in Discord: wenn seine Bewerbung
                eingegangen ist, ein Test freigegeben wurde oder abgelaufen ist, eine neue Nachricht da ist und wenn es
                einen neuen Stand gibt. Jede Nachricht ist ein fester Satz mit Link ins Portal. Sie nennt nie einen Namen,
                einen Dienstgrad, ein Ergebnis oder einen Grund, und der neue Stand sagt nicht, welcher es ist.
                Der Schritt vom Test zum Vorstellungsgespräch löst keine Nachricht aus, denn der Bewerber sieht beide
                als eine Stufe. Hat er Direktnachrichten gesperrt, bleibt ihm die Glocke im Portal; im Kanal taucht
                nichts dafür auf.</p>
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
                Bewerbung ausfüllt. Die Fragen pflegt die Administration Division; es gibt Auswahlfragen und Freitextfragen.</p>
                <p>Eine Bewerbung kann <strong>mehrere Tests</strong> bekommen, etwa den Einstellungstest und den
                psychologischen Eignungstest, jeden aber nur einmal. Jeder Test hat eine eigene Uhr, eine eigene
                Korrektur und einen eigenen Knopf im Portal des Bewerbers. Eine Frage mit 0 Punkten, zum Beispiel
                eine Selbsteinschätzung, musst du nicht bewerten.</p>
                <p>Der Test hat eine <strong>Bearbeitungszeit</strong>. Sie beginnt in dem Moment, in dem der
                Bewerber die Fragen zum ersten Mal abruft - nicht, wenn du ihn freigibst. Braucht jemand aus
                gutem Grund länger, kannst du <strong>Zusatzminuten</strong> gewähren, ohne den Test neu zu
                starten.</p>
                <p>Ein <strong>Zurücksetzen</strong> löscht die bisherigen Antworten dieses einen Tests und startet
                seine Zeit neu. Es gibt je Test nur einen laufenden Versuch - sei also sicher, bevor du zurücksetzt.</p>
                <p>Über <strong>Anonym korrigieren</strong> in der Bewerbungsliste korrigierst du abgegebene Tests,
                ohne zu sehen, von wem sie sind. Jede Abgabe trägt nur ein Kürzel; Name und Aktenzeichen
                erscheinen erst, wenn du die Korrektur abgeschlossen hast.</p>
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
                <p>Konten von <strong>Partnerbehörden</strong> sind keine FIB-Agenten. Sie sehen
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
                <p>Eine Einzelfreigabe lässt sich <strong>befristen</strong>: beim Freigeben wählst du
                1 Tag, 7 Tage, 30 Tage, ein Datum oder unbegrenzt. Mit dem Ablauf endet der Zugriff sofort; im
                Verlauf der Akte steht, dass die Freigabe abgelaufen ist. Die Frist lässt sich später
                verlängern oder verkürzen. Wer eine Freigabe beantragt, kann ein Ende vorschlagen.</p>
                <p>Unter <em>Einstellungen → Partnerbehörden</em> steht außerdem, welche Inhalte eine Behörde
                <strong>nie</strong> sieht - zum Beispiel Doks und Befragungen. Das schlägt jede Freigabe,
                auch eine Einzelfreigabe mit „Ganze Akte“.</p>
                <p>Je Behörde lassen sich dort auch <strong>Funktionen</strong> freischalten: die
                FIBI-Rechtsauskunft, Beziehungsgraph, Funkplan, Lageberichte und Anfragen. Die Rechtsauskunft
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
                <p>Das <strong>Organigramm</strong> zeigt den Aufbau der Behörde: oben das <strong>Board of
                Directors</strong> mit seinen Plätzen („besetzt / 4“), darunter CID, CTD und HRT als Spalten,
                jede mit „besetzt / Limit“. Die Administration Division läuft quer durch die Abteilungen, die
                genehmigten Taskforces stehen darunter. Intern zeigt es alle aktiven Agenten mit ihrem
                Codenamen; im HRT stehen die HRT-Titel wie Operator oder Commanding Operator.</p>
                <p>Nach außen gibt es stattdessen die öffentliche Seite <strong>Mitarbeiter</strong>. Dort steht
                jeder aktive Agent mit hinterlegtem Klarnamen, der sich dafür eingeschaltet hat. Die Führung ab
                Section Chief steht obenan als Organigramm, eine Ebene je Dienstgrad, jede Karte mit
                Foto und Telefonnummer. Alle anderen stehen darunter nur mit Klarnamen, ohne Codename, Dienstgrad,
                Einheit, Dienstnummer und Foto. Teamleitungen und Partnerbehörden erscheinen nie.</p>
                <p>Telefonnummer und Foto trägt die Führung in den <em>Einstellungen</em> unter
                <em>Mitarbeiterliste</em> ein; ohne Nummer bleibt die Zeile „Tel.:“ leer, ohne Foto zeigt die Karte
                die Initialen. Das Foto ist eine eigene Kopie, dein Profilbild geht nie nach außen. Wer befördert,
                degradiert oder entlassen wird, wandert nach spätestens einer halben Minute an die richtige Stelle
                oder verschwindet, samt Foto.</p>
                <p>Ob du selbst dort stehst, entscheidest du in deinem <em>Profil</em> unter <em>Öffentliche
                Mitarbeiterliste</em>. Anfangs ist der Schalter aus, du stehst also nicht dort, bis du ihn
                einschaltest; schaltest du ihn wieder aus, fehlst du sofort, mit Karte und Foto. Die Seite verrät
                nicht, dass jemand fehlt. So wirkt die Liste vollständig, und ein Name, der draußen die Runde macht,
                aber dort nicht steht, gilt schnell als erfunden.</p>
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
                <p>Einzahlen darf jeder Agent. Auszahlen darfst du ab <strong>Senior Special Agent</strong>; die
                Auszahlung steht dann immer auf deinem eigenen Namen. Nur die Führung bucht eine Auszahlung für
                jemand anderen ein.</p>
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
                die Auszahlung bucht es ab. Deinen Stand siehst du auf derselben Seite. Wie hoch das Budget ist
                und welche Positionen du beantragen kannst, hängt an deiner Abteilung und deinem Dienstgrad; das
                Board richtet sich nach seiner Heimatabteilung.</p>
                <p>Was du beantragen kannst, steht im <em>Katalog</em> auf dieser Seite. Er wird nicht
                mitgeliefert, sondern von der Führung gepflegt, mit Preis, Zuschussanteil und Mindestdienstgrad
                je Position. Was die Fraktionskasse übernimmt und in welcher Menge, regelt die
                <em>Dienstverordnung</em> (Menü <em>Wissen</em>).</p>
                <p>Mit dem <strong>Gehalt</strong> hat das Monatsbudget nichts zu tun: Es ist ein Rahmen für
                Anträge, kein Lohn. Gehalt zahlt die Seite nicht aus.</p>
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
                <p>Was noch an ihm hängt, übergibt die Führung mit <em>Arbeit übergeben</em> oben in seiner
                Personalakte an Nachfolger. Nach einer Kündigung öffnet sich das Fenster von selbst. Es listet alles
                Offene: seine Wiedervorlagen und Aufgaben, die Vorgänge, Operationen, Taskforces, Fraktionen,
                Personengruppen und Parteien, denen er zugeteilt ist, und die V-Personen, die er führt. Der
                <strong>Nachfolger</strong> oben gilt für alle Zeilen; einzelne Zeilen stellst du auf jemand anderen
                um oder wählst sie ab. Eine Leitung oder Taskforce-Rolle geht mit über, außer der Nachfolger hat
                dort schon eine eigene.</p>
                <p>Zwei Dinge gehen nicht über, sondern enden mit dem Ausscheiden: offene
                <strong>Finanzierungsanträge</strong> werden zurückgezogen oder abgelehnt, weil die Auszahlung an den
                Antragsteller ginge, und seine Teilnahme an künftigen <strong>Terminen</strong> wird gestrichen.
                Einträge in Akten, die du selbst nicht öffnen darfst, zählt das Fenster nur mit und lässt sie, wie
                sie sind. Abgewählte Zeilen erscheinen beim nächsten Öffnen wieder; mit <em>Später</em> schiebst du
                die Übergabe auf. Jeder Nachfolger bekommt eine Meldung, was er übernommen hat.</p>
                <p>Die <strong>Dienstnummer</strong> eines Gekündigten ist sofort wieder frei. Wird die Kündigung
                zurückgenommen und trägt die Nummer inzwischen jemand anderes, wählt der Zurückgekehrte eine neue.</p>
                <p>Kommt jemand zurück, meldet er sich mit demselben Discord-Konto an und findet seine
                Personalakte vor.</p>
                """,
                Steps:
                [
                    new Step("PersonOff", "Kündigung eintragen",
                        "In der Personalakte, mit Grund und Datum. Das Konto verliert sofort jeden Zugriff."),
                    new Step("MoveDown", "Arbeit übergeben",
                        "Das Fenster öffnet sich danach von selbst. Nachfolger oben wählen, einzelne Zeilen umstellen."),
                    new Step("NotificationsActive", "Nachfolger informiert",
                        "Jeder Nachfolger bekommt eine Meldung mit dem, was er übernommen hat."),
                ]),
        ]);
}
