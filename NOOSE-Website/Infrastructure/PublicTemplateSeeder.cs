using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Infrastructure;

/// <summary>Puts one starting template per kind into an empty installation.</summary>
/// <remarks>
/// Seeds only while the table is empty, exactly like <see cref="WarnhinweisSeeder"/> and unlike
/// <see cref="PublicModuleSeeder"/>, which tops up per key. A module key lives in the code and cannot be deleted; a
/// template is a row that belongs to whoever runs the site, and per-kind seeding would resurrect a deleted one on
/// every restart. Without a seed the automatic confirmations simply stay silent — there is no fallback text in code.
/// </remarks>
public static class PublicTemplateSeeder
{
    private static (PublicTemplateKind Kind, string Title, string Text)[] Starting() =>
    [
        (PublicTemplateKind.TicketEingang, "Eingangsbestätigung Ticket",
            $"""
             Sehr geehrte/r BUERGER,

             vielen Dank für Ihre Nachricht. Ihr Anliegen ist am DATUM um UHRZEIT bei uns eingegangen und wird unter dem Aktenzeichen AKTENZEICHEN geführt.

             Eine Rückmeldung erhalten Sie in diesem Ticket. Bitte antworten Sie ausschließlich hier, damit alles zu Ihrem Anliegen an einer Stelle bleibt.

             Mit freundlichen Grüßen
             {TicketRules.AgencySender}
             """),
        (PublicTemplateKind.TicketAntwort, "Antwort auf ein Anliegen",
            $"""
             Sehr geehrte/r BUERGER,

             zu Ihrem Anliegen AKTENZEICHEN können wir Ihnen Folgendes mitteilen:

             [Bitte hier ergänzen]

             Für Rückfragen stehen wir Ihnen in diesem Ticket zur Verfügung.

             Mit freundlichen Grüßen
             {TicketRules.AgencySender}
             """),
        (PublicTemplateKind.HinweisEingang, "Eingangsbestätigung Hinweis",
            """
            Sehr geehrte/r BUERGER,

            vielen Dank für Ihren Hinweis. Er ist am DATUM um UHRZEIT eingegangen und wird unter dem Aktenzeichen AKTENZEICHEN geprüft.

            Wir melden uns, sobald wir eine Rückfrage haben. Bitte haben Sie Verständnis, dass wir zu laufenden Prüfungen keine Auskunft geben können.

            Mit freundlichen Grüßen
            FIB
            """),
        (PublicTemplateKind.HinweisRueckfrage, "Rückfrage zu einem Hinweis",
            """
            Sehr geehrte/r BUERGER,

            zu Ihrem Hinweis AKTENZEICHEN haben wir eine Rückfrage:

            [Bitte hier ergänzen]

            Bitte antworten Sie in diesem Schriftwechsel. Ihre Angaben helfen uns bei der Prüfung.

            Mit freundlichen Grüßen
            FIB
            """),
        (PublicTemplateKind.HinweisAblehnung, "Hinweis wird nicht weiter verfolgt",
            """
            Sehr geehrte/r BUERGER,

            wir haben Ihren Hinweis AKTENZEICHEN geprüft und verfolgen ihn nicht weiter. Das ist kein Vorwurf an Sie: häufig fehlen belastbare Anhaltspunkte, oder der Sachverhalt liegt außerhalb unserer Zuständigkeit.

            Vielen Dank, dass Sie sich die Zeit genommen haben.

            Mit freundlichen Grüßen
            FIB
            """),
    ];

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: the probe reads "is this table empty", and through the soft-delete filter a table
        // holding nothing but deleted templates looks empty - so every restart resurrected them as active
        if (await db.OeffentlicheVorlagen.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        var order = 10;
        foreach (var (kind, title, text) in Starting())
        {
            db.OeffentlicheVorlagen.Add(new OeffentlicheVorlage
            {
                Kind = kind,
                Title = title,
                Text = text,
                IsActive = true,
                SortOrder = order,
            });
            order += 10;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
