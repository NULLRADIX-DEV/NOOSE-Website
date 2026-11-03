using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Shared classification logic.</summary>
public static class ClassificationHelper
{
    /// <summary>Enforce rank gate.</summary>
    public static void CheckRankGate(Classification @new, ClaimsPrincipal actor)
    {
        if (@new == Classification.SecuredStateThreatening && !actor.MayHighestClassification())
        {
            throw new InvalidOperationException(
                "'Gesichert staatsgefährdend' darf erst ab Senior Special Agent direkt gesetzt werden – sonst per Antrag (Phase 5).");
        }
    }

    /// <summary>Build history entry.</summary>
    public static ClassificationHistory Entry(string entityType, string entityId, Classification value, string? justification, ClaimsPrincipal actor)
        => new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Value = value,
            Justification = string.IsNullOrWhiteSpace(justification) ? null : justification.Trim(),
            Timestamp = DateTime.UtcNow,
            AgentId = actor.GetAgentId(),
            AgentName = actor.GetCodename(),
        };

    /// <summary>Justification as the viewer may read it; a partner gets none, like the reason row.</summary>
    public static string? JustificationFor(string? justification, ViewerScope scope)
        => scope.IsPartner ? null : justification;

    /// <summary>History of a record the viewer may see, newest first, justifications gated per viewer.</summary>
    public static async Task<List<ClassificationHistory>> HistoryAsync(
        AppDbContext db, string entityType, string entityId, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            return new();
        }
        // untracked, so the redaction never saves
        var history = await db.ClassificationHistory.AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
        foreach (var entry in history)
        {
            entry.Justification = JustificationFor(entry.Justification, scope);
        }
        return history;
    }
}
