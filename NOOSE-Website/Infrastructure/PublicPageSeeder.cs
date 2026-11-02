using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure;

/// <summary>Seeds the four editorial starter pages as drafts, idempotently.</summary>
/// <remarks>
/// Only missing slugs are added and nothing is ever overwritten, so an edited page survives every deploy. All four
/// start as drafts with placeholder text: a seeder that published its own text would put words in the agency's mouth.
/// </remarks>
public static class PublicPageSeeder
{
    /// <summary>One starter page; the body is a placeholder an author is meant to replace.</summary>
    public sealed record Starter(string Slug, string Title, string MenuTitle, string IconName, int SortOrder, string DraftHtml);

    public static readonly IReadOnlyList<Starter> Starters =
    [
        new("auftrag", "Unser Auftrag", "Auftrag", "Shield", 10,
            "<p>Das Federal Investigation Bureau schützt die staatliche Ordnung vor organisierter "
            + "Kriminalität und verfassungsfeindlichen Bestrebungen.</p><p><em>Dieser Text ist ein Platzhalter "
            + "und muss vor der Veröffentlichung ersetzt werden.</em></p>"),
        new("befugnisse", "Unsere Befugnisse", "Befugnisse", "Gavel", 20,
            "<p>Hier stehen die gesetzlichen Grundlagen, auf die sich Maßnahmen des FIB stützen, und die "
            + "Grenzen dieser Befugnisse.</p><p><em>Dieser Text ist ein Platzhalter und muss vor der "
            + "Veröffentlichung ersetzt werden.</em></p>"),
        new("zustaendigkeiten", "Zuständigkeiten", "Zuständigkeiten", "Groups", 30,
            "<p>Hier steht, welche Anliegen das FIB bearbeitet und welche an andere Behörden gehören.</p>"
            + "<p><em>Dieser Text ist ein Platzhalter und muss vor der Veröffentlichung ersetzt werden.</em></p>"),
        // the odd one out: this row is not an Information page but the heading and intro of /faq, which has a
        // module and a route of its own. Held out of the /info menu by PublicPageService; retracting it takes
        // the whole FAQ down, which is why the FAQ panel names it
        new("faq", "Häufige Fragen", "FAQ", "Forum", 40,
            "<p>Antworten auf die Fragen, die uns am häufigsten erreichen.</p><p><em>Dieser Text ist ein "
            + "Platzhalter und muss vor der Veröffentlichung ersetzt werden.</em></p>"),
    ];

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // deleted rows count as known: re-creating a page someone threw away would be a surprise, not a repair
        var known = await db.OeffentlicheSeiten
            .IgnoreQueryFilters()
            .Select(p => p.Slug)
            .ToListAsync(cancellationToken);
        var missing = Starters
            .Where(starter => !known.Contains(starter.Slug, StringComparer.Ordinal))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var starter in missing)
        {
            db.OeffentlicheSeiten.Add(new OeffentlicheSeite
            {
                Slug = starter.Slug,
                Title = starter.Title,
                MenuTitle = starter.MenuTitle,
                IconName = starter.IconName,
                SortOrder = starter.SortOrder,
                DraftHtml = starter.DraftHtml,
                Status = PublicPageStatus.Entwurf,
                ShowInMenu = true,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
