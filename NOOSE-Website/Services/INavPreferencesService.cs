using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Per-account navigation preferences: favorites, hidden/order, recents, drawer/group state.</summary>
public interface INavPreferencesService
{
    /// <summary>Fires after this circuit's user changed their preferences.</summary>
    event Action? Changed;

    /// <summary>Current preferences for an account (cached); empty defaults when unset.</summary>
    Task<NavPreferences> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Pin or unpin a favorite (page or record), matched by its stable id.</summary>
    Task ToggleFavoriteAsync(string agentId, NavFavorite favorite, CancellationToken cancellationToken = default);

    /// <summary>Reorder favorites by their stable ids.</summary>
    Task ReorderFavoritesAsync(string agentId, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>Hide or show a catalog entry by key.</summary>
    Task SetHiddenAsync(string agentId, string key, bool hidden, CancellationToken cancellationToken = default);

    /// <summary>Set the personal entry order (catalog keys).</summary>
    Task SetOrderAsync(string agentId, IReadOnlyList<string> orderedKeys, CancellationToken cancellationToken = default);

    /// <summary>Set the custom landing route; null/empty clears it.</summary>
    Task SetStartRouteAsync(string agentId, string? route, CancellationToken cancellationToken = default);

    /// <summary>Remember the drawer open state.</summary>
    Task SetDrawerOpenAsync(string agentId, bool open, CancellationToken cancellationToken = default);

    /// <summary>Remember the drawer area the user last looked at.</summary>
    Task SetLastAreaAsync(string agentId, string? area, CancellationToken cancellationToken = default);

    /// <summary>Record a visited page/record (dedupes + caps).</summary>
    Task PushRecentAsync(string agentId, RecentItem item, CancellationToken cancellationToken = default);

    /// <summary>Stamp the chronicle as seen up to this instant.</summary>
    Task SetChronikLastSeenAsync(string agentId, DateTime seenUtc, CancellationToken cancellationToken = default);

    /// <summary>Stamp the changelog as seen up to this instant; the hint card appears only for what came after.</summary>
    Task SetNeuerungenLastSeenAsync(string agentId, DateTime seenUtc, CancellationToken cancellationToken = default);

    /// <summary>Turns the glossary explain-bubbles on or off for this agent.</summary>
    Task SetGlossarBlasenAsync(string agentId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Lets one notification category ring the bell or arrive already read; locked categories are ignored.</summary>
    Task SetNotificationMutedAsync(string agentId, NotificationType type, bool muted, CancellationToken cancellationToken = default);

    /// <summary>Keeps the bell quiet while a filed absence covers today.</summary>
    Task SetQuietDuringAbsenceAsync(string agentId, bool quiet, CancellationToken cancellationToken = default);

    /// <summary>Records that an agent reached one onboarding step. Idempotent, and cheap enough to call blind.</summary>
    Task MarkOnboardingStepAsync(string agentId, string stepKey, CancellationToken cancellationToken = default);

    /// <summary>Saves a list view under a name; the same name overwrites. Rules in <see cref="SavedViewRules"/>.</summary>
    Task<SavedViewOutcome> SaveViewAsync(string agentId, string label, string route, string icon, CancellationToken cancellationToken = default);

    /// <summary>Deletes one saved list view by its id.</summary>
    Task RemoveViewAsync(string agentId, string viewId, CancellationToken cancellationToken = default);

    /// <summary>Stable id for a favorite (used for reordering).</summary>
    static string FavoriteId(NavFavorite f)
        => f.Kind == "page" ? $"page:{f.Key}" : $"record:{f.EntityType}:{f.EntityId}";
}
