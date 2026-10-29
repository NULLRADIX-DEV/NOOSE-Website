using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Models.Kasse;

namespace NOOSE_Website.Services;

/// <summary>Funding requests: a basket of catalog positions decided as one, paid as one Grüngeld withdrawal, charged to the requester's monthly budget.</summary>
public class FinancingService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    IFinancingBudgetService budgetService,
    IKassenService kasse,
    INotificationService notifications) : IFinancingService
{
    private const string CasePrefix = "FIN";

    /// <summary>Funding always leaves the legal account.</summary>
    private const KassenKonto PayoutAccount = KassenKonto.Gruengeld;

    // ---- reads ----

    public async Task<List<FinancingRequestDisplay>> GetOwnAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var me = actor.GetAgentId();
        if (string.IsNullOrEmpty(me))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAsync(db, db.FinancingRequests.AsNoTracking().Where(r => r.AgentId == me), cancellationToken);
    }

    public async Task<List<FinancingRequestDisplay>> GetVisibleAsync(FinancingStatus? status, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FinancingRequests.AsNoTracking()
            .OnlyVisible(actor.MayClassifiedRead(), actor.GetAgentId());
        if (status is { } wanted)
        {
            query = query.Where(r => r.Status == wanted);
        }
        return await ProjectAsync(db, query, cancellationToken);
    }

    public async Task<List<FinancingRequestDisplay>> GetForAgentAsync(string agentId, int max, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // own file or a reader of everything; anything else sees nothing
        if (!actor.MayClassifiedRead() && actor.GetAgentId() != agentId)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAsync(db,
            db.FinancingRequests.AsNoTracking().Where(r => r.AgentId == agentId),
            cancellationToken, Math.Clamp(max, 1, 200));
    }

    public async Task<int> GetOpenCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FinancingRequests.CountAsync(r => r.Status == FinancingStatus.Requested, cancellationToken);
    }

    public async Task<FinancingRequest?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.AsNoTracking()
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

    // ---- writes ----

    public async Task<FinancingRequest> CreateAsync(FinancingRequestInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var me = actor.GetAgentId()
            ?? throw new InvalidOperationException("Kein Agenten-Kontext für den Antrag.");
        var justification = input.Justification.TrimToNull()
            ?? throw new InvalidOperationException("Bitte eine Begründung angeben.");
        if (justification.Length > 2000)
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
                "Jede Position darf nur einmal im Antrag stehen — bitte stattdessen die Menge erhöhen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == me, cancellationToken)
            ?? throw new InvalidOperationException("Agent nicht gefunden.");
        // supervision is RP-wide invisible and must never surface in a request list; the admin flag would
        // otherwise let a team lead past MayWrite()
        if (agent.IsTeamLead)
        {
            throw new InvalidOperationException("Die Team-Leitung stellt keine Finanzierungsanträge.");
        }

        var wanted = input.Lines.Select(l => l.ItemId).ToList();
        var items = await db.FinancingItems.AsNoTracking()
            .Where(i => wanted.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var request = new FinancingRequest
        {
            AgentId = me,
            Status = FinancingStatus.Requested,
            Justification = justification,
        };

        var sorting = 0;
        foreach (var line in input.Lines)
        {
            if (!items.TryGetValue(line.ItemId, out var item))
            {
                throw new InvalidOperationException("Eine gewählte Position existiert nicht mehr.");
            }
            if (!item.IsActive)
            {
                throw new InvalidOperationException($"Die Position „{item.Name}“ ist derzeit nicht finanzierbar.");
            }
            if (agent.Rank is null || item.MinimumRank > agent.Rank)
            {
                throw new InvalidOperationException(
                    $"Die Position „{item.Name}“ ist erst ab {RankDisplay.Name(item.MinimumRank)} verfügbar.");
            }
            if (line.Quantity < 1)
            {
                throw new InvalidOperationException($"Bitte für „{item.Name}“ eine Menge von mindestens 1 angeben.");
            }
            if (line.Quantity > item.MaxQuantity)
            {
                throw new InvalidOperationException(
                    $"Von „{item.Name}“ sind höchstens {item.MaxQuantity} pro Antrag möglich.");
            }

            // snapshot: later catalog edits must never move a filed request
            request.Lines.Add(new FinancingRequestLine
            {
                ItemId = item.Id,
                ItemName = item.Name,
                Category = item.Category,
                UnitPrice = item.UnitPrice,
                SubsidyPercent = item.SubsidyPercent,
                Quantity = line.Quantity,
                Sorting = sorting++,
            });
        }

        request.RequestedGross = request.Lines.Sum(FinancingMath.RequestedGross);
        request.RequestedSubsidy = request.Lines.Sum(FinancingMath.RequestedSubsidy);

        // case-number allocation needs an enclosing transaction so counter + record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        request.CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken);
        db.FinancingRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await NotifyDecidersAsync(request, actor, cancellationToken);
        return request;
    }

    public async Task DecideAsync(string id, bool approved, FinancingDecisionInput decision, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");

        if (request.Status is not (FinancingStatus.Requested or FinancingStatus.Rejected))
        {
            throw new InvalidOperationException(request.Status == FinancingStatus.Approved
                ? "Über diesen Antrag wurde bereits entschieden — die Genehmigung lässt sich zurücknehmen."
                : $"Ein Antrag im Zustand „{FinancingStatusDisplay.Name(request.Status)}“ kann nicht entschieden werden.");
        }
        if (request.AgentId is null)
        {
            throw new InvalidOperationException(
                "Der Antragsteller wurde gelöscht — über diesen Antrag kann nicht mehr entschieden werden.");
        }
        var target = approved ? FinancingStatus.Approved : FinancingStatus.Rejected;
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

            var subsidy = FinancingMath.SubsidyTotal(request.Lines, FinancingMath.EffectiveQuantity);
            if (subsidy <= 0)
            {
                throw new InvalidOperationException(
                    "Es wurde keine Position genehmigt — dann bitte den Antrag ablehnen.");
            }

            // neither Requested nor Rejected reserves budget, so the remaining amount never counts this request
            var budget = await budgetService.GetStatusAsync(request.AgentId, cancellationToken);
            var overrun = subsidy - budget.Remaining;
            if (overrun > 0 && string.IsNullOrWhiteSpace(decision.OverrunReason))
            {
                throw new InvalidOperationException(
                    $"Die Genehmigung überschreitet das Restbudget um {Money.Format(overrun)}. " +
                    "Bitte eine Begründung für die Überschreitung angeben.");
            }

            var (year, month) = FinancingPeriod.Current();
            request.BudgetYear = year;
            request.BudgetMonth = month;
            request.ApprovedSubsidy = subsidy;
            request.OverrunAmount = overrun > 0 ? overrun : null;
            request.OverrunReason = overrun > 0 ? decision.OverrunReason.TrimToNull() : null;
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
            ? $"Dein Finanzierungsantrag wurde angenommen: {Money.Format(request.ApprovedSubsidy ?? 0m)}"
            : "Dein Finanzierungsantrag wurde abgelehnt.", cancellationToken);
    }

    public async Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");
        if (request.AgentId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Nur der Antragsteller kann seinen eigenen Antrag zurückziehen.");
        }
        RequireTransition(request.Status, FinancingStatus.Withdrawn);

        request.Status = FinancingStatus.Withdrawn;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeApprovalAsync(string id, bool reject, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");
        if (request.Status != FinancingStatus.Approved)
        {
            throw new InvalidOperationException(
                "Zurücknehmen geht nur bei einem angenommenen, noch nicht ausgezahlten Antrag.");
        }
        var target = reject ? FinancingStatus.Rejected : FinancingStatus.Requested;
        RequireTransition(request.Status, target);

        ClearApproval(request, struckLines: reject);
        request.Status = target;
        request.DeciderName = reject ? actor.GetCodename() : null;
        request.DecidedAt = reject ? DateTime.UtcNow : null;
        request.DecisionNote = note.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);

        await NotifyRequesterAsync(request, reject
            ? "Dein Finanzierungsantrag wurde abgelehnt."
            : "Die Genehmigung deines Finanzierungsantrags wurde zurückgenommen.", cancellationToken);
    }

    public async Task PayAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");
        RequireTransition(request.Status, FinancingStatus.Paid);
        if (!string.IsNullOrEmpty(request.KassenBuchungId))
        {
            throw new InvalidOperationException("Dieser Antrag wurde bereits ausgezahlt.");
        }
        var amount = request.ApprovedSubsidy ?? 0m;
        if (amount <= 0)
        {
            throw new InvalidOperationException("Für diesen Antrag ist kein Zuschuss genehmigt.");
        }

        var codename = await db.Users.AsNoTracking()
            .Where(a => a.Id == request.AgentId).Select(a => a.Codename)
            .FirstOrDefaultAsync(cancellationToken) ?? "(unbekannt)";
        var paidAt = DateTime.UtcNow;
        var paidBy = actor.GetCodename();

        // one transaction: the treasury booking and the paid request commit together or not at all
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var booking = await kasse.BookAsync(db, new KassenBuchungInput
        {
            Account = PayoutAccount,
            Kind = KassenBuchungArt.Auszahlung,
            Amount = amount,
            Reason = $"Finanzierung {request.CaseNumber} · {codename}",
            // the recipient, so the treasury ledger can be filtered by who the money went to
            BookedById = request.AgentId,
            // never backdated: the non-negative guard checks every point of the ledger
            Timestamp = paidAt,
        }, actor, cancellationToken);

        // Compare-and-swap, not a tracked update: two deciders paying at the same moment would otherwise
        // book two different withdrawals (distinct ids, so the unique index cannot catch it) and the last
        // writer would win, leaving one orphaned booking and the money out twice. The loser matches no row
        // and throws, which rolls this transaction back — booking included.
        var claimed = await db.FinancingRequests
            .Where(r => r.Id == id && r.Status == FinancingStatus.Approved && r.KassenBuchungId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.KassenBuchungId, booking.Id)
                .SetProperty(r => r.Status, FinancingStatus.Paid)
                .SetProperty(r => r.PaidAt, (DateTime?)paidAt)
                .SetProperty(r => r.PaidByName, paidBy), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Dieser Antrag wurde soeben von jemand anderem ausgezahlt.");
        }

        // ExecuteUpdate bypasses the audit interceptor, so record the stage change and the money by hand
        db.AuditLogs.Add(ManualAudit.Row(nameof(FinancingRequest), request.Id, AuditAction.Modified, actor,
            new Dictionary<string, object?[]>
            {
                ["Status"] = new object?[]
                {
                    FinancingStatusDisplay.Name(FinancingStatus.Approved),
                    FinancingStatusDisplay.Name(FinancingStatus.Paid),
                },
                ["Ausgezahlt"] = new object?[] { null, Money.Format(amount) },
            }));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task CancelPaymentAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireDecide(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");
        // the target state has three legal predecessors, so the transition table alone would also let a
        // Requested or Rejected request through and leave it "approved" without an approved amount
        if (request.Status != FinancingStatus.Paid)
        {
            throw new InvalidOperationException("Stornieren geht nur bei einem ausgezahlten Antrag.");
        }
        RequireTransition(request.Status, FinancingStatus.Approved);

        var amount = request.ApprovedSubsidy ?? 0m;
        var bookingId = request.KassenBuchungId;

        // same transaction as the request update, so the balance and the record can never disagree.
        // Same permission gate as IKassenService.DeleteAsync, already satisfied by RequireDecide.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!string.IsNullOrEmpty(bookingId))
        {
            // Hard delete on purpose: a soft-deleted payout would sit in the treasury trash and could be
            // restored there, putting the withdrawal back live while this request no longer points at it —
            // paying again would then debit Grüngeld twice. The reversal stays readable in the audit rows
            // below and on the request's timeline.
            var removed = await db.KassenBuchungen
                .Where(b => b.Id == bookingId)
                .ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
            {
                // ExecuteDelete bypasses the audit interceptor
                db.AuditLogs.Add(ManualAudit.Row(nameof(KassenBuchung), bookingId, AuditAction.Deleted, actor,
                    ManualAudit.Change("Storniert", Money.Format(amount), null)));
            }
        }
        request.KassenBuchungId = null;
        request.Status = FinancingStatus.Approved;
        request.PaidAt = null;
        request.PaidByName = null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(FinancingRequest), request.Id, AuditAction.Modified, actor,
            ManualAudit.Change("Auszahlung storniert", Money.Format(amount), null)));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.FinancingRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return;
        }
        var ownAndOpen = request.AgentId == actor.GetAgentId() && request.Status == FinancingStatus.Requested;
        if (!actor.IsLeadership() && !ownAndOpen)
        {
            throw new UnauthorizedAccessException(
                "Löschen darf die Führung oder der Antragsteller, solange sein Antrag noch offen ist.");
        }
        if (request.Status == FinancingStatus.Paid)
        {
            throw new InvalidOperationException(
                "Ein ausgezahlter Antrag kann nicht gelöscht werden — bitte zuerst die Auszahlung stornieren.");
        }
        // Interceptor rewrites Remove to soft-delete; a deleted approval frees its reserved budget
        db.FinancingRequests.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FinancingRequest>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FinancingRequests.AsNoTracking()
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
        var request = await db.FinancingRequests.IgnoreQueryFilters()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsantrag '{id}' nicht gefunden.");
        request.IsDeleted = false;
        request.DeletedAt = null;
        request.DeletedById = null;

        // While it sat in the trash its budget month may have been closed with this reservation excluded.
        // A frozen period must not be recomputed, so an approval charged to a past month would be payable
        // without any month accounting for it — reopen it for a fresh decision instead.
        var (year, month) = FinancingPeriod.Current();
        if (request.Status == FinancingStatus.Approved
            && (request.BudgetYear != year || request.BudgetMonth != month))
        {
            ClearApproval(request, struckLines: false);
            request.Status = FinancingStatus.Requested;
            request.DeciderName = null;
            request.DecidedAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- state machine ----

    /// <summary>The only allowed stage changes; every write path checks against this table.</summary>
    public static bool IsTransitionAllowed(FinancingStatus current, FinancingStatus target) => (current, target) switch
    {
        (FinancingStatus.Requested, FinancingStatus.Approved) => true,
        (FinancingStatus.Requested, FinancingStatus.Rejected) => true,
        (FinancingStatus.Requested, FinancingStatus.Withdrawn) => true,
        // an approval can be paid out, taken back, or turned into a rejection
        (FinancingStatus.Approved, FinancingStatus.Paid) => true,
        (FinancingStatus.Approved, FinancingStatus.Requested) => true,
        (FinancingStatus.Approved, FinancingStatus.Rejected) => true,
        // a rejection stays reversible; a payout can be cancelled
        (FinancingStatus.Rejected, FinancingStatus.Approved) => true,
        (FinancingStatus.Paid, FinancingStatus.Approved) => true,
        _ => false,
    };

    private static void RequireTransition(FinancingStatus current, FinancingStatus target)
    {
        if (!IsTransitionAllowed(current, target))
        {
            throw new InvalidOperationException(
                $"Wechsel von „{FinancingStatusDisplay.Name(current)}“ zu „{FinancingStatusDisplay.Name(target)}“ ist nicht möglich.");
        }
    }

    /// <summary>Releases the reserved budget; struck lines keep a zero so a rejection stays readable.</summary>
    internal static void ClearApproval(FinancingRequest request, bool struckLines)
    {
        foreach (var line in request.Lines)
        {
            line.ApprovedQuantity = struckLines ? 0 : null;
        }
        request.ApprovedSubsidy = null;
        request.BudgetYear = null;
        request.BudgetMonth = null;
        request.OverrunAmount = null;
        request.OverrunReason = null;
    }

    // ---- helpers ----

    private static async Task<List<FinancingRequestDisplay>> ProjectAsync(AppDbContext db,
        IQueryable<FinancingRequest> query, CancellationToken cancellationToken, int? max = null)
    {
        var ordered = query.Include(r => r.Lines).OrderByDescending(r => r.CreatedAt);
        var rows = max is { } take
            ? await ordered.Take(take).ToListAsync(cancellationToken)
            : await ordered.ToListAsync(cancellationToken);

        var agentIds = rows.Select(r => r.AgentId).Distinct().ToList();
        // flat lookup by id, not a nav-property projection: Pomelo cannot translate the latter on MySQL
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
            .Select(r => new FinancingRequestDisplay(r,
                r.AgentId is null ? "(unbekannt)" : codenames.GetValueOrDefault(r.AgentId, "(unbekannt)")))
            .ToList();
    }

    private async Task NotifyDecidersAsync(FinancingRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // same recipient shape as the other leadership notifications: never team leads, never partners
            var deciders = await db.Users.AsNoTracking().OnlySelectable()
                .Where(a => a.IsAdmin || a.Rank >= Rank.SupervisorySpecialAgent)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyAsync(deciders, NotificationType.Financing,
                $"Neuer Finanzierungsantrag von {actor.GetCodename() ?? "einem Agenten"}: {Money.Format(request.RequestedSubsidy)}",
                $"/finanzierungen/{request.Id}", actor.GetAgentId(), cancellationToken);
        }
        catch { /* best effort */ }
    }

    private async Task NotifyRequesterAsync(FinancingRequest request, string title, CancellationToken cancellationToken)
    {
        try
        {
            await notifications.NotifyAsync(request.AgentId, NotificationType.Financing, title,
                $"/finanzierungen/{request.Id}", cancellationToken);
        }
        catch { /* best effort */ }
    }

    private static void RequireDecide(ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
    }
}
