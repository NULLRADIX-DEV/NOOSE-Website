using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordMembershipCheck" />
public sealed class DiscordMembershipCheck(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordBotClient bot,
    IDiscordRoleSyncService roleSync,
    ILogger<DiscordMembershipCheck> logger) : IDiscordMembershipCheck
{
    // the page waits on this; the bot's own client allows far longer
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<DiscordMembership> CheckAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(agentId) || !bot.IsConfigured)
            {
                return DiscordMembership.Unknown;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);

            var guildId = (await roleSync.GetSettingsAsync(limit.Token)).GuildId;
            if (!DiscordWebhookService.IsSnowflake(guildId))
            {
                return DiscordMembership.Unknown;
            }

            await using var db = await dbFactory.CreateDbContextAsync(limit.Token);
            var discordId = await db.Users.AsNoTracking()
                .Where(u => u.Id == agentId)
                .Select(u => u.DiscordId)
                .FirstOrDefaultAsync(limit.Token);
            if (!DiscordWebhookService.IsSnowflake(discordId))
            {
                return DiscordMembership.Unknown;
            }

            var roles = await bot.GetMemberRolesAsync(guildId!, discordId!, limit.Token);
            return roles is null ? DiscordMembership.NotMember : DiscordMembership.Member;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // never the token, the Discord id or the server id; a timeout also lands here
            logger.LogWarning("Discord membership check for agent {AgentId} failed ({Error}).", agentId, Describe(ex));
            return DiscordMembership.Unknown;
        }
    }

    /// <summary>Status and code of a Discord refusal, else the exception type; never a message that could echo a secret.</summary>
    private static string Describe(Exception ex)
        => ex is DiscordBotException discord ? $"{discord.Status}/{discord.Code?.ToString() ?? "-"}" : ex.GetType().Name;
}
