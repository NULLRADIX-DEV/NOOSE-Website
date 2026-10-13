using System.Security.Claims;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Scheduled follow-ups/reminders on records; due ones notify the assignee and the record's followers.</summary>
public interface IFollowupService
{
    /// <summary>All follow-ups of a record (open first); empty when the record isn't visible to the caller.</summary>
    Task<List<FollowupItem>> GetForRecordAsync(string entityType, string entityId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Create a follow-up (record must be visible); default assignee is the creator.</summary>
    Task CreateAsync(string entityType, string entityId, FollowupInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    Task RefreshAsync(string id, FollowupInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task ReopenAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Caller's open, due follow-ups (assigned or following), resolved to name + href and visibility-filtered. For the dashboard.</summary>
    Task<List<FollowupDashboardItem>> GetMyDueAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="GetMyDueAsync"/>, but everything due up to the given instant, so upcoming ones show too.</summary>
    Task<List<FollowupDashboardItem>> GetMineUntilAsync(ClaimsPrincipal actor, DateTime untilUtc, CancellationToken cancellationToken = default);
}
