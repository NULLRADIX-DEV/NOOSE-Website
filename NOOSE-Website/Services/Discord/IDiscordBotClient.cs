namespace NOOSE_Website.Services;

/// <summary>One role of the Discord server.</summary>
public sealed record DiscordGuildRole(string Id, string Name, int Position, bool Managed);

/// <summary>The server's roles plus the highest position the bot's own roles reach (0 if it has none).</summary>
public sealed record DiscordGuildRoles(IReadOnlyList<DiscordGuildRole> Roles, int BotTopPosition);

/// <summary>Discord answered with an error; the message holds the status and Discord's own text, never the token.</summary>
public sealed class DiscordBotException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The small slice of the Discord REST API the role sync needs, authenticated as the bot.</summary>
public interface IDiscordBotClient
{
    /// <summary>False without a bot token; every call then throws <see cref="InvalidOperationException"/>.</summary>
    bool IsConfigured { get; }

    /// <summary>Role ids the member carries; null if the user is not on the server.</summary>
    Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct);

    Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct);

    Task AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct);

    Task RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct);
}
