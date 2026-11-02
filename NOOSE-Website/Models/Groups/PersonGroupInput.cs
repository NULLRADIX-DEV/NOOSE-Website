using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Groups;

/// <summary>Create/edit person group.</summary>
public class PersonGroupInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Targets { get; set; }

    /// <summary>Group category kind.</summary>
    public GroupsKind Kind { get; set; } = GroupsKind.Grouping;

    public Classification Classification { get; set; } = Classification.Unknown;
    public string? ClassificationJustification { get; set; }
    public int? EstimatedMemberCount { get; set; }
    public DocumentClassification SecrecyLevel { get; set; } = DocumentClassification.None;

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }

    /// <summary>Initial members list.</summary>
    public List<GroupMemberInput> Members { get; set; } = new();
}

/// <summary>Add/edit group membership.</summary>
public class GroupMemberInput
{
    public string PersonId { get; set; } = string.Empty;
    public string? Role { get; set; }
    public bool IsLead { get; set; }

    /// <summary>Display only; ignored by service.</summary>
    public string? PersonName { get; set; }

    /// <summary>Auto-creates new person if PersonId is empty.</summary>
    public string? NewPersonName { get; set; }
}

/// <summary>Group capture progress: captured vs estimated.</summary>
public record PersonGroupProgress(int Captured, int? Estimated);
