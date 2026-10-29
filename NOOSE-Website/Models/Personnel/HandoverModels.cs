namespace NOOSE_Website.Models.Personnel;

/// <summary>What a handover row stands for, in the order the dialog lists them.</summary>
public enum HandoverKind
{
    Followup = 0,
    Job = 1,
    Case = 2,
    Operation = 3,
    Taskforce = 4,
    Informant = 5,
    Faction = 6,
    PersonGroup = 7,
    Party = 8,

    /// <summary>Closes with the departure: the payout would go to the leaving agent.</summary>
    FinancingRequest = 9,

    /// <summary>Closes with the departure: the leaving agent is taken off the guest list.</summary>
    Appointment = 10,
}

/// <summary>One thing still hanging on the leaving agent.</summary>
/// <param name="Id">The record's id; for a follow-up its own id.</param>
/// <param name="Role">Lead or taskforce role that goes along to the successor; null for a plain membership.</param>
public sealed record HandoverRow(HandoverKind Kind, string Id, string Title, string? Detail, string? Href, string? Role);

/// <summary>The handover as the dialog shows it.</summary>
/// <param name="Hidden">Rows on records the viewer may not open: counted, never listed or moved.</param>
public sealed record HandoverPlan(string AgentId, string Codename, IReadOnlyList<HandoverRow> Rows, int Hidden);

/// <summary>One row the operator ticked; the successor is ignored for the kinds that close.</summary>
public sealed record HandoverChoice(HandoverKind Kind, string Id, string? SuccessorId);

/// <summary>What a handover did: rows passed to a successor and rows closed with the departure.</summary>
public sealed record HandoverResult(int Passed, int Closed);

/// <summary>Display labels for the dialog and the successor's notice.</summary>
public static class HandoverDisplay
{
    /// <summary>Kinds that close with the departure instead of passing to a successor.</summary>
    public static bool Closes(HandoverKind kind) => kind is HandoverKind.FinancingRequest or HandoverKind.Appointment;

    /// <summary>Section heading in the dialog.</summary>
    public static string Section(HandoverKind kind) => Plural(kind);

    /// <summary>"1 Aufgabe", "3 Aufgaben".</summary>
    public static string Count(HandoverKind kind, int count) => $"{count} {(count == 1 ? Singular(kind) : Plural(kind))}";

    /// <summary>What the departure does to a kind that closes; empty for the kinds that pass on.</summary>
    public static string Closing(HandoverKind kind) => kind switch
    {
        HandoverKind.FinancingRequest =>
            "Ohne Nachfolger: beantragte werden zurückgezogen, genehmigte abgelehnt, weil die Auszahlung an den Antragsteller geht.",
        HandoverKind.Appointment => "Ohne Nachfolger: die Teilnahme an künftigen Terminen wird gestrichen.",
        _ => string.Empty,
    };

    /// <summary>The outcome line after a handover.</summary>
    public static string Done(HandoverResult result)
    {
        static string Entries(int n) => n == 1 ? "1 Eintrag" : $"{n} Einträge";
        return (result.Passed, result.Closed) switch
        {
            (0, 0) => "Nichts geändert: die Einträge hingen nicht mehr an diesem Agenten.",
            (_, 0) => $"{Entries(result.Passed)} übergeben.",
            (0, _) => $"{Entries(result.Closed)} geschlossen.",
            _ => $"{Entries(result.Passed)} übergeben, {result.Closed} geschlossen.",
        };
    }

    private static string Singular(HandoverKind kind) => kind switch
    {
        HandoverKind.Followup => "Wiedervorlage",
        HandoverKind.Job => "Aufgabe",
        HandoverKind.Case => "Vorgang",
        HandoverKind.Operation => "Operation",
        HandoverKind.Taskforce => "Taskforce",
        HandoverKind.Informant => "V-Person",
        HandoverKind.Faction => "Fraktion",
        HandoverKind.PersonGroup => "Personengruppe",
        HandoverKind.Party => "Partei",
        HandoverKind.FinancingRequest => "Finanzierungsantrag",
        HandoverKind.Appointment => "Termin",
        _ => "Eintrag",
    };

    private static string Plural(HandoverKind kind) => kind switch
    {
        HandoverKind.Followup => "Wiedervorlagen",
        HandoverKind.Job => "Aufgaben",
        HandoverKind.Case => "Vorgänge",
        HandoverKind.Operation => "Operationen",
        HandoverKind.Taskforce => "Taskforces",
        HandoverKind.Informant => "V-Personen",
        HandoverKind.Faction => "Fraktionen",
        HandoverKind.PersonGroup => "Personengruppen",
        HandoverKind.Party => "Parteien",
        HandoverKind.FinancingRequest => "Finanzierungsanträge",
        HandoverKind.Appointment => "Termine",
        _ => "Einträge",
    };
}
