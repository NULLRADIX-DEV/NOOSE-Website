using System.Collections.Concurrent;
using System.Security.Claims;
using NOOSE_Website.Authorization;

namespace NOOSE_Website.Services.Import;

/// <summary>How many Google imports one agent may start; Docs and Slides draw from the same slots.</summary>
/// <remarks>Here and not in the rate-limit middleware: an import started from a Blazor button travels over SignalR
/// and never passes it. Process-wide like the app itself, which runs on one instance.</remarks>
public static class GoogleImportQuota
{
    /// <summary>Imports and refreshes one agent may start within <see cref="Window"/>.</summary>
    public const int PerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Recent = new(StringComparer.Ordinal);

    /// <summary>Takes one slot of the agent's quota or refuses.</summary>
    public static void Take(ClaimsPrincipal actor)
    {
        var key = actor.GetAgentId() ?? string.Empty;
        var queue = Recent.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = DateTime.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }
            if (queue.Count >= PerWindow)
            {
                throw new InvalidOperationException(
                    $"Du hast in den letzten {Window.TotalMinutes:0} Minuten {PerWindow} Importe gestartet. Warte kurz.");
            }
            queue.Enqueue(now);
        }
    }
}
