using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>Onboarding plan of a newly released agent: mentor, probation end and outcome; at most one open per agent.</summary>
[Table("AgentEinarbeitungen")]
public class AgentProbation : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("PateId")]
    public string? MentorAgentId { get; set; }

    [Column("BeginntAm")]
    public DateTime StartedAt { get; set; }

    [Column("EndetAm")]
    public DateTime EndsAt { get; set; }

    [Column("ErinnerungsStufe")]
    public ProbationReminderStage ReminderStage { get; set; }

    [Column("AbgeschlossenAm")]
    public DateTime? ClosedAt { get; set; }

    [Column("Ergebnis")]
    public ProbationOutcome? Outcome { get; set; }

    [Column("AbgeschlossenVonName")]
    public string? ClosedByName { get; set; }

    [NotMapped]
    public bool IsOpen => ClosedAt is null;

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
