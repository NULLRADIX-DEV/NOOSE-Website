using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Groups;

/// <summary>A person group (loose collection of people) as a case file with members, assigned agents and classification.</summary>
[Table("Personengruppen")]
public class PersonGroup : IAuditable, ISoftDelete, IClassifiableRecord, IArchivable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-G-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    [Column("Beschreibung")]
    public string? Description { get; set; }
    [Column("Ziele")]
    public string? Targets { get; set; }

    [Column("Art")]
    public GroupsKind Kind { get; set; } = GroupsKind.Grouping;

    [Column("Einstufung")]
    public Classification Classification { get; set; } = Classification.Unknown;

    /// <summary>Estimated total group size (optional).</summary>
    [Column("GeschaetzteMitgliederzahl")]
    public int? EstimatedMemberCount { get; set; }

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

    public List<PersonGroupMember> Members { get; set; } = new();
    public List<PersonGroupAgent> Agents { get; set; } = new();
    public List<PersonGroupPhoto> Photos { get; set; } = new();

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
