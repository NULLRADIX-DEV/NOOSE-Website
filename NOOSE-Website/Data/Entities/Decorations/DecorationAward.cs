using System.ComponentModel.DataAnnotations.Schema;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Data.Entities.Decorations;

/// <summary>One decoration awarded to an agent; revoked rather than deleted, apart from the badge sweep's table.</summary>
/// <remarks>Awarder, recipient rank and signatory are snapshots, so a reprint keeps the signature of the day.</remarks>
[Table("Ehrungen")]
public class DecorationAward : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("EhrenzeichenId")]
    public string DecorationId { get; set; } = string.Empty;
    public Decoration? Decoration { get; set; }

    /// <summary>Ceremony date, local calendar day like a personnel note.</summary>
    [Column("VerliehenAm")]
    public DateTime AwardedOn { get; set; }

    [Column("Verleihungstext")]
    public string Citation { get; set; } = string.Empty;

    /// <summary>Recipient's rank when awarded.</summary>
    [Column("Dienstgrad")]
    public Rank? RecipientRank { get; set; }

    /// <summary>No foreign key: an award outlives the account that gave it.</summary>
    [Column("VerliehenVonId")]
    public string? AwardedById { get; set; }

    [Column("VerliehenVonName")]
    public string? AwardedByName { get; set; }

    /// <summary>Signatory's codename when awarded; null prints the plain office line.</summary>
    [Column("UnterzeichnerName")]
    public string? SignatoryName { get; set; }

    [Column("UnterzeichnerDienstgrad")]
    public Rank? SignatoryRank { get; set; }

    /// <summary>Board entry that announced it, trashed again on revocation.</summary>
    [Column("AnkuendigungId")]
    public string? AnnouncementId { get; set; }

    [Column("AberkanntAm")]
    public DateTime? RevokedAt { get; set; }

    [Column("AberkanntVonName")]
    public string? RevokedByName { get; set; }

    [Column("AberkennungsGrund")]
    public string? RevocationReason { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
