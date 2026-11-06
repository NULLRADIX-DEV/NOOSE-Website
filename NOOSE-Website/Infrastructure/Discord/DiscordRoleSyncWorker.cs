using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Discord;

/// <summary>Works off the role sync queue one job at a time, and queues the full run every five minutes.</summary>
public sealed class DiscordRoleSyncWorker(
    IDiscordRoleSyncQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DiscordRoleSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(ConsumeAsync(stoppingToken), ScheduleAsync(stoppingToken));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
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
    private async Task RunAsync(DiscordRoleSyncJob job, CancellationToken stoppingToken)
    {
        try
        {
            if (job is AllJob)
            {
                queue.MarkFullRunStarted();
            }
            using var scope = scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IDiscordRoleSyncService>();
            await (job switch
            {
                AgentJob agent => sync.SyncAgentAsync(agent.AgentId, agent.Reason, stoppingToken),
                DeletedJob deleted => sync.SyncDeletedAsync(deleted.DiscordId, deleted.TeamLead, stoppingToken),
                AllJob all => sync.SyncAllAsync(all.Reason, stoppingToken),
                _ => Task.CompletedTask,
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord role sync job {Job} failed.", job.GetType().Name);
        }
    }

    private async Task ScheduleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            queue.EnqueueAll(DiscordRoleSyncReason.Scheduled);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
