namespace NOOSE_Website.Services;

/// <summary>Whether someone sits on the FIB's Discord server; Unknown never counts against them.</summary>
public enum DiscordMembership
{
    Member,
    NotMember,
    Unknown,
}

/// <summary>Asks Discord whether an account is on the server; used before an application is taken.</summary>
public interface IDiscordMembershipCheck
{
    /// <summary>NotMember only when Discord says the user is not on the server; no bot, server id or valid Discord id, and every Discord or network failure, read as Unknown. Never throws except on the caller's own cancellation.</summary>
    Task<DiscordMembership> CheckAsync(string agentId, CancellationToken cancellationToken = default);
}
