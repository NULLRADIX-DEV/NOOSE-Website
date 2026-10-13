using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Timeline;

/// <summary>Window, filters and page cursor for the agency-wide chronicle.</summary>
/// <param name="ExcludeAgentId">Leaves out what this agent did; system rows and score jumps carry no actor and stay.</param>
public sealed record ChronikQuery(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? AgentIds = null,
    IReadOnlyList<TimelineCategory>? Categories = null,
    string? Text = null,
    DateTime? BeforeUtc = null,
    int MinEvents = 40,
    string? ExcludeAgentId = null);

/// <summary>One record-level event on the global chronicle; always anchored on a record the viewer may see.</summary>
public sealed record ChronikEvent(
    DateTime Timestamp,
    TimelineCategory Category,
    string EntityType,
    string EntityId,
    string Name,
    string Title,
    string? Detail,
    string? ActorName,
    string? Href,
    bool RecordDeleted,
    IReadOnlyList<AuditDisplay.FieldChange>? Changes = null);

/// <summary>Chronicle page: events newest first, plus the exclusive upper bound of the next page.</summary>
public sealed record ChronikResult(
    IReadOnlyList<ChronikEvent> Events,
    DateTime? NextCursorUtc,
    bool HasMore);

/// <summary>Bucket width of the overview band; derived from the window, never chosen by the caller.</summary>
public enum ChronikBucketUnit
{
    Hour,
    Day,
    Week,
    Month,
}

/// <summary>One stacked segment of a band bucket; Slot indexes the band's category groups.</summary>
public readonly record struct ChronikDensitySegment(int Slot, int Count);

/// <summary>One bar of the density band: local bucket start, total and its per-group split.</summary>
/// <remarks>Local, not UTC, so the band and the feed's day headers cannot drift apart.</remarks>
public sealed record ChronikDensityBucket(
    DateTime StartLocal,
    int Total,
    IReadOnlyList<ChronikDensitySegment> Segments);

/// <summary>Band buckets plus the headline numbers the KPI row shows.</summary>
public sealed record ChronikDensity(
    IReadOnlyList<ChronikDensityBucket> Buckets,
    ChronikBucketUnit Unit,
    int Total,
    int DistinctAgents,
    int DistinctRecords,
    int WindowDays,
    bool Capped)
{
    /// <summary>Events per day across the whole window, not just the days that carry events.</summary>
    public int AveragePerDay => WindowDays <= 0 ? 0 : (int)Math.Round((double)Total / WindowDays);

    /// <summary>Highest bucket total; the band's y-axis domain.</summary>
    public int Peak => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.Total);

    /// <summary>Nothing to plot.</summary>
    public static ChronikDensity Empty { get; } =
        new(Array.Empty<ChronikDensityBucket>(), ChronikBucketUnit.Day, 0, 0, 0, 1, false);
}

/// <summary>An agent selectable in the chronicle filter.</summary>
public sealed record ChronikAgentOption(string Id, string Name);

/// <summary>Filter options for the chronicle (record types + acting agents).</summary>
public sealed record ChronikFilterOptions(IReadOnlyList<string> Types, IReadOnlyList<ChronikAgentOption> Agents);
