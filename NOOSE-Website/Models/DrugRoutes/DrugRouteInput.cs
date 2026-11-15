using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.DrugRoutes;

/// <summary>Create/edit drug route record.</summary>
public class DrugRouteInput
{
    public string Designation { get; set; } = string.Empty;
    public string? Substance { get; set; }
    public string? Description { get; set; }
    public string? Findings { get; set; }
    public string? Remarks { get; set; }

    /// <summary>Faction running the route; null while unknown.</summary>
    public string? FactionId { get; set; }

    /// <summary>Display only; ignored by service.</summary>
    public string? FactionName { get; set; }

    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }
}

/// <summary>Add/edit one station.</summary>
public class DrugRouteStationInput
{
    public DrugRouteStationKind Kind { get; set; } = DrugRouteStationKind.Other;
    public string Location { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Add a person to a route.</summary>
public class DrugRoutePersonInput
{
    public string PersonId { get; set; } = string.Empty;
    public string? Role { get; set; }

    /// <summary>Display only; ignored by service.</summary>
    public string? PersonName { get; set; }

    /// <summary>Auto-creates new person if PersonId is empty.</summary>
    public string? NewPersonName { get; set; }
}

/// <summary>An active route already carries this designation; nothing was written.</summary>
/// <param name="ExistingId">The route holding the name; null when the actor may not see it.</param>
public sealed class DrugRouteDuplicateException(string designation, string? existingId, string? existingCaseNumber)
    : InvalidOperationException(existingCaseNumber is { } nr
        ? $"Die Route „{designation}“ ist bereits als {nr} erfasst."
        : $"Eine Route „{designation}“ ist bereits erfasst, die Akte ist für dich nicht einsehbar.")
{
    public string? ExistingId { get; } = existingId;
}
