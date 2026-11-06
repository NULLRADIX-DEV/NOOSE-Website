using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Discord;

/// <summary>Works off the direct message queue one job at a time.</summary>
public sealed class DiscordDmWorker(
    IDiscordDmQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DiscordDmWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* ignore */
        }
    }

    /// <summary>One job in its own scope; a failure is logged and never ends the worker.</summary>
    private async Task RunAsync(DiscordDmJob job, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dm = scope.ServiceProvider.GetRequiredService<IDiscordDmService>();
            await dm.DeliverAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            // the job holds the text, so log the agent id only
            logger.LogError(ex, "Discord direct message job for agent {AgentId} failed.", job.AgentId);
        }
    }
}
