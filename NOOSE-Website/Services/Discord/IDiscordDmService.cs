using System.Security.Claims;
using NOOSE_Website.Infrastructure.Discord;

namespace NOOSE_Website.Services;

/// <summary>The two switches, whether the bot has a token, and the invite link to the Discord server; the switches read as off until an admin turns them on.</summary>
public sealed record DiscordDmSettings(bool Personal, bool Applicants, bool BotConfigured, string InviteLink)
{
    /// <summary>Applied while no admin has set a link of their own.</summary>
    public const string DefaultInviteLink = "https://discord.gg/9TeYjJyYxf";
}

/// <summary>Delivers direct messages through the bot and keeps the two switches that allow them.</summary>
public interface IDiscordDmService
{
    /// <summary>Both switches (default off), whether the bot has a token, and the invite link (the default while none is set).</summary>
    Task<DiscordDmSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Admin with write access (write guard first); stores both switches and the invite link in one go. A link must start with https://discord.gg/ or https://discord.com/invite/, else <see cref="InvalidOperationException"/>; empty or the default itself puts the default back.</summary>
    Task SaveSettingsAsync(bool personal, bool applicants, string? inviteLink, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Personal pings and the digest go as direct messages: switch on and bot token present.</summary>
    Task<bool> PersonalActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Applicant notices go as direct messages: switch on and bot token present.</summary>
    Task<bool> ApplicantsActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Worker, no guard; a closed direct message (50007 or 403) stamps the agent, success clears it, any failure falls back to the channel for agents only. Never throws except on the run's own cancellation; never logs the text.</summary>
    /// <remarks>Callers enqueue only recipients the channel would have pinged; an ambiguous timeout may deliver both the message and the fallback.</remarks>
    Task DeliverAsync(DiscordDmJob job, CancellationToken cancellationToken = default);
}
