using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Recruiting;

/// <summary>Assigns one test to one application; the applicant fills it in the portal.</summary>
[Table("BewerbungTestZuweisungen")]
public class BewerbungTestAssignment : IAuditable, ISoftDelete, ITestAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string BewerbungId { get; set; } = string.Empty;
    public Bewerbung? Bewerbung { get; set; }

    public string TestId { get; set; } = string.Empty;
    public BewerbungTest? Test { get; set; }

    [Column("ZugewiesenVon")]
    public string? AssignedByName { get; set; }

    /// <summary>When the applicant started the attempt; the clock runs from here, not from the assignment.</summary>
    /// <remarks>Stamped exactly once by a compare-and-swap: prerender, a second tab and F5 all race for it.</remarks>
    [Column("GestartetAm")]
    public DateTime? StartedAt { get; set; }

    /// <summary>The authoritative deadline in UTC; null means this attempt carries no limit.</summary>
    [Column("FristBis")]
    public DateTime? DeadlineAt { get; set; }

    /// <summary>Minutes frozen at the start; without it a later edit to the test shortens a running attempt.</summary>
    [Column("BearbeitungszeitMinuten")]
    public int? TimeLimitMinutes { get; set; }

    /// <summary>Extra minutes granted by HRB; kept as the lasting evidence that time was given.</summary>
    [Column("ZusatzMinuten")]
    public int ExtraMinutes { get; set; }

    /// <summary>Closed by the clock rather than by the applicant; the marker the grading panel shows.</summary>
    [Column("ZeitAbgelaufen")]
    public bool TimedOut { get; set; }

    /// <summary>Which attempt this is; a reset keeps the row, so this also reseeds the option shuffle.</summary>
    [Column("Versuch")]
    public int AttemptCount { get; set; } = 1;

    [Column("AbgeschlossenAm")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the grading was declared finished; null while questions are still being marked.</summary>
    [Column("BewertetAm")]
    public DateTime? GradedAt { get; set; }

    [Column("BewertetVon")]
    public string? GradedByName { get; set; }

    /// <summary>Result frozen at that moment; without it a later edit to the test rewrites an old verdict.</summary>
    [Column("ErgebnisPunkte")]
    public int? FinalPoints { get; set; }

    [Column("ErgebnisMaxPunkte")]
    public int? FinalMaxPoints { get; set; }

    /// <summary>Threshold at the moment of freezing; the verdict is derived from it, so it is frozen with it.</summary>
    [Column("ErgebnisBestehensgrenze")]
    public int? FinalPassPercent { get; set; }

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
