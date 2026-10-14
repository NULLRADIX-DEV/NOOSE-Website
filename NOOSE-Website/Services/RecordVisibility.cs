using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Read gate of the six classifiable record types (Person, Faction, PersonGroup, Party, Operation, Case).</summary>
/// <remarks>
/// One rule, one place. It used to live in six near-identical private statics plus a seventh copy in
/// <see cref="Visibility"/> and an eighth in the search, and they had already drifted: the person list was missing
/// the TRU/HRB arm the other five had, so a TRU agent could open their own classified person but never find it.
/// Documents deliberately do NOT go through here — see <see cref="IClassifiableRecord"/>.
/// </remarks>
public static class RecordVisibility
{
    /// <summary>Secrecy level of a record: None when unrestricted, else the audience flag, otherwise leadership.</summary>
    /// <remarks>"Unrestricted" asks all three flags, not just <c>IsClassified</c>, so that this and
    /// <see cref="OnlyVisible{T}"/> answer the same for a row that broke the setters' invariant. Reading only the
    /// first flag would rate such a row None here and Tru there — the point-check would show what the list hides.</remarks>
    public static DocumentClassification LevelOf(bool classified, bool tru, bool hrb)
        => !classified && !tru && !hrb ? DocumentClassification.None
            : tru ? DocumentClassification.Tru
            : hrb ? DocumentClassification.Hrb
            : DocumentClassification.Leadership;

    /// <summary>In-memory twin of <see cref="OnlyVisible{T}"/>, for rows already materialised.</summary>
    public static bool IsVisible(ViewerScope scope, bool classified, bool tru, bool hrb)
        => scope.CanSee(LevelOf(classified, tru, hrb));

    /// <summary>Records the viewer may see. Partners go through their own release gate at the call site.</summary>
    /// <remarks>
    /// The first arm asks all three flags rather than <c>IsClassified</c> alone. The setters keep the invariant that
    /// a TRU/HRB record is also classified, so the two forms should agree — but if a row ever breaks it, this one
    /// hides it and the shorter one would have shown it to everybody.
    /// </remarks>
    public static IQueryable<T> OnlyVisible<T>(this IQueryable<T> query, ViewerScope scope)
        where T : class, IClassifiableRecord
    {
        // locals so EF parameterizes rather than baking the viewer's flags into the SQL
        bool mayClassified = scope.MayClassifiedRead, isTru = scope.IsTru, isHrb = scope.IsHrb;
        return query.Where(x => (!x.IsClassified && !x.IsTRUClassified && !x.IsHRBClassified)
            || mayClassified
            || (x.IsTRUClassified && isTru)
            || (x.IsHRBClassified && isHrb));
    }

    /// <summary>Records without any secrecy level: the ones every internal agent sees, for an audience the app does not know.</summary>
    public static IQueryable<T> OnlyUnrestricted<T>(this IQueryable<T> query)
        where T : class, IClassifiableRecord
        => query.Where(x => !x.IsClassified && !x.IsTRUClassified && !x.IsHRBClassified);
}
