using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.OrgChart;

namespace NOOSE_Website.Services;

/// <summary>Assembles org-chart data in flat queries (no N+1, no CROSS APPLY on MySQL).</summary>
public class OrgChartService(IDbContextFactory<AppDbContext> dbFactory) : IOrgChartService
{
    public async Task<OrgChartData> GetAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // rankless agents have no level to chart
        var roster = await db.Users.AsNoTracking().OnlySelectable()
            .Where(a => a.Rank != null)
            .OrderBy(a => a.Codename)
            .ToListAsync(cancellationToken);

        // unit derived per row
        var seated = roster.Select(a => (Agent: a, Unit: DivisionRules.Of(a))).Where(s => !s.Unit.IsNone).ToList();
        var board = Column(seated.Where(s => s.Unit.IsBoard).Select(s => s.Agent), null);
        var divisions = DivisionDisplay.All
            .Select(d => Column(seated.Where(s => !s.Unit.IsBoard && s.Unit.Home == d).Select(s => s.Agent), d))
            .ToList();

        // flag crosses units
        var administration = roster
            .Where(a => a.IsHRB)
            .OrderByDescending(a => a.Rank)
            .ThenBy(a => a.Codename)
            .ToList();

        // visible and approved taskforces only
        var mayAllTf = viewer.MayAllTaskforcesSee();
        var meId = viewer.GetAgentId();
        var taskforces = await db.Taskforces.AsNoTracking()
            .OnlyVisible(db, mayAllTf, meId)
            .Where(t => t.Status == TaskforceStatus.Approved)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        // load all members in one flat query, then group in memory
        var tfIds = taskforces.Select(t => t.Id).ToList();
        var selectable = db.Users.OnlySelectable();
        var members = tfIds.Count == 0
            ? new List<TaskforceAgent>()
            : await db.TaskforceAgents.AsNoTracking()
                .Where(ta => tfIds.Contains(ta.TaskforceId) && selectable.Any(u => u.Id == ta.AgentId))
                .Include(ta => ta.Agent)
                .ToListAsync(cancellationToken);

        var staffings = taskforces
            .Select(t => new TaskforceStaffing(t,
                members
                    .Where(m => m.TaskforceId == t.Id)
                    .OrderBy(m => m.Role == TaskforceRole.Member) // leads first
                    .ThenBy(m => m.Role)
                    .ThenBy(m => m.Agent!.Codename)
                    .ToList()))
            .ToList();

        return new OrgChartData(board, divisions, administration, staffings);
    }

    /// <summary>One column from its members, rank tiers highest first.</summary>
    private static OrgChartColumn Column(IEnumerable<Agent> members, Division? division)
    {
        var list = members.ToList();
        var tiers = list
            .GroupBy(a => a.Rank!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new RankGroup(g.Key, g.ToList()))
            .ToList();
        var limit = division is { } d ? DivisionDisplay.Limit(d) : DivisionDisplay.BoardLimit;
        return new OrgChartColumn(division, list.Count, limit, tiers);
    }
}
