using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>A proposal to award a decoration, filed by any agent and decided by the Administration.</summary>
[Table("Ehrungsvorschlaege")]
public class DecorationProposal : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("EhrenzeichenId")]
    public string DecorationId { get; set; } = string.Empty;

    [Column("Begruendung")]
    public string Reason { get; set; } = string.Empty;

    [Column("Status")]
    public DecorationProposalStatus Status { get; set; } = DecorationProposalStatus.Open;

    [Column("VorschlagenderName")]
    public string? ProposerName { get; set; }

    [Column("EntschiedenVonName")]
    public string? DecidedByName { get; set; }

    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

    [Column("EhrungId")]
    public string? AwardId { get; set; }

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
