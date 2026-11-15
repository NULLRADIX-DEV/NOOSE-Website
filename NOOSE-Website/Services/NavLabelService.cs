using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="INavLabelService" />
public class NavLabelService(IDbContextFactory<AppDbContext> dbFactory) : INavLabelService
{
    public async Task<NavLocation> ResolveAsync(string? relativePath, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        var clean = (relativePath ?? string.Empty).Split('?')[0].Split('#')[0].Trim('/');
        var section = NavCatalog.ByRoute(clean);
        var segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sectionOnly = new NavLocation(section, null, null, null, section?.Icon ?? string.Empty, "/" + clean);

        // list/static route: section only
        if (segments.Length < 2 || INavLabelService.RecordTypeForPrefix(segments[0].ToLowerInvariant()) is not { } rt)
        {
            return sectionOnly;
        }

        var id = segments[1];
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // never leak a name the viewer cannot see
            if (!await Visibility.IsRecordVisibleAsync(db, rt.TypeKey, id, scope, cancellationToken))
            {
                return sectionOnly;
            }
            var name = await ResolveNameAsync(db, rt.TypeKey, id, cancellationToken);
            if (name is null)
            {
                return sectionOnly;
            }
            return new NavLocation(section, rt.TypeKey, id, name, rt.Icon, $"/{segments[0].ToLowerInvariant()}/{id}");
        }
        catch
        {
            /* best effort */
            return sectionOnly;
        }
    }

    // cheap PK lookup of the record's display name (soft-deleted filtered out by the global filter)
    private static async Task<string?> ResolveNameAsync(AppDbContext db, string typeKey, string id, CancellationToken cancellationToken)
    {
        return typeKey switch
        {
            "Person" => await db.People.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken),
            "Faction" => await db.Factions.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken),
            "PersonGroup" => await db.PersonGroups.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken),
            "Party" => await db.Parties.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken),
            "DrugRoute" => await db.DrugRoutes.Where(x => x.Id == id).Select(x => x.Designation).FirstOrDefaultAsync(cancellationToken),
            "Operation" => await db.Operations.Where(x => x.Id == id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken),
            "Case" => await db.Cases.Where(x => x.Id == id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken),
            "Document" => await db.Documents.Where(x => x.Id == id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken),
            "Law" => await db.Laws.Where(x => x.Id == id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken),
            "Taskforce" => await db.Taskforces.Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
    }
}
