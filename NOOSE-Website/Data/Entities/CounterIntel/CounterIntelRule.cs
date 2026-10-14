using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.CounterIntel;

/// <summary>Leadership-defined counter-intelligence rule; the condition set lives in DefinitionJson.</summary>
[Table("GegenaufklaerungsRegeln")]
public class CounterIntelRule : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Schweregrad")]
    public CounterIntelSeverity Severity { get; set; } = CounterIntelSeverity.Warning;

    /// <summary>Inactive rules are kept but never evaluated.</summary>
    [Column("IstAktiv")]
    public bool IsActive { get; set; } = true;

    /// <summary>Whether new findings ring the leadership bell; the cockpit shows them either way.</summary>
    [Column("Meldet")]
    public bool Notify { get; set; } = true;

    /// <summary>Nothing before this instant (UTC) is reported; reset when the rule starts to mean something new.</summary>
    [Column("MeldenAb")]
    public DateTime? ReportFrom { get; set; }

    /// <summary>Display order in the panel and in the findings tab (smaller first).</summary>
    [Column("Reihenfolge")]
    public int Order { get; set; }

    /// <summary>Serialized CounterIntelRuleDefinition.</summary>
    public string DefinitionJson { get; set; } = string.Empty;

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
