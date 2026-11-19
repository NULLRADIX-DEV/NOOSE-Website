using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Usage;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

public sealed class UsageStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 19);

    [Fact]
    public async Task Flush_writes_the_counters_and_a_second_flush_adds_nothing()
    {
        using var ctx = new SqliteTestContext();
        var collector = new UsageCollector(new TestClock(Start));
        var store = new UsageStore(ctx.Factory, collector);
        collector.Add("a1", "page:personen");
        collector.Add("a2", "page:personen");

        await store.FlushAsync();
        await store.FlushAsync();

        using var db = ctx.NewContext();
        var row = await db.UsageDays.SingleAsync();
        Assert.Equal((Today, "page:personen", 2L, 2), (row.Day, row.Key, row.Events, row.Agents));
    }

    [Fact]
    public async Task Later_flushes_add_events_and_only_raise_the_agent_count()
    {
        using var ctx = new SqliteTestContext();
        var collector = new UsageCollector(new TestClock(Start));
        var store = new UsageStore(ctx.Factory, collector);
        collector.Add("a1", "page:personen");
        await store.FlushAsync();
        collector.Add("a1", "page:personen");
        collector.Add("a2", "page:personen");
        await store.FlushAsync();

        using var db = ctx.NewContext();
        var row = await db.UsageDays.SingleAsync();
        Assert.Equal(3, row.Events);
        Assert.Equal(2, row.Agents);

        // a restart: a fresh collector knows one agent only; the stored number stays at 2
        var restarted = new UsageCollector(new TestClock(Start));
        restarted.Add("a3", "page:personen");
        await new UsageStore(ctx.Factory, restarted).FlushAsync();
        using var after = ctx.NewContext();
        var again = await after.UsageDays.SingleAsync();
        Assert.Equal(4, again.Events);
        Assert.Equal(2, again.Agents);
    }

    [Fact]
    public async Task Purge_deletes_only_rows_older_than_the_retention()
    {
        using var ctx = new SqliteTestContext();
        var store = new UsageStore(ctx.Factory, new UsageCollector(new TestClock(Start)));
        using (var db = ctx.NewContext())
        {
            db.UsageDays.AddRange(
                new UsageDay { Day = Today.AddDays(-UsageRules.RetentionDays - 1), Key = "search:run", Events = 1, Agents = 1 },
                new UsageDay { Day = Today.AddDays(-UsageRules.RetentionDays), Key = "search:run", Events = 1, Agents = 1 },
                new UsageDay { Day = Today, Key = "search:run", Events = 1, Agents = 1 });
            db.SaveChanges();
        }

        Assert.Equal(1, await store.PurgeAsync(Start.UtcDateTime));

        using var check = ctx.NewContext();
        Assert.Equal(2, await check.UsageDays.CountAsync());
    }

    [Fact]
    public void The_table_holds_a_day_a_key_and_two_numbers_and_nothing_else()
    {
        var columns = typeof(UsageDay).GetProperties().Select(p => p.Name).Order().ToList();
        Assert.Equal(["Agents", "Day", "Events", "Key"], columns);
    }
}
