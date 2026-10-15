using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Recruiting;

/// <summary>An applicant's answer to one test question (selected option and/or free text).</summary>
[Table("BewerbungTestAntworten")]
public class BewerbungTestAnswer : IAuditable, ISoftDelete, ITestAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AssignmentId { get; set; } = string.Empty;
    public BewerbungTestAssignment? Assignment { get; set; }

    public string QuestionId { get; set; } = string.Empty;
    public BewerbungTestQuestion? Question { get; set; }

    [Column("OptionId")]
    public string? SelectedOptionId { get; set; }

    /// <summary>Every ticked option, comma-separated; the scalar above stays filled whenever exactly one was ticked.</summary>
    /// <remarks>One row per question, not one per option: the grader UI and the evaluation both key on that shape.</remarks>
    [Column("OptionIds")]
    public string? SelectedOptionIds { get; set; }

    [Column("Freitext")]
    public string? FreeTextAnswer { get; set; }

    /// <summary>HRB manual grade override; null = auto result applies.</summary>
    /// <remarks>Kept for rows graded before points existed; <see cref="ManualPoints"/> wins where both are set.</remarks>
    [Column("ManuellRichtig")]
    public bool? ManualCorrect { get; set; }

    /// <summary>Points the grader awarded by hand; null = fall back to the automatic result.</summary>
    /// <remarks>Clamped to the question's maximum on write and on read — the field itself is never trusted.</remarks>
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
