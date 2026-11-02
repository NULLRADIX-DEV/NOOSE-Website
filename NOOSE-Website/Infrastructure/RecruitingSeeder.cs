using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;

namespace NOOSE_Website.Infrastructure;

/// <summary>Seeds the default recruiting message templates (category "Bewerbung") once, idempotently.</summary>
public static class RecruitingSeeder
{
    /// <summary>Category marker that scopes a document template to the recruiting feature.</summary>
    public const string TemplateCategory = "Bewerbung";

    public static async Task SeedTemplatesAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: a template the HRB deleted is still a decision, and reading past the soft-delete
        // filter made it look absent - so the next application start created it again
        var existing = await db.DocumentTemplates
            .IgnoreQueryFilters()
            .Where(t => t.Category == TemplateCategory)
            .ToListAsync(cancellationToken);
        var have = new HashSet<string>(existing.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var sorting = 0;
        var changed = false;
        foreach (var (name, html) in Templates)
        {
            sorting++;
            if (have.Contains(name))
            {
                continue;
            }
            db.DocumentTemplates.Add(new DocumentTemplate
            {
                Name = name,
                Category = TemplateCategory,
                ContentHtml = html,
                IsActive = true,
                Sorting = sorting,
            });
            changed = true;
        }

        // migrate already-seeded letters from the old single NAME token to the recipient BEWERBER token,
        // leaving the sender's signature NAME (and any admin edits) untouched
        foreach (var t in existing)
        {
            var upgraded = UpgradeSalutationTokens(t.ContentHtml);
            if (!string.Equals(upgraded, t.ContentHtml, StringComparison.Ordinal))
            {
                t.ContentHtml = upgraded;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Swap the recipient salutation/subject NAME token for BEWERBER; the signature NAME stays.</summary>
    private static string UpgradeSalutationTokens(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }
        return html
            .Replace("Sehr geehrte NAME", "Sehr geehrte BEWERBER")
            .Replace("Sehr geehrter NAME", "Sehr geehrter BEWERBER")
            .Replace("von Herrn NAME", "von Herrn BEWERBER");
    }

    // BEWERBER (recipient) is filled with the applicant's name; NAME (sender) is rendered as a redaction
    // block so the agent stays anonymous; DATUM / UHRZEIT / DIENSTGRAD are filled when the letter is sent
    // (see BewerbungTemplateRenderer).
    private static readonly (string Name, string Html)[] Templates =
    [
        ("Eingangsbestätigung",
            Head("Auswahlverfahren") +
            "<p><strong>Eingangsbestätigung</strong></p>" +
            "<p>Sehr geehrte BEWERBER,</p>" +
            "<p>hiermit bestätigen wir Ihnen den Eingang Ihrer Bewerbung. Die Sicherheitsüberprüfung und das " +
            "anschließende Auswahlverfahren werden einige Zeit in Anspruch nehmen. Wir bitten Sie daher von " +
            "weiteren Statusanfragen abzusehen.</p>" +
            Sign() + Footer()),

        ("Anhörung zwecks Sicherheitsüberprüfung",
            Head("Auswahlverfahren") +
            "<p><strong>Anordnung zur persönlichen Anhörung im Rahmen der Sicherheitsüberprüfung</strong></p>" +
            "<p>Sehr geehrter BEWERBER,</p>" +
            "<p>im Rahmen der gegen Sie eingeleiteten Sicherheitsüberprüfung wurden Sachverhalte festgestellt, " +
            "die einer persönlichen Erörterung bedürfen.</p>" +
            "<p>Die Anhörung wird am DATUM um UHRZEIT Uhr im FIB-Tower, Los Santos, San Andreas stattfinden.</p>" +
            SecurityClause() +
            "<p>Eine Missachtung der Sicherheitsauflagen führt zum sofortigen Entzug der Zutrittsberechtigung " +
            "und zum Ausschluss aus dem Auswahlverfahren.</p>" +
            Sign() + Footer()),

        ("Einladung zum Auswahlverfahren",
            Head("Auswahlverfahren") +
            "<p><strong>Einladung zum mündlichen Auswahlverfahren</strong></p>" +
            "<p>Sehr geehrter BEWERBER,</p>" +
            "<p>im Rahmen des laufenden Auswahlverfahrens ergeht hiermit die Einladung zu einem persönlichen " +
            "Vorstellungsgespräch.</p>" +
            "<p>Das Auswahlverfahren wird am DATUM um UHRZEIT Uhr im FIB-Tower, Los Santos, San Andreas " +
            "stattfinden. Bitte planen Sie sich viel Zeit ein.</p>" +
            "<p>Diese Einladung erfolgt ausdrücklich unter dem Vorbehalt der noch ausstehenden Ergebnisse der " +
            "Sicherheitsüberprüfung. Sollten im Rahmen dieser Überprüfung sicherheitsrelevante Erkenntnisse zutage " +
            "treten, verliert diese Einladung mit sofortiger Wirkung ihre Gültigkeit.</p>" +
            SecurityClause() +
            "<p>Eine Missachtung der Sicherheitsauflagen führt zum sofortigen Entzug der Zutrittsberechtigung " +
            "und zum Ausschluss aus dem Auswahlverfahren.</p>" +
            Sign() + Footer()),

        ("Anfrage von Personalakten",
            "<p><strong>Federal Investigation Bureau</strong><br>Sicherheitsfreigabe: 6</p>" +
            "<p><strong>Anforderung von Personalunterlagen</strong><br>hier: VORNAME NACHNAME</p>" +
            "<p>Sehr geehrte Damen und Herren,</p>" +
            "<p>das Federal Investigation Bureau fordert Sie hiermit im Wege der Amtshilfe dazu auf, die " +
            "vollständige und ungeschwärzte Personalakte (einschließlich Disziplinarverfahren, internen Ermittlungen " +
            "und Aktennotizen) von Herrn BEWERBER an uns zu übermitteln.</p>" +
            "<p>Wir verweisen in diesem Zusammenhang auf die Verfassung des Staates San Andreas sowie die gesetzliche " +
            "Pflicht zur Amtshilfe und interbehördlichen Zusammenarbeit. Demnach sind alle Behörden des öffentlichen " +
            "Dienstes dazu verpflichtet, dem Federal Investigation Bureau zur Erfüllung seiner gesetzlichen " +
            "Aufgaben uneingeschränkte Unterstützung zu leisten.</p>" +
            "<p>Aufgrund der zeitkritischen Einstufung des Verfahrens wird eine Übermittlung der Dokumente auf sicherem, " +
            "digitalem oder physischem Dienstweg bis zum DATUM erwartet.</p>" +
            "<p>Sollten der Übermittlung wider Erwarten Hinderungsgründe entgegenstehen, ist dies unverzüglich unter " +
            "Angabe der genauen Rechtsnorm schriftlich zu begründen.</p>" +
            Sign()),

        ("Verweigerung zu Ablehnungsgründen",
            Head("Auswahlverfahren") +
            "<p><strong>Mitteilung über die Verweigerung von Auskünften</strong></p>" +
            "<p>Sehr geehrter BEWERBER,</p>" +
            "<p>das Federal Investigation Bureau erteilt grundsätzlich keinerlei Auskünfte über den Verlauf, " +
            "Fortführung, Beendigung oder die konkreten Gründe für eine Ablehnung innerhalb des behördlichen " +
            "Auswahlverfahrens.</p>" +
            "<p>Sämtliche im Rahmen der Personalgewinnung erhobenen Daten, Ergebnisse, Profile sowie die Erkenntnisse " +
            "aus der Sicherheitsüberprüfung sind als Verschlusssache eingestuft. Die Offenlegung dieser Informationen " +
            "ist zum Schutz von Auswahlkriterien sowie aus Gründen der geheimdienstlichen Sicherheit absolut " +
            "ausgeschlossen.</p>" +
            "<p>Das Ausscheiden aus dem Auswahlverfahren ist eine endgültige behördliche Entscheidung. Wir bitten Sie " +
            "von weiteren Sachstandsanfragen abzusehen und werden keine weiteren Auskünfte zu diesem Vorgang erteilen.</p>" +
            Sign() + Footer()),
    ];

    private static string Head(string subtitle)
        => "<p><strong>Federal Investigation Bureau</strong><br>" +
           $"{subtitle}<br>Abteilung: Criminal Investigation Division<br>Sicherheitsfreigabe: 6</p>";

    private static string SecurityClause()
        => "<p>Mit diesem Schreiben sind Sie temporär autorisiert, das ausgewiesene militärische Sperrgebiet zum Zweck " +
           "der Terminwahrnehmung zu betreten. Bei Ansprache durch Special Agents ist unverzüglich auf den Unterzeichner " +
           "zu verweisen. Das Betreten des militärischen Sperrgebiets hat zwingend unbewaffnet zu erfolgen. Bei Erreichen " +
           "der Sperrgebietsgrenze ist eine vollständige Maskierung anzulegen. Stellen Sie sicher, dass Sie vor der Anreise " +
           "kein Smartphone oder Funkgerät mit sich führen.</p>";

    private static string Sign()
        => "<p>Mit freundlichen Grüßen<br>NAME<br><em>DIENSTGRAD</em></p>";

    private static string Footer()
        => "<p><em>Das Federal Investigation Bureau entspricht hohen Standards und der höchsten Stufe der " +
           "Geheimhaltung. Sprechen Sie mit niemandem über Ihr Interesse oder den Stand des Auswahlverfahrens.</em></p>";
}
