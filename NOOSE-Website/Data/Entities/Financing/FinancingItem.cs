using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Financing;

/// <summary>A financeable catalog position: fixed unit price plus the rules that bound a request for it.</summary>
[Table("Finanzierungspositionen")]
public class FinancingItem : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Position name; unique (service-checked).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Grouping label from the admin-maintained suggestion catalog.</summary>
    [Column("Kategorie")]
    public string? Category { get; set; }

    [Column("Beschreibung")]
    public string? Description { get; set; }

    /// <summary>Fixed price of one unit; the request total is this times the quantity.</summary>
    [Column("Einzelpreis")]
    public decimal UnitPrice { get; set; }

    /// <summary>Share NOOSE covers (1-100); the rest is the agent's own contribution.</summary>
    [Column("Zuschussanteil")]
    public int SubsidyPercent { get; set; } = 100;

    /// <summary>Lower ranks never see the position when filing a request.</summary>
    [Column("MindestDienstgrad")]
    public Rank MinimumRank { get; set; } = Rank.Trainee;

    /// <summary>Upper bound for the quantity of this position within one request.</summary>
    [Column("MaxMenge")]
    public int MaxQuantity { get; set; } = 1;

    /// <summary>Only active positions can be requested.</summary>
    [Column("IstAktiv")]
    public bool IsActive { get; set; } = true;

    [Column("Sortierung")]
    public int Sorting { get; set; }

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
