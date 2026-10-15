using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Presentations;

/// <summary>A Google Slides deck imported as PDF; each of its pages is one slide of the presentation.</summary>
/// <remarks>Refreshing writes the new PDF under a new stored name and bumps <see cref="Revision"/>; the row and its id
/// stay, so the slides keep pointing at it and the browser never shows the pages of the old file.</remarks>
[Table("PraesentationDateien")]
public class PresentationFile : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PresentationId { get; set; } = string.Empty;
    public Presentation? Presentation { get; set; }

    /// <summary>Name in the presentation store; never leaves the server.</summary>
    [Column("DateiName")]
    public string FileNameSaved { get; set; } = string.Empty;

    /// <summary>Title of the Google deck, or the uploaded file's name.</summary>
    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Groesse")]
    public long SizeBytes { get; set; }

    [Column("Seiten")]
    public int PageCount { get; set; }

    /// <summary>Counts the refreshes; part of the file's URL, so a cached old PDF is never drawn.</summary>
    [Column("Revision")]
    public int Revision { get; set; } = 1;

    [Column("ImportQuelle")]
    public ImportSourceKind SourceKind { get; set; }

    /// <summary>Google file id the import came from; null for an upload.</summary>
    [Column("ImportQuelleId")]
    public string? SourceId { get; set; }

    [Column("ImportiertAm")]
    public DateTime ImportedAt { get; set; }

    [Column("ImportiertVonId")]
    public string? ImportedById { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
