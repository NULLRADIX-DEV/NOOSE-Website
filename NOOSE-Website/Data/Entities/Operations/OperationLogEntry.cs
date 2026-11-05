using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Operations;

/// <summary>Timestamped line in the log kept while an operation runs; visibility inherited from the operation.</summary>
[Table("Einsatztagebuch")]
public class OperationLogEntry : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string OperationId { get; set; } = string.Empty;
    public Operation? Operation { get; set; }

    [Column("Art")]
    public OperationLogKind Kind { get; set; } = OperationLogKind.Situation;

    /// <summary>Raw text incl. inline @{Type:Id} mention tokens.</summary>
    public string Text { get; set; } = string.Empty;

    [Column("Ort")]
    public string? Location { get; set; }

    /// <summary>Written after the operation had ended.</summary>
    [Column("IstNachtrag")]
    public bool IsAddendum { get; set; }

    [Column("AutorName")]
    public string? AuthorName { get; set; }

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
