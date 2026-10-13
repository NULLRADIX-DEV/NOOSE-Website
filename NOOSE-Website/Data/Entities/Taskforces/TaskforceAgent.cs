using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Taskforces;

/// <summary>Assignment of an agent to a taskforce; any non-member role may manage assignments. Agent FK is Restrict.</summary>
[Table("TaskforceAgenten")]
public class TaskforceAgent : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TaskforceId { get; set; } = string.Empty;
    public Taskforce? Taskforce { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    [Column("Rolle")]
    public TaskforceRole Role { get; set; } = TaskforceRole.Member;

    /// <summary>This member's read mark over the chat; null = nothing read since joining.</summary>
    [Column("ZuletztGelesenAm")]
    public DateTime? LastReadAt { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
