using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Which personal Discord pings an agent may hold back, and when each one goes out.</summary>
/// <remarks>
/// Only categories whose channel post pings the recipients themselves: a role ping reaches everyone holding the
/// role, and one shared post cannot leave a single member out. Personnel entries stay out too, they are an
/// official notice about the agent.
/// </remarks>
public static class DiscordPings
{
    /// <summary>Local hour the daily digest goes out.</summary>
    public const int DigestHour = 18;

    /// <summary>The categories an agent may move to the digest or switch off, in display order.</summary>
    public static readonly IReadOnlyList<NotificationType> Personal =
    [
        NotificationType.Mention,
        NotificationType.JobAssigned,
        NotificationType.JobDueSoon,
        NotificationType.Followup,
    ];

    /// <summary>Whether an agent decides for themselves when this category pings them.</summary>
    public static bool IsPersonal(NotificationType type) => Personal.Contains(type);

    /// <summary>The agent's choice for this category; anything not personal pings as it happens.</summary>
    public static DiscordPingMode ModeFor(NavPreferences prefs, NotificationType type)
    {
        if (!IsPersonal(type))
        {
            return DiscordPingMode.Instant;
        }
        var name = type.ToString();
        return prefs.MutedPings.Contains(name) ? DiscordPingMode.Off
            : prefs.DigestPings.Contains(name) ? DiscordPingMode.Digest
            : DiscordPingMode.Instant;
    }
}
