using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>One change of an agent's home division, written by every path that moves it.</summary>
[Table("Versetzungsverlauf")]
public class DivisionTransfer : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("VonAbteilung")]
    public Division? FromDivision { get; set; }

    [Column("NachAbteilung")]
    public Division? ToDivision { get; set; }

    [Column("Zeitpunkt")]
    public DateTime At { get; set; }

    [Column("DurchName")]
    public string? ByName { get; set; }

    /// <summary>The granted request behind it, if any.</summary>
    [Column("AntragId")]
    public string? RequestId { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
