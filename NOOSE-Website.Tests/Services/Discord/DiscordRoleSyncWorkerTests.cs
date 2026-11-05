using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The worker hands queued jobs to the sync service, one scope each, and no failure ends it.</summary>
public class DiscordRoleSyncWorkerTests
{
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Host()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => NewSync());
            _provider = services.BuildServiceProvider();
            Worker = new DiscordRoleSyncWorker(Queue, _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DiscordRoleSyncWorker>.Instance);
        }

        public DiscordRoleSyncQueue Queue { get; } = new();

        public DiscordRoleSyncWorker Worker { get; }

        public ConcurrentQueue<string> Calls { get; } = new();

        /// <summary>One per scope the worker opened.</summary>
        public ConcurrentBag<IDiscordRoleSyncService> Services { get; } = [];

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Calls.Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            Assert.True(Calls.Count >= count, $"Expected {count} calls, saw {Calls.Count}.");
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
            await _provider.DisposeAsync();
        }

        private IDiscordRoleSyncService NewSync()
        {
            var sync = Substitute.For<IDiscordRoleSyncService>();
            Services.Add(sync);
            sync.SyncAgentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var agentId = call.ArgAt<string>(0);
                Calls.Enqueue($"agent:{agentId}:{call.ArgAt<string>(1)}");
                return agentId switch
                {
                    "faulted" => Task.FromException(new InvalidOperationException("faulted task")),
                    "thrown" => throw new InvalidOperationException("thrown before a task"),
                    _ => Task.CompletedTask,
                };
            });
            sync.SyncDeletedAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Calls.Enqueue($"deleted:{call.ArgAt<string>(0)}:{call.ArgAt<bool>(1)}");
                return Task.CompletedTask;
            });
            sync.SyncAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Calls.Enqueue($"all:{call.ArgAt<string>(0)}");
                return Task.CompletedTask;
            });
            return sync;
        }
    }

    [Fact]
    public async Task Each_job_goes_to_the_matching_service_call_with_its_bare_reason()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue("a1");
        host.Queue.EnqueueDeleted("200000000000000001", isTeamLead: true);
        host.Queue.EnqueueAll(DiscordRoleSyncReason.Manual);
        await host.WaitForCallsAsync(3);

        Assert.Equal(
            ["agent:a1:Änderung am Agenten", "deleted:200000000000000001:True", "all:von Hand angestoßen"],
            host.Calls);
    }

    [Fact]
    public async Task A_failing_job_is_logged_and_the_next_one_still_runs_in_its_own_scope()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue("faulted");
        host.Queue.Enqueue("thrown");
        host.Queue.Enqueue("ok");
        await host.WaitForCallsAsync(3);

        Assert.Equal(
            ["agent:faulted:Änderung am Agenten", "agent:thrown:Änderung am Agenten", "agent:ok:Änderung am Agenten"],
            host.Calls);
        Assert.Equal(3, host.Services.Distinct().Count());
        Assert.False(host.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Stopping_ends_the_worker_even_with_nothing_queued()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        await host.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(host.Worker.ExecuteTask!.IsCompletedSuccessfully);
    }
}
