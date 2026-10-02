namespace NOOSE_Website.Services;

/// <summary>Lets only one full threat-score recalculation run at a time, with a cooldown between two (singleton).</summary>
/// <remarks>
/// A full recalculation writes every active person and faction. Without the gate, every open tab could start one
/// more run in parallel, and a click right after the last run did all the work again.
/// </remarks>
public sealed class ThreatScoreRecalculationGate
{
    /// <summary>Minimum time between the end of one full recalculation and the start of the next.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _running = new(1, 1);

    // only read and written while the gate is held
    private DateTime? _lastFinishedUtc;

    /// <summary>Takes the gate without waiting; false while another recalculation runs.</summary>
    public bool TryEnter() => _running.Wait(0);

    /// <summary>Releases the gate. Call it only after <see cref="TryEnter"/> returned true.</summary>
    public void Exit() => _running.Release();

    /// <summary>Time left until the next run may start; <see cref="TimeSpan.Zero"/> when it may start now.</summary>
    public TimeSpan RemainingCooldown(DateTime nowUtc)
        => _lastFinishedUtc is { } last && nowUtc - last < Cooldown ? Cooldown - (nowUtc - last) : TimeSpan.Zero;

    /// <summary>Records the end of a successful run, which starts the cooldown.</summary>
    public void MarkFinished(DateTime nowUtc) => _lastFinishedUtc = nowUtc;
}
