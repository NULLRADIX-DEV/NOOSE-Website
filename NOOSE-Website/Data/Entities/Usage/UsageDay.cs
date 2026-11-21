using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Usage;

/// <summary>How often one feature was used on one Berlin calendar day, and by how many different agents.</summary>
/// <remarks>Not auditable on purpose: no agent, record or time of day.</remarks>
[Table("NutzungsTage")]
public class UsageDay
{
    [Column("Tag")]
    public DateOnly Day { get; set; }

    [Column("Schluessel")]
    public string Key { get; set; } = string.Empty;

    [Column("Ereignisse")]
    public long Events { get; set; }

    /// <summary>Different agents that day; counted in memory, only the number is stored.</summary>
    [Column("Agenten")]
    public int Agents { get; set; }
}
