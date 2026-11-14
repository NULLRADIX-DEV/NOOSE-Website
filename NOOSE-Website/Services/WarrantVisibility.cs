using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Warrants;

namespace NOOSE_Website.Services;

/// <summary>Central warrant visibility rule: never further than the person, case and investigation it concerns.</summary>
/// <remarks>
/// No requester exception: a warrant whose person was classified after the request disappears for the requester too.
/// A warrant of an investigation is seen only by those who see the investigation; partners see nothing else. Whether
/// the viewer may read the warrants tab of that investigation is a second, finer check in <see cref="WarrantService"/>.
/// </remarks>
public static class WarrantVisibility
{
    /// <summary>Filters a warrant query to the rows the viewer may see.</summary>
    public static IQueryable<Warrant> OnlyVisible(this IQueryable<Warrant> query, AppDbContext db, ViewerScope scope)
    {
        var investigations = db.Investigations.OnlyVisible(db, scope);
        if (scope.IsPartner)
        {
            // a classified or trashed person hides its warrants from partners as well
            return query.Where(w => w.InvestigationId != null && investigations.Any(i => i.Id == w.InvestigationId)
                && db.People.Any(p => p.Id == w.PersonId && !p.IsClassified));
        }
        if (!scope.IsInternalAgent)
        {
            return query.Where(_ => false);
        }
        // the records' own read gate, so a classified or trashed person hides its warrants as well
        var people = db.People.OnlyVisible(scope);
        var cases = db.Cases.OnlyVisible(scope);
        return query.Where(w => people.Any(p => p.Id == w.PersonId)
            && (w.CaseId == null || cases.Any(c => c.Id == w.CaseId))
            && (w.InvestigationId == null || investigations.Any(i => i.Id == w.InvestigationId)));
    }

    /// <summary>True if the warrant exists and the viewer may see it.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string warrantId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => db.Warrants.OnlyVisible(db, scope).AnyAsync(w => w.Id == warrantId, cancellationToken);
}
