using System.Collections.Concurrent;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure.Presentations;

/// <summary>What changed in a live session.</summary>
public enum PresentationLiveEvent
{
    Started,
    SlideChanged,
    ModeChanged,
    ViewersChanged,
    Ended,

    /// <summary>A slide was added, edited, moved or removed while the deck runs; live pages load it again.</summary>
    SlidesChanged,

    /// <summary>The presenter invited or uninvited agents; whoever watches only by invitation checks again.</summary>
    InvitesChanged,
}

/// <summary>An immutable picture of one live session, safe to hand to any circuit.</summary>
/// <param name="InvitedIds">Agents the presenter invited to this very session; the invitation ends with it.</param>
public sealed record LiveSessionSnapshot(
    string PresentationId,
    string Title,
    string PresenterId,
    string PresenterCodename,
    DateTime StartedAt,
    int SlideIndex,
    PresentationFollowMode Mode,
    IReadOnlyList<string> ViewerCodenames,
    IReadOnlyList<string> InvitedIds);

/// <summary>Process-wide in-memory broadcaster for live presentation sessions.</summary>
public sealed class PresentationBroadcaster
{
    /// <summary>Fired with the presentation and what changed; subscribers read the new state from the registry.</summary>
    public event Action<string, PresentationLiveEvent>? Changed;

    public void Report(string presentationId, PresentationLiveEvent kind) => Changed?.Invoke(presentationId, kind);
}

/// <summary>The live sessions of this host, in memory.</summary>
/// <remarks>
/// A restart ends every session, which is acceptable: the host runs as one instance like its workers, and a
/// presentation is minutes long. Viewers and the presenter are counted per circuit (a key per open page), so two
/// tabs of the same agent do not end the session when one closes. Nothing here checks rights; the live service does
/// that before it calls in.
/// </remarks>
public sealed class PresentationLiveRegistry
{
    /// <summary>How long a session survives without the presenter's page open, e.g. across a reconnect.</summary>
    public static readonly TimeSpan PresenterGrace = TimeSpan.FromMinutes(2);

    private sealed class State(string presentationId, string title, string presenterId, string presenterCodename, DateTime startedAt)
    {
        public readonly object Gate = new();
        public string PresentationId { get; } = presentationId;
        public string Title { get; set; } = title;
        public string PresenterId { get; } = presenterId;
        public string PresenterCodename { get; } = presenterCodename;
        public DateTime StartedAt { get; } = startedAt;
        public int SlideIndex { get; set; }
        public PresentationFollowMode Mode { get; set; } = PresentationFollowMode.Strict;
        public Dictionary<string, string> Viewers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PresenterConnections { get; } = new(StringComparer.Ordinal);
        public DateTime? PresenterGoneSince { get; set; } = startedAt;
        public HashSet<string> Invited { get; } = new(StringComparer.Ordinal);

        public LiveSessionSnapshot Snapshot()
            => new(PresentationId, Title, PresenterId, PresenterCodename, StartedAt, SlideIndex, Mode,
                Viewers.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture).ToList(),
                Invited.Order(StringComparer.Ordinal).ToList());
    }

    private readonly ConcurrentDictionary<string, State> _sessions = new(StringComparer.Ordinal);

    /// <summary>Starts a session, or returns the running one when the same presenter starts again; null when someone else presents.</summary>
    public LiveSessionSnapshot? Start(string presentationId, string title, string presenterId, string presenterCodename, DateTime now)
    {
        var state = _sessions.GetOrAdd(presentationId, _ => new State(presentationId, title, presenterId, presenterCodename, now));
        lock (state.Gate)
        {
            return state.PresenterId == presenterId ? state.Snapshot() : null;
        }
    }

    public LiveSessionSnapshot? Get(string presentationId)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return null;
        }
        lock (state.Gate)
        {
            return state.Snapshot();
        }
    }

    public IReadOnlyList<LiveSessionSnapshot> All()
        => _sessions.Values.Select(s => { lock (s.Gate) { return s.Snapshot(); } }).ToList();

    /// <summary>Moves the session to a slide; false when there is no session or the actor is not its presenter.</summary>
    public bool SetSlide(string presentationId, string presenterId, int index)
        => Mutate(presentationId, presenterId, s => s.SlideIndex = Math.Max(0, index));

    public bool SetMode(string presentationId, string presenterId, PresentationFollowMode mode)
        => Mutate(presentationId, presenterId, s => s.Mode = mode);

    public bool End(string presentationId) => _sessions.TryRemove(presentationId, out _);

    /// <summary>Follows a renamed deck, so banner and list show the new title.</summary>
    public void SetTitle(string presentationId, string title)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return;
        }
        lock (state.Gate)
        {
            state.Title = title;
        }
    }

    /// <summary>Invites agents to the running session; returns the ones not invited before, or null when there is
    /// no session or the actor is not its presenter.</summary>
    public IReadOnlyList<string>? Invite(string presentationId, string presenterId, IEnumerable<string> agentIds)
    {
        List<string>? added = null;
        return Mutate(presentationId, presenterId, s => added = agentIds.Where(s.Invited.Add).ToList()) ? added : null;
    }

    /// <summary>Takes an invitation back; false when nothing was taken, also when the actor is not the presenter.</summary>
    public bool Uninvite(string presentationId, string presenterId, string agentId)
    {
        var removed = false;
        return Mutate(presentationId, presenterId, s => removed = s.Invited.Remove(agentId)) && removed;
    }

    /// <summary>Whether the agent is invited to the session running now.</summary>
    public bool IsInvited(string presentationId, string agentId)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return false;
        }
        lock (state.Gate)
        {
            return state.Invited.Contains(agentId);
        }
    }

    /// <summary>Counts a viewer's page; returns the session as it stands.</summary>
    public LiveSessionSnapshot? Join(string presentationId, string connectionKey, string codename)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return null;
        }
        lock (state.Gate)
        {
            state.Viewers[connectionKey] = codename;
            return state.Snapshot();
        }
    }

    /// <summary>Forgets a viewer's page; true when it was counted.</summary>
    public bool Leave(string presentationId, string connectionKey)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return false;
        }
        lock (state.Gate)
        {
            return state.Viewers.Remove(connectionKey);
        }
    }

    /// <summary>The presenter's page is open; the session stays alive while at least one is.</summary>
    public bool PresenterConnected(string presentationId, string presenterId, string connectionKey)
        => Mutate(presentationId, presenterId, s =>
        {
            s.PresenterConnections.Add(connectionKey);
            s.PresenterGoneSince = null;
        });

    public void PresenterDisconnected(string presentationId, string connectionKey, DateTime now)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return;
        }
        lock (state.Gate)
        {
            if (state.PresenterConnections.Remove(connectionKey) && state.PresenterConnections.Count == 0)
            {
                state.PresenterGoneSince = now;
            }
        }
    }

    /// <summary>Ends the sessions whose presenter has been away longer than the grace; returns their ids.</summary>
    public IReadOnlyList<string> ExpireAbandoned(DateTime now)
    {
        var ended = new List<string>();
        foreach (var state in _sessions.Values)
        {
            lock (state.Gate)
            {
                // decided and removed under the lock, and only this very session: a presenter reconnecting or a
                // session started anew in between must survive
                if (state.PresenterGoneSince is { } gone && now - gone > PresenterGrace
                    && _sessions.TryRemove(new KeyValuePair<string, State>(state.PresentationId, state)))
                {
                    ended.Add(state.PresentationId);
                }
            }
        }
        return ended;
    }

    private bool Mutate(string presentationId, string presenterId, Action<State> change)
    {
        if (!_sessions.TryGetValue(presentationId, out var state))
        {
            return false;
        }
        lock (state.Gate)
        {
            if (state.PresenterId != presenterId)
            {
                return false;
            }
            change(state);
            return true;
        }
    }
}

/// <summary>Ends live sessions whose presenter's page has been closed longer than the grace.</summary>
public sealed class PresentationLiveSweeper(PresentationLiveRegistry registry, PresentationBroadcaster broadcaster,
    ILogger<PresentationLiveSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var id in registry.ExpireAbandoned(DateTime.UtcNow))
                    {
                        broadcaster.Report(id, PresentationLiveEvent.Ended);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Live presentation sweep failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
    }
}
