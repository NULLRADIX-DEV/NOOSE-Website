using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Cases;

/// <summary>Form model for creating/editing a case record.</summary>
public class CaseInput
{
    public string Title { get; set; } = string.Empty;
    public string? Type { get; set; }
    public CaseStatus Status { get; set; } = CaseStatus.Open;
    public string? Description { get; set; }
    public string? Summary { get; set; }
    public string? ClosingNote { get; set; }
    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }
}
