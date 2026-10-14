using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>One released entry of the public leadership chart: a snapshot, never the agent record itself.</summary>
/// <remarks>
/// This is the one place where the house shows a real name outwards, and it does so only for the leadership band and
/// only for entries someone actively released. <c>Agent</c> stays in <c>PublicVisibility.NeverPublic</c>: nothing here
/// is projected from it at read time. The name, the rank wording, the role and the photo are all editorial copies,
/// so a promotion, a rename or a new picture changes the chart only when the editor says so.
/// <para>
/// The photo is a COPY under the public upload path, exactly like a wanted notice's. The internal avatar endpoint
/// stays behind <c>Policies.ActiveAgent</c> and is never the file this serves.
/// </para>
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
