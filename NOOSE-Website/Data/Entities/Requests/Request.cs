using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Requests;

/// <summary>An inbox-workflow request with a polymorphic target; currently used for classification upgrades.</summary>
[Table("Antraege")]
public class Request : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Typ")]
    public RequestType Type { get; set; } = RequestType.Upgrade;

    /// <summary>Target record CLR type name (nameof).</summary>
    [Column("ZielTyp")]
    public string TargetType { get; set; } = string.Empty;

    [Column("ZielId")]
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Denormalized target display (name + case number) for the inbox.</summary>
    [Column("ZielBezeichnung")]
    public string TargetDesignation { get; set; } = string.Empty;

    [Column("ZielEinstufung")]
    public Classification TargetClassification { get; set; }

    [Column("Begruendung")]
    public string? Justification { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Requested;

    [Column("AntragstellerName")]
    public string? RequesterName { get; set; }

    [Column("EntscheiderName")]
    public string? DeciderName { get; set; }

    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

    [Column("Entscheidungsnotiz")]
    public string? DecisionNote { get; set; }

    // ---- Partner-Freigabe fields (only set when Type == PartnerFreigabe) ----
    [Column("FreigabeBehoerde")]
    public PartnerAgency? FreigabeAgency { get; set; }

    [Column("FreigabePartnerAgentId")]
    public string? FreigabePartnerAgentId { get; set; }

    [Column("FreigabeInklusiveKinder")]
    public bool FreigabeIncludesChildren { get; set; }

    /// <summary>Wished end of the release (UTC); null = unlimited. Applied to the share on approval.</summary>
    [Column("FreigabeGueltigBis")]
    public DateTime? FreigabeValidUntil { get; set; }

    // ---- Veroeffentlichung field (only set when Type == Veroeffentlichung) ----
    /// <summary>Snapshot row this request would publish; no FK, mirroring the Freigabe block above.</summary>
    [Column("VeroeffentlichungFahndungId")]
    public string? PublicationWantedId { get; set; }

    // ---- Kopfgeld field (only set when Type == Kopfgeld) ----
    /// <summary>Bounty share this request would confirm; its own column rather than reusing the one above.</summary>
    /// <remarks>
    /// PublicWantedService joins its pending list on <see cref="PublicationWantedId"/>, so a shared column would let
    /// bounty requests appear in the publication inbox.
    /// </remarks>
    [Column("KopfgeldAnteilId")]
    public string? BountyShareId { get; set; }

    // ---- IAuditable ----
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
