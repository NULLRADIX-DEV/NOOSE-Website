using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Usage;

namespace NOOSE_Website.Services.Usage;

/// <inheritdoc cref="IUsageStore" />
public sealed class UsageStore(IDbContextFactory<AppDbContext> dbFactory, UsageCollector collector) : IUsageStore
{
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var deltas = collector.Snapshot();
        if (deltas.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var delta in deltas)
        {
            await UpsertAsync(db, delta, cancellationToken);
            // per row, so a failure replays nothing
            collector.Acknowledge([delta]);
        }
    }

    // update first, insert when new
    private static async Task UpsertAsync(AppDbContext db, UsageDelta delta, CancellationToken cancellationToken)
    {
        if (await UpdateAsync(db, delta, cancellationToken) > 0)
        {
            return;
        }

        try
        {
            db.UsageDays.Add(new UsageDay { Day = delta.Day, Key = delta.Key, Events = delta.Events, Agents = delta.Agents });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await UpdateAsync(db, delta, cancellationToken);
        }
    }

    private static async Task<int> UpdateAsync(AppDbContext db, UsageDelta delta, CancellationToken cancellationToken)
    {
        var day = delta.Day;
        var key = delta.Key;
        var events = delta.Events;
        var agents = delta.Agents;
        var rows = await db.UsageDays.Where(u => u.Day == day && u.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Events, u => u.Events + events), cancellationToken);
        if (rows > 0)
        {
            // only rises
            await db.UsageDays.Where(u => u.Day == day && u.Key == key && u.Agents < agents)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Agents, agents), cancellationToken);
        }
        return rows;
    }

    public async Task<int> PurgeAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var cutoff = UsageCollector.DayOf(nowUtc).AddDays(-UsageRules.RetentionDays);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UsageDays.Where(u => u.Day < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
