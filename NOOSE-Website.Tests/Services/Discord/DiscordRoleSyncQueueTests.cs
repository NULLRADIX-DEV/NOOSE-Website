using NOOSE_Website.Infrastructure.Discord;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The queue between the write paths and the worker: jobs come out as they went in, and adding never blocks.</summary>
public class DiscordRoleSyncQueueTests
{
    private static List<DiscordRoleSyncJob> Drain(IDiscordRoleSyncQueue queue)
    {
        var jobs = new List<DiscordRoleSyncJob>();
        while (queue.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }
        return jobs;
    }

    [Fact]
    public void Enqueue_adds_an_agent_job_with_the_change_reason()
    {
        var queue = new DiscordRoleSyncQueue();

        queue.Enqueue("agent-1");

        Assert.Equal(new AgentJob("agent-1", DiscordRoleSyncReason.AgentChanged), Assert.Single(Drain(queue)));
    }

    [Fact]
    public void EnqueueDeleted_adds_a_deleted_job_with_the_discord_id()
    {
        var queue = new DiscordRoleSyncQueue();

        queue.EnqueueDeleted("200000000000000001");

        Assert.Equal(new DeletedJob("200000000000000001", DiscordRoleSyncReason.AccountDeleted), Assert.Single(Drain(queue)));
    }

    [Fact]
    public void EnqueueAll_adds_an_all_job_with_the_given_reason()
    {
        var queue = new DiscordRoleSyncQueue();

        queue.EnqueueAll(DiscordRoleSyncReason.Daily);

        Assert.Equal(new AllJob("Tagesabgleich"), Assert.Single(Drain(queue)));
    }

    [Fact]
    public void Jobs_come_out_in_the_order_they_went_in()
    {
        var queue = new DiscordRoleSyncQueue();

        queue.Enqueue("a");
        queue.EnqueueDeleted("200000000000000001");
        queue.EnqueueAll(DiscordRoleSyncReason.Manual);
        queue.Enqueue("b");

        Assert.Equal(
            [
                new AgentJob("a", DiscordRoleSyncReason.AgentChanged),
                new DeletedJob("200000000000000001", DiscordRoleSyncReason.AccountDeleted),
                new AllJob(DiscordRoleSyncReason.Manual),
                new AgentJob("b", DiscordRoleSyncReason.AgentChanged),
            ],
            Drain(queue));
    }

    [Fact]
    public void Reader_is_empty_without_jobs()
    {
        var queue = new DiscordRoleSyncQueue();

        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Adding_never_blocks_or_drops_jobs()
    {
        var queue = new DiscordRoleSyncQueue();

        for (var i = 0; i < 5_000; i++)
        {
            queue.Enqueue($"agent-{i}");
        }

        Assert.Equal(5_000, Drain(queue).Count);
    }

    [Fact]
    public void Reasons_are_bare_because_the_service_adds_the_audit_prefix()
    {
        Assert.Equal("Änderung am Agenten", DiscordRoleSyncReason.AgentChanged);
        Assert.Equal("Konto gelöscht", DiscordRoleSyncReason.AccountDeleted);
        Assert.Equal("Tagesabgleich", DiscordRoleSyncReason.Daily);
        Assert.Equal("von Hand angestoßen", DiscordRoleSyncReason.Manual);
    }
}
