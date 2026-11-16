using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>A request to move an agent to another division: filed, heard by the Administration, decided by the Board.</summary>
[Table("Versetzungsantraege")]
public class TransferRequest : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    [Column("VonAbteilung")]
    public Division? FromDivision { get; set; }

    [Column("NachAbteilung")]
    public Division ToDivision { get; set; }

    [Column("Begruendung")]
    public string Reason { get; set; } = string.Empty;

    [Column("Status")]
    public TransferRequestStatus Status { get; set; } = TransferRequestStatus.Open;

    [Column("AntragstellerName")]
    public string? RequesterName { get; set; }

    [Column("Anhoerung")]
    public string? HearingNote { get; set; }

    [Column("AngehoertVonName")]
    public string? HearingByName { get; set; }

    [Column("AngehoertAm")]
    public DateTime? HearingAt { get; set; }

    [Column("EntscheidungNotiz")]
    public string? DecisionNote { get; set; }

    [Column("EntschiedenVonName")]
    public string? DecidedByName { get; set; }

    [Column("EntschiedenAm")]
    public DateTime? DecidedAt { get; set; }

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
