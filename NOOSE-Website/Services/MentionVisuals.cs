using MudBlazor;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>Icon and German label per mentionable record type; shared by picker, chips and rendered HTML.</summary>
public static class MentionVisuals
{
    /// <summary>Material icon for a mention of that type.</summary>
    public static string Symbol(string? type) => type switch
    {
        nameof(Person) => Icons.Material.Filled.Badge,
        nameof(Faction) => Icons.Material.Filled.Groups,
        nameof(PersonGroup) => Icons.Material.Filled.Diversity3,
        nameof(Party) => Icons.Material.Filled.AccountBalance,
        nameof(DrugRoute) => Icons.Material.Filled.Route,
        nameof(Operation) => Icons.Material.Filled.Radar,
        nameof(Taskforce) => Icons.Material.Filled.Groups2,
        nameof(Case) => Icons.Material.Filled.FolderSpecial,
        nameof(Job) => Icons.Material.Filled.AssignmentTurnedIn,
        nameof(Appointment) => Icons.Material.Filled.Event,
        nameof(Meeting) => Icons.Material.Filled.Groups,
        nameof(Document) => Icons.Material.Filled.Article,
        nameof(Source) => Icons.Material.Filled.AttachFile,
        nameof(TextImage) => Icons.Material.Filled.Image,
        // not a mention at all: the snippet list borrows this picker rather than cloning it
        nameof(TextSnippet) => Icons.Material.Filled.ShortText,
        nameof(Agent) => Icons.Material.Filled.Person,
        // same icon as the menu entry
        nameof(Investigation) => Icons.Material.Filled.Gavel,
        // an entry wears the icon of its tab
        _ when InvestigationReferences.IsType(type) && InvestigationTabs.ForChildType(type!) is { } slug
            => InvestigationTabs.Find(slug)?.Icon ?? Icons.Material.Filled.Link,
        _ => Icons.Material.Filled.Link,
    };

    /// <summary>German type label shown beside a candidate; empty for types the picker never offers.</summary>
    public static string Label(string? type) => type switch
    {
        nameof(Person) => "Person",
        nameof(Faction) => "Fraktion",
        nameof(PersonGroup) => "Gruppe",
        nameof(Party) => "Partei",
        nameof(DrugRoute) => "Drogenroute",
        nameof(Operation) => "Operation",
        nameof(Taskforce) => "Taskforce",
        nameof(Case) => "Vorgang",
        nameof(Job) => "Aufgabe",
        nameof(Appointment) => "Termin",
        nameof(Meeting) => "Besprechung",
        nameof(Document) => "Dokument",
        nameof(Source) => "Quelle",
        nameof(Agent) => "Agent",
        nameof(TextSnippet) => "Baustein",
        nameof(Investigation) => "Ermittlung",
        nameof(InvestigationCharge) => "Ermittlung: Tatvorwurf",
        nameof(InvestigationPerson) => "Ermittlung: Zeuge/Beschuldigter",
        nameof(InvestigationEvidence) => "Ermittlung: Beweismittel",
        nameof(InvestigationInterrogation) => "Ermittlung: Vernehmung",
        nameof(InvestigationTask) => "Ermittlung: Aufgabe",
        nameof(InvestigationNote) => "Ermittlung: Notiz",
        _ => "",
    };
}
