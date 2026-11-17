using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Filter for the admin audit/access log viewer.</summary>
public sealed class AuditLogFilter
{
    public string? AgentId { get; set; }
    public string? EntityType { get; set; }
    public AuditAction? Action { get; set; }
    public string? EntityId { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>Hard cap on returned rows; the viewer pages the result client-side.</summary>
    public const int MaxRows = 1000;
}

/// <summary>One change-log row for display.</summary>
public sealed record AuditChangeRow(
    long Id, DateTime TimestampUtc, string? AgentName, string EntityType, string EntityId,
    AuditAction Action, string? ChangesJson, string? AgentId = null);

/// <summary>One access-log row for display.</summary>
public sealed record AuditAccessRow(
    long Id, DateTime TimestampUtc, string? AgentName, string EntityType, string EntityId, string? AgentId = null);

/// <summary>Capped page of change-log rows plus the unfiltered match total.</summary>
public sealed record AuditLogPage(IReadOnlyList<AuditChangeRow> Rows, int TotalCount)
{
    public bool Capped => TotalCount > Rows.Count;
}

/// <summary>Capped page of access-log rows plus the unfiltered match total.</summary>
public sealed record AccessLogPage(IReadOnlyList<AuditAccessRow> Rows, int TotalCount)
{
    public bool Capped => TotalCount > Rows.Count;
}

/// <summary>Agent option for the audit filter dropdown; Marker flags team leads and partner agencies.</summary>
public sealed record AuditAgentOption(string Id, string Codename, string? Marker = null);

/// <summary>Dropdown source values for the audit viewer filters.</summary>
public sealed record AuditFilterOptions(
    IReadOnlyList<AuditAgentOption> Agents, IReadOnlyList<string> EntityTypes);
