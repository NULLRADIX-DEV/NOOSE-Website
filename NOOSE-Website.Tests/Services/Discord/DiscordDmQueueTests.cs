using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The queue between the write paths and the DM worker: jobs come out as they went in, and adding never blocks.</summary>
public class DiscordDmQueueTests
{
    private static List<DiscordDmJob> Drain(IDiscordDmQueue queue)
    {
        var jobs = new List<DiscordDmJob>();
        while (queue.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }
        return jobs;
    }

    private static DiscordDmJob Job(string agentId)
        => new(agentId, "200000000000000001", "Text", NotificationType.JobAssigned, "/aufgaben");

    [Fact]
    public void A_new_queue_is_empty()
        => Assert.Empty(Drain(new DiscordDmQueue()));

    [Fact]
    public void Jobs_come_out_in_the_order_they_went_in()
    {
        var queue = new DiscordDmQueue();

        queue.Enqueue(Job("a"));
        queue.Enqueue(Job("b"));
        queue.Enqueue(Job("c"));

        Assert.Equal(["a", "b", "c"], Drain(queue).Select(j => j.AgentId));
    }

    [Fact]
    public void A_job_keeps_every_field()
    {
        var queue = new DiscordDmQueue();
        var job = new DiscordDmJob("a", "200000000000000001", "Text", NotificationType.PingDigest, "/benachrichtigungen",
            "<@200000000000000001> 1× Aufgabe");

        queue.Enqueue(job);

        Assert.Equal(job, Assert.Single(Drain(queue)));
    }

    [Fact]
    public void An_applicant_job_has_no_fallback()
    {
        var job = new DiscordDmJob("a", "200000000000000001", "Text", null, null);

        Assert.Null(job.FallbackType);
        Assert.Null(job.FallbackHref);
        Assert.Null(job.FallbackContent);
    }

    [Fact]
    public async Task A_waiting_reader_gets_a_job_added_later()
    {
        var queue = new DiscordDmQueue();
        var read = queue.Reader.ReadAsync().AsTask();
        Assert.False(read.IsCompleted);

        queue.Enqueue(Job("late"));

        Assert.Equal("late", (await read.WaitAsync(TimeSpan.FromSeconds(10))).AgentId);
    }

    [Fact]
    public void Adding_many_jobs_never_blocks_or_drops()
    {
        var queue = new DiscordDmQueue();

        for (var i = 0; i < 10_000; i++)
        {
            queue.Enqueue(Job(i.ToString()));
        }

        Assert.Equal(10_000, Drain(queue).Count);
    }
}
