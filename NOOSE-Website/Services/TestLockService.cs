using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;

namespace NOOSE_Website.Services;

/// <summary>Pages locked for an agent by their running internal test attempts.</summary>
/// <param name="AssignmentId">The attempt to send the agent back to.</param>
/// <param name="TestTitle">Shown in the notice.</param>
/// <param name="Keys">Locked nav keys of all running attempts together.</param>
public sealed record TestLock(string AssignmentId, string TestTitle, IReadOnlyList<string> Keys)
{
    public IReadOnlyList<string> Routes { get; } = TestLockRules.Routes(Keys);

    public IReadOnlyList<string> Labels { get; } = TestLockRules.Labels(Keys);

    public bool Blocks(string? path) => TestLockRules.IsBlocked(path, Routes);
}

/// <summary>Reads the page lock of a running internal test; pages, search, FIBI and the bot all ask here.</summary>
public interface ITestLockService
{
    /// <summary>The viewer's own lock, or null when no running attempt locks anything.</summary>
    Task<TestLock?> GetActiveAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<TestLock?> GetActiveForAgentAsync(string? agentId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITestLockService" />
/// <remarks>Started and not handed in counts as running, through the submit grace, the same window in which
/// answers are still accepted. Not cached: a lock must start with the first question and end with the hand-in.</remarks>
public class TestLockService(IDbContextFactory<AppDbContext> dbFactory) : ITestLockService
{
    public Task<TestLock?> GetActiveAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => actor.Identity?.IsAuthenticated == true
            ? GetActiveForAgentAsync(actor.GetAgentId(), cancellationToken)
            : Task.FromResult<TestLock?>(null);

    public async Task<TestLock?> GetActiveForAgentAsync(string? agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return null;
        }
        var openFrom = DateTime.UtcNow - TestDeadline.SubmitGrace;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var running = await db.AgentTestAssignments.AsNoTracking()
            .Where(a => a.AgentId == agentId && a.StartedAt != null && a.CompletedAt == null
                && (a.DeadlineAt == null || a.DeadlineAt > openFrom))
            .Join(db.BewerbungTests.Where(t => t.BlockedPages != null), a => a.TestId, t => t.Id,
                (a, t) => new { a.Id, a.StartedAt, t.Title, t.BlockedPages })
            .ToListAsync(cancellationToken);

        var keys = running.SelectMany(r => TestLockRules.Parse(r.BlockedPages)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return null;
        }
        var first = running.OrderBy(r => r.StartedAt).First();
        return new TestLock(first.Id, first.Title, keys);
    }
}
