using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Llm;

/// <summary>A NOOSEI chat thread. Owner-private: leadership and admins get counts and cost through the request log,
/// never the content. Deliberately outside the global trash, which is agency-wide visible.</summary>
[Table("KiUnterhaltungen")]
public class NooseiConversation : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("BesitzerId")]
    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("LetzteNachrichtAm")]
    public DateTime LastMessageAt { get; set; }

    [Column("NachrichtenAnzahl")]
    public int MessageCount { get; set; }

    /// <summary>Record the chat was opened from, so follow-ups keep their context.</summary>
    [Column("AnkerTyp")]
    public string? AnchorEntityType { get; set; }

    [Column("AnkerId")]
    public string? AnchorEntityId { get; set; }

    /// <summary>Fingerprint of the owner's scope at the last turn. A change drops tool results from the replay,
    /// because their text was authorised under rights the owner no longer has.</summary>
    [Column("RechteStempel")]
    public string? ScopeStamp { get; set; }

    /// <summary>Agency chat or a partner's law chat. A conversation never changes mode: an account moved from
    /// internal to partner must not replay internal answers into the law chat.</summary>
    [Column("Modus")]
    public NooseiChatMode Mode { get; set; }

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
