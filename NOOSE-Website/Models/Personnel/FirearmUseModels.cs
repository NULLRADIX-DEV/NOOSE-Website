using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Personnel;

/// <summary>Filters of the leadership overview; every part is optional.</summary>
/// <param name="AgentId">Only reports of this agent.</param>
/// <param name="Division">Only agents whose home division is this one (the Board counts by its home).</param>
/// <param name="FromUtc">Inclusive start of the period.</param>
/// <param name="ToUtc">Exclusive end of the period.</param>
public sealed record FirearmUseFilter(
    string? AgentId = null,
    Division? Division = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);

/// <summary>One report as shown in a list.</summary>
/// <param name="Home">Home division the agent sits in today; null for an account without a rank.</param>
/// <param name="MayDelete">Whether the viewer may delete this very row; never true for the viewer's own report.</param>
public sealed record FirearmUseRow(
    string Id,
    string AgentId,
    string Codename,
    Rank? Rank,
    Division? Home,
    bool IsBoard,
    DateTime Timestamp,
    string Reason,
    bool MayDelete);
