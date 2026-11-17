using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;

namespace NOOSE_Website.Services.Public;

/// <summary>Who may open one ticket and who may read its internal thread; query and point check in one place.</summary>
/// <remarks>
/// Modelled on <c>TaskforceVisibility</c>. Two different questions on purpose:
/// <list type="bullet">
/// <item>the desk (leadership) sees every ticket and every internal note — it is their correspondence;</item>
/// <item>an agent attached to a single ticket sees that one, and nothing else.</item>
/// </list>
/// The read-only supervision reads along, like everywhere else in the house; a ticket is not a classified record.
/// </remarks>
public static class TicketVisibility
{
    /// <summary>True for the desk itself: leadership and the read-only supervision above it. Its read mark is the desk's.</summary>
    public static bool IsDesk(ClaimsPrincipal actor) => actor.MayClassifiedRead();

    /// <summary>Reads every ticket: the desk plus the Administration Division, which does not move the desk's read mark.</summary>
    public static bool ReadsDesk(ClaimsPrincipal actor) => actor.MayTicketRead();

    /// <summary>True when the actor is attached to this ticket.</summary>
    public static async Task<bool> IsParticipantAsync(AppDbContext db, string ticketId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        if (string.IsNullOrEmpty(agentId))
        {
            return false;
        }
        return await db.TicketBeteiligte.AsNoTracking()
            .AnyAsync(p => p.TicketId == ticketId && p.AgentId == agentId, cancellationToken);
    }

    /// <summary>True when the actor may open this one ticket at all.</summary>
    public static async Task<bool> MayReadAsync(AppDbContext db, string ticketId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
        => ReadsDesk(actor) || await IsParticipantAsync(db, ticketId, actor, cancellationToken);

    /// <summary>True when the actor may read and write the internal thread of this ticket.</summary>
    /// <remarks>Same set as <see cref="MayReadAsync"/> today; separate, because the citizen thread may narrow later.</remarks>
    public static Task<bool> MayReadInternalAsync(AppDbContext db, string ticketId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
        => MayReadAsync(db, ticketId, actor, cancellationToken);

    /// <summary>Batch twin for a viewer scope: which of these tickets the viewer may open.</summary>
    /// <remarks>
    /// Modelled on <c>TaskforceVisibility.VisibleIdsAsync</c>, and the reason it exists is the link engine: a
    /// reference to a ticket must resolve per row, and one query for the whole page beats one per link. Existence
    /// is part of the answer, so a deleted ticket drops out here rather than rendering as a dangling reference.
    /// </remarks>
    public static async Task<HashSet<string>> ReadableIdsAsync(AppDbContext db, IReadOnlyCollection<string> ticketIds,
        ViewerScope scope, CancellationToken cancellationToken = default)
    {
        if (ticketIds.Count == 0 || !scope.IsInternalAgent)
        {
            return new();
        }

        var existing = await db.Tickets.AsNoTracking()
            .Where(t => ticketIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        if (scope.MayClassifiedRead || scope.IsAdministrationMember)
        {
            // the scope side of Permission.RequireTicketRead
            return existing.ToHashSet();
        }
        // empty and null alike, as in IsParticipantAsync: an agent id of "" would match a malformed row
        var agentId = scope.MeId;
        if (string.IsNullOrEmpty(agentId) || existing.Count == 0)
        {
            return new();
        }

        var attached = await db.TicketBeteiligte.AsNoTracking()
            .Where(p => existing.Contains(p.TicketId) && p.AgentId == agentId)
            .Select(p => p.TicketId)
            .ToListAsync(cancellationToken);
        return attached.ToHashSet();
    }

    /// <summary>Scope twin of <see cref="MayReadAsync(AppDbContext, string, ClaimsPrincipal, CancellationToken)"/>.</summary>
    public static async Task<bool> MayReadAsync(AppDbContext db, string ticketId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => (await ReadableIdsAsync(db, new[] { ticketId }, scope, cancellationToken)).Count > 0;
}
