using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;

namespace NOOSE_Website.Services;

/// <summary>NOOSE treasury: two cash accounts with a deposit/withdrawal/correction ledger; the balance is folded from live bookings, never stored.</summary>
public class KassenService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber) : IKassenService
{
    private const string CasePrefix = "KAS";

    // ---- reads ----

    public async Task<decimal> GetBalanceAsync(KassenKonto account, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ComputeBalanceAsync(db, account, null, cancellationToken);
    }

    public async Task<decimal> GetBalanceExcludingAsync(KassenKonto account, string excludeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ComputeBalanceAsync(db, account, excludeId, cancellationToken);
    }

    public async Task<IReadOnlyList<KassenKontoSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var summaries = new List<KassenKontoSummary>(KassenKontoDisplay.All.Count);
        foreach (var account in KassenKontoDisplay.All)
        {
            var rows = await OrderedAsync(db, account, cancellationToken);
            var balance = Fold(rows.Select(r => (r.Kind, r.Amount)));
            var last = rows.Count == 0 ? (DateTime?)null : rows[^1].Timestamp;
            summaries.Add(new KassenKontoSummary(account, balance, rows.Count, last));
        }
        return summaries;
    }

    public async Task<IReadOnlyList<KassenBuchungDisplay>> GetLedgerAsync(KassenKonto account, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.KassenBuchungen
            .Include(b => b.BookedBy)
            .Where(b => b.Account == account)
            .OrderBy(b => b.Timestamp).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

        // Only a paid funding request points at a booking, so the whole set is small — cheaper than an
        // IN clause over every booking id, and a paid request can never be soft-deleted away from here.
        var financing = await db.FinancingRequests.AsNoTracking()
            .Where(r => r.KassenBuchungId != null)
            .Select(r => new { BookingId = r.KassenBuchungId!, r.Id, r.CaseNumber })
            .ToListAsync(cancellationToken);
        var byBooking = financing.ToDictionary(f => f.BookingId);

        var list = new List<KassenBuchungDisplay>(rows.Count);
        decimal running = 0m;
        foreach (var b in rows)
        {
            var before = running;
            running = Apply(running, b.Kind, b.Amount);
            var codename = b.BookedBy?.Codename ?? "(unbekannt)";
            byBooking.TryGetValue(b.Id, out var link);
            list.Add(new KassenBuchungDisplay(b, codename, running - before, running, link?.Id, link?.CaseNumber));
        }
        list.Reverse(); // newest first for display
        return list;
    }

    public async Task<KassenBuchung?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.KassenBuchungen.Include(b => b.BookedBy).FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<KassenBuchungDisplay?> GetDisplayAsync(string id, CancellationToken cancellationToken = default)
    {
        var booking = await GetAsync(id, cancellationToken);
        if (booking is null)
        {
            return null;
        }
        var ledger = await GetLedgerAsync(booking.Account, cancellationToken);
        return ledger.FirstOrDefault(d => d.Buchung.Id == id);
    }

    // ---- writes ----

    public async Task<KassenBuchung> BookAsync(KassenBuchungInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // case-number allocation needs an enclosing transaction so counter + record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var booking = await BookAsync(db, input, actor, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return booking;
    }

    public async Task<KassenBuchung> BookAsync(AppDbContext db, KassenBuchungInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireKassenBookingWrite(actor, input.Kind);
        Validate(input);
        await EnsureNonNegativeAsync(db, input, null, cancellationToken);

        var booking = new KassenBuchung
        {
            CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken),
            Account = input.Account,
            Kind = input.Kind,
            Amount = input.Amount,
            Reason = input.Reason.TrimToNull(),
            BookedById = string.IsNullOrWhiteSpace(input.BookedById) || !MayBookForOthers(actor, input.Kind)
                ? actor.GetAgentId() : input.BookedById,
            Timestamp = input.Timestamp,
        };
        db.KassenBuchungen.Add(booking);
        await db.SaveChangesAsync(cancellationToken);
        return booking;
    }

    public async Task UpdateAsync(string id, KassenBuchungInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var booking = await db.KassenBuchungen.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Kassenbuchung '{id}' nicht gefunden.");

        // resulting balance is computed without this booking's own (old) row
        await EnsureNonNegativeAsync(db, input, id, cancellationToken);

        booking.Account = input.Account;
        booking.Kind = input.Kind;
        booking.Amount = input.Amount;
        booking.Reason = input.Reason.TrimToNull();
        if (!string.IsNullOrWhiteSpace(input.BookedById))
        {
            booking.BookedById = input.BookedById;
        }
        booking.Timestamp = input.Timestamp;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var booking = await db.KassenBuchungen.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (booking is null)
        {
            return;
        }
        // Interceptor rewrites Remove to soft-delete; downstream balances recompute on read.
        db.KassenBuchungen.Remove(booking);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<KassenBuchung>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.KassenBuchungen.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(b => b.IsDeleted)
            .OrderByDescending(b => b.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var booking = await db.KassenBuchungen.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Kassenbuchung '{id}' nicht gefunden.");
        booking.IsDeleted = false;
        booking.DeletedAt = null;
        booking.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- balance engine ----

    /// <summary>Chronological fold of live bookings: deposit adds, withdrawal subtracts, correction sets the absolute balance.</summary>
    private static decimal Apply(decimal running, KassenBuchungArt kind, decimal amount) => kind switch
    {
        KassenBuchungArt.Einzahlung => running + amount,
        KassenBuchungArt.Auszahlung => running - amount,
        KassenBuchungArt.Korrektur => amount,
        _ => running,
    };

    private static decimal Fold(IEnumerable<(KassenBuchungArt Kind, decimal Amount)> ordered)
    {
        decimal running = 0m;
        foreach (var (kind, amount) in ordered)
        {
            running = Apply(running, kind, amount);
        }
        return running;
    }

    private static async Task<List<KassenBuchung>> OrderedAsync(AppDbContext db, KassenKonto account, CancellationToken cancellationToken)
        => await db.KassenBuchungen.AsNoTracking()
            .Where(b => b.Account == account)
            .OrderBy(b => b.Timestamp).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Current balance of an account; excludeId leaves one booking out (for edit-time checks).</summary>
    private static async Task<decimal> ComputeBalanceAsync(AppDbContext db, KassenKonto account, string? excludeId, CancellationToken cancellationToken)
    {
        var rows = await db.KassenBuchungen.AsNoTracking()
            .Where(b => b.Account == account && (excludeId == null || b.Id != excludeId))
            .OrderBy(b => b.Timestamp).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => new { b.Kind, b.Amount })
            .ToListAsync(cancellationToken);
        return Fold(rows.Select(r => (r.Kind, r.Amount)));
    }

    /// <summary>Splices the candidate booking into the account's ledger at its chronological position and rejects it if the running balance would go below zero at ANY point (a correction resets the total, so checking only the end is not enough).</summary>
    private static async Task EnsureNonNegativeAsync(AppDbContext db, KassenBuchungInput input, string? excludeId, CancellationToken cancellationToken)
    {
        var rows = await db.KassenBuchungen.AsNoTracking()
            .Where(b => b.Account == input.Account && (excludeId == null || b.Id != excludeId))
            .Select(b => new { b.Timestamp, b.CreatedAt, b.Id, b.Kind, b.Amount })
            .ToListAsync(cancellationToken);

        var seq = rows.Select(r => (r.Timestamp, r.CreatedAt, r.Id, r.Kind, r.Amount)).ToList();
        // splice the candidate at its Timestamp; same-instant ties sort it last
        seq.Add((input.Timestamp, DateTime.UtcNow, "￿", input.Kind, input.Amount));

        decimal running = 0m;
        foreach (var r in seq.OrderBy(x => x.Timestamp).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            running = Apply(running, r.Kind, r.Amount);
            if (running < 0)
            {
                throw new InvalidOperationException(
                    $"Die Buchung würde die Kasse {KassenKontoDisplay.Name(input.Account)} ins Minus bringen " +
                    "(der Kontostand darf zu keinem Zeitpunkt negativ sein).");
            }
        }
    }

    private static void Validate(KassenBuchungInput input)
    {
        if (input.Amount < 0)
        {
            throw new InvalidOperationException("Der Betrag darf nicht negativ sein.");
        }
        if (input.Kind != KassenBuchungArt.Korrektur && input.Amount <= 0)
        {
            throw new InvalidOperationException("Bitte einen Betrag größer 0 angeben.");
        }
    }

    // a payout below leadership is always booked in the actor's own name
    private static bool MayBookForOthers(ClaimsPrincipal actor, KassenBuchungArt kind)
        => kind == KassenBuchungArt.Einzahlung || actor.IsLeadership();

    private static void RequireManage(ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
    }
}
