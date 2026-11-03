using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>A tip message; either the internal handling thread or the conversation with the citizen (by audience).</summary>
/// <remarks>
/// One table, two threads, exactly like <see cref="Recruiting.BewerbungMessage"/>. <see cref="AuthorAgentId"/> is set
/// on internal rows only — a citizen-facing row structurally carries no agent, so the outward projection has nothing
/// to strip and cannot forget to.
/// </remarks>
[Table("HinweisNachrichten")]
public class HinweisNachricht : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("HinweisId")]
    public string HinweisId { get; set; } = string.Empty;
    public Hinweis? Hinweis { get; set; }

    [Column("Zielgruppe")]
    public TipMessageAudience Audience { get; set; }

    [Column("Text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>True when the citizen wrote it; only meaningful for the citizen audience.</summary>
    [Column("VonBuerger")]
    public bool AuthorIsCitizen { get; set; }

    /// <summary>Internal thread only; a citizen-facing row leaves this null and reads as "FIB" outside.</summary>
    [Column("AutorAgentId")]
    public string? AuthorAgentId { get; set; }
    public Agent? AuthorAgent { get; set; }

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
