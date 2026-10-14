using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.CounterIntel;

/// <summary>One access-log row reduced to what the insider-threat rules need (timestamp already local).</summary>
public readonly record struct AccessRow(string AgentId, string? AgentName, DateTime LocalTimestamp, string EntityType, string EntityId);

/// <summary>KPI summary of the access log over the window.</summary>
public sealed record CounterIntelOverview(int TotalAccesses, int DistinctAgents, int DistinctRecords, int OffHoursAccesses, int WindowDays);

/// <summary>One agent's 24-hour access histogram.</summary>
public sealed record HeatAgent(string AgentId, string AgentName, IReadOnlyList<int> Hours);

/// <summary>Access heatmap (agent × hour-of-day) + the maximum cell for intensity scaling.</summary>
public sealed record CounterIntelHeatmap(IReadOnlyList<HeatAgent> Agents, int MaxCount);

/// <summary>Access count for one record type.</summary>
public sealed record TypeCount(string EntityType, int Count);

/// <summary>A single recent access.</summary>
public sealed record RecentAccess(DateTime WhenLocal, string EntityType, string EntityId, string? Href);

/// <summary>An agent's access profile.</summary>
public sealed record AgentAccessProfile(
    string AgentId, string AgentName, int Total, int DistinctRecords, int OffHours,
    IReadOnlyList<TypeCount> ByType, IReadOnlyList<RecentAccess> Recent);

/// <summary>A flagged insider-threat pattern. Severity is the measured peak, Grade the rule's own level.</summary>
public sealed record InsiderFlag(
    string AgentId, string AgentName, string Rule, string Detail, int Severity, string? Href,
    string RuleId = "", CounterIntelSeverity Grade = CounterIntelSeverity.Warning);

/// <summary>An agent selectable in the cockpit.</summary>
public sealed record AgentOption(string Id, string Name);

/// <summary>A record selectable in the rule editor's "specific records" filter.</summary>
public sealed record RecordOption(string EntityType, string Id, string Label);

/// <summary>
/// One log line enriched with everything the rule engine may filter on. Access-log lines arrive as
/// <see cref="CounterIntelActionKind.Read"/>, audit-log lines as the matching write action.
/// </summary>
public sealed record CounterIntelEvent
{
    public required string AgentId { get; init; }
    public string? AgentName { get; init; }
    public required DateTime LocalTimestamp { get; init; }

    /// <summary>The log row's own timestamp; the watch compares against it, never against local time.</summary>
    public DateTime TimestampUtc { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required CounterIntelActionKind Action { get; init; }

    // resolved target properties; null when the record was not looked up or no longer exists
    public bool? TargetIsClassified { get; init; }
    public Classification? TargetClassification { get; init; }
    public IReadOnlyCollection<string>? TargetTagIds { get; init; }

    // resolved actor properties; null when the roster row is gone
    public Rank? ActorRank { get; init; }
    public bool ActorIsTru { get; init; }
    public bool ActorIsHrb { get; init; }
    public bool ActorIsAdmin { get; init; }
    public PartnerAgency? ActorPartnerAgency { get; init; }

    /// <summary>True/false once both sides resolve to a person file; null when either does not.</summary>
    public bool? ActorSharesOrgWithTarget { get; init; }

    // display only, never a condition: whether a flag can link to a personnel file at all
    public bool ActorHasNoPersonnelFile { get; init; }

    /// <summary>The event is a tip whose anonymity promise still holds, so no flag may name the person behind it.</summary>
    public bool ActorIdentityWithheld { get; init; }

    /// <summary>Stable key for distinct-record counting.</summary>
    public string RecordKey => $"{EntityType}:{EntityId}";
}

/// <summary>A rule paired with its parsed definition, ready for evaluation.</summary>
/// <param name="Notify">Whether new findings ring the leadership bell.</param>
/// <param name="ReportFrom">Nothing before this instant (UTC) is ever reported; null until the watch arms the rule.</param>
public sealed record CounterIntelRuleView(
    string Id, string Name, string? Description, CounterIntelSeverity Severity,
    bool IsActive, int Order, CounterIntelRuleDefinition Definition,
    bool Notify = true, DateTime? ReportFrom = null);

/// <summary>Editor payload for creating or updating a rule.</summary>
public sealed class CounterIntelRuleInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CounterIntelSeverity Severity { get; set; } = CounterIntelSeverity.Warning;
    public bool IsActive { get; set; } = true;
    public bool Notify { get; set; } = true;
    public int Order { get; set; }
    public CounterIntelRuleDefinition Definition { get; set; } = new();
}
