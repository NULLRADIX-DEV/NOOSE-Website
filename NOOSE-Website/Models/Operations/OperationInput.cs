using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Operations;

/// <summary>Create/edit operation input.</summary>
public class OperationInput
{
    public string Title { get; set; } = string.Empty;
    public string? Type { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Planned;
    public string? Location { get; set; }
    /// <summary>Local time; the service stores UTC.</summary>
    public DateTime? Start { get; set; }
    /// <summary>Local time; the service stores UTC.</summary>
    public DateTime? End { get; set; }
    public string? Expiry { get; set; }
    public string? Result { get; set; }
    public string? Remarks { get; set; }
    public string? Legitimation { get; set; }
    public string? InvolvedAgencies { get; set; }
    public string? Evidence { get; set; }
    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }
}
