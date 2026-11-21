namespace NOOSE_Website.Models.Statistics;

/// <summary>One feature in the usage statistics.</summary>
/// <param name="AgentDays">Sum over the days of the different agents per day; not a count of people.</param>
/// <param name="PeakAgents">Most different agents on any one day; a lower bound on the people behind the row.</param>
public sealed record UsageRow(string Key, string Label, string Group, long Events, int AgentDays, int PeakAgents)
{
    /// <summary>Never used in the window.</summary>
    public bool Never => Events == 0;
}

/// <summary>Usage of the listed features within a window.</summary>
/// <param name="FirstDay">First day anything was counted at all; before it, a zero means nothing.</param>
/// <param name="ClippedByRetention">The chosen range reaches back further than counters are kept.</param>
public sealed record UsageStatistics(
    DateOnly From,
    DateOnly To,
    DateOnly? FirstDay,
    bool ClippedByRetention,
    IReadOnlyList<UsageRow> Rows)
{
    public static UsageStatistics Empty { get; } = new(default, default, null, false, []);
}
