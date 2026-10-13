namespace NOOSE_Website.Models.Enums;

/// <summary>When a personal Discord ping reaches the agent.</summary>
public enum DiscordPingMode
{
    /// <summary>Pinged with the channel post, as it happens.</summary>
    Instant = 0,

    /// <summary>Counted in the daily digest instead of pinged on its own.</summary>
    Digest = 1,

    /// <summary>Never pinged; the notice stays in the inbox.</summary>
    Off = 2,
}
