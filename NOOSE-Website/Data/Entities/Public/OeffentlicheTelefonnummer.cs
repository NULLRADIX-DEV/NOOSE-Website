using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>What a leadership card on the public roster carries by hand: phone and photo copy; one row per agent.</summary>
/// <remarks>
/// Its own table, not columns on <c>Agent</c>: <c>UserManager.UpdateAsync</c> writes the whole user row from
/// whatever copy it holds, and a stale copy would silently roll a freshly entered value back. The name predates the
/// photo; it stays because the version before it reads this table and a rollback must still find it.
/// </remarks>
[Table("OeffentlicheTelefonnummern")]
public class OeffentlicheTelefonnummer : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("AgentId")]
    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    /// <summary>Empty for none, never null: the version before the photo reads it as required.</summary>
    [Column("Telefonnummer")]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Name of the copy under the public upload path; never an agent avatar file name.</summary>
    [Column("FotoDateiname")]
    public string? PhotoFileName { get; set; }

    [Column("FotoTyp")]
    public string? PhotoContentType { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
