using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Jobs;

/// <summary>A job/to-do (team board): visible to all active agents, hence no classification — unlike a case.</summary>
[Table("Aufgaben")]
public class Job : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-A-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Open;

    [Column("Prioritaet")]
    public JobPriority Priority { get; set; } = JobPriority.Normal;

    /// <summary>Optional due date; overdue = in the past while still open.</summary>
    [Column("Faelligkeit")]
    public DateTime? DueDate { get; set; }

    /// <summary>Set once status moves to done/cancelled.</summary>
    [Column("ErledigtAm")]
    public DateTime? DoneAt { get; set; }

    /// <summary>Highest due reminder already sent; dedupes the recurring background check.</summary>
    [Column("FaelligkeitErinnerungsstufe")]
    public JobDueReminderStage DueReminderStage { get; set; } = JobDueReminderStage.None;

    /// <summary>Restricted: only assignees, creator and supervisors see the job; otherwise visible to all.</summary>
    [Column("IstEingeschraenkt")]
    public bool IsRestricted { get; set; }

    public List<JobAssignment> Assignments { get; set; } = new();

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
