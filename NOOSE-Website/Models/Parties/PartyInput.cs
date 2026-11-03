using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Parties;

/// <summary>Create/edit party record.</summary>
public class PartyInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Targets { get; set; }
    public string? Remarks { get; set; }
    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }

    /// <summary>Initial members list.</summary>
    public List<PartyMemberInput> Members { get; set; } = new();
}

/// <summary>Add/edit party membership.</summary>
public class PartyMemberInput
{
    public string PersonId { get; set; } = string.Empty;
    public string? Role { get; set; }
    public bool IsLead { get; set; }

    /// <summary>Display only; ignored by service.</summary>
    public string? PersonName { get; set; }

    /// <summary>Auto-creates new person if PersonId is empty.</summary>
    public string? NewPersonName { get; set; }
}
