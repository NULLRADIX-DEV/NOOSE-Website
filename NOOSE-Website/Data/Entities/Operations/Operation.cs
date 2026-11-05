using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Operations;

/// <summary>An operation / mission report as a standalone event case file with assigned agents and classification.</summary>
[Table("Operationen")]
public class Operation : IAuditable, ISoftDelete, IClassifiableRecord, IArchivable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-OP-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Typ")]
    public string? Type { get; set; }

    public OperationStatus Status { get; set; } = OperationStatus.Planned;

    [Column("Ort")]
    public string? Location { get; set; }

    [Column("Beginn")]
    public DateTime? Start { get; set; }

    [Column("Ende")]
    public DateTime? End { get; set; }

    [Column("Ablauf")]
    public string? Expiry { get; set; }

    [Column("Ergebnis")]
    public string? Result { get; set; }

    [Column("Bemerkungen")]
    public string? Remarks { get; set; }

    /// <summary>Why the bureau took part, as the duty regulation asks for a foreign incident.</summary>
    [Column("Legitimation")]
    public string? Legitimation { get; set; }

    /// <summary>Other agencies on scene; text with mention tokens.</summary>
    [Column("BeteiligteBehoerden")]
    public string? InvolvedAgencies { get; set; }

    /// <summary>Evidence secured, as free text.</summary>
    [Column("Beweismittel")]
    public string? Evidence { get; set; }

    [Column("Einstufung")]
    public Classification Classification { get; set; } = Classification.Unknown;

    /// <summary>Restricted from general view; true when any secrecy level (leadership/TRU/HRB) is set.</summary>
    [Column("IstVerschlusssache")]
    public bool IsClassified { get; set; }

    /// <summary>Restricted to TRU (implies IsClassified); selects the audience instead of leadership-only.</summary>
    [Column("IstVerschlusssacheTRU")]
    public bool IsTRUClassified { get; set; }

    /// <summary>Restricted to HRB (implies IsClassified); selects the audience instead of leadership-only.</summary>
    [Column("IstVerschlusssacheHRB")]
    public bool IsHRBClassified { get; set; }

    /// <summary>Unified secrecy level: None when unrestricted, else TRU/HRB by audience flag, otherwise leadership.</summary>
    [NotMapped]
    public DocumentClassification SecrecyLevel
    {
        get => !IsClassified ? DocumentClassification.None
            : IsTRUClassified ? DocumentClassification.Tru
            : IsHRBClassified ? DocumentClassification.Hrb
            : DocumentClassification.Leadership;
        set
        {
            IsClassified = value != DocumentClassification.None;
            IsTRUClassified = value == DocumentClassification.Tru;
            IsHRBClassified = value == DocumentClassification.Hrb;
        }
    }

    /// <summary>Any secrecy level set (drives the lock indicator).</summary>
    [NotMapped]
    public bool IsRestricted => IsClassified || IsTRUClassified || IsHRBClassified;

    /// <summary>Aging disabled: record never goes stale.</summary>
    [Column("VeralterungDeaktiviert")]
    public bool AgingDisabled { get; set; }

    public List<OperationAgent> Agents { get; set; } = new();

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

    [Column("IstArchiviert")]
    public bool IsArchived { get; set; }
    [Column("ArchiviertAm")]
    public DateTime? ArchivedAt { get; set; }
    [Column("ArchiviertVonId")]
    public string? ArchivedById { get; set; }
    /// <summary>Optional note shown on the banner and in the audit row.</summary>
    [Column("Archivgrund")]
    public string? ArchiveReason { get; set; }
}
