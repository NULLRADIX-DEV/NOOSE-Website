using System.Security.Claims;
using NOOSE_Website.Infrastructure.Discord;

namespace NOOSE_Website.Services;

/// <summary>The two switches and whether the bot has a token; the switches read as off until an admin turns them on.</summary>
public sealed record DiscordDmSettings(bool Personal, bool Applicants, bool BotConfigured);

/// <summary>Delivers direct messages through the bot and keeps the two switches that allow them.</summary>
public interface IDiscordDmService
{
    /// <summary>Both switches (default off) and whether the bot has a token.</summary>
    Task<DiscordDmSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Admin with write access (write guard first); stores both switches.</summary>
    Task SaveSettingsAsync(bool personal, bool applicants, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Personal pings and the digest go as direct messages: switch on and bot token present.</summary>
    Task<bool> PersonalActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Applicant notices go as direct messages: switch on and bot token present.</summary>
    Task<bool> ApplicantsActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Worker, no guard; a closed direct message (50007 or 403) stamps the agent, success clears it, any failure falls back to the channel for agents only. Never throws except on the run's own cancellation; never logs the text.</summary>
    Task DeliverAsync(DiscordDmJob job, CancellationToken cancellationToken = default);
}
