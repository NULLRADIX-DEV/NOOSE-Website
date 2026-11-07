using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Regulation;

/// <summary>One section of the service regulation ("Abschnitt 3: Handlungsvorschriften"); its number follows from the order.</summary>
[Table("DvoAbschnitte")]
public class RegulationSection : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    /// <summary>The foreword: shown first and never numbered.</summary>
    [Column("IstVorwort")]
    public bool IsPreamble { get; set; }

    public ICollection<RegulationParagraph> Paragraphs { get; set; } = new List<RegulationParagraph>();

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
