using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Announcements;

/// <summary>Bulletin-board announcement, optionally pushed as a broadcast and optionally requiring acknowledgment; broadcast features are leadership-only.</summary>
[Table("Ankuendigungen")]
public class Announcement : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. NOOSE-N-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Body text with optional @-mentions; may be empty (title-only note).</summary>
    [Column("Inhalt")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Pinned to the top of the board.</summary>
    [Column("Wichtig")]
    public bool Important { get; set; }

    [Column("Zielgruppe")]
    public AnnouncementAudience Audience { get; set; } = AnnouncementAudience.AllActive;

    /// <summary>Taskforce id for taskforce-scoped audience; otherwise null.</summary>
    [Column("ZielId")]
    public string? TargetId { get; set; }

    /// <summary>Minimum rank for rank-scoped audience; otherwise null.</summary>
    [Column("MinDienstgrad")]
    public Rank? MinRank { get; set; }

    /// <summary>Also push as a bell broadcast to the recipients (leadership).</summary>
    [Column("AlsBroadcast")]
    public bool AsBroadcast { get; set; }

    /// <summary>Recipients must acknowledge (leadership).</summary>
    [Column("QuittierungVerlangt")]
    public bool AcknowledgmentRequired { get; set; }

    /// <summary>Acknowledgment deadline; whoever still owes it is reminded once it passes. Null = no reminder.</summary>
    [Column("QuittierenBis")]
    public DateTime? AcknowledgmentDueAt { get; set; }

    /// <summary>When the reminder went out; the worker claims it so it is sent once.</summary>
    [Column("QuittierungErinnertAm")]
    public DateTime? AcknowledgmentRemindedAt { get; set; }

    /// <summary>Recipient snapshot for acknowledgment; populated only when required.</summary>
    public List<AnnouncementAcknowledgment> Acknowledgments { get; set; } = new();

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
