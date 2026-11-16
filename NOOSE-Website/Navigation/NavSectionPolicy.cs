using NOOSE_Website.Authorization;

namespace NOOSE_Website.Navigation;

/// <summary>Maps a nav section to the same policy its drawer wrapper uses, plus a display name.</summary>
public static class NavSectionPolicy
{
    /// <summary>Gating policy for a section (favorites/customizer reuse the exact drawer policies).</summary>
    public static string For(NavSection section) => section switch
    {
        NavSection.VerwaltungFreigaben => Policies.HighestClassificationPage,
        NavSection.VerwaltungBewerbungen => Policies.HrbOrLeadership,
        NavSection.VerwaltungFuehrung => Policies.LeadershipPage,
        NavSection.VerwaltungAdmin => Policies.AdminPage,
        NavSection.Administration => Policies.AdministrationWorkspace,
        NavSection.Partner => Policies.PartnerView,
        _ => Policies.InternalAgent,
    };

    /// <summary>German heading for a section in the drawer and the customizer.</summary>
    public static string Name(NavSection section) => section switch
    {
        NavSection.Primary => "Allgemein",
        NavSection.MeinDienst => "Mein Dienst",
        NavSection.Akten => "Akten",
        NavSection.VorgaengeEinsaetze => "Vorgänge & Einsätze",
        NavSection.Fahndung => "Fahndung & Überwachung",
        NavSection.Wissen => "Wissen",
        NavSection.Analyse => "Analyse",
        NavSection.Dienststelle => "Dienststelle",
        NavSection.VerwaltungFreigaben => "Verwaltung",
        NavSection.VerwaltungBewerbungen => "Verwaltung · Bewerbungen",
        NavSection.VerwaltungFuehrung => "Verwaltung · Führung",
        NavSection.VerwaltungAdmin => "Verwaltung · Admin",
        NavSection.Administration => "Administration",
        NavSection.Partner => "Freigegebene Akten",
        _ => string.Empty,
    };

    /// <summary>Sections shown to an internal agent, in catalog order.</summary>
    public static readonly NavSection[] InternalSections =
    [
        NavSection.Primary,
        NavSection.MeinDienst, NavSection.Akten, NavSection.VorgaengeEinsaetze,
        NavSection.Fahndung, NavSection.Wissen, NavSection.Analyse, NavSection.Dienststelle,
        NavSection.VerwaltungFreigaben, NavSection.VerwaltungBewerbungen,
        NavSection.VerwaltungFuehrung, NavSection.VerwaltungAdmin,
        NavSection.Administration,
    ];
}
