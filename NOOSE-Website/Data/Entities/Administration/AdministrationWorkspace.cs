using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>The one workspace row of the Administration Division; carrier of comments, follow-ups and timeline.</summary>
[Table("Administration")]
public class AdministrationWorkspace : IAuditable
{
    public const string SingletonId = "administration";

    /// <summary>Fixed id, see <see cref="SingletonId"/>.</summary>
    public string Id { get; set; } = SingletonId;

    [Column("InformationenHtml")]
    public string? InformationHtml { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
