using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="INavPreferencesService" />
public class NavPreferencesService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : INavPreferencesService
{
    private const int RecentsCap = 15;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public event Action? Changed;

    /// <summary>The demo visitor's own preferences; scoped service, so one circuit.</summary>
    private NavPreferences? _demoSession;
    private readonly object _demoSessionGate = new();

    private static string CacheKey(string agentId) => $"nav:{agentId}";

    public async Task<NavPreferences> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return new NavPreferences();
        }
        // ignores the stored row
        if (IsSharedDemo(agentId))
        {
            lock (_demoSessionGate)
            {
                return _demoSession ??= new NavPreferences();
            }
        }
        if (cache.TryGetValue(CacheKey(agentId), out NavPreferences? cached) && cached is not null)
        {
            return cached;
        }

        // The fill runs under the same lock as a mutation, not just the mutation itself. A miss reads the blob
        // outside it, a mutation commits meanwhile, and the reader's cache write then puts the pre-mutation blob
        // back for the rest of the cache window - the very overwrite the lock exists to prevent, entered from
        // the read side. Re-checked inside, because the agent we queued behind has usually just filled it.
        var writer = WriterFor(agentId);
        await writer.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(CacheKey(agentId), out NavPreferences? filled) && filled is not null)
            {
                return filled;
            }
            NavPreferences prefs;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var json = await db.Users.AsNoTracking()
                    .Where(a => a.Id == agentId)
                    .Select(a => a.NavPreferencesJson)
                    .FirstOrDefaultAsync(cancellationToken);
                prefs = Deserialize(json);
            }
            catch
            {
                /* best effort */
                return new NavPreferences();
            }
            cache.Set(CacheKey(agentId), prefs, CacheDuration);
            return prefs;
        }
        finally
        {
            writer.Release();
        }
    }

    public Task ToggleFavoriteAsync(string agentId, NavFavorite favorite, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p =>
        {
            var id = INavPreferencesService.FavoriteId(favorite);
            var existing = p.Favorites.FirstOrDefault(f => INavPreferencesService.FavoriteId(f) == id);
            if (existing is not null)
            {
                p.Favorites.Remove(existing);
            }
            else
            {
                p.Favorites.Add(favorite);
            }
        }, cancellationToken);

    public Task ReorderFavoritesAsync(string agentId, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p =>
        {
            var byId = p.Favorites.ToDictionary(INavPreferencesService.FavoriteId);
            var reordered = new List<NavFavorite>();
            foreach (var id in orderedIds)
            {
                if (byId.Remove(id, out var f))
                {
                    reordered.Add(f);
                }
            }
            // any not listed keep at the end
            reordered.AddRange(p.Favorites.Where(f => byId.ContainsKey(INavPreferencesService.FavoriteId(f))));
            p.Favorites = reordered;
        }, cancellationToken);

    public Task SetHiddenAsync(string agentId, string key, bool hidden, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p =>
        {
            if (hidden)
            {
                p.HiddenKeys.Add(key);
            }
            else
            {
                p.HiddenKeys.Remove(key);
            }
        }, cancellationToken);

    public Task SetOrderAsync(string agentId, IReadOnlyList<string> orderedKeys, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.Order = orderedKeys.ToList(), cancellationToken);

    public Task SetStartRouteAsync(string agentId, string? route, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.StartRoute = string.IsNullOrWhiteSpace(route) ? null : route, cancellationToken);

    // drawer/area state are self-updating in the UI, so no live-refresh broadcast
    public Task SetDrawerOpenAsync(string agentId, bool open, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.DrawerOpen = open, cancellationToken, notify: false);

    public Task SetLastAreaAsync(string agentId, string? area, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.LastArea = string.IsNullOrWhiteSpace(area) ? null : area,
            cancellationToken, notify: false);

    // high-frequency: no broadcast (drawer does not show recents)
    public Task PushRecentAsync(string agentId, RecentItem item, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p =>
        {
            p.Recents.RemoveAll(r => item.EntityId is not null
                ? r.EntityType == item.EntityType && r.EntityId == item.EntityId
                : r.EntityId is null && r.Route == item.Route);
            p.Recents.Insert(0, item);
            if (p.Recents.Count > RecentsCap)
            {
                p.Recents.RemoveRange(RecentsCap, p.Recents.Count - RecentsCap);
            }
        }, cancellationToken, notify: false);

    // page-local marker: no broadcast, the drawer does not show it
    public Task SetChronikLastSeenAsync(string agentId, DateTime seenUtc, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.ChronikLastSeenUtc = seenUtc, cancellationToken, notify: false);

    // same shape as the chronicle marker: the update window reads it, the drawer shows nothing.
    // Forward only: a window closed late in one tab must not undo the page read in another.
    public Task SetNeuerungenLastSeenAsync(string agentId, DateTime seenUtc, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.NeuerungenLastSeenUtc = p.NeuerungenLastSeenUtc is { } seen && seen > seenUtc
            ? seen
            : seenUtc, cancellationToken, notify: false);

    public Task SetGlossarBlasenAsync(string agentId, bool enabled, CancellationToken cancellationToken = default)
        => MutateAsync(agentId, p => p.GlossarBlasen = enabled, cancellationToken);

    // one tiny idempotent step per call, not a save-all: the agent is navigating while these land, and the
    // whole blob is last-writer-wins against the recents push that every navigation fires
    public Task MarkOnboardingStepAsync(string agentId, string stepKey, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(stepKey)
            ? Task.CompletedTask
            : MutateAsync(agentId, p => p.OnboardingDone.Add(stepKey), cancellationToken, notify: false);

    // notifies: the drawer and the header menus list the views, and both should see a save at once
    public async Task<SavedViewOutcome> SaveViewAsync(string agentId, string label, string route, string icon,
        CancellationToken cancellationToken = default)
    {
        // refused before the lock, so a nameless or foreign route costs no write
        if (string.IsNullOrWhiteSpace(agentId) || IsSharedDemo(agentId)
            || SavedViewRules.Normalise(label).Length == 0 || !SavedViewRules.IsLocalRoute(route))
        {
            return SavedViewOutcome.Invalid;
        }
        var outcome = SavedViewOutcome.Invalid;
        await MutateAsync(agentId, p => outcome = SavedViewRules.Add(p.SavedViews, label, route, icon), cancellationToken);
        return outcome;
    }

    public Task RemoveViewAsync(string agentId, string viewId, CancellationToken cancellationToken = default)
        => IsSharedDemo(agentId)
            ? Task.CompletedTask
            : MutateAsync(agentId, p => SavedViewRules.Remove(p.SavedViews, viewId), cancellationToken);

    /// <summary>The public demo's one account, which every anonymous visitor shares.</summary>
    /// <remarks>
    /// Its preferences live in <see cref="_demoSession"/> and never reach the row: one visitor hiding every entry
    /// emptied the drawer for all later ones. Views stay refused even per session, the header hides the button.
    /// </remarks>
    private static bool IsSharedDemo(string agentId)
        => string.Equals(agentId, DemoIdentity.AgentId, StringComparison.Ordinal);

    /// <summary>One writer at a time per agent, because the read-modify-write below is not atomic.</summary>
    /// <remarks>
    /// The whole blob is rewritten on every call, so two overlapping mutations lose one of the two changes -
    /// silently, and not at random. Every navigation fires <see cref="PushRecentAsync"/>, and a page that
    /// stamps something on the same navigation is racing it: the onboarding marker for "profil" was written
    /// and overwritten on every single visit, so the step could never tick. Striped rather than one lock per
    /// agent so the table is bounded; collisions between two agents only cost each other a turn.
    /// </remarks>
    private static readonly SemaphoreSlim[] Writers =
        [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private static SemaphoreSlim WriterFor(string agentId)
        => Writers[(int)((uint)StringComparer.Ordinal.GetHashCode(agentId) % Writers.Length)];

    // read-modify-write of the JSON column; ExecuteUpdate bypasses the read-only barrier (pure UI prefs)
    private async Task MutateAsync(string agentId, Action<NavPreferences> mutate, CancellationToken cancellationToken, bool notify = true)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return;
        }

        if (IsSharedDemo(agentId))
        {
            // copy on write: the drawer may be enumerating the old one
            lock (_demoSessionGate)
            {
                var session = Deserialize(JsonSerializer.Serialize(_demoSession ?? new NavPreferences()));
                mutate(session);
                _demoSession = session;
            }
        }
        else
        {
            var writer = WriterFor(agentId);
            await writer.WaitAsync(cancellationToken);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var json = await db.Users.AsNoTracking()
                    .Where(a => a.Id == agentId)
                    .Select(a => a.NavPreferencesJson)
                    .FirstOrDefaultAsync(cancellationToken);
                var prefs = Deserialize(json);
                mutate(prefs);
                var updated = JsonSerializer.Serialize(prefs);
                await db.Users.Where(a => a.Id == agentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.NavPreferencesJson, updated), cancellationToken);
                // inside the lock: a cache write from a mutation that lost the race would resurrect its stale blob
                cache.Set(CacheKey(agentId), prefs, CacheDuration);
            }
            finally
            {
                writer.Release();
            }
        }

        if (notify)
        {
            Changed?.Invoke();
        }
    }

    private static NavPreferences Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new NavPreferences()
            : JsonSerializer.Deserialize<NavPreferences>(json) ?? new NavPreferences();
}
