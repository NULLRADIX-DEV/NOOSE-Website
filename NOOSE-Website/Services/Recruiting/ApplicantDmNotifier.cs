using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IApplicantDmNotifier" />
public sealed class ApplicantDmNotifier(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordDmService directMessages,
    IDiscordWebhookService webhook,
    IDiscordDmQueue queue,
    ILogger<ApplicantDmNotifier> logger) : IApplicantDmNotifier
{
    public async Task NotifyAsync(string applicantAgentId, ApplicantDmEvent applicantEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(applicantAgentId) || !await directMessages.ApplicantsActiveAsync(cancellationToken))
            {
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var discordId = await db.Users.AsNoTracking()
                .Where(u => u.Id == applicantAgentId && u.Status == AgentStatus.Applicant)
                .Select(u => u.DiscordId)
                .FirstOrDefaultAsync(cancellationToken);
            if (!DiscordWebhookService.IsSnowflake(discordId))
            {
                return;
            }

            var (text, route) = ApplicantDmTexts.For(applicantEvent);
            var baseUrl = (await webhook.GetConfigAsync(cancellationToken)).SiteBaseUrl;
            // a blank line keeps the link from gluing onto the sentence
            queue.Enqueue(new DiscordDmJob(applicantAgentId, discordId!,
                $"{text}\n\n{DiscordWebhookService.Link(baseUrl, route)}", null, null));
        }
        catch (Exception ex)
        {
            // never the text or the Discord id
            logger.LogWarning("Applicant direct message for agent {AgentId} not queued ({Error}).",
                applicantAgentId, ex.GetType().Name);
        }
    }
}
