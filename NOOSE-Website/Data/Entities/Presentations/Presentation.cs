using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Presentations;

/// <summary>A slide deck an agent builds and presents live; visible to all, one group, or the invited.</summary>
[Table("Praesentationen")]
public class Presentation : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Sichtbarkeit")]
    public PresentationAccess Access { get; set; }

    /// <summary>Group of a closed presentation; ignored otherwise.</summary>
    [Column("Zielgruppe")]
    public AnnouncementAudience? Audience { get; set; }

    /// <summary>Taskforce of a closed presentation aimed at one.</summary>
    [Column("ZielId")]
    public string? TargetId { get; set; }

    /// <summary>Lowest rank of a closed presentation aimed at a rank.</summary>
    [Column("MindestDienstgrad")]
    public Rank? MinRank { get; set; }

    /// <summary>Who owns it; may edit and delete next to leadership.</summary>
    [Column("BesitzerId")]
    public string OwnerId { get; set; } = string.Empty;

    public List<PresentationSlide> Slides { get; set; } = [];
    public List<PresentationInvite> Invites { get; set; } = [];

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
