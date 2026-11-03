using System.Security.Claims;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Shares an unclassified record into a Discord channel the leadership set up, and keeps that channel list.</summary>
public interface IChannelShareService
{
    /// <summary>Channels the actor may share into; empty when they may not share, Discord is off or none is set up.</summary>
    Task<IReadOnlyList<ShareChannel>> GetChannelsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The channel list for the settings, webhooks masked, with the version a save must hand back; leadership and supervision.</summary>
    Task<ShareChannelList> GetSettingsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Replace the channel list; an empty URL keeps the stored webhook, a stale version is refused. Leadership with write access.</summary>
    Task SaveSettingsAsync(IReadOnlyList<ShareChannelInput> channels, string version, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Log, then post an unclassified record's notice and link; missing, hidden and classified are refused alike. Returns the channel name.</summary>
    Task<string> ShareAsync(ChannelShareRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
