using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>The phone number a leadership card on the public roster shows; one row per agent.</summary>
/// <remarks>
/// Its own table, not a column on <c>Agent</c>: <c>UserManager.UpdateAsync</c> writes the whole user row from
/// whatever copy it holds, and a stale copy would silently roll a freshly entered number back.
/// </remarks>
[Table("OeffentlicheTelefonnummern")]
public class OeffentlicheTelefonnummer : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("AgentId")]
    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    [Column("Telefonnummer")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
