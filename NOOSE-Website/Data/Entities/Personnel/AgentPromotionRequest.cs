using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>A promotion request; leadership proposes a target rank, the Board and admins decide all, a Section Chief the own division.</summary>
[Table("AgentBefoerderungsantraege")]
public class AgentPromotionRequest : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("ZielDienstgrad")]
    public Rank TargetRank { get; set; }

    [Column("Begruendung")]
    public string? Justification { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Requested;

    [Column("AntragstellerName")]
    public string? RequesterName { get; set; }

    [Column("EntscheiderName")]
    public string? DeciderName { get; set; }

    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

    [Column("Entscheidungsnotiz")]
    public string? DecisionNote { get; set; }

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
