using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;
using Step = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededStep;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter eight: the FIB concept and the service regulation in short form, paragraph references kept.</summary>
internal static class DutyRegulationChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-dienstvorschrift", "dienstvorschrift", "Dienstvorschrift",
        "Konzept und Dienstverordnung in Kurzform: Dienstregeln, Freigaben, Dienstgrade, Abteilungen, Beförderung, Sanktionen und Regeldienst.",
        "Policy",
        [
            new Article("art-dvo-dienstverordnung", "dienstverordnung", "Die Dienstverordnung",
                "Die verbindliche Grundlage des Dienstes - und was dieses Kapitel daraus macht.",
                """
                <p>Die <strong>Dienstverordnung</strong> legt fest, unter welchen Bedingungen du Dienst tust:
                wer was darf, wie du auftrittst, was du tragen und fahren darfst, und was geschieht, wenn du
                dich nicht daran hältst. Sie ist keine Empfehlung - Verstöße ziehen dienstrechtliche Folgen bis
                zur sofortigen Entbindung vom Dienst nach sich.</p>
                <p>Neben ihr steht das <strong>Konzept des FIB</strong>: Dienstgrade, Abteilungen, Bewaffnung,
                Gehalt, Dienstregeln, Kleidung und Fahrzeuge. Die Dienstverordnung wirkt <strong>ergänzend zum
                Konzept</strong> (Dienstregel 7). Wo beide dasselbe regeln, geht das Konzept vor.</p>
                <p>Dieses Kapitel ist die <strong>Kurzfassung</strong> von beidem. Es steht hier, damit du eine
                Regel in demselben Werkzeug nachschlagen kannst, in dem du arbeitest - über die Handbuchsuche und
                die Erklär-Blasen des Glossars. Der vollständige Text der Dienstverordnung liegt als Dokument in
                der <em>Dokumenten-Bibliothek</em>.</p>
                <p>Die Paragrafen sind überall mitangegeben (§2.4.1 und so weiter), damit du jede Regel im
                vollen Text wiederfindest. Der Text der Dienstverordnung stammt aus der Zeit vor dem Umbau zum
                FIB und kann noch die alten Dienstgrade und Einheiten nennen; maßgeblich sind dann die
                Dienstgrade und Abteilungen aus diesem Kapitel.</p>
                <p>Widersprechen sich Kurzfassung und Dienstverordnung, <strong>gilt die
                Dienstverordnung</strong> - es sei denn, das Konzept regelt den Punkt anders. Melde den
                Widerspruch, statt dich auf das Handbuch zu berufen.</p>
                """),

            new Article("art-dvo-dienstregeln", "dienstregeln", "Die sieben Dienstregeln",
                "Was das Konzept jedem Agenten auferlegt - und wo es dazu mehr gibt.",
                """
                <p>Das Konzept des FIB nennt sieben <strong>Dienstregeln</strong>. Sie gelten für jeden Agenten,
                in jeder Abteilung:</p>
                <ol>
                <li><strong>Professionell auftreten.</strong> Jeder Agent tritt jederzeit professionell auf
                (§3.1).</li>
                <li><strong>Schusswaffengebrauch dokumentieren.</strong> Wer in der CID oder der CTD dient und in
                einer Einsatzlage eine Schusswaffe einsetzt, hält das auf dieser Seite fest. Ausgenommen sind
                der Special Agent in Charge und alle Dienstgrade darüber. Die Regel soll Unverhältnismäßigkeit
                vermeiden und das Konzept wahren: CID und CTD ermitteln, sie sind keine Kampfeinheit. Wie die
                Meldung funktioniert, steht unter <em>Dienstvorschrift → Schusswaffengebrauch melden</em>.</li>
                <li><strong>Verschwiegenheit.</strong> Du bist gegenüber Unbefugten zur Verschwiegenheit
                verpflichtet (§3.3).</li>
                <li><strong>Waffen sinnvoll nutzen.</strong> Waffen sind jederzeit sinnvoll und pflichtbewusst zu
                nutzen. Welche Waffe zu welchem Dienstgrad gehört, steht unter
                <em>Ausrüstung &amp; Einrichtungen → Dienstkleidung und Bewaffnung</em>.</li>
                <li><strong>Herstellerfarbe.</strong> Fahrzeuge dürfen nur in Herstellerfarbe gefahren werden.
                Mehr dazu unter <em>Ausrüstung &amp; Einrichtungen → Dienstfahrzeuge</em>.</li>
                <li><strong>Kleidungsvorschrift.</strong> Du hältst dich stets an die Kleidungsvorschrift. Sie
                steht unter <em>Ausrüstung &amp; Einrichtungen → Dienstkleidung und Bewaffnung</em>.</li>
                <li><strong>Die Dienstverordnung wirkt ergänzend zum Konzept.</strong> Sie füllt aus, was das
                Konzept offenlässt; wo beide dasselbe regeln, geht das Konzept vor.</li>
                </ol>
                <p>Ein Verstoß gegen eine Dienstregel ist ein Dienstverstoß und wird nach §3.7 geahndet - die
                Stufen stehen unter <em>Dienstvorschrift → Sanktionsstufen</em>.</p>
                """),

            new Article("art-dvo-schusswaffengebrauch", "schusswaffengebrauch", "Schusswaffengebrauch melden",
                "Dienstregel 2: wer wann meldet, wie der Knopf funktioniert und wer die Meldungen sieht.",
                """
                <p>Dienstregel 2 des Konzepts verpflichtet die Mitglieder der <strong>CID</strong> und der
                <strong>CTD</strong>, den Gebrauch einer Schusswaffe in Einsatzlagen <strong>auf dieser Seite zu
                dokumentieren</strong>. <strong>Ausgenommen sind der Special Agent in Charge und alle Dienstgrade
                darüber.</strong> Die Regel hält das Konzept ein: CID und CTD ermitteln, sie sind keine
                Kampfeinheit, und jeder Schuss soll sich erklären lassen. Melden kann jeder interne Agent; Pflicht
                ist es für alle in CID und CTD unterhalb des Special Agent in Charge.</p>
                <p>In der Kopfzeile sitzt neben der <em>Schnellerfassung</em> ein eigener Knopf für den
                Schusswaffengebrauch. Ein Klick öffnet einen kleinen Dialog mit einem Feld für die
                <strong>Begründung</strong>. Zwei, drei Sätze genügen: wie war die Lage, warum war der Gebrauch
                nötig, was ist daraus geworden. Gespeichert werden dein Name, der Zeitpunkt und die Begründung.
                Ein angefangener Text bleibt als Entwurf erhalten, falls du den Dialog schließt, und verschwindet
                nach dem Speichern.</p>
                <p>Trag die Meldung ein, sobald die Lage vorbei ist, solange du dich noch genau erinnerst.</p>
                <p>Wer die Meldungen sieht, ist bewusst eng gefasst:</p>
                <ul>
                <li>Die Übersicht <em>Schusswaffengebrauch</em> unter <em>Verwaltung · Führung</em> zeigt alle
                Meldungen der Führung und der Aufsicht, filterbar nach Agent, Abteilung und Zeitraum.</li>
                <li>Deine eigenen Meldungen stehen in einem eigenen Abschnitt deiner Personalakte. Bei anderen
                Agenten zeigt die Akte den Abschnitt nur der Führung.</li>
                <li>Bei einer neuen Meldung bekommen die Führung deiner Abteilung (ab Section Chief)
                und das Board eine Nachricht in der Glocke, ohne den Text deiner Begründung.</li>
                <li>Nach außen geht nichts. Partnerbehörden sehen keine Meldung, und sie erscheint auf keiner
                öffentlichen Seite.</li>
                </ul>
                <p>Eine Meldung löschen darf nur die Führung, nie dein eigener Eintrag; sie landet im
                <em>Papierkorb</em> und lässt sich von dort zurückholen. Wer eine Meldung vergessen hat, trägt sie
                nach - mit der Begründung, warum erst jetzt.</p>
                """,
                NavKey: "schusswaffengebrauch",
                Steps:
                [
                    new Step("Add", "Knopf in der Kopfzeile",
                        "Neben der Schnellerfassung auf den Knopf für den Schusswaffengebrauch klicken."),
                    new Step("Edit", "Begründung schreiben",
                        "Lage, Anlass und Ergebnis in zwei, drei Sätzen. Der Zeitpunkt wird mit gespeichert."),
                    new Step("Save", "Speichern",
                        "Die Meldung steht danach in deiner Personalakte und in der Übersicht der Führung."),
                ]),

            new Article("art-dvo-freigaben", "sicherheitsfreigaben", "Sicherheitsfreigaben",
                "Die vier Stufen der Dienstverordnung - und wie sie sich zu den VS-Stufen dieser Seite verhalten.",
                """
                <p>Der Zugang zu sicherheitsrelevanten Informationen läuft über
                <strong>Sicherheitsfreigaben</strong>. Sie werden nach zwei Prinzipien zugleich vergeben (§1.1):
                dem <strong>Rangprinzip</strong> - was dein Dienstgrad mitbringt - und dem
                <strong>Fachprinzip</strong> - was dein Einsatzbereich nötig macht. Das Fachprinzip kann eine
                Freigabe situationsbedingt anheben.</p>
                <p>Es gibt vier Stufen. Wem sie nach dem Rangprinzip zufallen, ist für die acht Dienstgrade des
                FIB so festgelegt:</p>
                <ul>
                <li><strong>Confidential</strong> - die Grundstufe für interne Informationen, für alle Agenten
                vom Trainee an.</li>
                <li><strong>Secret</strong> - operative Inhalte, laufende Maßnahmen, alle Lageberichte und
                sensible Ermittlungsansätze. Nach dem Rangprinzip ab dem Section Chief, also mit dem
                Beginn der Führung.</li>
                <li><strong>Top Secret</strong> - Stufe 5, dem Board of Directors vorbehalten: Assistant
                Director, Deputy Director und Director.</li>
                <li><strong>Special Access Program</strong> - Stufe 6 und keine gewöhnliche Freigabe, sondern
                eine Zusatzstufe für Ermittlungen, die über die reguläre Zuständigkeit der
                Generalstaatsanwaltschaft hinausgehen und nochmals abgeschirmt behandelt werden.</li>
                </ul>
                <p><strong>Diese Leiter ist nicht die VS-Stufe dieser Seite.</strong> Die Seite kennt
                <em>Offen</em>, <em>Führung</em>, <em>HRT</em> und <em>Administration</em>. Das sind vier
                Sichtbarkeiten, keine Freigabestufen: die Dienstverordnung staffelt nach Rang, die Seite nach Rang
                <em>und</em> Abteilung.</p>
                <p>Als Faustregel: <em>Führung</em> auf der Seite deckt ungefähr Secret und darüber ab,
                <em>HRT</em> und <em>Administration</em> sind das Fachprinzip in Reinform. Für das <em>Special
                Access Program</em> gibt es auf der Seite absichtlich keine Entsprechung - solche Vorgänge gehören
                nicht in eine Akte, die jemand versehentlich öffnet.</p>
                <p>Zum Koordinationsrecht des FIB, das die Zusammenarbeit mit anderen Behörden regelt, siehe
                <em>Dienstvorschrift → Zusammenarbeit mit anderen Behörden</em>. Die Gesetzbücher des FIB pflegt
                die Führung unter <em>Gesetze</em>.</p>
                """,
                DiagramKey: "sicherheitsfreigaben"),

            new Article("art-dvo-dienstgrade", "dienstgrade", "Dienstgrade",
                "Die acht Grade, die Titel im HRT und was die Dienstvorschrift an jeden von ihnen knüpft.",
                """
                <p>Das FIB kennt acht Dienstgrade (§2.1). Sie sind nicht bloß eine Reihenfolge - an jedem hängen
                Befugnisse, Freigaben und Ausrüstung. Im <strong>Hostage Rescue Team</strong> tragen die vier
                mittleren Stufen eigene Titel:</p>
                <table>
                <thead><tr><th>Dienstgrad</th><th>Titel im HRT</th><th>Was daran hängt</th></tr></thead>
                <tbody>
                <tr><td><strong>Trainee</strong></td><td>-</td><td>Einstieg in CID oder CTD. Grundeinweisung und
                Tarnausbildung vor dem ersten Regeldienst, dazu die Fortbildung „(Erweiterte)
                Verhörmethoden“</td></tr>
                <tr><td><strong>Special Agent</strong></td><td>Operator</td><td>HRT und Administration Division
                stehen offen. Darf allein in Undercover-Ermittlungen gehen und Besucher in das Dienstgebäude lassen
                (§9.1)</td></tr>
                <tr><td><strong>Senior Special Agent</strong></td><td>Senior Operator</td><td>Darf die
                Sanktionsstufen 1 und 2 verhängen. Auf der Seite: höchste Einstufung vergeben, selbst
                veröffentlichen und aus der Kasse auszahlen</td></tr>
                <tr><td><strong>Special Agent in Charge</strong></td><td>Leading Operator</td><td>Sanktionsstufen
                3 bis 6. Zutritt zur Harbor Container Facility. Von der Meldepflicht für Schusswaffengebrauch
                und von den Beschränkungen der Einsatzbegleitung ausgenommen</td></tr>
                <tr><td><strong>Section Chief</strong></td><td>Commanding Operator</td><td>Beginn der
                Führung, Freigabe Secret, Leitung einer Abteilung. Stellt Beförderungsanträge, entscheidet in
                der eigenen Abteilung über Beförderungen, vergibt dort Dienstgrade bis zum Special Agent in
                Charge und unterschreibt die Urkunden der Abteilung</td></tr>
                <tr><td><strong>Assistant Director</strong></td><td>-</td><td>Mitglied des Board of Directors.
                Freigabe Top Secret. Entscheidet für alle Abteilungen über Beförderungen</td></tr>
                <tr><td><strong>Deputy Director</strong></td><td>-</td><td>Board of Directors, Stellvertretung des
                Directors. Freigabe Top Secret</td></tr>
                <tr><td><strong>Director</strong></td><td>-</td><td>Höchster Dienstgrad, Leitung der Behörde.
                Freigabe Top Secret</td></tr>
                </tbody>
                </table>
                <p>Das <strong>Board of Directors</strong> sind der Director, der Deputy Director und die Assistant
                Directors; es hat vier Plätze. Die Titel im HRT gelten nur im Haus: in der Personalliste, im
                Profil, im Organigramm und in der Kopfzeile. Alles, was die Behörde verlässt - Briefe an Bewerber,
                öffentliche Seiten, Urkunden -, trägt den Dienstgrad ohne Abteilungstitel.</p>
                <p>Der Behördenleitung steht es jederzeit frei, einen beliebigen Agenten in einen beliebigen
                Dienstgrad zu setzen (§2.4.4) - die Kriterien der Beförderung binden sie nicht.</p>
                <p>Welche Waffen, welches Gehalt und welche Fahrzeuge zu welchem Dienstgrad gehören, steht im
                Kapitel <em>Ausrüstung &amp; Einrichtungen</em>.</p>
                """),

            new Article("art-dvo-abteilungen", "abteilungen", "Abteilungen und Board",
                "Drei Abteilungen, das Board und die Administration Division - mit Plätzen und Zugang.",
                """
                <p>Das FIB gliedert sich in das <strong>Board of Directors</strong> und drei Abteilungen (§2.2).
                Dazu kommt die <strong>Administration Division</strong>: Sie ist keine eigene Abteilung, sondern
                eine Aufgabe, die Mitglieder der anderen Abteilungen mittragen.</p>
                <table>
                <thead><tr><th>Einheit</th><th>Plätze</th><th>Wer gehört dazu</th></tr></thead>
                <tbody>
                <tr><td><strong>Board of Directors</strong></td><td>4</td><td>Director, Deputy Director und die
                Assistant Directors</td></tr>
                <tr><td><strong>Hostage Rescue Team (HRT)</strong></td><td>9</td><td>Ab Special Agent</td></tr>
                <tr><td><strong>Criminal Investigation Division (CID)</strong></td><td>21</td><td>Alle
                Dienstgrade, vom Trainee an</td></tr>
                <tr><td><strong>Counterterrorism Division (CTD)</strong></td><td>6</td><td>Alle Dienstgrade, vom
                Trainee an</td></tr>
                <tr><td><strong>Administration Division</strong></td><td>kein festes Limit</td><td>Ab Special
                Agent, zusätzlich zur eigenen Abteilung</td></tr>
                </tbody>
                </table>
                <p>Jeder Agent gehört <strong>genau einer</strong> der drei Abteilungen an: CID, CTD oder HRT. Sie
                schließen einander aus - wer im HRT dient, ist nicht zugleich in der CID. Das HRT beginnt erst ab
                dem Dienstgrad Special Agent (§2.4.1); darunter ist die CID oder die CTD die Heimat.</p>
                <p>Das <strong>Board</strong> ergibt sich allein aus dem Dienstgrad: Wer Assistant Director oder
                höher ist, sitzt im Board. Seine bisherige Abteilung merkt sich die Seite trotzdem; wird jemand
                später wieder unter den Assistant Director gestuft, kehrt er dorthin zurück.</p>
                <p>Die <strong>Administration Division</strong> ist ein zusätzliches Kennzeichen am Konto, das es
                ebenfalls erst ab Special Agent gibt. Wer es trägt, behält seine Abteilung. Die Aufgaben stehen
                unter <em>Dienstvorschrift → Aufgabenverteilung</em>. Auf der Seite darf, wer sie trägt,
                Bewerbungen bearbeiten, Tests zuweisen, Ausbildungsmodule abhaken und das Handbuch pflegen.</p>
                <p>Die Plätze sind ein <strong>weiches Limit</strong>. Die Seite zählt mit und zeigt im
                Organigramm je Einheit „besetzt / Limit“. Ist eine Abteilung voll, fragt sie ausdrücklich nach,
                bevor jemand einen Platz darüber hinaus bekommt, und prüft diese Bestätigung selbst. Möglich
                ist das also, aber nie aus Versehen.</p>
                <p>Die Abteilung eines bestehenden Agenten ändern nur <strong>das Board und Admins</strong>, in der
                Agenten-Verwaltung. Ein neues Konto gibt das Board oder ein Admin in jede Abteilung frei, ein
                Section Chief nur in die eigene. Wird
                jemand unter Special Agent herabgestuft, wechselt er aus dem HRT in die CID und verliert die
                Administration Division.</p>
                """),

            new Article("art-dvo-aufgaben", "aufgabenverteilung", "Aufgabenverteilung",
                "Was Administration Division, HRT, CID und CTD jeweils verantworten.",
                """
                <p>Das Konzept verteilt die Aufgaben so auf die Einheiten:</p>
                <p><strong>Administration Division</strong></p>
                <ul>
                <li>Personalgewinnung</li>
                <li>Personalverwaltung</li>
                <li>Disziplinarwesen</li>
                <li>Zuarbeit zu den Direktoren</li>
                <li>Öffentlichkeitsarbeit</li>
                <li>Behördenübergreifende Kommunikation</li>
                <li>Ticketbearbeitung und Weitergabe an die entsprechende Abteilung</li>
                <li>Querschnittsstelle Parlament</li>
                <li>Veröffentlichung von Behördenankündigungen</li>
                </ul>
                <p><strong>Hostage Rescue Team (HRT)</strong></p>
                <ul>
                <li>Geiselbefreiung</li>
                <li>Terrorismusbekämpfung (Auftrag durch die CTD)</li>
                <li>Schnelle Eingreiftruppe</li>
                <li>Einsatzkoordination bei Großeinsätzen</li>
                <li>Evakuierung hoher Staatsbeamter bei Gefahrenlagen</li>
                <li>Unterstützung des LSPD</li>
                <li>Überwachung und Absicherung bei bestimmten Anlässen</li>
                <li>Spezialaufträge</li>
                </ul>
                <p><strong>Criminal Investigation Division (CID)</strong></p>
                <ul>
                <li>Ermittlungen gegen organisierte Kriminalität</li>
                <li>Forensische Analyse</li>
                <li>Korruption</li>
                <li>Ausarbeitung des „National Threat Assessments“ (zum Beispiel die Lageberichte)</li>
                </ul>
                <p><strong>Counterterrorism Division (CTD)</strong></p>
                <ul>
                <li>Terrorismus: Bombendrohungen und Attentatsdrohungen</li>
                <li>Entführungen</li>
                </ul>
                <p>Das Board of Directors hat keine eigene Aufgabenliste; es führt die Behörde. Wo Plätze und
                Zugang zu den Einheiten geregelt sind, steht unter <em>Dienstvorschrift → Abteilungen und
                Board</em>.</p>
                """),

            new Article("art-dvo-ausbildungen", "aus-und-fortbildungen", "Aus- und Fortbildungen",
                "Was du absolviert haben musst - und in welcher Reihenfolge.",
                """
                <p>Vor dem erstmaligen Antritt des Regeldienstes sind <strong>Grundeinweisung</strong> und
                <strong>Tarnausbildung</strong> erfolgreich zu absolvieren (§2.3). Ohne sie beginnt der Dienst
                nicht.</p>
                <p>Im Dienstgrad des Trainee kommt die Fortbildung <strong>„(Erweiterte)
                Verhörmethoden“</strong> hinzu. Ihr Abschluss ist Voraussetzung für die Beförderung zum Special
                Agent.</p>
                <p>Die <strong>Drohnenausbildung</strong> berechtigt zur Verwendung der Aufklärungsdrohne und ist
                für die Beförderung zum Special Agent verpflichtend. Wer die Drohne ohne diese Berechtigung oder
                entgegen den Bestimmungen einsetzt, begeht eine Dienstpflichtverletzung.</p>
                <p>Ab dem Dienstgrad Special Agent stehen die beiden Spezialisierungen offen: die
                <strong>Spezialisierungsausbildung des Hostage Rescue Team</strong> und die
                <strong>Ausbildung für die Administration Division</strong>. Erst sie machen dich zum Teil der
                jeweiligen Einheit.</p>
                <p>Auf der Seite werden diese Ausbildungen als <strong>Ausbildungsmodule</strong> in der
                Personalakte geführt und einzeln abgehakt. Die Liste der Module wird nicht mitgeliefert - die
                Führung legt sie unter <em>Einstellungen</em> an, damit sie zur jeweils geltenden Fassung der
                Dienstverordnung passt.</p>
                """),

            new Article("art-dvo-befoerderung", "befoerderungskriterien", "Beförderungskriterien",
                "Wer entscheidet - und was für jeden Schritt nach oben verlangt wird.",
                """
                <p>Das Verfahren - Antrag, Entscheidung, neue Anmeldung - steht im Kapitel
                <em>Personal &amp; Führung</em>. Hier stehen <strong>wer entscheidet</strong> und die
                <strong>Voraussetzungen</strong>, die die Dienstverordnung in §2.4 an jeden Schritt knüpft.</p>
                <p><strong>Wer entscheidet:</strong> Über Beförderungen entscheidet das <strong>Board</strong>,
                für alle Abteilungen. Ein <strong>Section Chief</strong> entscheidet innerhalb seiner eigenen
                Abteilung und befördert dort bis zum Special Agent in Charge. Die Schritte zum Section Chief und
                ins Board bleiben beim Board.</p>
                <p><strong>Zum Special Agent</strong> (§2.4.1): abgeschlossene Drohnenausbildung, dazu gute
                Aktivität, saubere Aktenarbeit und einschlägige Ermittlungsergebnisse. Ab hier stehen das HRT und
                die Administration Division offen.</p>
                <p><strong>Zum Senior Special Agent</strong> (§2.4.2): vorbildliche Aktivität, makellose
                Aktenarbeit, lückenlose Dokumentation von Geschehnissen und nachweisbare Führungsqualitäten.
                Zwingend sind außerdem <strong>aktive Mitarbeit in der eigenen Abteilung</strong> und eine
                <strong>abgeschlossene Infiltration</strong>. Auch wer alles erfüllt, hat keinen Anspruch: die
                Posten sind begrenzt.</p>
                <p><strong>Zum Special Agent in Charge</strong> (§2.4.3): Vorausgesetzt werden unentbehrliche Dienste für die
                Abteilung sowie das vollste Vertrauen der Behördenleitung.</p>
                <p><strong>Zum Section Chief:</strong> Ab hier beginnt die Führung. Der Section Chief leitet eine
                Abteilung. Die Leitung muss frei sein, und vorausgesetzt wird das vollste Vertrauen des Boards.</p>
                <p><strong>Ins Board</strong> (Assistant Director und darüber): Das Board hat vier Plätze; wer
                hineinkommt, bestimmt das Board selbst.</p>
                <p><strong>Außerordentlich</strong> (§2.4.4): Der Behördenleitung steht es jederzeit frei, einen
                beliebigen Agenten in einen beliebigen Dienstgrad zu setzen. Deshalb prüft die Seite bei einem
                Beförderungsantrag auch nicht, ob der Zielgrad der nächsthöhere ist.</p>
                """),

            new Article("art-dvo-verhalten", "verhalten-im-dienst", "Verhalten im Dienst",
                "Auftreten, Kollegen, Verschwiegenheit - und das Gebot des Eigenschutzes.",
                """
                <p><strong>Auftreten</strong> (§3.1): Als Repräsentant der Behörde trittst du gegenüber allen
                Personen äußerst respektvoll, freundlich und selbstsicher auf. Erwartet wird ein seriöses und
                gepflegtes Erscheinungsbild. Das deckt sich mit Dienstregel 1: jederzeit professionell.</p>
                <p><strong>Kollegen</strong> (§3.2): Jedem Kollegen ist stets mit Respekt entgegenzutreten -
                unabhängig von Dienstalter und Dienstgrad.</p>
                <p><strong>Verschwiegenheit</strong> (§3.3): Alle Informationen, an die du allein durch deine
                Stellung gelangst, sind nur für den Dienstgebrauch bestimmt und dürfen unter keinen Umständen an
                unbeteiligte Dritte weitergegeben werden. Ausgenommen ist die durch Dienstanweisung vorgesehene
                Zusammenarbeit mit anderen staatlichen Behörden. Das ist Dienstregel 3.</p>
                <p><strong>Öffentliche Stellungnahmen</strong> (§3.4): Im Namen der Behörde spricht
                ausschließlich die Behördenleitung oder wer von ihr ausdrücklich befugt wurde. Das gilt auch für
                alles, was du auf den öffentlichen Seiten dieser Anwendung veröffentlichst.</p>
                <p><strong>Waffen und Asservate</strong> (§3.5): Sichergestellte Waffen, waffenähnliche
                Gegenstände und sonstige Asservate sind unverzüglich der Asservatenkammer zuzuführen -
                Zwischenlagerungen sind unzulässig. Jedes Asservat ist zudem umgehend auf dieser Seite
                einzutragen.</p>
                <p><strong>Eigenschutz</strong> (§4.1): Deine eigene Sicherheit und die deiner Kollegen ist das
                oberste Gut. Handlungen, die sie fahrlässig oder vorsätzlich riskieren, sind strengstens
                untersagt.</p>
                <p>Die sieben Dienstregeln des Konzepts stehen unter <em>Dienstvorschrift → Die sieben
                Dienstregeln</em>.</p>
                """),

            new Article("art-dvo-weisung", "weisungsbefugnis", "Weisungsbefugnis",
                "Wer wem etwas sagen darf - und wann der Dienstgrad nicht zählt.",
                """
                <p>Die <strong>Weisungsbefugnis</strong> richtet sich grundsätzlich nach der Rangordnung der
                Dienstgrade (§3.6). Dienstliche Anordnungen sind entsprechend verbindlich umzusetzen.</p>
                <p>Übergeordnet steht das <strong>Parlamentspräsidium</strong>: der Vorsitz des Bundesrates und
                der Speaker; in dessen Abwesenheit der stellvertretende Speaker.</p>
                <p>Es gibt eine wichtige Ausnahme. In besonderen Einsatzkonstellationen - vor allem in einer
                eingesetzten <strong>Taskforce</strong> - ist die jeweils ernannte Einsatz- oder Gesamtleitung
                weisungsbefugt. Diese Befugnis gilt <strong>unabhängig vom regulären Dienstgrad</strong> der
                Beteiligten und für die Dauer der Maßnahme.</p>
                <p>Im Klartext: In einer Taskforce kann ein Special Agent einem Deputy Director Anweisungen
                geben, wenn er die Einsatzleitung hat. Die Seite bildet das nicht ab - sie kennt nur Dienstgrade
                und Zuteilungen. Wer die Leitung hat, gehört deshalb in die Beschreibung der Taskforce.</p>
                """),

            new Article("art-dvo-sanktionen", "sanktionsstufen", "Sanktionsstufen",
                "Sechs Stufen, jede mit eigener Schwelle - und wo sie festgehalten werden.",
                """
                <p>Disziplinarmaßnahmen richten sich nach §27 des Landesbeamtengesetzes und werden nach Schwere
                des Verstoßes gestuft (§3.7). Jede Stufe darf erst ab einem bestimmten Dienstgrad verhängt
                werden:</p>
                <table>
                <thead><tr><th>Stufe</th><th>Maßnahme</th><th>Zulässig ab</th></tr></thead>
                <tbody>
                <tr><td>1</td><td>Mündlicher oder schriftlicher Verweis bei geringfügigen oder erstmaligen
                Verstößen; Eintrag als neutraler oder negativer Vermerk in die Personalakte</td><td>Senior
                Special Agent</td></tr>
                <tr><td>2</td><td>Ermahnung, Verwarnung oder Schulungsverpflichtung bei wiederholten oder
                schwerwiegenden Verstößen</td><td>Senior Special Agent</td></tr>
                <tr><td>3</td><td>Entzug von Sonderrechten oder Befugnissen, etwa Ausrüstungs- oder
                Fahrzeugfreigaben</td><td>Special Agent in Charge</td></tr>
                <tr><td>4</td><td>Temporäre Suspendierung bis zur abschließenden Klärung des
                Sachverhalts</td><td>Special Agent in Charge</td></tr>
                <tr><td>5</td><td>Degradierung, also Herabstufung im Dienstgrad</td><td>Special Agent in
                Charge</td></tr>
                <tr><td>6</td><td>Entlassung: sofortige und dauerhafte Entbindung vom Dienst</td><td>Special
                Agent in Charge</td></tr>
                </tbody>
                </table>
                <p>Vor jeder Maßnahme hat der betroffene Agent das <strong>Recht auf Stellungnahme</strong>. Bei
                schwerwiegendem Fehlverhalten, das die Behörde oder ihre Mitglieder unmittelbar gefährdet, ist
                eine vorläufige Suspendierung ohne vorherige Anhörung zulässig (§3.7.1).</p>
                <p>Das Disziplinarwesen gehört zu den Aufgaben der Administration Division. Wer eine Maßnahme
                verhängen darf, bestimmt aber allein der Dienstgrad aus der Tabelle.</p>
                <p>Festgehalten wird das in der <strong>Personalakte</strong>, im Abschnitt <em>Vermerke</em>.
                Ein Verweis ist dort ein Vermerk der Art <em>Negativer Vermerk</em>; die Bezeichnung lässt sich
                überschreiben, wenn <em>Verweis</em> oder <em>Ermahnung</em> genauer trifft.</p>
                """),

            new Article("art-dvo-regeldienst", "regeldienst", "Ermittlung, Undercover, Infiltration",
                "Der Kern des Dienstes - und wo die Genehmigungspflicht beginnt.",
                """
                <p>Die <strong>Ermittlung</strong> ist der Kern des Regeldienstes (§4.2): Zielpersonen werden
                identifiziert, beobachtet und analysiert, Informationen systematisch ausgewertet, Beweismittel
                gesichert und operative Maßnahmen vorbereitet.</p>
                <p><strong>Taskforces</strong> (§4.3) werden bei komplexen Verfahren oder besonderen
                Gefährdungslagen eingerichtet. Die Führungsebene wählt die Mitglieder nach Fachkompetenz aus; die
                Taskforce arbeitet auf einen klar umrissenen Auftrag hin und wird danach aufgelöst oder in den
                regulären Ermittlungsdienst überführt.</p>
                <p><strong>Undercover-Tätigkeiten</strong> (§4.4) gehören zum regulären Ermittlungsdienst. Sie
                dienen der verdeckten Informationsgewinnung, der unauffälligen Beobachtung und der diskreten
                Kontaktaufnahme. Wer allein gehen darf und was dabei gilt, steht unter
                <em>Dienstvorschrift → Undercover-Ermittlungen</em>.</p>
                <p>Die <strong>Infiltration</strong> (§4.5) ist etwas anderes: das verdeckte Eindringen in
                kriminelle oder sicherheitsrelevante Strukturen zur langfristigen Informationsgewinnung. Sie
                erfolgt <strong>ausschließlich nach interner Genehmigung</strong> und unter strenger
                Risikoabwägung. Eine abgeschlossene Infiltration ist zugleich Voraussetzung für die Beförderung
                zum Senior Special Agent.</p>
                """),

            new Article("art-dvo-undercover", "undercover-ermittlungen", "Undercover-Ermittlungen",
                "Wer allein gehen darf - und was dabei nicht gilt.",
                """
                <p>Mitglieder des FIB können <strong>ab dem Dienstgrad Special Agent allein</strong> in
                Undercover-Ermittlungen gehen. Bei solchen Ermittlungen gilt:</p>
                <ul>
                <li><strong>Keine bestimmte Kleidung.</strong> Die Kleidungsvorschrift ist ausgesetzt; wie du
                auftrittst, bestimmt die Tarnung.</li>
                <li><strong>Keine Dienstwaffen.</strong> Du nutzt sie nicht.</li>
                <li><strong>Keine Dienstfahrzeuge.</strong> Für verdeckte Maßnahmen darfst du zivile Fahrzeuge
                oder Fahrzeuge Dritter führen (§7.1); Tarnfahrzeuge nur, wenn sie zur Tarnung passen.</li>
                </ul>
                <p>Vermittelt werden Tarnung, Legendenbildung und verdecktes Auftreten in der
                <strong>Tarnausbildung</strong> (§2.3); ohne sie beginnt der Dienst nicht. Das langfristige
                verdeckte Eindringen in eine Struktur ist die <strong>Infiltration</strong> und braucht eine
                interne Genehmigung - siehe <em>Dienstvorschrift → Ermittlung, Undercover, Infiltration</em>.</p>
                """),

            new Article("art-dvo-zusammenarbeit", "behoerdliche-zusammenarbeit",
                "Zusammenarbeit mit anderen Behörden",
                "Koordinationsrecht, operative Unabhängigkeit - und wann ein Einsatzbericht fällig wird.",
                """
                <p>Die Behörde arbeitet mit sämtlichen staatlichen Stellen zusammen und pflegt einen
                lagebezogenen Informationsaustausch unter Beachtung der Geheimhaltungsstufen (§4.6). Bei
                Sachverhalten mit Einfluss auf die nationale Sicherheit steht ihr das
                <strong>Koordinationsrecht</strong> gegenüber allen Behörden zu; betroffene Stellen sind dann zur
                Zusammenarbeit verpflichtet.</p>
                <p><strong>Operative Unabhängigkeit</strong> (§4.6.1): Agenten nehmen operative Tätigkeiten
                grundsätzlich eigenständig wahr. Gemeinsame Streifen-, Transport- oder Einsatzfahrten mit
                Angehörigen anderer Behörden finden nicht statt. Ausnahmen ordnet allein die Führungsebene an -
                allgemein für bestimmte Lagen oder im Einzelfall für namentlich benannte Agenten.</p>
                <p><strong>Einsatzlagen anderer Behörden</strong> (§4.6.2): Die eigenständige und unaufgeforderte
                Begleitung fremder Einsatzlagen, insbesondere des Los Santos Police Department, ist untersagt.
                Kurzfristig anfahren darfst du sie nur, um mögliche Zielpersonen, Observationsobjekte oder andere
                Ermittlungsansätze festzustellen, die in die Zuständigkeit der Behörde fallen. Ergibt sich kein
                konkreter dienstlicher Bezug, verlässt du den Ort unverzüglich.</p>
                <p>Ausdrücklich untersagt ist die Begleitung aus persönlichem Interesse, zur bloßen Beobachtung,
                aus mangelnder eigener Auslastung oder bei Lagen mit geringem Gefährdungspotenzial - etwa
                ATM-Raube oder gewöhnliche Fahrzeugverfolgungen.</p>
                <p>Nimmst du dennoch an einer fremden Einsatzlage teil, ist darüber <strong>unverzüglich ein
                Einsatzbericht auf dieser Seite anzufertigen</strong>, nach der behördeninternen Vorlage. Das ist
                eine <em>Operation</em> im Bereich <em>Ermittlung</em>. Ausgenommen von dieser Pflicht und von
                den Beschränkungen sind die Behördenleitung und alle ab Special Agent in Charge.</p>
                <p>Die Missachtung dieser Vorschrift ist ein schwerer Dienstverstoß und wird nach §3.7 geahndet;
                wiederholte oder besonders schwere Verstöße können die sofortige Entlassung nach sich
                ziehen.</p>
                """,
                Steps:
                [
                    new Step("Visibility", "Dienstlichen Bezug prüfen",
                        "Gibt es eine Zielperson, ein Observationsobjekt, einen Ermittlungsansatz? Wenn nicht, wieder abfahren."),
                    new Step("Assignment", "Operation anlegen",
                        "Ermittlung → Operationen → Neu. Ort, Zeit und die eigene Beteiligung eintragen."),
                    new Step("Description", "Ergebnis nachtragen",
                        "Was war die Lage, was hast du festgestellt, was folgt daraus für unsere Akten."),
                ]),
        ]);
}
