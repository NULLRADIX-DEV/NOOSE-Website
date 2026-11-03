using System.Collections.Concurrent;

namespace NOOSE_Website.Services;

/// <summary>Per-agent share quota, process-wide; a Blazor click never passes the HTTP rate limiter.</summary>
public sealed class ChannelShareQuota(TimeProvider? clock = null)
{
    public const int PerWindow = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);

    /// <summary>Takes one slot of the agent's quota or refuses.</summary>
    public void Take(string agentId)
    {
        var queue = _recent.GetOrAdd(agentId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var now = _clock.GetUtcNow();
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }
            if (queue.Count >= PerWindow)
            {
                throw new InvalidOperationException(
                    $"Du hast in den letzten {Window.TotalMinutes:0} Minuten {PerWindow} Akten geteilt. Warte kurz.");
            }
            queue.Enqueue(now);
        }
    }
}
