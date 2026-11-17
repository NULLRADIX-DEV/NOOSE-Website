using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Food;

/// <summary>An agent's food and drink request: decided as one and issued as one withdrawal from the evidence room.</summary>
[Table("Verpflegungsanforderungen")]
public class FoodRequest : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    /// <summary>Requesting agent; also the recipient of the issue.</summary>
    public string? AgentId { get; set; }
    public Agent? Agent { get; set; }

    public FoodRequestStatus Status { get; set; } = FoodRequestStatus.Requested;

    [Column("Begruendung")]
    public string? Justification { get; set; }

    /// <summary>Requested food pieces across all lines.</summary>
    [Column("EssenBeantragt")]
    public int FoodRequested { get; set; }

    /// <summary>Requested drink pieces across all lines.</summary>
    [Column("TrinkenBeantragt")]
    public int DrinkRequested { get; set; }

    /// <summary>Food pieces after the decision-time cuts; this is what counts against the weekly limit.</summary>
    [Column("EssenGenehmigt")]
    public int? FoodApproved { get; set; }

    [Column("TrinkenGenehmigt")]
    public int? DrinkApproved { get; set; }

    /// <summary>Monday of the limit week the approval counts against, stamped on approval.</summary>
    [Column("LimitWoche")]
    public DateTime? LimitWeekStart { get; set; }

    [Column("EntscheiderName")]
    public string? DeciderName { get; set; }
    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }
    [Column("Entscheidungsnotiz")]
    public string? DecisionNote { get; set; }

    /// <summary>How many food pieces the approval went past the remaining weekly limit.</summary>
    [Column("UeberschreitungEssen")]
    public int? OverrunFood { get; set; }
    [Column("UeberschreitungTrinken")]
    public int? OverrunDrink { get; set; }
    [Column("UeberschreitungsBegruendung")]
    public string? OverrunReason { get; set; }

    [Column("AusgegebenAm")]
    public DateTime? IssuedAt { get; set; }
    [Column("AusgegebenVonName")]
    public string? IssuedByName { get; set; }

    /// <summary>Evidence-room withdrawal created by the issue; unique, so the same request cannot be issued twice.</summary>
    [Column("AsservatEintragId")]
    public string? EvidenceEntryId { get; set; }

    public List<FoodRequestLine> Lines { get; set; } = new();

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
