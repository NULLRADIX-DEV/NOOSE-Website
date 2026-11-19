namespace NOOSE_Website.Services.Usage;

/// <summary>What one feature gained since the last successful write: events, and the agents seen that day so far.</summary>
public readonly record struct UsageDelta(DateOnly Day, string Key, long Events, int Agents);

/// <summary>Counts feature uses in memory; the agent sets never leave it and are dropped after the day.</summary>
/// <remarks>A restart forgets today's sets, so the stored agent count can undercount, never overcount.</remarks>
public sealed class UsageCollector(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<(DateOnly Day, string Key), Bucket> _buckets = [];

    private sealed class Bucket
    {
        public long Pending;
        public int Reported;
        public readonly HashSet<string> Agents = new(StringComparer.Ordinal);
    }

    /// <summary>Berlin calendar day of an instant; the day the statistics speak of.</summary>
    public static DateOnly DayOf(DateTime utc) => MeetingTime.Day(utc);

    /// <summary>Counts one use. The caller has already decided that this agent and key may be counted.</summary>
    public void Add(string agentId, string key)
    {
        var day = DayOf(_clock.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            if (!_buckets.TryGetValue((day, key), out var bucket))
            {
                bucket = new Bucket();
                _buckets[(day, key)] = bucket;
            }
            bucket.Pending++;
            bucket.Agents.Add(agentId);
        }
    }

    /// <summary>What still has to be written; changes nothing until <see cref="Acknowledge"/>.</summary>
    public IReadOnlyList<UsageDelta> Snapshot()
    {
        lock (_gate)
        {
            return [.. _buckets
                .Where(b => b.Value.Pending > 0 || b.Value.Agents.Count > b.Value.Reported)
                .Select(b => new UsageDelta(b.Key.Day, b.Key.Key, b.Value.Pending, b.Value.Agents.Count))];
        }
    }

    /// <summary>Marks a snapshot as written and discards the agent sets of days that are over.</summary>
    public void Acknowledge(IReadOnlyList<UsageDelta> written)
    {
        var today = DayOf(_clock.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            foreach (var delta in written)
            {
                if (_buckets.TryGetValue((delta.Day, delta.Key), out var bucket))
                {
                    bucket.Pending = Math.Max(0, bucket.Pending - delta.Events);
                    bucket.Reported = Math.Max(bucket.Reported, delta.Agents);
                }
            }

            foreach (var key in _buckets
                         .Where(b => b.Key.Day < today && b.Value.Pending == 0 && b.Value.Agents.Count <= b.Value.Reported)
                         .Select(b => b.Key)
                         .ToList())
            {
                _buckets.Remove(key);
            }
        }
    }
}
