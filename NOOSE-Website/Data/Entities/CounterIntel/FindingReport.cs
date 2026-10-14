using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.CounterIntel;

/// <summary>What the finding watch already reported, one row per source, key and flagged account.</summary>
/// <remarks>
/// Deliberately neither auditable nor soft-deleted: it names the agents that stood out, and the change protocol is
/// read by the read-only supervision. Only the watch writes it; nothing ever shows a row of it on its own.
/// </remarks>
[Table("AuffaelligkeitMeldungen")]
public class FindingReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Quelle")]
    public FindingSource Source { get; set; }

    /// <summary>Rule id for a rule finding, anomaly kind name for a NOOSEI one.</summary>
    [Column("Schluessel")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The flagged account; no foreign key, like the access log.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Rule: newest event already consumed by a report. NOOSEI: start of the week the row speaks for (UTC).</summary>
    [Column("GemeldetBis")]
    public DateTime CoveredUntil { get; set; }

    /// <summary>Level of the last report; NOOSEI reports again only when it rises within the week.</summary>
    [Column("Stufe")]
    public CounterIntelSeverity Grade { get; set; }

    /// <summary>When the bell last rang for this row (UTC); null for a silent baseline.</summary>
    [Column("GemeldetAm")]
    public DateTime? ReportedAt { get; set; }
}
