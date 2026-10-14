using System.ComponentModel.DataAnnotations.Schema;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Data.Entities.Decorations;

/// <summary>A named honour leadership can award; the catalog behind every <see cref="DecorationAward"/>.</summary>
/// <remarks>No soft delete: once awarded it can only be switched off, and an unused one is removed for good.</remarks>
[Table("Ehrenzeichen")]
public class Decoration : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    /// <summary>Prefill of the citation; placeholders expand only when an award applies it.</summary>
    [Column("Standardtext")]
    public string? DefaultCitation { get; set; }

    /// <summary>Key into <c>DecorationSymbols</c>.</summary>
    [Column("Symbol")]
    public string Symbol { get; set; } = string.Empty;

    [Column("Stufe")]
    public DecorationTier Tier { get; set; }

    [Column("Aktiv")]
    public bool IsActive { get; set; } = true;

    [Column("Reihenfolge")]
    public int Order { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
