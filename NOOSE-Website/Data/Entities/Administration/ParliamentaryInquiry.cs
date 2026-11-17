using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>An inquiry from parliament, answered by the Administration within a deadline.</summary>
[Table("ParlamentarischeAnfragen")]
public class ParliamentaryInquiry : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Frage")]
    public string Question { get; set; } = string.Empty;

    [Column("EingereichtVonName")]
    public string? SubmitterName { get; set; }

    /// <summary>Filed by a parliament account itself rather than recorded by the Administration.</summary>
    [Column("VonParlament")]
    public bool FromParliament { get; set; }

    [Column("EingangAm")]
    public DateTime ReceivedAt { get; set; }

    [Column("FristBis")]
    public DateTime DueAt { get; set; }

    [Column("BearbeiterId")]
    public string? AssignedToId { get; set; }

    [Column("Status")]
    public ParliamentaryInquiryStatus Status { get; set; } = ParliamentaryInquiryStatus.Received;

    [Column("Antwort")]
    public string? Answer { get; set; }

    [Column("BeantwortetVonName")]
    public string? AnsweredByName { get; set; }

    [Column("BeantwortetAm")]
    public DateTime? AnsweredAt { get; set; }

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
