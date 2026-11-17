using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>A disciplinary proceeding against one agent, from opening to discontinuation or decision.</summary>
[Table("Disziplinarverfahren")]
public class DisciplinaryProceeding : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    /// <summary>The agent the proceeding is against.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Simplified hearing instead of formal proceedings (trainee or probation).</summary>
    [Column("Vereinfacht")]
    public bool Simplified { get; set; }

    [Column("Status")]
    public DisciplinaryStatus Status { get; set; } = DisciplinaryStatus.Open;

    /// <summary>Provisionally relieved from duty while the proceeding runs.</summary>
    [Column("VorlaeufigSuspendiert")]
    public bool ProvisionallySuspended { get; set; }

    /// <summary>Why it was opened; internal only.</summary>
    [Column("Anlass")]
    public string Grounds { get; set; } = string.Empty;

    [Column("EroeffnetAm")]
    public DateTime OpenedAt { get; set; }

    [Column("Massnahme")]
    public DisciplinaryMeasure? Measure { get; set; }

    [Column("Sachverhalt")]
    public string? Facts { get; set; }

    [Column("Begruendung")]
    public string? Reasoning { get; set; }

    [Column("Belehrung")]
    public string? RightsNotice { get; set; }

    [Column("AbgeschlossenAm")]
    public DateTime? ClosedAt { get; set; }

    [Column("EntschiedenVonName")]
    public string? DecidedByName { get; set; }

    /// <summary>Rank of the agent when the decision was made; the notice prints it.</summary>
    [Column("DienstgradBeiAbschluss")]
    public Rank? RankAtClose { get; set; }

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
