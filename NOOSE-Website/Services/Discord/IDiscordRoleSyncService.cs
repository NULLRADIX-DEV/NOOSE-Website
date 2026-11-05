using System.Security.Claims;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Copies rank, division and flags of agents to Discord roles, one way: the site is the truth.</summary>
public interface IDiscordRoleSyncService
{
    /// <summary>Switch, server id and mapping; a missing or unreadable mapping reads as empty, the switch as off.</summary>
    Task<DiscordRoleSyncSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Admin with write access; bad ids or the resident role in another entry throw <see cref="InvalidOperationException"/>.</summary>
    Task SaveSettingsAsync(DiscordRoleSyncSettings settings, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Leadership or read-only supervision; the server's roles for the picker, null without token or server id or when Discord fails.</summary>
    Task<DiscordGuildRoles?> GetGuildRolesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Admin; what a run would add and remove, unchanged agents left out, nothing written; throws <see cref="InvalidOperationException"/> without token or server id.</summary>
    Task<IReadOnlyList<DiscordRolePreviewRow>> PreviewAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Leadership or read-only supervision; token, switch, last full run, and agents with an error or not on the server (codename only).</summary>
    Task<DiscordRoleSyncStatus> GetStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Admin with write access; queues a run over every agent for the worker and returns at once.</summary>
    Task RunNowAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Worker; syncs one agent from the current row. Does nothing while off; the reason is bare ("Beförderung"), the audit prefix is added here.</summary>
    Task SyncAgentAsync(string agentId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Worker; a deleted account counts as terminated, unless a live row still has that Discord id.</summary>
    Task SyncDeletedAsync(string discordId, CancellationToken cancellationToken = default);

    /// <summary>Worker; every agent with a Discord id, then stamps the run. A failure per agent never stops it.</summary>
    Task SyncAllAsync(string reason, CancellationToken cancellationToken = default);
}
