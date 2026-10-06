using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IRequestService" />
public class RequestService(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications) : IRequestService
{
    public async Task<bool> HasOpenRequestAsync(string targetType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // type-scoped: an open publication or partner request against the same record says nothing about an upgrade,
        // and the classification panel reads this as "an upgrade request is running"
        return await db.Requests.AnyAsync(
            a => a.Type == RequestType.Upgrade && a.TargetType == targetType && a.TargetId == targetId
                && a.Status == RequestStatus.Requested, cancellationToken);
    }

    public async Task UpgradeRequestAsync(string targetType, string targetId, string targetDesignation, Classification target,
        string justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        // top classification needs a request
        if (target != Classification.SecuredStateThreatening)
        {
            throw new InvalidOperationException("Ein Antrag ist nur für die Einstufung „Gesichert staatsgefährdend“ erforderlich.");
        }
        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new InvalidOperationException("Bitte eine Begründung für den Hochstufungs-Antrag angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await Visibility.IsRecordVisibleAsync(db, targetType, targetId, actor.IsLeadership(), cancellationToken))
        {
            throw new InvalidOperationException("Die Ziel-Akte wurde nicht gefunden.");
        }

        if (await db.Requests.AnyAsync(a => a.Type == RequestType.Upgrade && a.TargetType == targetType
                && a.TargetId == targetId && a.Status == RequestStatus.Requested, cancellationToken))
        {
            throw new InvalidOperationException("Für diese Akte läuft bereits ein Hochstufungs-Antrag.");
        }

        db.Requests.Add(new Request
        {
            Type = RequestType.Upgrade,
            TargetType = targetType,
            TargetId = targetId,
            TargetDesignation = targetDesignation.Trim(),
            TargetClassification = target,
            Justification = justification.Trim(),
            Status = RequestStatus.Requested,
            RequesterName = actor.GetCodename(),
        });
        await db.SaveChangesAsync(cancellationToken);

        await NotifyMentionsAsync(justification, "Begründung", targetType, targetId, actor, cancellationToken);
    }

    private Task NotifyMentionsAsync(string? text, string what, string targetType, string targetId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MentionNotify.DeltaAsync(notifications, null, text, $"einer {what} zu einem Antrag",
            targetType, targetId, actor, cancellationToken);

    public async Task<List<Request>> GetOpenAsync(bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var open = await db.Requests
            .Where(a => a.Status == RequestStatus.Requested && a.Type == RequestType.Upgrade)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var visible = new List<Request>();
        foreach (var a in open)
        {
            if (await Visibility.IsRecordVisibleAsync(db, a.TargetType, a.TargetId, isLeadership, cancellationToken))
            {
                visible.Add(a);
            }
        }
        return visible;
    }

    public async Task<int> GetOpenCountAsync(bool isLeadership, CancellationToken cancellationToken = default)
    {
        var upgradeCount = (await GetOpenAsync(isLeadership, cancellationToken)).Count;
        if (!isLeadership) return upgradeCount;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var partnerCount = await db.Requests
            .CountAsync(r => (r.Type == RequestType.PartnerFreigabe || r.Type == RequestType.PartnerAnfrage)
                && r.Status == RequestStatus.Requested, cancellationToken);
        return upgradeCount + partnerCount;
    }

    public async Task<List<Request>> GetMyAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Requests
            .Where(a => a.CreatedById == agentId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task DecideAsync(string requestId, bool approved, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireHighestClassification(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var request = await db.Requests.FirstOrDefaultAsync(a => a.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Antrag nicht gefunden.");
        if (request.Status != RequestStatus.Requested)
        {
            throw new InvalidOperationException("Dieser Antrag wurde bereits entschieden.");
        }
        // approval here means "set the classification on the target"; any other type would run that with an unset
        // classification and silently downgrade the record. Partner releases and publications are decided elsewhere.
        if (request.Type != RequestType.Upgrade)
        {
            throw new InvalidOperationException("Ungültiger Antragstyp.");
        }

        if (!await Visibility.IsRecordVisibleAsync(db, request.TargetType, request.TargetId, actor.IsLeadership(), cancellationToken))
        {
            throw new InvalidOperationException("Die Ziel-Akte ist nicht (mehr) für dich sichtbar.");
        }

        if (approved)
        {
            if (!await ClassificationOnTargetSetAsync(db, request, cancellationToken))
            {
                throw new InvalidOperationException("Die Ziel-Akte ist nicht mehr vorhanden.");
            }
            var entry = ClassificationHelper.Entry(request.TargetType, request.TargetId, request.TargetClassification, request.Justification, actor);
            entry.RequestId = request.Id;
            db.ClassificationHistory.Add(entry);
        }

        request.Status = approved ? RequestStatus.Approved : RequestStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        try
        {
            await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
                approved ? "Dein Hochstufungs-Antrag wurde genehmigt." : "Dein Hochstufungs-Antrag wurde abgelehnt.",
                "/profil", cancellationToken);
        }
        catch { /* best effort */ }

        await NotifyMentionsAsync(request.DecisionNote, "Entscheidung", request.TargetType, request.TargetId,
            actor, cancellationToken);
    }

    /// <summary>Set classification on target record.</summary>
    private static async Task<bool> ClassificationOnTargetSetAsync(AppDbContext db, Request request, CancellationToken cancellationToken)
    {
        switch (request.TargetType)
        {
            case nameof(Person):
                var person = await db.People.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (person is null) return false;
                person.Classification = request.TargetClassification;
                return true;
            case nameof(Faction):
                var faction = await db.Factions.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (faction is null) return false;
                faction.Classification = request.TargetClassification;
                return true;
            case nameof(PersonGroup):
                var group = await db.PersonGroups.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (group is null) return false;
                group.Classification = request.TargetClassification;
                return true;
            case nameof(Party):
                var party = await db.Parties.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (party is null) return false;
                party.Classification = request.TargetClassification;
                return true;
            case nameof(Operation):
                var operation = await db.Operations.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (operation is null) return false;
                operation.Classification = request.TargetClassification;
                return true;
            case nameof(Case):
                var @case = await db.Cases.FirstOrDefaultAsync(x => x.Id == request.TargetId, cancellationToken);
                if (@case is null) return false;
                @case.Classification = request.TargetClassification;
                return true;
            default:
                return false;
        }
    }
}
