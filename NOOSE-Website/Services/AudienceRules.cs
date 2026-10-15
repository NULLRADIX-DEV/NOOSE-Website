using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who belongs to an audience (all, TRU, HRB, taskforce, from rank); shared by announcements and agent tests.</summary>
/// <remarks>Reads only the three audience values, so every feature that targets a group resolves it the same way.
/// Leadership and authors are the caller's business: membership here is the group alone.</remarks>
public static class AudienceRules
{
    /// <summary>Narrows an agent query to the members of an audience.</summary>
    public static IQueryable<Agent> Members(this IQueryable<Agent> agents, AppDbContext db,
        AnnouncementAudience audience, string? targetId, Rank? minRank)
        => audience switch
        {
            AnnouncementAudience.TruUnit => agents.Where(u => u.IsTRU),
            AnnouncementAudience.HrbUnit => agents.Where(u => u.IsHRB),
            AnnouncementAudience.FromRank => agents.Where(u => u.Rank != null && u.Rank >= minRank),
            AnnouncementAudience.Taskforce => agents.Where(u =>
                db.TaskforceAgents.Any(ta => ta.TaskforceId == targetId && ta.AgentId == u.Id)),
            _ => agents,
        };

    /// <summary>Ids of selectable agents in an audience; read-only supervision and partners are never members.</summary>
    public static Task<List<string>> RecipientIdsAsync(AppDbContext db, AnnouncementAudience audience,
        string? targetId, Rank? minRank, CancellationToken cancellationToken = default)
        => db.Users.OnlySelectable()
            .Members(db, audience, targetId, minRank)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Whether one viewer is in an audience.</summary>
    public static async Task<bool> IsMemberAsync(AppDbContext db, AnnouncementAudience audience, string? targetId,
        Rank? minRank, string? meId, bool isTru, bool isHrb, Rank? myRank, CancellationToken cancellationToken = default)
        => audience switch
        {
            AnnouncementAudience.AllActive => true,
            AnnouncementAudience.TruUnit => isTru,
            AnnouncementAudience.HrbUnit => isHrb,
            AnnouncementAudience.FromRank => myRank != null && minRank != null && myRank >= minRank,
            AnnouncementAudience.Taskforce => targetId != null && !string.IsNullOrEmpty(meId)
                && await db.TaskforceAgents.AnyAsync(ta => ta.TaskforceId == targetId && ta.AgentId == meId, cancellationToken),
            _ => false,
        };
}
