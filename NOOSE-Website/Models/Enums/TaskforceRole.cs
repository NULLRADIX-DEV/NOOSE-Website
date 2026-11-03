namespace NOOSE_Website.Models.Enums;

/// <summary>Taskforce member role.</summary>
public enum TaskforceRole
{
    /// <summary>Regular member.</summary>
    Member = 0,
    /// <summary>Lead investigator.</summary>
    LeadInvestigator = 1,
    /// <summary>CID operational lead.</summary>
    CidLead = 2,
    /// <summary>HRT tactical lead (stored as the legacy TRU lead).</summary>
    TruLead = 3,
}

/// <summary>Display labels.</summary>
public static class TaskforceRoleDisplay
{
    public static string Name(TaskforceRole role) => role switch
    {
        TaskforceRole.Member => "Mitglied",
        TaskforceRole.LeadInvestigator => "Chefermittler",
        TaskforceRole.CidLead => "CID-Lead",
        TaskforceRole.TruLead => "HRT-Lead",
        _ => "—",
    };

    /// <summary>Non-member roles are leads.</summary>
    public static bool IsLead(TaskforceRole role) => role != TaskforceRole.Member;

    public static readonly IReadOnlyList<TaskforceRole> All = new[]
    {
        TaskforceRole.Member,
        TaskforceRole.LeadInvestigator,
        TaskforceRole.CidLead,
        TaskforceRole.TruLead,
    };
}
