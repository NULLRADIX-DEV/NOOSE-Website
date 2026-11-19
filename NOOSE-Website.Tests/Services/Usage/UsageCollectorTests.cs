using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class UsageCollectorTests
{
    // 10:00 UTC in November is 11:00 in Berlin
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Counts_events_and_distinct_agents_per_day_and_key()
    {
        var collector = new UsageCollector(new TestClock(Start));
        collector.Add("a1", "page:personen");
        collector.Add("a1", "page:personen");
        collector.Add("a2", "page:personen");
        collector.Add("a1", "search:run");

        var snapshot = collector.Snapshot().ToDictionary(d => d.Key);
        Assert.Equal(new UsageDelta(new DateOnly(2026, 11, 19), "page:personen", 3, 2), snapshot["page:personen"]);
        Assert.Equal(new UsageDelta(new DateOnly(2026, 11, 19), "search:run", 1, 1), snapshot["search:run"]);
    }

    [Fact]
    public void The_day_is_the_berlin_day_not_the_utc_day()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero));
        var collector = new UsageCollector(clock);
        collector.Add("a1", "search:run");

        Assert.Equal(new DateOnly(2026, 11, 20), Assert.Single(collector.Snapshot()).Day);
    }

    [Fact]
    public void Acknowledge_writes_each_event_once_and_keeps_what_came_in_meanwhile()
    {
        var collector = new UsageCollector(new TestClock(Start));
        collector.Add("a1", "search:run");
        var first = collector.Snapshot();
        collector.Add("a2", "search:run");
        collector.Acknowledge(first);

        var rest = Assert.Single(collector.Snapshot());
        Assert.Equal(1, rest.Events);
        Assert.Equal(2, rest.Agents);

        collector.Acknowledge([rest]);
        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public void Nothing_is_lost_when_a_write_fails()
    {
        var collector = new UsageCollector(new TestClock(Start));
        collector.Add("a1", "search:run");
        _ = collector.Snapshot();

        // no Acknowledge: the next snapshot still carries it
        Assert.Equal(1, Assert.Single(collector.Snapshot()).Events);
    }

    [Fact]
    public void An_agent_is_counted_once_per_day_and_again_the_next_day()
    {
        var clock = new TestClock(Start);
        var collector = new UsageCollector(clock);
        collector.Add("a1", "search:run");
        collector.Acknowledge(collector.Snapshot());

        clock.Now = Start.AddDays(1);
        collector.Add("a1", "search:run");

        var delta = Assert.Single(collector.Snapshot());
        Assert.Equal(new DateOnly(2026, 11, 20), delta.Day);
        Assert.Equal(1, delta.Agents);
    }

    [Fact]
    public void The_agent_sets_of_a_finished_day_are_dropped_once_written()
    {
        var clock = new TestClock(Start);
        var collector = new UsageCollector(clock);
        collector.Add("a1", "search:run");
        collector.Add("a2", "search:run");
        collector.Acknowledge(collector.Snapshot());

        clock.Now = Start.AddDays(1);
        collector.Acknowledge([]);
        // an agent returning to yesterday's row cannot happen; today starts from nothing
        collector.Add("a3", "search:run");
        var delta = Assert.Single(collector.Snapshot());
        Assert.Equal(1, delta.Agents);
    }
}
