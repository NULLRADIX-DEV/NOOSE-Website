using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>Retired editorial entry of the old public leadership chart; nothing reads it anymore.</summary>
/// <remarks>
/// The roster page projects names straight from the agents now. The table stays so a rollback finds its data and
/// so deleting an agent can still clear the restricted foreign key; drop it in a later release.
/// </remarks>
[Table("OeffentlicheFuehrungsprofile")]
public class OeffentlichesFuehrungsprofil : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The handle the outward page and the photo endpoint use; never the row id.</summary>
    /// <remarks>
    /// An outward projection is addressed by its own public key, exactly as a notice is addressed by its case
    /// number: the row id is an internal fact, and a guessable one at that once it appears in an image URL.
    /// </remarks>
    [Column("OeffentlicherSchluessel")]
    public string PublicKey { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Which agent the entry was created from; kept for the editor, never projected outwards.</summary>
    [Column("AgentId")]
    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    /// <summary>Name as the public sees it — the editorial copy, not <c>Agent.RealName</c>.</summary>
    [Column("AnzeigeName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Title line as released; a copy of the rank wording, so a promotion rewrites no published chart.</summary>
    [Column("Dienstgradbezeichnung")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Free role line, e.g. "Leitung Einsatz".</summary>
    [Column("Funktion")]
    public string? RoleText { get; set; }

    [Column("Sortierung")]
    public int SortOrder { get; set; }

    /// <summary>Name of the copy under the public upload path; never an agent avatar file name.</summary>
    [Column("FotoDateiname")]
    public string? PhotoFileName { get; set; }

    [Column("FotoTyp")]
    public string? PhotoContentType { get; set; }

    /// <summary>Released decorations as a JSON copy, set in the editor; a correction rewrites its entry, a revocation drops it.</summary>
    [Column("EhrenzeichenJson")]
    public string? DecorationsJson { get; set; }

    [Column("VeroeffentlichtAm")]
    public DateTime? PublishedAt { get; set; }

    [Column("VeroeffentlichtVonId")]
    public string? PublishedById { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
