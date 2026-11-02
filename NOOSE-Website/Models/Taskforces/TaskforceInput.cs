using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Taskforces;

/// <summary>Create/edit taskforce input.</summary>
public class TaskforceInput
{
    public string Name { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public TaskforceScope Scope { get; set; } = TaskforceScope.InternalAgency;
    public string? Remarks { get; set; }
    public bool IsClassified { get; set; }

    /// <summary>Why the secrecy level is loosened; required by the service when the audience widens.</summary>
    public string? SecrecyReason { get; set; }
}
