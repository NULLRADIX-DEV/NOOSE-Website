using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Warrants;

/// <summary>A search, seizure or arrest order: requested by an agent, signed or refused by leadership, shown in roleplay.</summary>
[Table("Anordnungen")]
public class Warrant : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Art")]
    public WarrantType Type { get; set; }

    /// <summary>Stored stage; whether a signed warrant has run out is computed on read.</summary>
    public WarrantStatus Status { get; set; } = WarrantStatus.Requested;

    /// <summary>The person the order concerns; its visibility caps the warrant's.</summary>
    public string PersonId { get; set; } = string.Empty;

    /// <summary>Optional case the order belongs to; when set, it caps the visibility as well.</summary>
    [Column("VorgangId")]
    public string? CaseId { get; set; }

    /// <summary>What is to be searched or seized, or who is to be arrested, as printed on the paper.</summary>
    [Column("Gegenstand")]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Internal reasoning; never printed.</summary>
    [Column("Begruendung")]
    public string Justification { get; set; } = string.Empty;

    /// <summary>Snapshot of the cited paragraphs, one per line, so a later edit of the law book never moves a signed paper.</summary>
    [Column("Rechtsgrundlage")]
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of the cited paragraphs, for links back into the law book.</summary>
    [Column("GesetzIds")]
    public string LawIds { get; set; } = string.Empty;

    /// <summary>Requested validity in days; the signer may change it.</summary>
    [Column("GueltigkeitTage")]
    public int ValidityDays { get; set; }

    [Column("GueltigAb")]
    public DateTime? ValidFrom { get; set; }

    [Column("GueltigBis")]
    public DateTime? ValidUntil { get; set; }

    [Column("AntragstellerName")]
    public string? RequesterName { get; set; }

    /// <summary>Who signed or refused; an id without a foreign key, the name is the snapshot.</summary>
    [Column("EntscheiderId")]
    public string? DeciderId { get; set; }

    [Column("EntscheiderName")]
    public string? DeciderName { get; set; }

    /// <summary>Rank at the moment of the decision, printed under the signature.</summary>
    [Column("EntscheiderDienstgrad")]
    public Rank? DeciderRank { get; set; }

    /// <summary>Function of a partner signer (e.g. "DoJ · State Attorney Office"); null for a FIB signature.</summary>
    [Column("EntscheiderFunktion")]
    public string? DeciderFunction { get; set; }

    /// <summary>Investigation the order belongs to; its participants see and its prosecutor signs it.</summary>
    [Column("ErmittlungId")]
    public string? InvestigationId { get; set; }

    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

    [Column("Entscheidungsnotiz")]
    public string? DecisionNote { get; set; }

    [Column("AufgehobenAm")]
    public DateTime? CancelledAt { get; set; }

    [Column("AufgehobenVonName")]
    public string? CancelledByName { get; set; }

    [Column("Aufhebungsgrund")]
    public string? CancellationReason { get; set; }

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
