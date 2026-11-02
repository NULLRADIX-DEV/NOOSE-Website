using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Meetings;

/// <summary>Agency meeting as a linkable record; agenda and minutes sit behind the Senior Special Agent gate.</summary>
[Table("Besprechungen")]
public class Meeting : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-BS-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Start time (stored UTC). Required.</summary>
    [Column("Beginn")]
    public DateTime Start { get; set; }

    /// <summary>End time (optional, stored UTC).</summary>
    [Column("Ende")]
    public DateTime? End { get; set; }

    [Column("Ort")]
    public string? Location { get; set; }

    public MeetingStatus Status { get; set; } = MeetingStatus.Planned;

    /// <summary>Sanitized minutes HTML; leadership writes it.</summary>
    [Column("ProtokollHtml")]
    public string? MinutesHtml { get; set; }

    /// <summary>Source of the +14 day clone; unique, so a meeting can be cloned only once.</summary>
    [Column("VorherigeBesprechungId")]
    public string? PreviousMeetingId { get; set; }

    /// <summary>Dedupe stamp for the one-day reminder; cleared when the start moves.</summary>
    [Column("Erinnerung1TagGesendetAm")]
    public DateTime? ReminderDaySentAt { get; set; }

    /// <summary>Dedupe stamp for the 30-minute reminder; cleared when the start moves.</summary>
    [Column("Erinnerung30MinGesendetAm")]
    public DateTime? ReminderSoonSentAt { get; set; }

    /// <summary>Set once attendance is closed; from then on the snapshot is the history.</summary>
    [Column("AnwesenheitAbgeschlossenAm")]
    public DateTime? AttendanceClosedAt { get; set; }

    public List<MeetingAgendaItem> AgendaItems { get; set; } = new();

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
