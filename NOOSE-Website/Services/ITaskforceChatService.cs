using System.Security.Claims;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>Taskforce team chat; visibility and write rights inherit from the parent taskforce.</summary>
public interface ITaskforceChatService
{
    /// <summary>Loads recent messages, oldest first for display; olderAs pages further back.</summary>
    Task<List<TaskforceMessage>> GetMessagesAsync(string taskforceId, ViewerScope scope, int limit = 100, DateTime? olderAs = null, CancellationToken cancellationToken = default);

    /// <summary>Messages by others since the instant, per taskforce the caller belongs to; archived ones are left out.</summary>
    Task<List<TaskforceActivity>> GetActivitySinceAsync(ClaimsPrincipal actor, DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>Sends a message; throws if the taskforce is not visible or the text is empty.</summary>
    Task<TaskforceMessage> SendAsync(string taskforceId, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a message; allowed for the author or leadership.</summary>
    Task DeleteAsync(string messageId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
