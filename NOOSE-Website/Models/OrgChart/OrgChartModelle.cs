using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.OrgChart;

/// <summary>Read-only org-chart data: Board, division columns, Administration cross-section, visible approved taskforces.</summary>
public sealed record OrgChartData(
    OrgChartColumn Board,
    IReadOnlyList<OrgChartColumn> Divisions,
    IReadOnlyList<Agent> Administration,
    IReadOnlyList<TaskforceStaffing> Taskforces)
{
    /// <summary>Anyone charted at all.</summary>
    public bool HasAgents => Board.Count > 0 || Divisions.Any(d => d.Count > 0);

    /// <summary>Every charted agent once, Board first.</summary>
    public IEnumerable<Agent> Agents => Board.Agents.Concat(Divisions.SelectMany(d => d.Agents));
}

/// <summary>One column with its seats and rank tiers (highest first); a null division is the Board.</summary>
public sealed record OrgChartColumn(Division? Division, int Count, int Limit, IReadOnlyList<RankGroup> Ranks)
{
    public bool IsBoard => Division is null;

    public bool OverLimit => Count > Limit;

    public string Title => Division is { } d ? DivisionDisplay.Name(d) : DivisionDisplay.BoardName;

    public string Icon => Division is { } d ? DivisionDisplay.Icon(d) : DivisionDisplay.BoardIcon;

    public MudBlazor.Color Colour => Division is { } d ? DivisionDisplay.Colour(d) : DivisionDisplay.BoardColour;

    public IEnumerable<Agent> Agents => Ranks.SelectMany(r => r.Agents);
}

/// <summary>All agents of one rank within one column.</summary>
public sealed record RankGroup(Rank Rank, IReadOnlyList<Agent> Agents);

/// <summary>A taskforce with its visible staffing; lead first.</summary>
public sealed record TaskforceStaffing(Taskforce Taskforce, IReadOnlyList<TaskforceAgent> Members);
