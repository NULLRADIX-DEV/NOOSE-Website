using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>Assigns one catalog test to one agent; a retake is a new row, the old one stays as history.</summary>
[Table("AgentTestZuweisungen")]
public class AgentTestAssignment : IAuditable, ISoftDelete, ITestAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    public string TestId { get; set; } = string.Empty;
    public BewerbungTest? Test { get; set; }

    /// <summary>Who assigned it; the hand-in notifies them.</summary>
    [Column("ZugewiesenVonId")]
    public string? AssignedById { get; set; }

    [Column("ZugewiesenVon")]
    public string? AssignedByName { get; set; }

    /// <summary>Date by which the test should be taken; null = no due date. Not the attempt clock.</summary>
    [Column("FaelligAm")]
    public DateTime? DueAt { get; set; }

    /// <summary>When the agent started the attempt; the clock runs from here, not from the assignment.</summary>
    [Column("GestartetAm")]
    public DateTime? StartedAt { get; set; }

    /// <summary>End of the attempt clock in UTC; null means this attempt carries no limit.</summary>
    [Column("FristBis")]
    public DateTime? DeadlineAt { get; set; }

    /// <summary>Minutes frozen at the start; without it a later edit to the test shortens a running attempt.</summary>
    [Column("BearbeitungszeitMinuten")]
    public int? TimeLimitMinutes { get; set; }

    [Column("ZusatzMinuten")]
    public int ExtraMinutes { get; set; }

    [Column("ZeitAbgelaufen")]
    public bool TimedOut { get; set; }

    /// <summary>Release counter of this row; a reset keeps the row, so this also reseeds the option shuffle.</summary>
    [Column("Versuch")]
    public int AttemptCount { get; set; } = 1;

    [Column("AbgeschlossenAm")]
    public DateTime? CompletedAt { get; set; }

    [Column("BewertetAm")]
    public DateTime? GradedAt { get; set; }

    [Column("BewertetVon")]
    public string? GradedByName { get; set; }

    [Column("ErgebnisPunkte")]
    public int? FinalPoints { get; set; }

    [Column("ErgebnisMaxPunkte")]
    public int? FinalMaxPoints { get; set; }

    [Column("ErgebnisBestehensgrenze")]
    public int? FinalPassPercent { get; set; }

    /// <summary>Verdict frozen with the grading; null while ungraded or when the test has no threshold.</summary>
    [Column("Bestanden")]
    public bool? Passed { get; set; }

    /// <summary>The personnel-file entry the grading wrote; a regrade rewrites it instead of adding a second.</summary>
    [Column("PersonalakteVermerkId")]
    public string? PersonnelNoteId { get; set; }

    /// <summary>Claimed before the reminder a day ahead of the due date, so it goes out once.</summary>
    [Column("FaelligErinnertAm")]
    public DateTime? DueReminderSentAt { get; set; }

    /// <summary>Claimed before the overdue reminder, so it goes out once.</summary>
    [Column("UeberfaelligErinnertAm")]
    public DateTime? OverdueReminderSentAt { get; set; }

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
