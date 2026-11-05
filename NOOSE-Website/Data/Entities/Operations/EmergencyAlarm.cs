using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Operations;

/// <summary>A call for agents to report in, open for a fixed time; the recipients are fixed when it is triggered.</summary>
[Table("Einsatzalarme")]
public class EmergencyAlarm : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>What is going on.</summary>
    [Column("Lage")]
    public string Situation { get; set; } = string.Empty;

    [Column("Ort")]
    public string? Location { get; set; }

    /// <summary>Division that is called; null = all agents.</summary>
    [Column("Ziel")]
    public Division? Target { get; set; }

    /// <summary>Triggered without the usual sign-off because waiting would cost too much.</summary>
    [Column("GefahrImVerzug")]
    public bool IsEmergency { get; set; }

    /// <summary>Why waiting was not possible; set when <see cref="IsEmergency"/> is.</summary>
    [Column("Begruendung")]
    public string? Reason { get; set; }

    [Column("AusgeloestVonId")]
    public string TriggeredById { get; set; } = string.Empty;

    /// <summary>Codename at trigger time, kept so the alarm stays readable after the account is gone.</summary>
    [Column("AusgeloestVonName")]
    public string TriggeredByName { get; set; } = string.Empty;

    [Column("AusgeloestAm")]
    public DateTime TriggeredAt { get; set; }

    /// <summary>End of the answer window; the alarm counts as running until then or until it is ended.</summary>
    [Column("Ablauf")]
    public DateTime Expiry { get; set; }

    /// <summary>Set when the alarm was ended by hand or expired; null = still running.</summary>
    [Column("BeendetAm")]
    public DateTime? EndedAt { get; set; }

    [Column("BeendetVonId")]
    public string? EndedById { get; set; }

    /// <summary>When the triggerer pinged the open recipients again; at most once per alarm.</summary>
    [Column("NachgepingtAm")]
    public DateTime? RepingedAt { get; set; }

    /// <summary>Operation the alarm was turned into, if any.</summary>
    public string? OperationId { get; set; }
    public Operation? Operation { get; set; }

    public List<EmergencyAlarmRecipient> Recipients { get; set; } = new();

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
