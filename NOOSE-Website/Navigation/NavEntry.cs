using Microsoft.AspNetCore.Components.Routing;

namespace NOOSE_Website.Navigation;

/// <summary>Policy-gated drawer section an entry belongs to.</summary>
public enum NavSection
{
    Primary,
    MeinDienst,
    Akten,
    VorgaengeEinsaetze,
    Fahndung,
    Wissen,
    Analyse,
    Dienststelle,
    VerwaltungFreigaben,
    VerwaltungBewerbungen,
    VerwaltungFuehrung,
    VerwaltungAdmin,
    Administration,
    Partner,
}

/// <summary>Single navigation entry; the catalog is the source for label/icon/route/order.</summary>
/// <param name="InDrawer">False keeps the page in search, shortcuts and breadcrumbs but out of drawer and menus.</param>
/// <param name="DrawerTwinOf">Key of another entry for the same page; this one hides when the viewer may open that one.</param>
public sealed record NavEntry(
    string Key,
    string Route,
    string Icon,
    string Label,
    NavSection Section,
    NavArea Area,
    NavLinkMatch Match = NavLinkMatch.Prefix,
    string? BadgeKey = null,
    string? Description = null,
    string? Group = null,
    bool InDrawer = true,
    string? DrawerTwinOf = null);
