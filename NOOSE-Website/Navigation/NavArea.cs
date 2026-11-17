using MudBlazor;

namespace NOOSE_Website.Navigation;

/// <summary>Where an entry shows up; presentation only, unlike the policy-bearing NavSection.</summary>
public enum NavArea
{
    /// <summary>Plain links above the drawer groups.</summary>
    Primary,
    Akten,
    Ermittlung,
    Analyse,
    Dienststelle,
    Leitung,
    /// <summary>The avatar menu in the app bar.</summary>
    Ich,
    /// <summary>The help menu in the app bar.</summary>
    Hilfe,
    Partner,
}

/// <summary>Display metadata for the drawer groups and the app bar menus.</summary>
public static class NavAreaCatalog
{
    /// <summary>Drawer groups in order; Partner is runtime-only and never mixes with the others.</summary>
    public static readonly NavArea[] Areas =
    [
        NavArea.Akten,
        NavArea.Ermittlung,
        NavArea.Analyse,
        NavArea.Dienststelle,
        NavArea.Leitung,
    ];

    /// <summary>Areas rendered as app bar menus instead of drawer groups.</summary>
    public static readonly NavArea[] MenuAreas = [NavArea.Ich, NavArea.Hilfe];

    public static string Name(NavArea area) => area switch
    {
        NavArea.Primary => "Allgemein",
        NavArea.Akten => "Akten",
        NavArea.Ermittlung => "Ermittlung & Einsatz",
        NavArea.Analyse => "Analyse",
        NavArea.Dienststelle => "Dienststelle",
        NavArea.Leitung => "Leitung",
        NavArea.Ich => "Mein Bereich",
        NavArea.Hilfe => "Hilfe & Nachschlagen",
        NavArea.Partner => "Freigegebene Akten",
        _ => string.Empty,
    };

    public static string Icon(NavArea area) => area switch
    {
        NavArea.Primary => Icons.Material.Filled.SpaceDashboard,
        NavArea.Akten => Icons.Material.Filled.FolderCopy,
        NavArea.Ermittlung => Icons.Material.Filled.TravelExplore,
        NavArea.Analyse => Icons.Material.Filled.Insights,
        NavArea.Dienststelle => Icons.Material.Filled.AccountBalance,
        NavArea.Leitung => Icons.Material.Filled.AdminPanelSettings,
        NavArea.Ich => Icons.Material.Filled.AccountCircle,
        NavArea.Hilfe => Icons.Material.Filled.HelpOutline,
        NavArea.Partner => Icons.Material.Filled.FolderShared,
        _ => Icons.Material.Filled.Folder,
    };

    /// <summary>Address of a drawer group's overview page; null for areas without one.</summary>
    public static string? HubRoute(NavArea area) => area switch
    {
        NavArea.Akten => "/akten",
        NavArea.Ermittlung => "/ermittlung",
        NavArea.Analyse => "/analyse",
        NavArea.Dienststelle => "/dienststelle",
        NavArea.Leitung => "/leitung",
        _ => null,
    };

    /// <summary>Catalog key of a drawer group's overview page.</summary>
    public static string HubKey(NavArea area) => "hub." + area.ToString().ToLowerInvariant();

    /// <summary>True for the catalog entries of the overview pages themselves.</summary>
    public static bool IsHub(NavEntry entry) => entry.Key.StartsWith("hub.", StringComparison.Ordinal);

    /// <summary>Catalog entries of one area, in catalog order.</summary>
    public static IEnumerable<NavEntry> Entries(NavArea area)
        => NavCatalog.Internal.Where(e => e.Area == area);

    /// <summary>Entries of one area that drawer or menu list; the rest stay reachable via search and their pages.</summary>
    public static IEnumerable<NavEntry> ListedEntries(NavArea area)
        => Entries(area).Where(e => e.InDrawer);

    /// <summary>Every policy the catalog gates on; the drawer evaluates each exactly once.</summary>
    // lazy: the catalog builds its hub entries from this class, so an eager field would read it half-built
    public static IReadOnlyList<string> DistinctPolicies => _distinctPolicies ??=
        NavCatalog.Internal.Select(e => NavSectionPolicy.For(e.Section)).Distinct(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string>? _distinctPolicies;

    /// <summary>Drops entries whose twin the viewer may open as well, so one page shows up once.</summary>
    public static IEnumerable<NavEntry> WithoutTwins(IEnumerable<NavEntry> entries, Func<NavEntry, bool> allowed)
        => entries.Where(e => e.DrawerTwinOf is not { } twin
                              || NavCatalog.ByKey(twin) is not { } t
                              || !allowed(t));
}
