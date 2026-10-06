using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>Agency-wide partner access: switched-on functions and content that is never shown. No row = defaults.</summary>
[Table("PartnerBehoerdenProfile")]
public class PartnerAgencyProfile : IAuditable
{
    [Key]
    [Column("Behoerde")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public PartnerAgency Agency { get; set; }

    [Column("Funktionen")]
    public PartnerFeature Features { get; set; }

    /// <summary>Blocked child content; beats every record, child and account release.</summary>
    [Column("GesperrteInhalte")]
    public PartnerContent BlockedContent { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
