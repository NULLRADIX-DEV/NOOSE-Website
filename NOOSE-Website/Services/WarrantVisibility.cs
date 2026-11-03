using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Warrants;

namespace NOOSE_Website.Services;

/// <summary>Central warrant visibility rule: internal agents only, and never further than the person and case it concerns.</summary>
/// <remarks>
/// No requester exception: a warrant whose person was classified after the request disappears for the requester too.
/// Partners see none, released or not; there is no release path for a warrant.
/// </remarks>
public static class WarrantVisibility
{
    /// <summary>Filters a warrant query to the rows the viewer may see.</summary>
    public static IQueryable<Warrant> OnlyVisible(this IQueryable<Warrant> query, AppDbContext db, ViewerScope scope)
    {
        if (!scope.IsInternalAgent || scope.IsPartner)
        {
            return query.Where(_ => false);
        }
        // the records' own read gate, so a classified or trashed person hides its warrants as well
        var people = db.People.OnlyVisible(scope);
        var cases = db.Cases.OnlyVisible(scope);
        return query.Where(w => people.Any(p => p.Id == w.PersonId)
            && (w.CaseId == null || cases.Any(c => c.Id == w.CaseId)));
    }

    /// <summary>True if the warrant exists and the viewer may see it.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string warrantId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => db.Warrants.OnlyVisible(db, scope).AnyAsync(w => w.Id == warrantId, cancellationToken);
}
