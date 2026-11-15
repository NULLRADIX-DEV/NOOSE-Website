using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetCord.Rest;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>What the DVO test post did.</summary>
public enum RegulationTestOutcome
{
    Sent,
    NoChannel,
    BotOff,
}

/// <summary>Sends a sample DVO post from the bot settings.</summary>
public interface IRegulationTestPost
{
    /// <summary>Posts the sample into the DVO channel without pinging; a Discord error throws with its reason.</summary>
    Task<RegulationTestOutcome> SendAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRegulationTestPost" />
public sealed class RegulationTestPost(
    IDiscordBotSettingsService settings,
    IDiscordWebhookService webhook,
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceProvider services) : IRegulationTestPost
{
    public async Task<RegulationTestOutcome> SendAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        var bot = await settings.GetAsync(cancellationToken);
        // bot token only
        if (!bot.Enabled || services.GetService<IDiscordBotSender>() is not { } sender)
        {
            return RegulationTestOutcome.BotOff;
        }
        if (bot.DvoChannelId is not { } channel)
        {
            return RegulationTestOutcome.NoChannel;
        }
        var config = await webhook.GetConfigAsync(cancellationToken);
        var messages = NotificationSamples.Regulation(config.Roles.GetValueOrDefault(NotificationType.Announcement),
            await TesterDiscordIdAsync(dbFactory, actor, cancellationToken), config.SiteBaseUrl);
        try
        {
            foreach (var message in messages)
            {
                await sender.SendAsync(channel, message, AllowedMentionsProperties.None, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Der Bot konnte den Test-Beitrag nicht senden ({DiscordBotStatus.Describe(ex)}). Kanalrechte des Bots prüfen.", ex);
        }
        return RegulationTestOutcome.Sent;
    }

    /// <summary>Discord id of the tester, for a chip that pings nobody.</summary>
    internal static async Task<string?> TesterDiscordIdAsync(IDbContextFactory<AppDbContext> dbFactory, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (actor.GetAgentId() is not { } id)
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().Where(a => a.Id == id).Select(a => a.DiscordId).FirstOrDefaultAsync(cancellationToken);
    }
}
