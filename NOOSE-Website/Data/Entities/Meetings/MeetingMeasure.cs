using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Meetings;

/// <summary>Ties a job or follow-up to the agenda item it was decided under; the status lives on the target.</summary>
/// <remarks>
/// A pointer, not a copy: title, assignee, due date and done state are read from the job or follow-up every time, so
/// the item can never show a stale status. No FK on <see cref="TargetId"/>, because it names one of two tables.
/// Carried over with its item when the next meeting is cloned, which is what lets that meeting ask what came of it.
/// </remarks>
[Table("BesprechungMassnahmen")]
public class MeetingMeasure : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("BesprechungPunktId")]
    public string AgendaItemId { get; set; } = string.Empty;

    [Column("Art")]
    public MeetingMeasureKind Kind { get; set; }

    /// <summary>Id of the job or follow-up, depending on <see cref="Kind"/>.</summary>
    [Column("ZielId")]
    public string TargetId { get; set; } = string.Empty;

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
