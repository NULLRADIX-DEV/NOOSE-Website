using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter nine: the lookup tables of the service regulation - kit, radio, cars, buildings.</summary>
internal static class EquipmentChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-ausruestung", "ausruestung-und-einrichtungen", "Ausrüstung & Einrichtungen",
        "Kleidung, Waffen, Fahrzeuge, Funk und die Dienstgebäude - die Listen aus der Dienstverordnung.",
        "LocalPolice",
        [
            new Article("art-dvo-ausruestung", "dienstkleidung-und-bewaffnung", "Dienstkleidung und Bewaffnung",
                "Was du trägst - und welche Waffe ab welchem Dienstgrad freigegeben ist.",
                """
                <p>Im regulären Dienstbetrieb trägst du <strong>formelle Dienstkleidung</strong>: in der Regel
                einen Anzug oder vergleichbar seriöse Bekleidung wie Hemd oder Rollkragenpullover (§5.1). Das
                Erscheinungsbild hat jederzeit professionell und der Stellung der Behörde angemessen zu sein. In
                operativen Lagen ist die bereitgestellte <strong>Einsatzkleidung</strong> verbindlich.</p>
                <p>Regelungen zu Tarnung, Legendenbildung und verdecktem Auftreten werden gesondert in der
                Tarnausbildung vermittelt und sind strikt einzuhalten.</p>
                <p>Für den allgemeinen Dienstgebrauch stehen Handfeuerwaffen zur Verfügung; <strong>Langwaffen
                sind für Großeinsatz- und Schadenslagen</strong> gedacht (§5.2). Die gelb markierte Waffe dient
                ausschließlich der Tarnung - jeder andere Gebrauch hat dienstrechtliche Folgen. Die
                Farbgestaltung aller Waffen ist seriös zu halten; die einzige von der Behördenleitung genehmigte
                Abweichung sind Gold und das Luxus-Waffengehäuse.</p>
                <p>Die Freigaben bauen aufeinander auf - jeder Dienstgrad führt alles darunter mit (§5.2.1):</p>
                <table>
                <thead><tr><th>Ab Dienstgrad</th><th>Zusätzlich freigegeben</th></tr></thead>
                <tbody>
                <tr><td><strong>Junior Agent</strong></td><td>Fallschirm, Taschenlampe, Schlagstock, Taser,
                Messer, Schwere Pistole, SMG, Taktische SMG, Karabinergewehr, Bullpup Gewehr</td></tr>
                <tr><td><strong>Special Agent</strong></td><td>Pump Shotgun, Dienstgewehr</td></tr>
                <tr><td><strong>Senior Special Agent</strong></td><td>Spezialkarabiner, Schwere Schrotflinte,
                Scharfschützengewehr, Tränengas</td></tr>
                <tr><td><strong>Supervisory Special Agent</strong></td><td>Kampfgewehr</td></tr>
                </tbody>
                </table>
                <p>Zur Stufe des Special Agent führt die Dienstverordnung zusätzlich den Vermerk <em>nur während
                der Tarnung als METRO-Mitglied</em>. Auf welche der beiden Waffen er sich bezieht, geht aus dem
                Text nicht eindeutig hervor - frag im Zweifel die Führungsebene, bevor du dich darauf
                stützt.</p>
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
                die Behördenleitung, die Führungsebene und die Tactical Response Unit bei Einsatz-, Taktik- und
                Situationsübungen.</p>
                <p><strong>Dienstnummer und Codename:</strong> Jedes Mitglied bekommt eine eigene
                <strong>Dienstnummer</strong> für die interne Identifikation, vor allem im Funk. Sie steht in
                deinem Profil und in der Personalakte. Nach außen - überbehördlich oder öffentlich - wird
                ausschließlich mit zuvor intern dokumentierten <strong>Codenamen</strong> gearbeitet, damit sich
                die tatsächliche Identität nicht zurückverfolgen lässt.</p>
                """),

            new Article("art-dvo-fahrzeuge", "dienstfahrzeuge", "Dienstfahrzeuge",
                "Zustand, Lackierung, Freigaben - und wann ein Tarnfahrzeug erlaubt ist.",
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
                zulässig. Bei sichtbarer FIB-Lackierung sind ausschließlich Modifikationen erlaubt, die der
                Seriosität der Behörde entsprechen. Dienstfahrzeuge werden grundsätzlich nur in
                <strong>schwarzer oder dunkelgrauer</strong> Lackierung geführt; alles andere braucht die
                Genehmigung der zuständigen Leitung.</p>
                <p>Die Freigaben sind gestaffelt:</p>
                <table>
                <thead><tr><th>Stufe</th><th>Fahrzeuge</th></tr></thead>
                <tbody>
                <tr><td><strong>1</strong></td><td>Schafter, Baller, Granger, Buffalo, Torrence, Boot
                <em>Dinghy</em></td></tr>
                <tr><td><strong>2</strong></td><td>Manchez, Centurion (im öffentlichen Verkehr nur für
                TRU-Mitglieder in entsprechender Kleidung), Yosemite, Annis Electric, Dominator, Helikopter
                <em>Conada</em>, Helikopter <em>Buzzard</em>, Boot <em>Unterseeboot</em></td></tr>
                <tr><td><strong>3</strong></td><td>Vigero, Jugular</td></tr>
                <tr><td><strong>4</strong></td><td>Gauntlet</td></tr>
                </tbody>
                </table>
                <p>Die Dienstverordnung vermerkt dazu <em>verfügbar ab dem Dienstgrad des Special Agent</em>,
                ohne eindeutig zu sagen, ob sich das auf die ganze Liste oder auf eine einzelne Stufe bezieht.
                Kläre das mit der Führungsebene, bevor du dich auf eine Stufe berufst.</p>
                <p><strong>Tarnfahrzeuge</strong> (§7.2) dürfen ausschließlich im Zuge einer Tarnung geführt
                werden, und das Fahrzeug muss zu Rang und Zweck der Tarnaufgabe passen. Unberechtigtes Führen
                hat dienstrechtliche Folgen.</p>
                <ul>
                <li>SAMS: RTW, KTW, Helikopter mit Wärmebild</li>
                <li>LSPD: Alamo (HD), Drafter (HD), Seminole (METRO), Sentinel (SAHP)</li>
                <li>National Guard: Buffalo (MP), Terminus (MP)</li>
                <li>Weazel News: Alamo, Maverick</li>
                </ul>
                """),

            new Article("art-dvo-gebaeude", "dienstgebaeude", "Dienstgebäude und Einrichtungen",
                "Wo die Behörde arbeitet - und wer davon überhaupt wissen darf.",
                """
                <p>Sämtliche Räumlichkeiten, Anlagen und technischen Einrichtungen sind pfleglich,
                zweckgebunden und unter Einhaltung der Sicherheitsvorschriften zu nutzen (§9.0).</p>
                <p><strong>Galileo Observatory - Government Facility</strong> (§9.1) ist der offizielle
                Hauptsitz und zugleich Führungs-, Verwaltungs- und Einsatzeinrichtung. Das Gelände gilt als
                militärisches Sperrgebiet. Bürger und andere Staatsbeamte dürfen von der Einrichtung wissen, das
                Gebäude aber nur mit ausdrücklicher Genehmigung eines <strong>Special Agent oder höher</strong>
                betreten. Alle unterirdischen Bereiche, Sicherheitsanlagen, Fluchttunnel und Zellenbereiche
                unterliegen der höchsten Geheimhaltungsstufe; wer in sicherheitsrelevante Bereiche verbracht
                wird, wird über den Fluchttunnel transportiert.</p>
                <p><strong>Bunker of the Federal Investigation Bureau</strong> (§9.2) ist die geheime
                Sicherheits- und Ausweicheinrichtung: Ort der Grundeinweisungen, Tarnausbildungen und
                Sonderunterweisungen. Von seiner Existenz dürfen ausschließlich Angehörige der Behörde und
                Mitglieder des Parlamentspräsidiums wissen. Im Serverraum führt ein verborgener Zugang hinter
                einem Serverrack zu zwei weiteren Bereichen:</p>
                <ul>
                <li><strong>Bunker of the International Affairs Agency</strong> (§9.2.1) - streng geheime
                Operationen, dazu Büro- und Arbeitsbereiche für Beratung und operative Planung.</li>
                <li><strong>Data Room</strong> (§9.2.2) - primär zur Weiterbildung der TRU, nutzbar aber für
                alle Agenten. Hier stehen die zentralen Server der Behörde.</li>
                </ul>
                <p><strong>Blacksite</strong> (§9.3): geheime Einrichtung für besonders sensible Operationen und
                die Bearbeitung von Tatverdächtigen. Zwei Einzelhaftzellen, ein Meetingraum, ein Großraumbüro;
                der Innenbereich gilt als militärisches Sperrgebiet.</p>
                <p><strong>Harbor Container Facility</strong> (§9.4): ein als Überseecontainer getarnter Zugang
                im Hafen, dahinter ein streng geheimer Anhörungsraum für besonders sensible Tatverdächtige. Der
                Zugang ist <strong>ausschließlich ab Supervisory Special Agent</strong> genehmigt. Die
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
                (§9.10). Ausgenommen sind das Galileo Observatory als bekannter Hauptsitz, der Bunker der
                National Guard im Militärdienst und das Mission Row Police Department im Rahmen der
                Zusammenarbeit.</p>
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
