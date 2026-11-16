using MudBlazor;

namespace NOOSE_Website.Navigation;

/// <summary>Drawer area an entry is filed under; presentation only, unlike the policy-bearing NavSection.</summary>
public enum NavArea
{
    /// <summary>Pinned above the rail as plain links, not selectable as an area.</summary>
    Primary,
    Akten,
    Ermittlung,
    Dienststelle,
    MeinDienst,
    Administration,
    Verwaltung,
    Partner,
}

/// <summary>Display metadata for the drawer's area rail.</summary>
public static class NavAreaCatalog
{
    /// <summary>Areas in rail order; Partner is runtime-only and never mixes with the others.</summary>
    public static readonly NavArea[] Areas =
    [
        NavArea.Akten,
        NavArea.Ermittlung,
        NavArea.Dienststelle,
        NavArea.MeinDienst,
        NavArea.Administration,
        NavArea.Verwaltung,
    ];

    public static string Name(NavArea area) => area switch
    {
        NavArea.Primary => "Allgemein",
        NavArea.Akten => "Akten",
        NavArea.Ermittlung => "Ermittlung & Wissen",
        NavArea.Dienststelle => "Dienststelle",
        NavArea.MeinDienst => "Mein Dienst",
        NavArea.Administration => "Administration",
        NavArea.Verwaltung => "Verwaltung",
        NavArea.Partner => "Freigegebene Akten",
        _ => string.Empty,
    };

    public static string Icon(NavArea area) => area switch
    {
        NavArea.Primary => Icons.Material.Filled.SpaceDashboard,
        NavArea.Akten => Icons.Material.Filled.FolderCopy,
        NavArea.Ermittlung => Icons.Material.Filled.TravelExplore,
        NavArea.Dienststelle => Icons.Material.Filled.AccountBalance,
        NavArea.MeinDienst => Icons.Material.Filled.AccountCircle,
        NavArea.Administration => Icons.Material.Filled.AssuredWorkload,
        NavArea.Verwaltung => Icons.Material.Filled.AdminPanelSettings,
        NavArea.Partner => Icons.Material.Filled.FolderShared,
        _ => Icons.Material.Filled.Folder,
    };

    /// <summary>Catalog entries of one area, in catalog order.</summary>
    public static IEnumerable<NavEntry> Entries(NavArea area)
        => NavCatalog.Internal.Where(e => e.Area == area);

    /// <summary>Every policy the catalog gates on; the drawer evaluates each exactly once.</summary>
    public static readonly IReadOnlyList<string> DistinctPolicies =
        NavCatalog.Internal.Select(e => NavSectionPolicy.For(e.Section)).Distinct(StringComparer.Ordinal).ToList();
}
