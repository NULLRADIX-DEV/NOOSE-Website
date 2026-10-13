using System.Text.Json;

namespace NOOSE_Website.Models.Navigation;

/// <summary>Per-account navigation preferences, persisted as JSON on the Agent.</summary>
public sealed class NavPreferences
{
    /// <summary>Pinned pages and records, in display order.</summary>
    public List<NavFavorite> Favorites { get; set; } = [];

    /// <summary>Catalog keys the user has hidden.</summary>
    public HashSet<string> HiddenKeys { get; set; } = [];

    /// <summary>Catalog keys in preferred order; absent keys keep their default position.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>Custom landing route after login; null = "/".</summary>
    public string? StartRoute { get; set; }

    /// <summary>Remembered drawer open state.</summary>
    public bool DrawerOpen { get; set; } = true;

    /// <summary>Legacy collapsed sections; unused since the icon rail, kept so a save cannot drop it.</summary>
    public HashSet<string> CollapsedGroups { get; set; } = [];

    /// <summary>Drawer area shown last, as NavArea.ToString(); null = derive from the route.</summary>
    public string? LastArea { get; set; }

    /// <summary>Recently visited pages/records, newest first, capped.</summary>
    public List<RecentItem> Recents { get; set; } = [];

    /// <summary>Last time the chronicle was opened; drives its "new since your last visit" divider.</summary>
    public DateTime? ChronikLastSeenUtc { get; set; }

    /// <summary>Last time /neuerungen was seen; drives the one-off hint after a new release.</summary>
    public DateTime? NeuerungenLastSeenUtc { get; set; }

    /// <summary>Onboarding steps this agent has reached, by key. A set, so a step can be reached twice.</summary>
    /// <remarks>
    /// Keys rather than a counter or a list of booleans: the step list will grow and shrink over time, and a
    /// positional shape would silently re-interpret everybody's stored progress the first time it did.
    /// </remarks>
    public HashSet<string> OnboardingDone { get; set; } = [];

    /// <summary>Explain-on-hover bubbles for glossary terms in rich text. On unless the agent turns them off.</summary>
    /// <remarks>
    /// Default-on means an agent whose blob predates this property also gets them - there is deliberately no way
    /// to tell "never decided" from "deliberately on", because nothing here needs one.
    /// </remarks>
    public bool GlossarBlasen { get; set; } = true;

    /// <summary>List views the agent named and kept: a route plus its filters, in the order they were saved.</summary>
    /// <remarks>
    /// Beside <see cref="Favorites"/>, not inside it: a favorite is identified by its page key or record, and a view
    /// has neither - every one would share the same id and break reordering. Keeping them apart also keeps a saved
    /// view from ticking the "menu customised" onboarding step, which reads <see cref="Favorites"/>.
    /// </remarks>
    public List<SavedView> SavedViews { get; set; } = [];

    /// <summary>Notification categories that arrive already read, by <see cref="Enums.NotificationType"/> name.</summary>
    /// <remarks>
    /// Names rather than numbers, so a renumbered enum cannot silently mute a different category. Only what
    /// <see cref="Services.NotificationMutes.IsMutable"/> allows has any effect; anything else stored here is ignored.
    /// </remarks>
    public HashSet<string> MutedNotifications { get; set; } = [];

    /// <summary>While a filed absence covers today, every mutable category arrives already read.</summary>
    public bool QuietDuringAbsence { get; set; }

    /// <summary>Personal Discord pings held back for the daily digest, by <see cref="Enums.NotificationType"/> name.</summary>
    public HashSet<string> DigestPings { get; set; } = [];

    /// <summary>Personal Discord pings switched off entirely, by name; wins over <see cref="DigestPings"/>.</summary>
    /// <remarks>Only what <see cref="Services.DiscordPings.IsPersonal"/> allows has any effect.</remarks>
    public HashSet<string> MutedPings { get; set; } = [];

    /// <summary>Last look at "Meine Schicht"; looks less than half an hour apart are one visit.</summary>
    public DateTime? ShiftSeenUtc { get; set; }

    /// <summary>Last look of the visit before the current one; where "since your last visit" starts.</summary>
    public DateTime? ShiftBoundaryUtc { get; set; }

    /// <summary>Schema version for future migrations.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Parse the stored blob; empty or null yields defaults.</summary>
    public static NavPreferences FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new NavPreferences()
            : JsonSerializer.Deserialize<NavPreferences>(json) ?? new NavPreferences();
}

/// <summary>A pinned page or record in the favorites quick-access list.</summary>
public sealed record NavFavorite(
    string Kind,
    string? Key,
    string? EntityType,
    string? EntityId,
    string Label,
    string Route,
    string Icon);

/// <summary>A named list view: a relative route that carries its filters in the query.</summary>
/// <param name="Id">Stable handle for deleting; the name can be overwritten, the id stays.</param>
/// <param name="Icon">Snapshot of the list's menu icon, as a favorite keeps it.</param>
public sealed record SavedView(
    string Id,
    string Label,
    string Route,
    string Icon);

/// <summary>An auto-tracked recently visited page or record.</summary>
public sealed record RecentItem(
    string Route,
    string Label,
    string Icon,
    string? EntityType,
    string? EntityId,
    DateTime VisitedAtUtc);
