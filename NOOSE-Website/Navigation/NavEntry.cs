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

/// <summary>Single drawer entry; the catalog is the source for label/icon/route/order.</summary>
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
    string? Group = null);
