using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>An agent-saved search (name + serialized criteria); owned by an agent, hard-deletable.</summary>
[Table("GespeicherteSuchen")]
public class SavedSearch : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AgentId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [Column("SuchparameterJson")]
    public string SearchParameterJson { get; set; } = string.Empty;

    /// <summary>A worker reruns the search as its owner and reports hits the previous run did not have.</summary>
    [Column("Ueberwacht")]
    public bool IsWatched { get; set; }

    /// <summary>When the watch last claimed a run; null until the first one.</summary>
    [Column("ZuletztGeprueftAm")]
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Hit keys of the last run; null means the next run only sets the baseline.</summary>
    [Column("BekannteTrefferJson")]
    public string? KnownHitsJson { get; set; }

    /// <summary>Hit keys reported since the owner last opened the search.</summary>
    [Column("NeueTrefferJson")]
    public string? UnseenHitsJson { get; set; }

    /// <summary>Rights fingerprint the known hits were found under.</summary>
    [Column("RechteStempel")]
    public string? ScopeStamp { get; set; }

    /// <summary>The last run ran out of time before every category answered.</summary>
    [Column("LetzterLaufUnvollstaendig")]
    public bool LastRunIncomplete { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
