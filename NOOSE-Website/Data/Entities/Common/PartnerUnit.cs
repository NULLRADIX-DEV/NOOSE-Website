using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>A unit inside a partner agency (e.g. DoJ SID); carries the account's rights and defaults.</summary>
[Table("PartnerEinheiten")]
public class PartnerUnit : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Behoerde")]
    public PartnerAgency Agency { get; set; }

    public string Name { get; set; } = string.Empty;

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    /// <summary>Old tier written alongside, so an older build still reads a sane rank.</summary>
    [Column("AltRang")]
    public PartnerRank LegacyRank { get; set; } = PartnerRank.Member;

    /// <summary>Prosecution: members may be the responsible prosecutor and sign warrants of their investigation.</summary>
    [Column("IstStaatsanwaltschaft")]
    public bool IsProsecution { get; set; }

    [Column("DarfErmittlungAnlegen")]
    public bool MayCreateInvestigations { get; set; }

    /// <summary>Unit leads see every investigation of a cross-agency taskforce their agency takes part in.</summary>
    [Column("LeitungSiehtAlleErmittlungen")]
    public bool LeadSeesAllInvestigations { get; set; }

    /// <summary>Record types and tabs allowlist (<see cref="Models.Common.PartnerRankVisibility"/>); null = all released.</summary>
    [Column("SichtbarkeitJson")]
    public string? VisibilityJson { get; set; }

    /// <summary>Investigation tab defaults (slug → access level) for the unit and its accounts.</summary>
    [Column("ErmittlungVorgabenJson")]
    public string? InvestigationDefaultsJson { get; set; }

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
