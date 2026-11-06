using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The worker hands queued messages to the DM service, one scope each, and no failure ends it.</summary>
public class DiscordDmWorkerTests
{
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Host()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => NewService());
            _provider = services.BuildServiceProvider();
            Worker = new DiscordDmWorker(Queue, _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DiscordDmWorker>.Instance);
        }

        public DiscordDmQueue Queue { get; } = new();

        public DiscordDmWorker Worker { get; }

        public ConcurrentQueue<string> Calls { get; } = new();

        /// <summary>One per scope the worker opened.</summary>
        public ConcurrentBag<IDiscordDmService> Services { get; } = [];

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

        private IDiscordDmService NewService()
        {
            var service = Substitute.For<IDiscordDmService>();
            Services.Add(service);
            service.DeliverAsync(Arg.Any<DiscordDmJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var job = call.ArgAt<DiscordDmJob>(0);
                Calls.Enqueue($"{job.AgentId}:{job.Content}");
                return job.AgentId switch
                {
                    "faulted" => Task.FromException(new InvalidOperationException("faulted task")),
                    "thrown" => throw new InvalidOperationException("thrown before a task"),
                    _ => Task.CompletedTask,
                };
            });
            return service;
        }
    }

    private static DiscordDmJob Job(string agentId, string content = "Text")
        => new(agentId, "200000000000000001", content, NotificationType.JobAssigned, "/aufgaben");

    [Fact]
    public async Task Each_job_goes_to_deliver_in_order()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Job("a1", "eins"));
        host.Queue.Enqueue(Job("a2", "zwei"));
        await host.WaitForCallsAsync(2);

        Assert.Equal(["a1:eins", "a2:zwei"], host.Calls);
    }

    [Fact]
    public async Task A_failing_job_is_logged_and_the_next_one_still_runs_in_its_own_scope()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);

        host.Queue.Enqueue(Job("faulted"));
        host.Queue.Enqueue(Job("thrown"));
        host.Queue.Enqueue(Job("ok"));
        await host.WaitForCallsAsync(3);

        Assert.Equal(["faulted:Text", "thrown:Text", "ok:Text"], host.Calls);
        Assert.Equal(3, host.Services.Distinct().Count());
        Assert.False(host.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Stopping_ends_the_worker_while_it_waits_for_jobs()
    {
        await using var host = new Host();
        await host.Worker.StartAsync(CancellationToken.None);
        // one job through, so the loop is waiting and not still starting up
        host.Queue.Enqueue(Job("a1"));
        await host.WaitForCallsAsync(1);

        await host.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(host.Worker.ExecuteTask!.IsCompletedSuccessfully);
    }
}
