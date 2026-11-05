using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>A training-module catalog entry; only active modules appear in the personnel file to be checked off.</summary>
[Table("AusbildungsModule")]
public class TrainingModule : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Unique module name.</summary>
    public string Name { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("IstAktiv")]
    public bool IsActive { get; set; } = true;

    /// <summary>Part of every new agent's onboarding plan.</summary>
    [Column("IstPflicht")]
    public bool IsMandatory { get; set; }

    /// <summary>List sort order; smaller first.</summary>
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
