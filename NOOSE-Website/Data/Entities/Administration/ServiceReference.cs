using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>A service reference issued by the Administration on leaving, transfer or request.</summary>
[Table("Dienstzeugnisse")]
public class ServiceReference : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("Anlass")]
    public ServiceReferenceReason Reason { get; set; }

    [Column("Gesamtnote")]
    public EvaluationGrade? Grade { get; set; }

    [Column("TextHtml")]
    public string TextHtml { get; set; } = string.Empty;

    [Column("AusgestelltAm")]
    public DateTime IssuedOn { get; set; }

    [Column("AusgestelltVonName")]
    public string? IssuedByName { get; set; }

    [Column("Dienstgrad")]
    public Rank? RankSnapshot { get; set; }

    [Column("Abteilung")]
    public Division? DivisionSnapshot { get; set; }

    [Column("DienstBeginn")]
    public DateTime? ServiceStart { get; set; }

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
