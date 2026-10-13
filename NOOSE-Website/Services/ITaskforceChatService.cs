using System.Security.Claims;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>Taskforce team chat; visibility and write rights inherit from the parent taskforce.</summary>
public interface ITaskforceChatService
{
    /// <summary>Loads recent messages, oldest first for display; olderAs pages further back.</summary>
    Task<List<TaskforceMessage>> GetMessagesAsync(string taskforceId, ViewerScope scope, int limit = 100, DateTime? olderAs = null, CancellationToken cancellationToken = default);

    /// <summary>Unread messages by others, per taskforce the caller belongs to; newest activity first.</summary>
    Task<List<TaskforceActivity>> GetUnreadAsync(ClaimsPrincipal actor, bool includeArchived = false, CancellationToken cancellationToken = default);

    /// <summary>The caller's read mark on one taskforce chat and the unread count behind it.</summary>
    Task<TaskforceReadState> GetReadStateAsync(string taskforceId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves the caller's read mark to now; members with write access only, a no-op for everyone else.</summary>
    Task MarkReadAsync(string taskforceId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Sends a message; throws if the taskforce is not visible or the text is empty.</summary>
    Task<TaskforceMessage> SendAsync(string taskforceId, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a message; allowed for the author or leadership.</summary>
    Task DeleteAsync(string messageId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
