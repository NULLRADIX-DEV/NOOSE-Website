using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Llm;

/// <summary>One wire message of a conversation, including tool calls and their results, so a turn can be replayed.</summary>
[Table("KiNachrichten")]
public class NooseiMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("UnterhaltungId")]
    public string ConversationId { get; set; } = string.Empty;
    public NooseiConversation? Conversation { get; set; }

    /// <summary>Explicit order; timestamps within one turn are not distinguishable.</summary>
    [Column("Reihenfolge")]
    public int Sequence { get; set; }

    /// <summary>user | assistant | tool — stored as text so the table stays readable.</summary>
    [Column("Rolle")]
    public string Role { get; set; } = string.Empty;

    [Column("Inhalt")]
    public string? Content { get; set; }

    [Column("WerkzeugAufrufeJson")]
    public string? ToolCallsJson { get; set; }

    [Column("WerkzeugAufrufId")]
    public string? ToolCallId { get; set; }

    [Column("WerkzeugName")]
    public string? ToolName { get; set; }

    /// <summary>Records the tools read for this answer, as the chat renders them under it. Only on assistant
    /// rows, and only records the asker was allowed to see — a tool cannot return anything else.</summary>
    [Column("Quellen")]
    public string? SourcesJson { get; set; }

    /// <summary>Proposal cards under this answer and their state; never part of the replay.</summary>
    [Column("Vorschlaege")]
    public string? ProposalsJson { get; set; }

    [Column("KontingentTokens")]
    public long? QuotaTokens { get; set; }

    [Column("IstFehler")]
    public bool IsError { get; set; }

    /// <summary>The token ceiling cut this answer off. Its own flag, not <see cref="IsError" />, which excludes a
    /// row from the replay — a torso is still the best evidence of what was already said.</summary>
    [Column("Gekuerzt")]
    public bool Truncated { get; set; }

    /// <summary>Answered without record access, because the endpoint could not take the tool block.</summary>
    [Column("OhneAktenzugriff")]
    public bool Degraded { get; set; }

    /// <summary>Case numbers the answer cited that no tool result of the conversation mentions, comma-separated.
    /// A note under the answer, never a rejection — and never phrased as "does not exist".</summary>
    [Column("NichtBelegt")]
    public string? UnsupportedCitations { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
}
