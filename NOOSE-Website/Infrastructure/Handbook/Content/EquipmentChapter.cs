using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter nine: the lookup tables of the concept and the service regulation - kit, pay, radio, cars, buildings.</summary>
internal static class EquipmentChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-ausruestung", "ausruestung-und-einrichtungen", "Ausrüstung & Einrichtungen",
        "Kleidung, Waffen, Gehalt, Fahrzeuge, Funk und das Dienstgebäude - die Listen aus Konzept und Dienstverordnung.",
        "LocalPolice",
        [
            new Article("art-dvo-ausruestung", "dienstkleidung-und-bewaffnung", "Dienstkleidung und Bewaffnung",
                "Was du trägst - und welche Waffe ab welchem Dienstgrad freigegeben ist.",
                """
                <p>Du hältst dich stets an die <strong>Kleidungsvorschrift</strong> (§5.1, Dienstregel 6). Das
                Erscheinungsbild hat jederzeit professionell und der Stellung der Behörde angemessen zu sein.
                Regelungen zu Tarnung, Legendenbildung und verdecktem Auftreten werden gesondert in der
                Tarnausbildung vermittelt und sind strikt einzuhalten. Bei einer Undercover-Ermittlung ist keine
                bestimmte Kleidung vorgeschrieben.</p>
                <p><strong>CID und CTD:</strong> Als Trainee und als Special Agent trägst du einen
                <strong>schwarzen Anzug mit Maske</strong>. Ab dem Special Agent in Charge orientierst du dich am
                Stil <strong>business casual</strong>. Outfits müssen im Discord-Kanal angefragt und von der
                jeweiligen Einheitsleitung genehmigt werden. Jedes Mitglied von CID und CTD <strong>muss</strong>
                einen schwarzen Anzug im Kleiderschrank haben. Für den Senior Special Agent nennt das Konzept
                keine eigene Vorgabe; frag im Zweifel die Einheitsleitung.</p>
                <p><strong>HRT:</strong> Das HRT kann zwischen business casual und taktischen Outfits wechseln.
                Outfits jeder Art müssen im Discord-Kanal angefragt und von der Einheitsleitung genehmigt werden.
                Mischungen aus business casual und Taktik sind nur in bestimmtem Maß erlaubt.</p>
                <p>Für den allgemeinen Dienstgebrauch stehen Handfeuerwaffen zur Verfügung; <strong>Langwaffen
                sind für Großeinsatz- und Schadenslagen</strong> gedacht (§5.2). Die gelb markierte Waffe dient
                ausschließlich der Tarnung - jeder andere Gebrauch hat dienstrechtliche Folgen. Die
                Farbgestaltung aller Waffen ist seriös zu halten; die einzige von der Behördenleitung genehmigte
                Abweichung sind Gold und das Luxus-Waffengehäuse. Waffen sind jederzeit sinnvoll und
                pflichtbewusst zu nutzen (Dienstregel 4).</p>
                <p>Die Freigaben bauen aufeinander auf - jeder Dienstgrad führt mit, was in seiner eigenen Reihe
                darunter freigegeben ist (§5.2.1). Die Tabellen nennen je Stufe nur, was <em>neu</em> dazukommt.
                Was das Konzept nicht nennt, ist nicht freigegeben; frag im Zweifel die Einheitsleitung.</p>
                <p><strong>CID und CTD</strong></p>
                <table>
                <thead><tr><th>Ab Dienstgrad</th><th>Zusätzlich freigegeben</th></tr></thead>
                <tbody>
                <tr><td><strong>Trainee</strong></td><td>Leuchtpistole, Messer, Schlagstock, Taser,
                Gefechtspistole, Fallschirm, Einsatz PDW</td></tr>
                <tr><td><strong>Special Agent</strong></td><td>Karabinergewehr, Bullpup-Gewehr, Schwerer
                Revolver</td></tr>
                <tr><td><strong>Senior Special Agent</strong></td><td>Tactical-SMG</td></tr>
                <tr><td><strong>Special Agent in Charge</strong></td><td>Spezialkarabiner, Schwere
                Schrotflinte</td></tr>
                <tr><td><strong>Section Chief</strong></td><td>Schweres Scharfschützengewehr, Pump
                Schrotflinte</td></tr>
                </tbody>
                </table>
                <p><strong>HRT</strong></p>
                <table>
                <thead><tr><th>Ab Titel</th><th>Zusätzlich freigegeben</th></tr></thead>
                <tbody>
                <tr><td><strong>Operator</strong></td><td>Tactical-SMG, Spezialkarabiner, Dienstgewehr,
                Kampfschrotflinte</td></tr>
                <tr><td><strong>Senior Operator</strong></td><td>Militär-Gewehr, CombatMG (nur bei DEFCON 1 und
                2)</td></tr>
                <tr><td><strong>Leading Operator</strong></td><td>Schweres Scharfschützengewehr,
                Kampfgewehr</td></tr>
                <tr><td><strong>Commanding Operator</strong></td><td>Volle Bewaffnung</td></tr>
                </tbody>
                </table>
                <p><strong>Board of Directors:</strong> Assistant Director, Deputy Director und Director führen
                die volle Bewaffnung. Das Gehalt je Dienstgrad steht unter
                <em>Ausrüstung &amp; Einrichtungen → Gehalt je Dienstgrad</em>.</p>
                """),

            new Article("art-dvo-gehalt", "gehalt", "Gehalt je Dienstgrad",
                "Das monatliche Gehalt laut Konzept - und was die Seite damit nicht tut.",
                """
                <p>Das Konzept legt für jeden Dienstgrad ein <strong>monatliches Gehalt</strong> fest. Es hängt am
                Dienstgrad, nicht an der Abteilung: ein Operator im HRT bekommt dasselbe wie ein Special Agent.</p>
                <table>
                <thead><tr><th>Dienstgrad</th><th>Titel im HRT</th><th>Gehalt im Monat</th></tr></thead>
                <tbody>
                <tr><td><strong>Trainee</strong></td><td>-</td><td>4.000 USD</td></tr>
                <tr><td><strong>Special Agent</strong></td><td>Operator</td><td>4.500 USD</td></tr>
                <tr><td><strong>Senior Special Agent</strong></td><td>Senior Operator</td><td>4.750 USD</td></tr>
                <tr><td><strong>Special Agent in Charge</strong></td><td>Leading Operator</td><td>5.000
                USD</td></tr>
                <tr><td><strong>Section Chief</strong></td><td>Commanding Operator</td><td>5.250 USD</td></tr>
                <tr><td><strong>Assistant Director</strong></td><td>-</td><td>5.500 USD</td></tr>
                <tr><td><strong>Deputy Director</strong></td><td>-</td><td>5.750 USD</td></tr>
                <tr><td><strong>Director</strong></td><td>-</td><td>6.000 USD</td></tr>
                </tbody>
                </table>
                <p>Die Seite <strong>zahlt kein Gehalt aus</strong>. Die Tabelle steht hier zum Nachschlagen,
                ausgezahlt wird im Spiel. Mit dem <strong>Monatsbudget</strong> der Finanzierungen hat das Gehalt
                nichts zu tun: das ist ein Rahmen für Ausrüstungsanträge, kein Lohn.</p>
                """),

            new Article("art-dvo-funk", "funkverkehr", "Funkverkehr",
                "Funkcodes, Funkdisziplin, Frequenzen - und die Dienstnummer.",
                """
                <p>Diese Codes gelten im Funkverkehr (§6.1):</p>
                <table>
                <thead><tr><th>Funkcode</th><th>Bedeutung</th></tr></thead>
                <tbody>
                <tr><td>10-1</td><td>Anmeldung im Funkkreis</td></tr>
                <tr><td>10-12</td><td>Abmeldung aus dem Funkkreis</td></tr>
                <tr><td>10-1 alpha</td><td>Anmeldung zum Streifendienst</td></tr>
                <tr><td>10-12 alpha</td><td>Abmeldung vom Streifendienst</td></tr>
                <tr><td>10-2</td><td>Funkspruch wiederholen</td></tr>
                <tr><td>10-3</td><td>Negativ - letzten Funkspruch verneinen</td></tr>
                <tr><td>10-4</td><td>Positiv - letzten Funkspruch bejahen</td></tr>
                <tr><td>10-6 delta</td><td>Unterstützung / Anfahrt eines Einsatzes ankündigen</td></tr>
                <tr><td>10-20</td><td>Standortangabe oder -abfrage</td></tr>
                <tr><td>10-10</td><td>Verkehrsunfall</td></tr>
                <tr><td>10-60</td><td>Medizinisches Personal anfordern oder angefordert</td></tr>
                <tr><td>10-80</td><td>Verfolgungsjagd</td></tr>
                <tr><td>10-90</td><td>Verkehrskontrolle</td></tr>
                <tr><td>Code 1</td><td>Einsatzfahrt ohne Licht &amp; Sirene</td></tr>
                <tr><td>Code 2</td><td>Einsatzfahrt mit Licht, ohne Sirene</td></tr>
                <tr><td>Code 3</td><td>Einsatzfahrt mit Licht &amp; Sirene - höchste Eile geboten</td></tr>
                <tr><td>Code 4</td><td>Einsatz beendet, keine weitere Unterstützung benötigt</td></tr>
                <tr><td>Code 7</td><td>Pause</td></tr>
                <tr><td>Code 10</td><td>Unterstützungseinheit benötigt, keine imminente Gefahr</td></tr>
                <tr><td>Code 66</td><td>Letzter Funkspruch revidiert, Panic-Button ungültig</td></tr>
                <tr><td>11-99 / Code 99</td><td>Panic Button - Beamter in imminenter Gefahr</td></tr>
                </tbody>
                </table>
                <p><strong>Funkdisziplin</strong> (§6.1.1): deutliche, dialektfreie Sprache in normaler
                Stimmlage und mittlerer Lautstärke. Störgeräusche und Musikübertragungen sind zu unterlassen,
                das Senden von Funkfeuer ist in jeder Situation strengstens untersagt. Ein Funkspruch erfolgt
                erst, wenn der Kanal frei ist. Nur in lebensbedrohlichen Situationen oder bei vorrangigen
                Informationen darfst du unterbrechen - mit dem Wort <strong>„Break“</strong>.</p>
                <p><strong>Eigene Frequenzen:</strong> Hauptfunkfrequenz 400 MHz, Ausweichfrequenzen 401, 402
                und 403 MHz.</p>
                <p><strong>Überbehördliche Frequenzen:</strong></p>
                <ul>
                <li>Los Santos Police Department: Haupt 150, Einsatz 151, Ausweich 152, weitere Ausweich 238,
                239, 240 und 241 MHz</li>
                <li>Department of Justice: 500 MHz</li>
                <li>San Andreas Medical Department: 102 MHz</li>
                <li>National Guard: 666 MHz, Ausweich 667 MHz</li>
                <li>Parlament: 600 MHz</li>
                </ul>
                <p><strong>Funkpflicht:</strong> Während des operativen Dienstes hältst du dich dauerhaft im
                eigenen Funkkanal auf und bist dort erreichbar. Ein anderer Kanal ist nur zulässig, soweit die
                Aufgabe es erfordert - etwa zum Mithören oder zur Zusammenarbeit mit anderen Stellen. Wirst du
                dienstlich zum Wechsel zurück aufgefordert, kommst du dem unverzüglich nach. Ausgenommen sind
                die Behördenleitung, die Führungsebene und das Hostage Rescue Team bei Einsatz-, Taktik- und
                Situationsübungen.</p>
                <p><strong>Dienstnummer und Codename:</strong> Jedes Mitglied bekommt eine eigene
                <strong>Dienstnummer</strong> für die interne Identifikation, vor allem im Funk. Sie steht in
                deinem Profil und in der Personalakte. Nach außen - überbehördlich oder öffentlich - wird
                ausschließlich mit zuvor intern dokumentierten <strong>Codenamen</strong> gearbeitet, damit sich
                die tatsächliche Identität nicht zurückverfolgen lässt.</p>
                """),

            new Article("art-dvo-fahrzeuge", "dienstfahrzeuge", "Dienstfahrzeuge",
                "Zustand, Farbe, Freigaben je Dienstgrad und DEFCON - und wann ein Tarnfahrzeug erlaubt ist.",
                """
                <p>Bei hoheitlichen Aufgaben nutzt du die bereitgestellten <strong>Dienstfahrzeuge</strong>
                (§7.1). Für ermittlungstechnische oder verdeckte Maßnahmen darfst du zivile Fahrzeuge oder
                Fahrzeuge Dritter führen, soweit es dem operativen Zweck dient.</p>
                <p>Jedes eingesetzte Fahrzeug ist technisch einwandfrei, betriebsbereit und gepflegt zu halten.
                Dazu gehören ein <strong>Mindesttankstand von 50 %</strong>, die Verkehrstauglichkeit und die
                umgehende Behebung relevanter Schäden. Dienstfahrzeuge sind gegen unbefugten Zugriff zu sichern
                und dürfen Dritten ohne ausdrückliche Genehmigung weder zugänglich gemacht noch überlassen
                werden.</p>
                <p>Es gilt die Straßenverkehrsordnung; Abweichungen sind nur im Rahmen konkreter Einsatzlagen
                zulässig. Fahrzeuge werden <strong>nur in Herstellerfarbe</strong> gefahren (Dienstregel 5);
                Modifikationen haben der Seriosität der Behörde zu entsprechen.</p>
                <p>Welche Fahrzeuge zu welchem Dienstgrad gehören, legt das Konzept fest. Die Freigaben bauen
                aufeinander auf - jeder Dienstgrad fährt mit, was in seiner eigenen Reihe darunter freigegeben
                ist. Die Tabellen nennen je Stufe nur, was <em>neu</em> dazukommt, außer beim Operator: dort
                steht die ganze Liste.</p>
                <p><strong>CID und CTD</strong></p>
                <table>
                <thead><tr><th>Ab Dienstgrad</th><th>Fahrzeuge</th></tr></thead>
                <tbody>
                <tr><td><strong>Trainee</strong></td><td>Granger, Buffalo, Vapid Torrence, Schafter,
                Manchez</td></tr>
                <tr><td><strong>Special Agent</strong></td><td>Baller, Yosemite, Vigero</td></tr>
                <tr><td><strong>Senior Special Agent</strong></td><td>Toros (nur CID), Dominator, Jugular</td></tr>
                <tr><td><strong>Special Agent in Charge</strong></td><td>Aleutian</td></tr>
                <tr><td><strong>Section Chief</strong></td><td>Gauntlet</td></tr>
                </tbody>
                </table>
                <p><strong>HRT</strong></p>
                <table>
                <thead><tr><th>Ab Titel</th><th>Fahrzeuge</th></tr></thead>
                <tbody>
                <tr><td><strong>Operator</strong></td><td>Granger, Buffalo, Manchez, Baller, Vapid Torrence,
                Dominator, Yosemite, Bearcat, Schafter, Vigero, Jugular</td></tr>
                <tr><td><strong>Senior Operator</strong></td><td>Aleutian</td></tr>
                <tr><td><strong>Leading Operator</strong></td><td>keine zusätzlichen Fahrzeuge</td></tr>
                <tr><td><strong>Commanding Operator</strong></td><td>Gauntlet</td></tr>
                </tbody>
                </table>
                <p><strong>Board of Directors:</strong> Director, Deputy Director und Assistant Director fahren
                alle Fahrzeuge; beim Assistant Director nennt das Konzept den Rebla GTS zusätzlich.</p>
                <p><strong>Fahrzeuge je DEFCON-Stufe:</strong> Für die Gefahrenlage nennt das Konzept zusätzliche
                Fahrzeuge. Die Stufe setzt die Führung; sie steht als DEFCON auf der öffentlichen Seite
                <em>Lage</em>. DEFCON 5 ist die niedrigste Stufe („Niedrig“), DEFCON 1 die höchste („Kritisch“),
                dazwischen liegen DEFCON 4 („Erhöht“), 3 („Hoch“) und 2 („Schwer“). Die Fahrzeuge stehen
                ab dem Dienstgrad Special Agent bereit, soweit nichts anderes dabeisteht:</p>
                <table>
                <thead><tr><th>Stufe</th><th>Fahrzeuge</th></tr></thead>
                <tbody>
                <tr><td><strong>DEFCON 5</strong></td><td>Buzzard</td></tr>
                <tr><td><strong>DEFCON 4</strong></td><td>riot2, Conada und - nur für HRT und Board -
                Insurgent</td></tr>
                <tr><td><strong>DEFCON 3</strong></td><td>Annihilator</td></tr>
                <tr><td><strong>DEFCON 2</strong></td><td>keine genannt</td></tr>
                <tr><td><strong>DEFCON 1</strong></td><td>Dingi5</td></tr>
                </tbody>
                </table>
                <p><strong>Tarnfahrzeuge</strong> (§7.2) dürfen ausschließlich im Zuge einer Tarnung geführt
                werden, und das Fahrzeug muss zu Rang und Zweck der Tarnaufgabe passen. Unberechtigtes Führen
                hat dienstrechtliche Folgen. Das Konzept führt sie als bereits vorhanden und gibt sie
                <strong>ab dem Dienstgrad Special Agent</strong> frei:</p>
                <ul>
                <li>SAMS: RTW, KTW</li>
                <li>LSPD: Alamo, Drafter, Seminole, Sentinel</li>
                <li>National Guard: MP Buffalo, MP Terminus</li>
                <li>Weazel News: News Van</li>
                </ul>
                <p>Darüber hinaus nennt die Dienstverordnung den Helikopter des SAMS mit Wärmebild und den
                Maverick von Weazel News.</p>
                """),

            new Article("art-dvo-gebaeude", "dienstgebaeude", "Dienstgebäude und Einrichtungen",
                "Wo die Behörde arbeitet - und wer davon überhaupt wissen darf.",
                """
                <p>Sämtliche Räumlichkeiten, Anlagen und technischen Einrichtungen sind pfleglich,
                zweckgebunden und unter Einhaltung der Sicherheitsvorschriften zu nutzen (§9.0).</p>
                <p>Der <strong>FIB-Tower</strong> (§9.1) ist der offizielle Hauptsitz und zugleich Führungs-,
                Verwaltungs- und Einsatzeinrichtung. Der Wechsel des Gebäudemodells im Spiel (MLO-Wechsel)
                erfolgt erst, nachdem die Konzeptänderung durchgeführt ist; bis dahin kann das Haus im Spiel noch
                anders aussehen. Bürger und andere Staatsbeamte dürfen von der Einrichtung wissen, das Gebäude
                aber nur mit ausdrücklicher Genehmigung eines <strong>Special Agent oder höher</strong>
                betreten.</p>
                <p><strong>Bunker of the Federal Investigation Bureau</strong> (§9.2) ist die geheime
                Sicherheits- und Ausweicheinrichtung: Ort der Grundeinweisungen, Tarnausbildungen und
                Sonderunterweisungen. Von seiner Existenz dürfen ausschließlich Angehörige der Behörde und
                Mitglieder des Parlamentspräsidiums wissen. Im Serverraum führt ein verborgener Zugang hinter
                einem Serverrack zu zwei weiteren Bereichen:</p>
                <ul>
                <li><strong>Bunker of the International Affairs Agency</strong> (§9.2.1) - streng geheime
                Operationen, dazu Büro- und Arbeitsbereiche für Beratung und operative Planung.</li>
                <li><strong>Data Room</strong> (§9.2.2) - primär zur Weiterbildung des HRT, nutzbar aber für
                alle Agenten. Hier stehen die zentralen Server der Behörde.</li>
                </ul>
                <p><strong>Blacksite</strong> (§9.3): geheime Einrichtung für besonders sensible Operationen und
                die Bearbeitung von Tatverdächtigen. Zwei Einzelhaftzellen, ein Meetingraum, ein Großraumbüro;
                der Innenbereich gilt als militärisches Sperrgebiet.</p>
                <p><strong>Harbor Container Facility</strong> (§9.4): ein als Überseecontainer getarnter Zugang
                im Hafen, dahinter ein streng geheimer Anhörungsraum für besonders sensible Tatverdächtige. Der
                Zugang ist <strong>ausschließlich ab Special Agent in Charge</strong> genehmigt. Die
                Einrichtung dient außerdem als Schutzort bei Angriffslagen auf hochrangige Staatsbeamte.</p>
                <p><strong>Catacombs</strong> (§9.5): ein weitläufiges unterirdisches Gangsystem mit Büro,
                Verhörraum und Meetingraum. Die Zugänge sind <em>nicht</em> gesichert und auch von Zivilpersonen
                begehbar - deshalb sind eigene Sicherungs- und Überwachungsmaßnahmen zwingend, und jede Nutzung
                ist als Verschlusssache zu behandeln.</p>
                <p><strong>Rockford Hills Garage</strong> (§9.6): ein kleiner, verschlossener Rückzugsort;
                Existenz und Zweck sind absolut geheim zu halten.</p>
                <p><strong>Bunker of the National Guard</strong> (§9.7): militärisch gesicherte Anlage der
                National Guard, die die Behörde mitnutzen darf. Da sie nicht in unserem Eigentum steht, gilt
                besondere Zurückhaltung: es dürfen keinerlei Rückschlüsse auf unsere Nutzung, Anwesenheit oder
                Tätigkeit möglich sein - auch nicht gegenüber Angehörigen der National Guard.</p>
                <p><strong>Mission Row Police Department</strong> (§9.8): polizeiliche Einrichtung der
                Metropolitan Division, nutzbar im Rahmen der Zusammenarbeit - für Einsatzvor- und
                -nachbesprechungen und zur offiziellen Abhandlung von Tatverdächtigen im Zellenkomplex.</p>
                <p>Standorte, Zugangsdaten und Postleitzahlen werden bewusst <strong>nicht</strong> schriftlich
                festgehalten (§9.9). Ist dir eine Einrichtung oder ein Ablauf unbekannt, ist die Rückfrage bei
                der Führungsebene verpflichtend.</p>
                <p>Im Parlaments- oder Militärdienst sind die Einrichtungen grundsätzlich nicht zu betreten
                (§9.10). Ausgenommen sind der FIB-Tower als bekannter Hauptsitz, der Bunker der National Guard im
                Militärdienst und das Mission Row Police Department im Rahmen der Zusammenarbeit.</p>
                """),

            new Article("art-dvo-dienstmittel", "erstattungsfaehige-dienstmittel",
                "Erstattungsfähige Dienstmittel",
                "Was die Fraktionskasse übernimmt - mit den Höchstmengen.",
                """
                <p>Dienstlich benötigte Mittel können nach Anfrage bei der Führungsebene über die
                <strong>Fraktionskasse</strong> finanziert werden; der Antrag läuft über diese Seite (§8.0). In
                der Regel erstattungsfähig sind:</p>
                <table>
                <thead><tr><th>Position</th><th>Höchstmenge</th></tr></thead>
                <tbody>
                <tr><td>Jailbreak-Handy und Funkgerät</td><td>-</td></tr>
                <tr><td>Dienstfahrzeuge inklusive Full-Tuning (rein, also ohne Sonderausstattung oder
                Lackierung)</td><td>-</td></tr>
                <tr><td>Dienstliche Schuss- und Stichwaffen sowie Gasgranaten</td><td>-</td></tr>
                <tr><td>Fallschirme</td><td>-</td></tr>
                <tr><td>Westen</td><td>3</td></tr>
                <tr><td>Medkits</td><td>3</td></tr>
                <tr><td>Magazine je Waffe</td><td>10</td></tr>
                <tr><td>Zyanid</td><td>1</td></tr>
                <tr><td>Wahrheitsserum</td><td>4</td></tr>
                <tr><td>Beruhigungsspritze</td><td>4</td></tr>
                <tr><td>Adrenalin</td><td>2</td></tr>
                <tr><td>Chemikalien zur Herstellung der vier vorgenannten Produkte</td><td>nur für die oben
                genannten Mengen</td></tr>
                </tbody>
                </table>
                <p><strong>Ausnahmen</strong> (§8.1): Im Rahmen laufender Infiltrationen oder großangelegter
                Einsätze wie Taskforces können in begründeten Fällen auch weitere Mittel finanziert werden -
                zusätzliche Waffen, Fahrzeuge oder sonstiges einsatzrelevantes Material. Das genehmigt
                ausschließlich die Führungsebene, und es ist im Einzelfall zu dokumentieren.</p>
                <p>Auf der Seite steht diese Liste als <em>Katalog</em> unter <em>Finanzierungen</em>. Er wird
                nicht mitgeliefert, sondern von der Führung gepflegt - inklusive Preis, Zuschussanteil und
                Mindestdienstgrad je Position.</p>
                """),
        ]);
}
