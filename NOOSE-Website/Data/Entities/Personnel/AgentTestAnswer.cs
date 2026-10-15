using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>An agent's answer to one test question (selected options and/or free text).</summary>
[Table("AgentTestAntworten")]
public class AgentTestAnswer : IAuditable, ISoftDelete, ITestAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AssignmentId { get; set; } = string.Empty;
    public AgentTestAssignment? Assignment { get; set; }

    public string QuestionId { get; set; } = string.Empty;
    public BewerbungTestQuestion? Question { get; set; }

    [Column("OptionId")]
    public string? SelectedOptionId { get; set; }

    /// <summary>Every ticked option, comma-separated; the scalar above stays filled whenever exactly one was ticked.</summary>
    [Column("OptionIds")]
    public string? SelectedOptionIds { get; set; }

    [Column("Freitext")]
    public string? FreeTextAnswer { get; set; }

    /// <summary>Never written for agent tests; kept so the shared evaluation reads one shape.</summary>
    [Column("ManuellRichtig")]
    public bool? ManualCorrect { get; set; }

    /// <summary>Points the grader awarded by hand; null = fall back to the automatic result.</summary>
    [Column("ManuellPunkte")]
    public int? ManualPoints { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }

    [Column("IstGeloescht")]
    public bool IsDeleted { get; set; }
    [Column("GeloeschtAm")]
    public DateTime? DeletedAt { get; set; }
    [Column("GeloeschtVonId")]
    public string? DeletedById { get; set; }
}
