using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Recruiting;

/// <summary>A job application; submitted by a Discord-authenticated applicant, processed by HRB/leadership.</summary>
[Table("Bewerbungen")]
public class Bewerbung : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    /// <summary>Identity user (the applicant's Discord account, status Applicant).</summary>
    [Column("BewerberId")]
    public string ApplicantUserId { get; set; } = string.Empty;

    [Column("AkademischerGrad")]
    public string? AcademicDegree { get; set; }

    /// <summary>RP character name; HRB sees it, redacted in generated applicant letters.</summary>
    public string Name { get; set; } = string.Empty;

    [Column("Geburtsdatum")]
    public DateTime? BirthDate { get; set; }

    [Column("Arbeitgeber")]
    public string? Employer { get; set; }

    [Column("Vorerfahrung")]
    public string? PriorExperience { get; set; }

    [Column("Anschreiben")]
    public string? CoverLetter { get; set; }

    /// <summary>Applicant agrees to their application being streamed; null on applications from before the question.</summary>
    [Column("StreamFreigabe")]
    public bool? StreamConsent { get; set; }

    [Column("AnhangDateiname")]
    public string? AttachmentFileNameSaved { get; set; }
    [Column("AnhangOriginalname")]
    public string? AttachmentOriginalName { get; set; }
    [Column("AnhangTyp")]
    public string? AttachmentContentType { get; set; }

    public BewerbungStatus Status { get; set; } = BewerbungStatus.Eingereicht;

    [Column("BearbeiterId")]
    public string? AssignedAgentId { get; set; }
    [Column("BearbeiterName")]
    public string? AssignedAgentName { get; set; }

    /// <summary>Optional link to an existing Person file (for the threat score).</summary>
    [Column("PersonId")]
    public string? LinkedPersonId { get; set; }

    /// <summary>Auto-created "Bewerbungsverfahren" case; doubles as the auto-provisioning idempotency key.</summary>
    [Column("VorgangId")]
    public string? LinkedCaseId { get; set; }

    [Column("SicherheitBestanden")]
    public bool? SecurityCheckPassed { get; set; }

    [Column("Entscheidungsnotiz")]
    public string? DecisionNote { get; set; }
    [Column("EntscheiderName")]
    public string? DecidedByName { get; set; }
    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

    [Column("EingereichtAm")]
    public DateTime SubmittedAt { get; set; }

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
