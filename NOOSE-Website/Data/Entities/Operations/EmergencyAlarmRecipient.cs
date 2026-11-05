using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Operations;

/// <summary>An agent called by an alarm, created as a snapshot when it is triggered. FK to Agent is Restrict; FK to the alarm is Cascade.</summary>
[Table("EinsatzalarmEmpfaenger")]
public class EmergencyAlarmRecipient : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AlarmId { get; set; } = string.Empty;
    public EmergencyAlarm? Alarm { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    /// <summary>The agent's answer; null = still open.</summary>
    [Column("Antwort")]
    public EmergencyAlarmAnswer? Answer { get; set; }

    [Column("GeantwortetAm")]
    public DateTime? AnsweredAt { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
