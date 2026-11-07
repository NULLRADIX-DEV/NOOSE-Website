using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Read gate of a directive: the classification of the row alone.</summary>
public static class DirectiveVisibility
{
    /// <summary>Filters a directive query to the rows the viewer may see; mirrors <see cref="ViewerScope.CanSee"/>.</summary>
    public static IQueryable<Directive> OnlyVisible(this IQueryable<Directive> query, ViewerScope scope)
    {
        // fail-closed: a hand-built scope never sets IsInternalAgent
        if (!scope.IsInternalAgent || scope.IsPartner)
        {
            return query.Where(_ => false);
        }
        // locals so EF parameterizes
        var mayClassified = scope.MayClassifiedRead;
        var isTru = scope.IsTru;
        var isHrb = scope.IsHrb;
        return query.Where(d => d.Classification == DocumentClassification.None
            || mayClassified
            || (d.Classification == DocumentClassification.Tru && isTru)
            || (d.Classification == DocumentClassification.Hrb && isHrb));
    }

    /// <summary>True if the directive exists and the viewer may see it; missing and hidden answer alike.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string directiveId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => db.Directives.OnlyVisible(scope).AnyAsync(d => d.Id == directiveId, cancellationToken);

    /// <summary>Narrows to a typed number (DA-07) or a title fragment; the one place that decides a match.</summary>
    public static IQueryable<Directive> Matching(this IQueryable<Directive> query, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }
        // parsed ahead of the lambda: no out var in an expression tree
        var s = text.Trim();
        if (DirectiveRules.TryParseNumber(s, out var n))
        {
            return query.Where(d => d.Number == n);
        }
        return query.Where(d => d.Title.Contains(s));
    }
}
