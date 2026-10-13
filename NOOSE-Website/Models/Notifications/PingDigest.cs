using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Notifications;

/// <summary>How many unread notices of one collected category wait for the agent.</summary>
public sealed record PingDigestCount(NotificationType Type, int Count);

/// <summary>One agent's line in the daily digest: who is pinged, and the counts per collected category.</summary>
public sealed record PingDigestEntry(string DiscordId, IReadOnlyList<PingDigestCount> Counts);

/// <summary>One Discord message of the digest and the users it may ping.</summary>
public sealed record PingDigestMessage(string Content, IReadOnlyList<string> DiscordIds);
