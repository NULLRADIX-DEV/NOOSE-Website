using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IRewardService" />
public class RewardService(
    IDbContextFactory<AppDbContext> dbFactory,
    IKassenService kasse,
    ICaseNumberService caseNumbers,
    ITipService tips,
    IBuergerService buerger,
    IPublicWantedService wanted,
    ITipPriorityService tipPriority,
    IPublicModuleService modules) : IRewardService
{
    private const string ReceiptPrefix = "BEL";
    private const int ExcerptLength = 160;

    private const string NotFound = "Ausschreibung nicht gefunden.";
    private const string NotCaptured = "Erst die Ausschreibung auf gefasst setzen, dann die Belohnung auszahlen.";
    private const string AlreadyPaidText = "Die Belohnung dieser Ausschreibung ist bereits ausgezahlt.";
    private const string TopUpNeedsAccount = "Die Belohnung übersteigt das ausgesetzte Kopfgeld — dafür ein Konto "
        + "wählen, aus dem die Aufstockung bezahlt wird.";

    // ---- reads ----

    public async Task<RewardDraft> GetDraftAsync(string wantedId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireRewardPayout(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var notice = await BountyService.VisibleNoticeAsync(db, wantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        var shares = await CapacitiesAsync(db, notice.Id, cancellationToken);
        var paid = await AlreadyPaidAsync(db, notice.Id, cancellationToken);

        var rows = await db.Hinweise.AsNoTracking()
            .Where(h => h.WantedId == notice.Id)
            .OrderByDescending(h => h.Priority)
            .ThenByDescending(h => h.CreatedAt)
            .Select(h => new
            {
                h.Id,
                h.CaseNumber,
                h.Status,
                h.CreatedAt,
                h.Text,
                h.WantsAnonymity,
                h.AnonymityResolvedAt,
                FirstName = h.CitizenProfile!.FirstName,
                LastName = h.CitizenProfile!.LastName,
                h.CitizenProfile!.ConfirmedTips,
            })
            .ToListAsync(cancellationToken);

        // the receipt is what says a tip was paid, not its status: the capture status was once settable by hand, and
        // reading it as "already rewarded" locked a whole bounty away with no money ever having moved
        var tipIds = rows.Select(r => r.Id).ToList();
        var rewarded = tipIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.HinweisBelohnungen.AsNoTracking()
                .Where(b => tipIds.Contains(b.TipId))
                .Select(b => b.TipId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        var payable = new List<RewardDraftTip>();
        var blocked = new List<RewardDraftBlocked>();
        foreach (var row in rows)
        {
            if (TipAnonymity.IsHidden(row.WantsAnonymity, row.AnonymityResolvedAt))
            {
                // money needs a payee and the receipt names them; only leadership may lift the promise
                blocked.Add(new RewardDraftBlocked(row.Id, row.CaseNumber, "Anonymität nicht aufgelöst"));
                continue;
            }
            if (rewarded.Contains(row.Id))
            {
                blocked.Add(new RewardDraftBlocked(row.Id, row.CaseNumber, "Bereits belohnt"));
                continue;
            }
            if (!TipRules.MayBeRewarded(row.Status))
            {
                blocked.Add(new RewardDraftBlocked(row.Id, row.CaseNumber,
                    $"Status {TipStatusDisplay.Name(row.Status)} lässt sich nicht auf belohnt setzen"));
                continue;
            }
            payable.Add(new RewardDraftTip(row.Id, row.CaseNumber, Name(row.FirstName, row.LastName),
                TipTrust.Tier(row.ConfirmedTips), row.CreatedAt, Excerpt(row.Text)));
        }

        return new RewardDraft(
            notice.Id,
            notice.CaseNumber,
            notice.DisplayName,
            notice.Status == PublicWantedStatus.Gefasst,
            shares.Sum(s => s.Amount),
            shares.Where(s => RewardAllocation.NeedsBooking(s.Origin, s.Status)).Sum(s => s.Amount),
            shares.Where(s => !RewardAllocation.NeedsBooking(s.Origin, s.Status)).Sum(s => s.Amount),
            payable,
            blocked,
            paid);
    }

    public async Task<IReadOnlyList<RewardRow>> GetForNoticeAsync(string wantedId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await BountyService.VisibleNoticeAsync(db, wantedId, actor, cancellationToken) is null)
        {
            return [];
        }

        var shareIds = await db.FahndungKopfgeldAnteile.AsNoTracking()
            .Where(k => k.WantedId == wantedId)
            .Select(k => k.Id)
            .ToListAsync(cancellationToken);
        return shareIds.Count == 0
            ? []
            : await RowsAsync(db, b => shareIds.Contains(b.ShareId), cancellationToken);
    }

    public async Task<IReadOnlyList<RewardRow>> GetForTipAsync(string tipId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireTipRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RowsAsync(db, b => b.TipId == tipId, cancellationToken);
    }

    public async Task<IReadOnlyList<CitizenRewardRow>> GetOwnAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await RequireRewardViewAsync(cancellationToken);
        var profile = await buerger.GetOwnAsync(actor, cancellationToken);
        if (profile is null)
        {
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // rooted over the soft-delete filter: Tip is a REQUIRED navigation, so EF joins it INNER and a deleted
        // tip would take the citizen's paid receipt with it. HinweisBelohnung is deliberately not ISoftDelete,
        // so there is no !IsDeleted to write back - the widening only restores the uncut join.
        var rows = await db.HinweisBelohnungen.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Tip!.CitizenProfileId == profile.Id)
            .Select(b => new { b.ReceiptNumber, TipCaseNumber = b.Tip!.CaseNumber, b.Amount, b.PaidAt })
            .ToListAsync(cancellationToken);

        // one receipt per tip and payout, so its rows are summed rather than listed
        return rows
            .GroupBy(b => b.ReceiptNumber, StringComparer.Ordinal)
            .Select(g => new CitizenRewardRow(g.Key, g.First().TipCaseNumber, g.Sum(b => b.Amount),
                g.Max(b => b.PaidAt)))
            .OrderByDescending(r => r.PaidAt)
            .ToList();
    }

    public async Task<CitizenRewardReceipt?> GetReceiptAsync(string receiptNumber, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await RequireRewardViewAsync(cancellationToken);
        Permission.RequireCitizenPortal(actor);
        receiptNumber = CaseNumbers.Normalize(receiptNumber);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // rooted for the same reason as GetOwnAsync: the receipt must survive the deletion of its tip
        var rows = await db.HinweisBelohnungen.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.ReceiptNumber == receiptNumber)
            .Select(b => new
            {
                b.Amount,
                b.PaidAt,
                TipCaseNumber = b.Tip!.CaseNumber,
                WantedCaseNumber = b.Tip!.Wanted!.CaseNumber,
                CitizenUserId = b.Tip!.CitizenProfile!.UserId,
                FirstName = b.Tip!.CitizenProfile!.FirstName,
                LastName = b.Tip!.CitizenProfile!.LastName,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        // owner or leadership; anybody else gets the same "not found", or the route is an existence oracle for payouts
        var first = rows[0];
        // a null CitizenUserId means the account behind the tip was deleted: nobody owns the receipt any more,
        // and comparing two nulls would hand it to an anonymous caller
        if (!actor.IsLeadership()
            && (first.CitizenUserId is null || first.CitizenUserId != actor.GetAgentId()))
        {
            return null;
        }

        return new CitizenRewardReceipt(receiptNumber, first.TipCaseNumber, first.WantedCaseNumber,
            Name(first.FirstName, first.LastName), rows.Sum(r => r.Amount), rows.Max(r => r.PaidAt));
    }

    // ---- write ----

    public async Task<IReadOnlyList<string>> PayoutAsync(RewardPayoutInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireRewardPayout(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var notice = await BountyService.VisibleNoticeAsync(db, input.WantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        if (notice.Status != PublicWantedStatus.Gefasst)
        {
            throw new InvalidOperationException(NotCaptured);
        }
        if (await AlreadyPaidAsync(db, notice.Id, cancellationToken))
        {
            throw new InvalidOperationException(AlreadyPaidText);
        }

        var now = DateTime.UtcNow;
        var shares = await CapacitiesAsync(db, notice.Id, cancellationToken);
        // ordered at the boundary: RewardAllocation promises that the same payout always produces the same
        // bookings, and the order the operator happened to touch the amount fields in is not part of the payout
        var demands = (input.Tips ?? [])
            .Select(t => new RewardAllocation.TipDemand(t.TipId, t.Amount))
            .OrderBy(t => t.TipId, StringComparer.Ordinal)
            .ToList();

        // The paid figure is the operator's, not the bounty's. What the advertised shares do not cover becomes agency
        // money on the spot, born Ausgezahlt: it never enters BountyShares.Advertised, so neither the public sum nor
        // the coverage warning sees it, and it needs no migration. It also re-arms the idempotency token — without a
        // share of its own, a notice that never carried a bounty would have nothing AlreadyPaidAsync could find.
        var capacities = new List<RewardAllocation.ShareCapacity>(shares);
        var missing = demands.Sum(t => t.Amount) - shares.Sum(s => s.Amount);
        FahndungKopfgeldAnteil? topUp = null;
        if (missing > 0m)
        {
            var account = input.TopUpAccount ?? throw new InvalidOperationException(TopUpNeedsAccount);
            topUp = new FahndungKopfgeldAnteil
            {
                WantedId = notice.Id,
                Origin = BountyOrigin.NooseKasse,
                Amount = missing,
                Account = account,
                DonorAgentId = actor.GetAgentId(),
                Status = BountyShareStatus.Ausgezahlt,
                Timestamp = now,
            };
            capacities.Add(new RewardAllocation.ShareCapacity(topUp.Id, missing, topUp.Origin, topUp.Status,
                account, now, IsTopUp: true));
        }

        // the split rules, the sum invariant included, live in one place
        var slices = RewardAllocation.Distribute(capacities, demands);

        var tipIds = demands.Select(t => t.TipId).ToList();
        var payees = await db.Hinweise.AsNoTracking()
            .Where(h => tipIds.Contains(h.Id))
            .Select(h => new { h.Id, h.CaseNumber, h.WantedId, h.Status, h.WantsAnonymity, h.AnonymityResolvedAt })
            .ToListAsync(cancellationToken);
        foreach (var tipId in tipIds)
        {
            var payee = payees.FirstOrDefault(p => p.Id == tipId)
                ?? throw new InvalidOperationException("Hinweis nicht gefunden.");
            // the notice clause is the point: otherwise one case pays out to another case's tipster
            if (payee.WantedId != notice.Id)
            {
                throw new InvalidOperationException(
                    $"Hinweis {payee.CaseNumber} gehört nicht zu dieser Ausschreibung.");
            }
            if (TipAnonymity.IsHidden(payee.WantsAnonymity, payee.AnonymityResolvedAt))
            {
                throw new InvalidOperationException(
                    $"Hinweis {payee.CaseNumber}: die Anonymität muss vor einer Auszahlung aufgelöst sein.");
            }
            // paying twice is refused by AlreadyPaidAsync above, which settles a notice as a whole
            if (!TipRules.MayBeRewarded(payee.Status))
            {
                throw new InvalidOperationException($"Hinweis {payee.CaseNumber} steht auf "
                    + $"{TipStatusDisplay.Name(payee.Status)} und lässt sich nicht belohnen.");
            }
        }

        var byShare = capacities.ToDictionary(s => s.ShareId, StringComparer.Ordinal);
        var caseNumberOf = payees.ToDictionary(p => p.Id, p => p.CaseNumber, StringComparer.Ordinal);

        // one transaction: the case-number counter demands one, and the money, the shares and the tips commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Compare-and-swap before anything is tracked, exactly like PayInAsync: two tabs would otherwise pay the same
        // bounty out twice. Every advertised share is settled, drawn on to the last dollar or not — Ausgezahlt means
        // done, not drained, or the coverage warning keeps counting a closed case as an open obligation.
        var claimed = await db.FahndungKopfgeldAnteile
            .Where(k => k.WantedId == notice.Id)
            .Where(BountyShares.Advertised)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, BountyShareStatus.Ausgezahlt), cancellationToken);
        if (claimed != shares.Count)
        {
            throw new InvalidOperationException("Das Kopfgeld dieser Ausschreibung wurde soeben verändert.");
        }
        foreach (var share in shares)
        {
            // ExecuteUpdate bypasses the audit interceptor, so the money is recorded by hand
            db.AuditLogs.Add(ManualAudit.Row(nameof(FahndungKopfgeldAnteil), share.ShareId, AuditAction.Modified,
                actor, ManualAudit.Change("Status", BountyShareStatusDisplay.Name(share.Status),
                    BountyShareStatusDisplay.Name(BountyShareStatus.Ausgezahlt))));
        }
        if (topUp is not null)
        {
            // tracked, so the audit interceptor stamps it; the CAS above deliberately ran before it existed
            db.FahndungKopfgeldAnteile.Add(topUp);
        }

        var receipts = new List<string>(tipIds.Count);
        var targets = new List<TipRewardTarget>(tipIds.Count);
        foreach (var tipId in tipIds)
        {
            var receipt = await caseNumbers.NextAsync(db, ReceiptPrefix, cancellationToken);
            receipts.Add(receipt);

            foreach (var slice in slices.Where(s => s.TipId == tipId))
            {
                var share = byShare[slice.ShareId];
                string? bookingId = null;
                DateTime? selfPaid = null;
                if (RewardAllocation.NeedsBooking(share.Origin, share.Status))
                {
                    var booking = await kasse.BookAsync(db, new KassenBuchungInput
                    {
                        Account = share.Account
                            ?? throw new InvalidOperationException(
                                "Ein Kopfgeld-Anteil ohne Konto lässt sich nicht auszahlen."),
                        Kind = KassenBuchungArt.Auszahlung,
                        Amount = slice.Amount,
                        // case numbers only: every agent reads the cash book, and a citizen name in it would be the
                        // anonymity promise circumvented through the ledger
                        Reason = $"Belohnung {caseNumberOf[tipId]} · Fahndung {notice.CaseNumber ?? "(Entwurf)"}",
                        Timestamp = now,
                    }, actor, cancellationToken);
                    bookingId = booking.Id;
                }
                else
                {
                    // a pledged private share never reached the till; the donor hands his own money over
                    selfPaid = now;
                }

                db.HinweisBelohnungen.Add(new HinweisBelohnung
                {
                    ReceiptNumber = receipt,
                    TipId = tipId,
                    ShareId = slice.ShareId,
                    Amount = slice.Amount,
                    KassenBuchungId = bookingId,
                    SelfPaidAt = selfPaid,
                    PaidAt = now,
                });
            }

            var amount = slices.Where(s => s.TipId == tipId).Sum(s => s.Amount);
            targets.Add(await tips.MarkRewardedAsync(db, tipId, amount, receipt, actor, cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        // after the commit, never before: the advertised sum is 0 now, and a notification cannot be taken back
        await wanted.InvalidatePublicViewAsync(cancellationToken);
        await tipPriority.StampForNoticeAsync(notice.Id, cancellationToken);
        await tips.AfterRewardAsync(targets, cancellationToken);
        return receipts;
    }

    // ---- internals ----

    /// <summary>The operator's reward switch, and deliberately not the kill switch.</summary>
    /// <remarks>
    /// Gates what the citizen sees, never the payout: an internal money movement must not hang on a public switch, or
    /// the emergency shutdown would block the cash book. And it asks <c>PublicModuleState.IsEnabled</c> — the stored
    /// choice alone — rather than <c>RequireEnabledAsync</c>, which folds the kill switch in: that switch takes public
    /// content offline and leaves /buerger open, and a receipt is the private account content of one signed-in citizen.
    /// </remarks>
    private async Task RequireRewardViewAsync(CancellationToken cancellationToken)
    {
        var snapshot = await modules.GetAsync(cancellationToken);
        var state = snapshot.Find(PublicModules.Reward);
        if (state?.IsEnabled != true)
        {
            throw new InvalidOperationException(
                $"Das Modul „{state?.Label ?? PublicModules.Reward}“ ist derzeit abgeschaltet.");
        }
    }

    private static async Task<List<RewardAllocation.ShareCapacity>> CapacitiesAsync(AppDbContext db, string wantedId,
        CancellationToken cancellationToken)
        => await db.FahndungKopfgeldAnteile.AsNoTracking()
            .Where(k => k.WantedId == wantedId)
            .Where(BountyShares.Advertised)
            .Select(k => new RewardAllocation.ShareCapacity(k.Id, k.Amount, k.Origin, k.Status, k.Account, k.Timestamp))
            .ToListAsync(cancellationToken);

    /// <summary>Whether this notice was settled already.</summary>
    /// <remarks>
    /// Two queries rather than a nested Any over the notice set: IgnoreQueryFilters applies to a whole compilation
    /// rather than to the operand it is written on, and staying flat keeps that trap out of reach.
    /// </remarks>
    private static async Task<bool> AlreadyPaidAsync(AppDbContext db, string wantedId,
        CancellationToken cancellationToken)
    {
        var shareIds = await db.FahndungKopfgeldAnteile.AsNoTracking()
            .Where(k => k.WantedId == wantedId)
            .Select(k => k.Id)
            .ToListAsync(cancellationToken);
        return shareIds.Count != 0
            && await db.HinweisBelohnungen.AsNoTracking()
                .AnyAsync(b => shareIds.Contains(b.ShareId), cancellationToken);
    }

    private static async Task<IReadOnlyList<RewardRow>> RowsAsync(AppDbContext db,
        System.Linq.Expressions.Expression<Func<HinweisBelohnung, bool>> filter, CancellationToken cancellationToken)
    {
        // rooted for the same reason as the citizen reads: the payout row outlives its tip
        var rows = await db.HinweisBelohnungen.IgnoreQueryFilters().AsNoTracking()
            .Where(filter)
            .OrderByDescending(b => b.PaidAt)
            .Select(b => new
            {
                b.ReceiptNumber,
                b.TipId,
                TipCaseNumber = b.Tip!.CaseNumber,
                b.Tip!.WantsAnonymity,
                b.Tip!.AnonymityResolvedAt,
                FirstName = b.Tip!.CitizenProfile!.FirstName,
                LastName = b.Tip!.CitizenProfile!.LastName,
                b.Amount,
                Origin = b.Share!.Origin,
                Account = b.Share!.Account,
                b.KassenBuchungId,
                b.SelfPaidAt,
                b.PaidAt,
            })
            .ToListAsync(cancellationToken);

        var bookingIds = rows.Where(r => r.KassenBuchungId != null).Select(r => r.KassenBuchungId!).ToList();
        var bookings = bookingIds.Count == 0
            ? []
            : await db.KassenBuchungen.AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.CaseNumber, cancellationToken);

        return rows
            .Select(r => new RewardRow(r.ReceiptNumber, r.TipId, r.TipCaseNumber,
                TipAnonymity.IsHidden(r.WantsAnonymity, r.AnonymityResolvedAt)
                    ? null
                    : Name(r.FirstName, r.LastName),
                r.Amount, r.Origin, r.Account,
                r.KassenBuchungId is { } id ? bookings.GetValueOrDefault(id) : null,
                r.SelfPaidAt is not null, r.PaidAt))
            .ToList();
    }

    private static string Name(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return name.Length == 0 ? "(unbekannt)" : name;
    }

    private static string Excerpt(string text)
        => text.Length <= ExcerptLength ? text : text[..ExcerptLength] + "…";
}
