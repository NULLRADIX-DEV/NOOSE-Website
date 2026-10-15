using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Recruiting;

/// <summary>A reusable recruiting test definition (question set) that HRB builds and assigns to applicants.</summary>
[Table("BewerbungTests")]
public class BewerbungTest : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("IstAktiv")]
    public bool IsActive { get; set; } = true;

    [Column("Sortierung")]
    public int Sorting { get; set; }

    /// <summary>Pass threshold in percent; null = no pass/fail verdict.</summary>
    [Column("Bestehensgrenze")]
    public int? PassPercent { get; set; }

    /// <summary>Processing time in minutes; null = no limit. Runs from the applicant's start, not from the assignment.</summary>
    [Column("BearbeitungszeitMinuten")]
    public int? TimeLimitMinutes { get; set; }

    /// <summary>Applicants, agents or both; the existing catalog stays recruiting-only.</summary>
    [Column("Verwendung")]
    public TestUsage Usage { get; set; } = TestUsage.Bewerbung;

    /// <summary>Training module an agent passing this test gets ticked; null = none.</summary>
    [Column("AusbildungsmodulId")]
    public string? TrainingModuleId { get; set; }

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
