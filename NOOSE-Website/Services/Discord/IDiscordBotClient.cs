namespace NOOSE_Website.Services;

/// <summary>One role of the Discord server.</summary>
public sealed record DiscordGuildRole(string Id, string Name, int Position, bool Managed);

/// <summary>The server's roles plus the highest position the bot's own roles reach (0 if it has none).</summary>
public sealed record DiscordGuildRoles(IReadOnlyList<DiscordGuildRole> Roles, int BotTopPosition);

/// <summary>Discord answered with an error; the message holds the status and Discord's own text, never the token.</summary>
public sealed class DiscordBotException(int status, string message, int? code = null) : Exception(message)
{
    /// <summary>Discord's error code for a user who closed their direct messages or shares no server with the bot.</summary>
    public const int CannotSendToUser = 50007;

    public int Status { get; } = status;

    /// <summary>Discord's own error code from the answer's JSON, if it sent one.</summary>
    public int? Code { get; } = code;
}

/// <summary>The small slice of the Discord REST API the role sync and direct messages need, authenticated as the bot; ids must be digits only, else <see cref="ArgumentException"/>.</summary>
public interface IDiscordBotClient
{
    /// <summary>False without a bot token; every call then throws <see cref="InvalidOperationException"/>.</summary>
    bool IsConfigured { get; }

    /// <summary>Role ids the member carries; null if the user is not on the server.</summary>
    Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct);

    Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct);

    Task AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct);

    Task RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct);

    /// <summary>Sends a direct message without any mention (cut to 2000 characters); throws <see cref="DiscordBotException"/> if Discord refuses it.</summary>
    Task SendDirectMessageAsync(string userId, string content, CancellationToken ct);
}
