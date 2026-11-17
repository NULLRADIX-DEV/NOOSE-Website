using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <summary>Food requests: a basket of food and drinks decided as one, issued as one withdrawal from the FIB stock, counted against weekly limits.</summary>
public class FoodService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    IFoodLimitService limitService,
    IEvidenceService evidence,
    INotificationService notifications) : IFoodService
{
    private const string CasePrefix = "VER";

    // ---- reads ----

    public async Task<List<FoodRequestDisplay>> GetOwnAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var me = actor.GetAgentId();
        if (string.IsNullOrEmpty(me))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAsync(db, db.FoodRequests.AsNoTracking().Where(r => r.AgentId == me), cancellationToken);
    }

    public async Task<List<FoodRequestDisplay>> GetVisibleAsync(FoodRequestStatus? status, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FoodRequests.AsNoTracking()
            .OnlyVisible(actor.MayClassifiedRead(), actor.GetAgentId());
        if (status is { } wanted)
        {
            query = query.Where(r => r.Status == wanted);
        }
        return await ProjectAsync(db, query, cancellationToken);
    }

    public async Task<List<FoodRequestDisplay>> GetForAgentAsync(string agentId, int max, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // own file or a reader of everything
        if (!actor.MayClassifiedRead() && actor.GetAgentId() != agentId)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAsync(db,
            db.FoodRequests.AsNoTracking().Where(r => r.AgentId == agentId),
            cancellationToken, Math.Clamp(max, 1, 200));
    }

    public async Task<int> GetOpenCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FoodRequests.CountAsync(r => r.Status == FoodRequestStatus.Requested, cancellationToken);
    }

    public async Task<FoodRequest?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.AsNoTracking()
            .Include(r => r.Lines)
            .Include(r => r.Agent)
            .OnlyVisible(actor.MayClassifiedRead(), actor.GetAgentId())
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is not null)
        {
            request.Lines = request.Lines.OrderBy(l => l.Sorting).ToList();
        }
        return request;
    }

    public async Task<FoodRequest?> GetByEvidenceEntryAsync(string evidenceEntryId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FoodRequests.AsNoTracking()
            .OnlyVisible(actor.MayClassifiedRead(), actor.GetAgentId())
            .FirstOrDefaultAsync(r => r.EvidenceEntryId == evidenceEntryId, cancellationToken);
    }

    // ---- writes ----

    public async Task<FoodRequest> CreateAsync(FoodRequestInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var me = actor.GetAgentId()
            ?? throw new InvalidOperationException("Kein Agenten-Kontext für die Anforderung.");
        var justification = input.Justification.TrimToNull();
        if (justification is { Length: > 2000 })
        {
            throw new InvalidOperationException("Die Begründung darf höchstens 2000 Zeichen lang sein.");
        }
        if (input.Lines.Count == 0)
        {
            throw new InvalidOperationException("Bitte mindestens eine Position auswählen.");
        }
        if (input.Lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException(
                "Jede Position darf nur einmal in der Anforderung stehen — bitte stattdessen die Menge erhöhen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == me, cancellationToken)
            ?? throw new InvalidOperationException("Agent nicht gefunden.");
        // supervision is RP-wide invisible and must never surface in a request list
        if (agent.IsTeamLead)
        {
            throw new InvalidOperationException("Die Team-Leitung fordert keine Verpflegung an.");
        }

        var wanted = input.Lines.Select(l => l.ItemId).ToList();
        var items = await db.FoodItems.AsNoTracking()
            .Include(i => i.EvidenceItem)
            .Where(i => wanted.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var request = new FoodRequest
        {
            AgentId = me,
            Status = FoodRequestStatus.Requested,
            Justification = justification,
        };

        var sorting = 0;
        foreach (var line in input.Lines)
        {
            if (!items.TryGetValue(line.ItemId, out var item) || item.EvidenceItem is null)
            {
                throw new InvalidOperationException("Eine gewählte Position existiert nicht mehr.");
            }
            var name = item.EvidenceItem.Name;
            if (!item.IsActive)
            {
                throw new InvalidOperationException($"„{name}“ kann derzeit nicht angefordert werden.");
            }
            if (line.Quantity < 1)
            {
                throw new InvalidOperationException($"Bitte für „{name}“ eine Menge von mindestens 1 angeben.");
            }
            if (line.Quantity > item.MaxQuantity)
            {
                throw new InvalidOperationException(
                    $"Von „{name}“ sind höchstens {item.MaxQuantity} pro Anforderung möglich.");
            }

            // snapshot: later catalog edits must never move a filed request
            request.Lines.Add(new FoodRequestLine
            {
                ItemId = item.Id,
                EvidenceItemId = item.EvidenceItemId,
                ItemName = name,
                Kind = item.Kind,
                Quantity = line.Quantity,
                Sorting = sorting++,
            });
        }

        request.FoodRequested = Sum(request.Lines, FoodKind.Food, l => l.Quantity);
        request.DrinkRequested = Sum(request.Lines, FoodKind.Drink, l => l.Quantity);

        // counter + record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        request.CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken);
        db.FoodRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await NotifyDecidersAsync(request, actor, cancellationToken);
        return request;
    }

    public async Task DecideAsync(string id, bool approved, FoodDecisionInput decision, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");

        if (request.Status is not (FoodRequestStatus.Requested or FoodRequestStatus.Rejected))
        {
            throw new InvalidOperationException(request.Status == FoodRequestStatus.Approved
                ? "Über diese Anforderung wurde bereits entschieden — die Genehmigung lässt sich zurücknehmen."
                : $"Eine Anforderung im Zustand „{FoodRequestStatusDisplay.Name(request.Status)}“ kann nicht entschieden werden.");
        }
        if (request.AgentId is null)
        {
            throw new InvalidOperationException(
                "Der Anforderer wurde gelöscht — über diese Anforderung kann nicht mehr entschieden werden.");
        }
        var target = approved ? FoodRequestStatus.Approved : FoodRequestStatus.Rejected;
        RequireTransition(request.Status, target);

        if (approved)
        {
            foreach (var line in request.Lines)
            {
                var quantity = decision.ApprovedQuantities.TryGetValue(line.Id, out var cut) ? cut : line.Quantity;
                if (quantity < 0 || quantity > line.Quantity)
                {
                    throw new InvalidOperationException(
                        $"Die genehmigte Menge für „{line.ItemName}“ muss zwischen 0 und {line.Quantity} liegen.");
                }
                line.ApprovedQuantity = quantity;
            }

            var food = Sum(request.Lines, FoodKind.Food, EffectiveQuantity);
            var drink = Sum(request.Lines, FoodKind.Drink, EffectiveQuantity);
            if (food + drink <= 0)
            {
                throw new InvalidOperationException(
                    "Es wurde keine Position genehmigt — dann bitte die Anforderung ablehnen.");
            }

            // neither Requested nor Rejected counts, so the remaining limit never includes this request
            var limit = await limitService.GetStatusAsync(request.AgentId, cancellationToken);
            var overrunFood = food - limit.FoodRemaining;
            var overrunDrink = drink - limit.DrinkRemaining;
            var overrun = (food > 0 && overrunFood > 0) || (drink > 0 && overrunDrink > 0);
            if (overrun && string.IsNullOrWhiteSpace(decision.OverrunReason))
            {
                throw new InvalidOperationException(
                    $"Die Genehmigung überschreitet das Wochenlimit ({OverrunText(food, overrunFood, drink, overrunDrink)}). " +
                    "Bitte eine Begründung für die Überschreitung angeben.");
            }

            request.LimitWeekStart = FoodWeek.Current();
            request.FoodApproved = food;
            request.DrinkApproved = drink;
            request.OverrunFood = food > 0 && overrunFood > 0 ? overrunFood : null;
            request.OverrunDrink = drink > 0 && overrunDrink > 0 ? overrunDrink : null;
            request.OverrunReason = overrun ? decision.OverrunReason.TrimToNull() : null;
        }
        else
        {
            ClearApproval(request, struckLines: true);
        }

        request.Status = target;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = decision.Note.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);

        await NotifyRequesterAsync(request, approved
            ? $"Deine Verpflegungsanforderung wurde angenommen: {Summary(request.FoodApproved ?? 0, request.DrinkApproved ?? 0)}"
            : "Deine Verpflegungsanforderung wurde abgelehnt.", cancellationToken);
    }

    public async Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");
        if (request.AgentId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Nur der Anforderer kann seine eigene Anforderung zurückziehen.");
        }
        RequireTransition(request.Status, FoodRequestStatus.Withdrawn);

        request.Status = FoodRequestStatus.Withdrawn;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeApprovalAsync(string id, bool reject, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");
        if (request.Status != FoodRequestStatus.Approved)
        {
            throw new InvalidOperationException(
                "Zurücknehmen geht nur bei einer angenommenen, noch nicht ausgegebenen Anforderung.");
        }
        var target = reject ? FoodRequestStatus.Rejected : FoodRequestStatus.Requested;
        RequireTransition(request.Status, target);

        ClearApproval(request, struckLines: reject);
        request.Status = target;
        request.DeciderName = reject ? actor.GetCodename() : null;
        request.DecidedAt = reject ? DateTime.UtcNow : null;
        request.DecisionNote = note.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);

        await NotifyRequesterAsync(request, reject
            ? "Deine Verpflegungsanforderung wurde abgelehnt."
            : "Die Genehmigung deiner Verpflegungsanforderung wurde zurückgenommen.", cancellationToken);
    }

    public async Task IssueAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Withdrawal);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.AsNoTracking()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");
        RequireTransition(request.Status, FoodRequestStatus.Issued);
        if (!string.IsNullOrEmpty(request.EvidenceEntryId))
        {
            throw new InvalidOperationException("Diese Anforderung wurde bereits ausgegeben.");
        }

        var lines = request.Lines
            .Where(l => EffectiveQuantity(l) > 0)
            .OrderBy(l => l.Sorting)
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("Für diese Anforderung ist nichts genehmigt.");
        }
        if (lines.Any(l => string.IsNullOrEmpty(l.EvidenceItemId)))
        {
            throw new InvalidOperationException("Eine Position ist keinem Gegenstand der Asservatenkammer mehr zugeordnet.");
        }

        var codename = await db.Users.AsNoTracking()
            .Where(a => a.Id == request.AgentId).Select(a => a.Codename)
            .FirstOrDefaultAsync(cancellationToken) ?? "(unbekannt)";
        var issuedAt = DateTime.UtcNow;
        var issuedBy = actor.GetCodename();

        // one transaction: the withdrawal and the issued request commit together or not at all
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var entry = await evidence.CreateEntryAsync(db, new EvidenceEntryInput
        {
            Type = EvidenceEntryType.Withdrawal,
            OwnerType = EvidenceService.NooseOwner,
            HandlerAgentId = actor.GetAgentId() ?? string.Empty,
            Timestamp = issuedAt,
            Notes = $"Verpflegung {request.CaseNumber} · {codename}",
            Lines = lines.Select(l => new EvidenceLineInput
            {
                ItemId = l.EvidenceItemId,
                ItemName = l.ItemName,
                Quantity = EffectiveQuantity(l),
            }).ToList(),
        }, actor, cancellationToken);

        // compare-and-swap: a parallel issue matches no row and rolls this transaction back, booking included
        var claimed = await db.FoodRequests
            .Where(r => r.Id == id && r.Status == FoodRequestStatus.Approved && r.EvidenceEntryId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.EvidenceEntryId, entry.Id)
                .SetProperty(r => r.Status, FoodRequestStatus.Issued)
                .SetProperty(r => r.IssuedAt, (DateTime?)issuedAt)
                .SetProperty(r => r.IssuedByName, issuedBy), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Diese Anforderung wurde soeben von jemand anderem ausgegeben.");
        }

        // ExecuteUpdate bypasses the audit interceptor
        db.AuditLogs.Add(ManualAudit.Row(nameof(FoodRequest), request.Id, AuditAction.Modified, actor,
            new Dictionary<string, object?[]>
            {
                ["Status"] = new object?[]
                {
                    FoodRequestStatusDisplay.Name(FoodRequestStatus.Approved),
                    FoodRequestStatusDisplay.Name(FoodRequestStatus.Issued),
                },
                ["Ausgegeben"] = new object?[] { null, $"{Summary(request.FoodApproved ?? 0, request.DrinkApproved ?? 0)} · {entry.CaseNumber}" },
            }));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await NotifyRequesterAsync(request, "Deine Verpflegung wurde ausgegeben.", cancellationToken);
    }

    public async Task CancelIssueAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");
        // Approved has three legal predecessors, so check the source explicitly
        if (request.Status != FoodRequestStatus.Issued)
        {
            throw new InvalidOperationException("Stornieren geht nur bei einer ausgegebenen Anforderung.");
        }
        RequireTransition(request.Status, FoodRequestStatus.Approved);

        var entryId = request.EvidenceEntryId;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!string.IsNullOrEmpty(entryId))
        {
            // Hard delete on purpose: a soft-deleted withdrawal could be restored from the evidence trash
            // while this request no longer points at it, and issuing again would take the stock out twice.
            var caseNo = await db.EvidenceEntries.IgnoreQueryFilters()
                .Where(e => e.Id == entryId).Select(e => e.CaseNumber)
                .FirstOrDefaultAsync(cancellationToken);
            await db.EvidenceEntryLines.Where(l => l.EntryId == entryId).ExecuteDeleteAsync(cancellationToken);
            var removed = await db.EvidenceEntries.IgnoreQueryFilters()
                .Where(e => e.Id == entryId)
                .ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
            {
                // ExecuteDelete bypasses the audit interceptor
                db.AuditLogs.Add(ManualAudit.Row(nameof(EvidenceEntry), entryId, AuditAction.Deleted, actor,
                    ManualAudit.Change("Storniert", $"Verpflegung {request.CaseNumber}", null)));
            }
            db.AuditLogs.Add(ManualAudit.Row(nameof(FoodRequest), request.Id, AuditAction.Modified, actor,
                ManualAudit.Change("Ausgabe storniert", caseNo, null)));
        }
        request.EvidenceEntryId = null;
        request.Status = FoodRequestStatus.Approved;
        request.IssuedAt = null;
        request.IssuedByName = null;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return;
        }
        var ownAndOpen = request.AgentId == actor.GetAgentId() && request.Status == FoodRequestStatus.Requested;
        if (!actor.IsLeadership() && !ownAndOpen)
        {
            throw new UnauthorizedAccessException(
                "Löschen darf die Führung oder der Anforderer, solange seine Anforderung noch offen ist.");
        }
        if (request.Status == FoodRequestStatus.Issued)
        {
            throw new InvalidOperationException(
                "Eine ausgegebene Anforderung kann nicht gelöscht werden — bitte zuerst die Ausgabe stornieren.");
        }
        // soft delete; a deleted approval frees its weekly limit
        db.FoodRequests.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FoodRequest>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FoodRequests.AsNoTracking()
            .IgnoreQueryFilters()
            .Include(r => r.Lines)
            .Where(r => r.IsDeleted)
            .OrderByDescending(r => r.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FoodRequests.IgnoreQueryFilters()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsanforderung '{id}' nicht gefunden.");
        request.IsDeleted = false;
        request.DeletedAt = null;
        request.DeletedById = null;

        // an approval charged to a past week would be issuable without any week accounting for it
        if (request.Status == FoodRequestStatus.Approved && request.LimitWeekStart != FoodWeek.Current())
        {
            ClearApproval(request, struckLines: false);
            request.Status = FoodRequestStatus.Requested;
            request.DeciderName = null;
            request.DecidedAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- state machine ----

    /// <summary>The only allowed stage changes; every write path checks against this table.</summary>
    public static bool IsTransitionAllowed(FoodRequestStatus current, FoodRequestStatus target) => (current, target) switch
    {
        (FoodRequestStatus.Requested, FoodRequestStatus.Approved) => true,
        (FoodRequestStatus.Requested, FoodRequestStatus.Rejected) => true,
        (FoodRequestStatus.Requested, FoodRequestStatus.Withdrawn) => true,
        // an approval can be issued, taken back, or turned into a rejection
        (FoodRequestStatus.Approved, FoodRequestStatus.Issued) => true,
        (FoodRequestStatus.Approved, FoodRequestStatus.Requested) => true,
        (FoodRequestStatus.Approved, FoodRequestStatus.Rejected) => true,
        // a rejection stays reversible; an issue can be cancelled
        (FoodRequestStatus.Rejected, FoodRequestStatus.Approved) => true,
        (FoodRequestStatus.Issued, FoodRequestStatus.Approved) => true,
        _ => false,
    };

    private static void RequireTransition(FoodRequestStatus current, FoodRequestStatus target)
    {
        if (!IsTransitionAllowed(current, target))
        {
            throw new InvalidOperationException(
                $"Wechsel von „{FoodRequestStatusDisplay.Name(current)}“ zu „{FoodRequestStatusDisplay.Name(target)}“ ist nicht möglich.");
        }
    }

    /// <summary>Releases the weekly limit; struck lines keep a zero so a rejection stays readable.</summary>
    internal static void ClearApproval(FoodRequest request, bool struckLines)
    {
        foreach (var line in request.Lines)
        {
            line.ApprovedQuantity = struckLines ? 0 : null;
        }
        request.FoodApproved = null;
        request.DrinkApproved = null;
        request.LimitWeekStart = null;
        request.OverrunFood = null;
        request.OverrunDrink = null;
        request.OverrunReason = null;
    }

    /// <summary>Approved quantity once decided, otherwise the requested one.</summary>
    public static int EffectiveQuantity(FoodRequestLine line) => line.ApprovedQuantity ?? line.Quantity;

    private static int Sum(IEnumerable<FoodRequestLine> lines, FoodKind kind, Func<FoodRequestLine, int> quantity)
        => lines.Where(l => l.Kind == kind).Sum(quantity);

    /// <summary>Short German summary, e.g. "3 Essen, 2 Trinken".</summary>
    public static string Summary(int food, int drink)
    {
        var parts = new List<string>(2);
        if (food > 0)
        {
            parts.Add($"{food} Essen");
        }
        if (drink > 0)
        {
            parts.Add($"{drink} Trinken");
        }
        return parts.Count == 0 ? "nichts" : string.Join(", ", parts);
    }

    private static string OverrunText(int food, int overrunFood, int drink, int overrunDrink)
    {
        var parts = new List<string>(2);
        if (food > 0 && overrunFood > 0)
        {
            parts.Add($"Essen um {overrunFood}");
        }
        if (drink > 0 && overrunDrink > 0)
        {
            parts.Add($"Trinken um {overrunDrink}");
        }
        return string.Join(", ", parts);
    }

    // ---- helpers ----

    private static async Task<List<FoodRequestDisplay>> ProjectAsync(AppDbContext db,
        IQueryable<FoodRequest> query, CancellationToken cancellationToken, int? max = null)
    {
        var ordered = query.Include(r => r.Lines).OrderByDescending(r => r.CreatedAt);
        var rows = max is { } take
            ? await ordered.Take(take).ToListAsync(cancellationToken)
            : await ordered.ToListAsync(cancellationToken);

        var agentIds = rows.Select(r => r.AgentId).Distinct().ToList();
        // flat lookup by id: Pomelo cannot translate the nav projection
        var codenames = agentIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.Users.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Codename, cancellationToken);

        foreach (var row in rows)
        {
            row.Lines = row.Lines.OrderBy(l => l.Sorting).ToList();
        }
        return rows
            .Select(r => new FoodRequestDisplay(r,
                r.AgentId is null ? "(unbekannt)" : codenames.GetValueOrDefault(r.AgentId, "(unbekannt)")))
            .ToList();
    }

    private async Task NotifyDecidersAsync(FoodRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // same recipients as the other leadership notifications
            var deciders = await db.Users.AsNoTracking().OnlySelectable()
                .Where(a => a.IsAdmin || a.Rank >= Rank.SectionChief)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyAsync(deciders, NotificationType.Food,
                $"Neue Verpflegungsanforderung von {actor.GetCodename() ?? "einem Agenten"}: {Summary(request.FoodRequested, request.DrinkRequested)}",
                $"/verpflegung/{request.Id}", actor.GetAgentId(), cancellationToken);
        }
        catch { /* best effort */ }
    }

    private async Task NotifyRequesterAsync(FoodRequest request, string title, CancellationToken cancellationToken)
    {
        try
        {
            await notifications.NotifyAsync(request.AgentId, NotificationType.Food, title,
                $"/verpflegung/{request.Id}", cancellationToken);
        }
        catch { /* best effort */ }
    }

    private static void RequireDecide(ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
    }
}
