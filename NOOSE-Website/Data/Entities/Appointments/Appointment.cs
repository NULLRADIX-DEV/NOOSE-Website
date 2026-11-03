using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Appointments;

/// <summary>Calendar appointment as a linkable record with three visibility levels (public/restricted/private); no classification concept.</summary>
[Table("Termine")]
public class Appointment : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-TM-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Kategorie")]
    public AppointmentCategory Category { get; set; } = AppointmentCategory.Misc;

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Planned;

    [Column("Ort")]
    public string? Location { get; set; }

    /// <summary>Start time (stored UTC). Required.</summary>
    [Column("Beginn")]
    public DateTime Start { get; set; }

    /// <summary>End time (optional, stored UTC).</summary>
    [Column("Ende")]
    public DateTime? End { get; set; }

    /// <summary>All-day appointment; times are then hidden/ignored.</summary>
    [Column("Ganztaegig")]
    public bool AllDay { get; set; }

    [Column("Beschreibung")]
    public string? Description { get; set; }

    /// <summary>Visibility level: public, restricted (creator + participants + supervision) or private; supervision sees all.</summary>
    [Column("Sichtbarkeit")]
    public AppointmentVisibilityLevel Visibility { get; set; } = AppointmentVisibilityLevel.Public;

    public List<AppointmentAssignment> Participant { get; set; } = new();

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
