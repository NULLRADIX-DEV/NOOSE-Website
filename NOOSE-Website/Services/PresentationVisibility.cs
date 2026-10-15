using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who sees a presentation, who may manage it, and which groups its author may close it to.</summary>
/// <remarks>
/// Owner, leadership and the read-only supervision see every presentation; otherwise public means every internal
/// agent, closed means the group (the same membership rules as announcements, see <see cref="AudienceRules"/>),
/// private means the invited. Partners never. Live sessions, slide pictures and the list all ask here.
/// </remarks>
public static class PresentationVisibility
{
    /// <summary>Presentations the viewer may see.</summary>
    public static IQueryable<Presentation> OnlyVisible(IQueryable<Presentation> query, AppDbContext db, ViewerScope scope)
    {
        if (scope.IsPartner || !scope.IsInternalAgent)
        {
            return query.Where(_ => false);
        }
        // locals so EF parameterizes them
        var meId = scope.MeId;
        var readsAll = scope.IsLeadership || scope.MayClassifiedRead;
        var isTru = scope.IsTru;
        var isHrb = scope.IsHrb;
        var rank = scope.Rank;
        return query.Where(p => readsAll
            || p.OwnerId == meId
            || p.Access == PresentationAccess.Public
            || (p.Access == PresentationAccess.Closed
                && ((p.Audience == AnnouncementAudience.TruUnit && isTru)
                    || (p.Audience == AnnouncementAudience.HrbUnit && isHrb)
                    || (p.Audience == AnnouncementAudience.FromRank && rank != null && p.MinRank != null && rank >= p.MinRank)
                    || (p.Audience == AnnouncementAudience.Taskforce && p.TargetId != null
                        && db.TaskforceAgents.Any(ta => ta.TaskforceId == p.TargetId && ta.AgentId == meId))))
            || (p.Access == PresentationAccess.Private
                && db.PresentationInvites.Any(i => i.PresentationId == p.Id && i.AgentId == meId)));
    }

    /// <summary>Titles of the given presentations the viewer may see, by id; the rest are left out.</summary>
    /// <remarks>For a link, a timeline or chronicle row, a link-label search hit or a NOOSEI link listing pointing
    /// at a presentation. The shared reference resolver cannot ask here, because it is handed no scope and so knows
    /// neither the group nor the invites.</remarks>
    public static async Task<Dictionary<string, string>> VisibleTitlesAsync(AppDbContext db, IReadOnlyCollection<string> ids,
        ViewerScope scope, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new(StringComparer.Ordinal);
        }
        var rows = await OnlyVisible(db.Presentations, db, scope)
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Title })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.Title) ? "Präsentation" : p.Title,
            StringComparer.Ordinal);
    }

    /// <summary>Whether the viewer may see one presentation.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string presentationId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => OnlyVisible(db.Presentations, db, scope).AnyAsync(p => p.Id == presentationId, cancellationToken);

    /// <summary>Owner or leadership; the write check is the caller's guard.</summary>
    public static bool MayManage(ClaimsPrincipal actor, Presentation presentation)
        => actor.IsLeadership() || (presentation.OwnerId.Length > 0 && presentation.OwnerId == actor.GetAgentId());

    /// <summary>Whether the author belongs to the group they want to close the presentation to; leadership may pick any.</summary>
    /// <remarks>Otherwise any agent could address a briefing "to HRB only" from outside HRB and use the group as a
    /// distribution list it is not part of.</remarks>
    public static async Task<bool> MayTargetAsync(AppDbContext db, ClaimsPrincipal actor, AnnouncementAudience audience,
        string? targetId, Rank? minRank, CancellationToken cancellationToken = default)
    {
        if (actor.IsLeadership())
        {
            return true;
        }
        return audience switch
        {
            AnnouncementAudience.TruUnit => actor.IsTRU(),
            AnnouncementAudience.HrbUnit => actor.IsHRB(),
            AnnouncementAudience.FromRank => minRank is not null && actor.GetRank() is { } own && own >= minRank,
            AnnouncementAudience.Taskforce => await AudienceRules.IsMemberAsync(db, audience, targetId, minRank,
                actor.GetAgentId(), false, false, null, cancellationToken),
            _ => false,
        };
    }
}
