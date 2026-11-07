using Article = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededArticle;
using Chapter = NOOSE_Website.Infrastructure.Handbook.HandbookContent.SeededChapter;

namespace NOOSE_Website.Infrastructure.Handbook.Content;

/// <summary>Chapter eight: how the site handles ranks and divisions; the rule text itself lives on the regulation page.</summary>
/// <remarks>
/// The key stays <c>kap-dienstvorschrift</c> so the existing row is reused. The chapter used to retell the service
/// regulation; that text now lives only in the database behind <c>/dienstverordnung</c>, and the handbook points there.
/// </remarks>
internal static class DutyRegulationChapter
{
    internal static readonly Chapter Chapter = new(
        "kap-dienstvorschrift", "dienstgrade-und-abteilungen", "Dienstgrade und Abteilungen",
        "Wie die Seite Dienstgrade, Abteilungen, Board und Administration Division abbildet. Den Wortlaut regelt die Dienstverordnung.",
        "Policy",
        [
            new Article("art-dvo-dienstgrade", "dienstgrade", "Dienstgrade",
                "Die acht Dienstgrade, die Titel im HRT und welche Rechte die Seite an einen Dienstgrad knüpft.",
                """
                <p>Die Seite kennt acht <strong>Dienstgrade</strong>: Trainee, Special Agent, Senior Special
                Agent, Special Agent in Charge, Section Chief, Assistant Director, Deputy Director und Director.
                Im <strong>Hostage Rescue Team</strong> tragen die vier mittleren eigene Titel: Operator, Senior
                Operator, Leading Operator und Commanding Operator.</p>
                <p>Die Titel im HRT zeigt die Seite nur im Haus: in der Personalliste, im Profil, im Organigramm
                und in der Kopfzeile. Alles, was die Behörde verlässt - Briefe an Bewerber, öffentliche Seiten,
                Urkunden -, trägt den Dienstgrad ohne Abteilungstitel.</p>
                <p>An einige Dienstgrade knüpft die Seite selbst Rechte:</p>
                <ul>
                <li><strong>Special Agent</strong> (Operator): HRT und Administration Division lassen sich
                setzen.</li>
                <li><strong>Senior Special Agent</strong> (Senior Operator): die höchste Einstufung vergeben,
                selbst veröffentlichen und aus der Kasse auszahlen.</li>
                <li><strong>Section Chief</strong> (Commanding Operator): Beginn der Führung. Verschlusssachen
                und Klarnamen werden sichtbar, Beförderungsanträge stellen und in der eigenen Abteilung über
                Beförderungen bis zum Special Agent in Charge entscheiden.</li>
                <li><strong>Assistant Director</strong> und darüber: das Board of Directors. Es entscheidet über
                Beförderungen in allen Abteilungen.</li>
                </ul>
                <p>Was ein Dienstgrad im Dienst bedeutet, etwa Befugnisse, Freigaben, Ausrüstung und Gehalt,
                regelt die <em>Dienstverordnung</em> (Menü <em>Wissen</em>).</p>
                """),

            new Article("art-dvo-abteilungen", "abteilungen", "Abteilungen und Board",
                "Wie die Seite Abteilungen, Board und Administration Division führt - mit Plätzen und Zugang.",
                """
                <p>Jeder Agent gehört auf der Seite <strong>genau einer</strong> Abteilung an: CID, CTD oder HRT.
                Sie schließen einander aus - wer im HRT dient, ist nicht zugleich in der CID. Das HRT lässt sich
                erst ab dem Dienstgrad Special Agent setzen; darunter ist die CID oder die CTD die Heimat.</p>
                <p>Das <strong>Board of Directors</strong> ergibt sich allein aus dem Dienstgrad: Wer Assistant
                Director oder höher ist, sitzt im Board. Seine bisherige Abteilung merkt sich die Seite trotzdem;
                wird jemand später wieder unter den Assistant Director gestuft, kehrt er dorthin zurück.</p>
                <p>Die <strong>Administration Division</strong> ist keine Abteilung, sondern ein zusätzliches
                Kennzeichen am Konto, das es erst ab Special Agent gibt. Wer es trägt, behält seine Abteilung.
                Auf der Seite darf er Bewerbungen bearbeiten, Tests zuweisen, Ausbildungsmodule abhaken und das
                Handbuch pflegen.</p>
                <p>Die Plätze je Einheit sind ein <strong>weiches Limit</strong>: Board 4, HRT 9, CID 21, CTD 6;
                die Administration Division hat kein festes Limit. Die Seite zählt mit und zeigt im Organigramm
                je Einheit „besetzt / Limit“. Ist eine Abteilung voll, fragt sie ausdrücklich nach, bevor jemand
                einen Platz darüber hinaus bekommt, und prüft diese Bestätigung selbst. Möglich ist das also,
                aber nie aus Versehen.</p>
                <p>Die Abteilung eines bestehenden Agenten ändern nur <strong>das Board und Admins</strong>, in der
                Agenten-Verwaltung. Ein neues Konto gibt das Board oder ein Admin in jede Abteilung frei, ein
                Section Chief nur in die eigene. Wird jemand unter Special Agent herabgestuft, wechselt er aus dem
                HRT in die CID und verliert die Administration Division.</p>
                <p>Welche Aufgaben die Einheiten haben, regelt die <em>Dienstverordnung</em> (Menü
                <em>Wissen</em>).</p>
                """),
        ]);
}
