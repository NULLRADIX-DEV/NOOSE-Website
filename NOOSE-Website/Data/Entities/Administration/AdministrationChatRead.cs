using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>Per-agent read mark of the Administration chat; written only by ExecuteUpdate or a plain insert, never audited.</summary>
[Table("AdministrationChatGelesen")]
public class AdministrationChatRead
{
    [Key]
    public string AgentId { get; set; } = string.Empty;

    [Column("ZuletztGelesenAm")]
    public DateTime LastReadAt { get; set; }
}
