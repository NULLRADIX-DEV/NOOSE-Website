using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who an announcement reaches: its audience, its author, and leadership.</summary>
/// <remarks>
/// The gate is <c>IsLeadership()</c>, not <c>MayClassifiedRead()</c> — deliberately, so the read-only supervision
/// sees only announcements actually addressed to it. Partners are never in an audience.
/// </remarks>
public static class AnnouncementVisibility
{
    /// <summary>The viewer's own taskforce ids, which the Taskforce audience is matched against.</summary>
    public static async Task<List<string>> MyTaskforceIdsAsync(
        AppDbContext db, string? meId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(meId))
        {
            return new List<string>();
        }
        return await db.TaskforceAgents
            .Where(ta => ta.AgentId == meId)
            .Select(ta => ta.TaskforceId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Announcements the viewer may read.</summary>
    public static IQueryable<Announcement> OnlyVisible(this IQueryable<Announcement> query,
        ClaimsPrincipal actor, IReadOnlyCollection<string> myTaskforceIds)
        => query.OnlyVisible(ViewerScope.From(actor), myTaskforceIds);

    /// <summary>Announcements the viewer may read, from a scope rather than a principal.</summary>
    /// <remarks>Twin of the principal overload, which delegates here — same shape as
    /// <see cref="MeetingVisibility.MayReadAgenda(ViewerScope, DateTime, DateTime?, DateTime)" />. The record gate
    /// holds a scope, not a principal, and a second copy of this predicate would be a second chance to widen it.</remarks>
    public static IQueryable<Announcement> OnlyVisible(this IQueryable<Announcement> query,
        ViewerScope scope, IReadOnlyCollection<string> myTaskforceIds)
    {
        // locals so EF parameterizes them
        var meId = scope.MeId;
        var isLeadership = scope.IsLeadership;
        var isTru = scope.IsTru;
        var isHrb = scope.IsHrb;
        var myRank = scope.Rank;
        return query.Where(a => isLeadership
            || a.CreatedById == meId
            || a.Audience == AnnouncementAudience.AllActive
            || (a.Audience == AnnouncementAudience.Taskforce && a.TargetId != null && myTaskforceIds.Contains(a.TargetId))
            || (a.Audience == AnnouncementAudience.TruUnit && isTru)
            || (a.Audience == AnnouncementAudience.HrbUnit && isHrb)
            || (a.Audience == AnnouncementAudience.FromRank && myRank != null
                && a.MinRank != null && myRank >= a.MinRank));
    }

    /// <summary>Whether the viewer is in an announcement's audience; the author and leadership are handled by the caller.</summary>
    public static Task<bool> IsRecipientAsync(AppDbContext db, Announcement a, string? meId, bool isTru,
        bool isHrb, Rank? myRank, CancellationToken cancellationToken = default)
        => AudienceRules.IsMemberAsync(db, a.Audience, a.TargetId, a.MinRank, meId, isTru, isHrb, myRank,
            cancellationToken);
}
