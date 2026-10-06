using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>Releases every record of a type matching a scope to one agency, including records created later.</summary>
[Table("PartnerRegelFreigaben")]
public class PartnerReleaseRule : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Behoerde")]
    public PartnerAgency Agency { get; set; }

    /// <summary>Releasable CLR type name (nameof).</summary>
    [Column("EntitaetTyp")]
    public string EntityType { get; set; } = string.Empty;

    [Column("Umfang")]
    public PartnerRuleScope Scope { get; set; }

    /// <summary>True = matched records count as released whole (all children).</summary>
    [Column("InklusiveKinder")]
    public bool IncludesChildren { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
