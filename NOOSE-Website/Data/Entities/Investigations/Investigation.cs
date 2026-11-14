using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Investigations;

/// <summary>Investigation file run with the DoJ; hangs on a taskforce and is visible to its participants only.</summary>
[Table("Ermittlungen")]
public class Investigation : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    public string TaskforceId { get; set; } = string.Empty;
    public Taskforce? Taskforce { get; set; }

    /// <summary>The DoJ (SID) file number, e.g. IR-9-2026-001.</summary>
    [Column("AzSid")]
    public string? SidCaseNumber { get; set; }

    /// <summary>The FiveNet document number, e.g. DOC-72764.</summary>
    [Column("Az5Net")]
    public string? FiveNetCaseNumber { get; set; }

    public InvestigationStatus Status { get; set; } = InvestigationStatus.Investigating;

    [Column("EroeffnetAm")]
    public DateTime OpenedAt { get; set; }

    /// <summary>Indictment note (date, court, file); free text like the DoJ cover sheet.</summary>
    [Column("Anklage")]
    public string? Indictment { get; set; }

    /// <summary>Course of events, sanitized rich text without images.</summary>
    [Column("TathergangHtml")]
    public string? NarrativeHtml { get; set; }

    public List<InvestigationParticipant> Participants { get; set; } = new();

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
