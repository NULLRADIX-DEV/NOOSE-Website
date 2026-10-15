using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Presentations;

/// <summary>One slide of a presentation; its pictures are text images filed under the slide.</summary>
[Table("PraesentationFolien")]
public class PresentationSlide : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PresentationId { get; set; } = string.Empty;
    public Presentation? Presentation { get; set; }

    [Column("Sortierung")]
    public int Sorting { get; set; }

    [Column("Layout")]
    public SlideLayout Layout { get; set; }

    [Column("Titel")]
    public string? Title { get; set; }

    /// <summary>Rich text of the slide; sanitized on save like every rich-text column.</summary>
    [Column("InhaltHtml")]
    public string? BodyHtml { get; set; }

    /// <summary>Picture of an image layout: the id of a text image filed under this slide.</summary>
    [Column("BildId")]
    public string? ImageId { get; set; }

    /// <summary>Speaker notes; only the presenter's view shows them.</summary>
    [Column("Notizen")]
    public string? Notes { get; set; }

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
