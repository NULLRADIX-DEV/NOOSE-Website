using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IBountyService" />
public class BountyService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicWantedService wanted,
    IPublicModuleService modules,
    IKassenService kasse,
    INotificationService notifications,
    ITipPriorityService tipPriority,
    IDiscordWebhookService discord) : IBountyService
{
    /// <summary>Sanity ceiling per share; a typo of six extra zeroes is advertised money the agency cannot pay.</summary>
    private const decimal MaxAmount = 100_000_000m;

    private const int MaxReason = 500;

    private const string NotFound = "Ausschreibung nicht gefunden.";
    private const string ShareNotFound = "Kopfgeld-Anteil nicht gefunden.";
    private const string RequestNotFound = "Kopfgeld-Antrag nicht gefunden.";

    // ---- reads ----

    public async Task<IReadOnlyList<BountyShareRow>> GetSharesAsync(string wantedId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleNoticeAsync(db, wantedId, actor, cancellationToken) is null)
        {
            return [];
        }

        // projected onto the codename: a money list that carried the identity user would put clear names in front of
        // everyone allowed to read the panel
        var rows = await db.FahndungKopfgeldAnteile
            .AsNoTracking()
            .Where(k => k.WantedId == wantedId)
            .OrderByDescending(k => k.Timestamp)
            .Select(k => new
            {
                k.Id,
                k.Origin,
                k.Amount,
                k.Account,
                k.DonorAgentId,
                DonorName = k.DonorAgent!.Codename,
                k.Status,
                k.Timestamp,
                k.KassenBuchungId,
                k.WithdrawnReason,
            })
            .ToListAsync(cancellationToken);

        var bookingIds = rows.Where(r => r.KassenBuchungId != null).Select(r => r.KassenBuchungId!).ToList();
        var bookings = bookingIds.Count == 0
            ? []
            : await db.KassenBuchungen.AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.CaseNumber, cancellationToken);

        var me = actor.GetAgentId();
        return rows
            .Select(r => new BountyShareRow(r.Id, r.Origin, r.Amount, r.Account,
                string.IsNullOrWhiteSpace(r.DonorName) ? "(unbekannt)" : r.DonorName,
                r.DonorAgentId != null && r.DonorAgentId == me,
                r.Status, r.Timestamp,
                r.KassenBuchungId is { } b ? bookings.GetValueOrDefault(b) : null,
                r.WithdrawnReason))
            .ToList();
    }

    public async Task<BountySummary> GetSummaryAsync(string wantedId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await VisibleNoticeAsync(db, wantedId, actor, cancellationToken);
        if (row is null)
        {
            return BountySummary.Empty;
        }

        var shares = await db.FahndungKopfgeldAnteile
            .AsNoTracking()
            .Where(k => k.WantedId == wantedId)
            .Select(k => new { k.Origin, k.Amount, k.Status })
            .ToListAsync(cancellationToken);

        return new BountySummary(
            Advertised: shares.Where(s => BountyShares.IsAdvertised(s.Status)).Sum(s => s.Amount),
            Pending: shares.Where(s => s.Status == BountyShareStatus.Beantragt).Sum(s => s.Amount),
            Official: shares.Where(s => BountyShares.IsAdvertised(s.Status) && s.Origin == BountyOrigin.NooseKasse).Sum(s => s.Amount),
            Private: shares.Where(s => BountyShares.IsAdvertised(s.Status) && s.Origin == BountyOrigin.AgentPrivat).Sum(s => s.Amount),
            Secured: shares.Where(s => s.Status == BountyShareStatus.Gesichert).Sum(s => s.Amount),
            ShareCount: shares.Count(s => BountyShares.IsAdvertised(s.Status)),
            IsCap: row.BountyIsCap);
    }

    public async Task<IReadOnlyList<BountyCoverage>> GetCoverageAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Two summands, and the second is the one that is easy to miss: private money already handed in RAISED the
        // balance and is nevertheless spoken for, so leaving it out reports an all-clear that does not exist.
        var owed = await db.FahndungKopfgeldAnteile
            .AsNoTracking()
            .Where(k => k.Account != null
                && ((k.Origin == BountyOrigin.NooseKasse && k.Status == BountyShareStatus.Zugesagt)
                    || (k.Origin == BountyOrigin.AgentPrivat && k.Status == BountyShareStatus.Gesichert)))
            .GroupBy(k => k.Account!.Value)
            .Select(g => new { Account = g.Key, Total = g.Sum(k => k.Amount) })
            .ToListAsync(cancellationToken);

        var list = new List<BountyCoverage>(KassenKontoDisplay.All.Count);
        foreach (var account in KassenKontoDisplay.All)
        {
            var total = owed.FirstOrDefault(o => o.Account == account)?.Total ?? 0m;
            list.Add(new BountyCoverage(account, total, await kasse.GetBalanceAsync(account, cancellationToken)));
        }
        return list;
    }

    // ---- writes ----

    public async Task<BountyAddOutcome> AddOfficialAsync(string wantedId, decimal amount, KassenKonto account,
        string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireBountyWrite(actor);
        RequireAmount(amount);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await VisibleNoticeAsync(db, wantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        var person = await RequireOpenRecordAsync(db, row, actor, cancellationToken);
        await RequireLiveModuleAsync(row, cancellationToken);

        var share = new FahndungKopfgeldAnteil
        {
            WantedId = row.Id,
            Origin = BountyOrigin.NooseKasse,
            Amount = amount,
            Account = account,
            DonorAgentId = actor.GetAgentId(),
            Timestamp = DateTime.UtcNow,
        };

        // rank 1-2 may commit their own money but not the agency's; the request is filed after the classification
        // block above, so nobody files one that approval would have to refuse
        if (!actor.MayHighestClassification())
        {
            if (string.IsNullOrWhiteSpace(justification))
            {
                throw new InvalidOperationException("Ein Kopfgeld-Antrag braucht eine Begründung.");
            }
            if (await PendingRequests(db).AnyAsync(a => db.FahndungKopfgeldAnteile
                    .Any(k => k.Id == a.BountyShareId && k.WantedId == row.Id), cancellationToken))
            {
                throw new InvalidOperationException("Für diese Ausschreibung läuft bereits ein Kopfgeld-Antrag.");
            }

            share.Status = BountyShareStatus.Beantragt;
            db.FahndungKopfgeldAnteile.Add(share);
            db.Requests.Add(new Request
            {
                Type = RequestType.Kopfgeld,
                TargetType = nameof(Person),
                TargetId = person.Id,
                TargetDesignation = $"{person.Name} ({person.CaseNumber})",
                TargetClassification = person.Classification,
                Justification = justification!.Trim(),
                RequesterName = actor.GetCodename(),
                BountyShareId = share.Id,
            });
            // no bell: the publication request does not ring one either, and the inbox badge is the signal. A filed
            // request through NotificationType.RequestDecided would read "Antrag entschieden" in the bell list.
            await SaveAsync(db, wantedId, cancellationToken);
            return BountyAddOutcome.Requested;
        }

        // read only on the committing branch: a request changes nothing outside, so it needs no before/after
        var before = await AdvertisedAsync(db, wantedId, cancellationToken);
        share.Status = BountyShareStatus.Zugesagt;
        db.FahndungKopfgeldAnteile.Add(share);
        await SaveAsync(db, wantedId, cancellationToken);
        await PushRaiseAsync(row, before, before + amount, cancellationToken);
        return BountyAddOutcome.Committed;
    }

    public async Task AddPrivateAsync(string wantedId, decimal amount, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireBountyWrite(actor);
        RequireAmount(amount);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await VisibleNoticeAsync(db, wantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        await RequireOpenRecordAsync(db, row, actor, cancellationToken);
        await RequireLiveModuleAsync(row, cancellationToken);

        var before = await AdvertisedAsync(db, wantedId, cancellationToken);
        // no account yet: nothing has moved, and the account is decided when the money is actually handed in
        db.FahndungKopfgeldAnteile.Add(new FahndungKopfgeldAnteil
        {
            WantedId = row.Id,
            Origin = BountyOrigin.AgentPrivat,
            Amount = amount,
            DonorAgentId = actor.GetAgentId(),
            Status = BountyShareStatus.Zugesagt,
            Timestamp = DateTime.UtcNow,
        });
        await SaveAsync(db, wantedId, cancellationToken);
        await PushRaiseAsync(row, before, before + amount, cancellationToken);
    }

    public async Task PayInAsync(string shareId, KassenKonto account, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireBountyWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var share = await db.FahndungKopfgeldAnteile.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == shareId, cancellationToken)
            ?? throw new InvalidOperationException(ShareNotFound);
        var row = await VisibleNoticeAsync(db, share.WantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        if (share.Origin != BountyOrigin.AgentPrivat)
        {
            throw new InvalidOperationException("Behördliches Kopfgeld liegt bereits in der Kasse.");
        }
        if (share.Status != BountyShareStatus.Zugesagt)
        {
            throw new InvalidOperationException("Nur ein zugesagter privater Anteil lässt sich einzahlen.");
        }
        RequireOwnOrLeadership(share, actor);

        var paidAt = DateTime.UtcNow;
        var donor = await db.Users.AsNoTracking()
            .Where(u => u.Id == share.DonorAgentId).Select(u => u.Codename)
            .FirstOrDefaultAsync(cancellationToken) ?? "(unbekannt)";

        // one transaction: the deposit and the secured share commit together or not at all
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var booking = await kasse.BookAsync(db, new KassenBuchungInput
        {
            Account = account,
            Kind = KassenBuchungArt.Einzahlung,
            Amount = share.Amount,
            Reason = $"privates Kopfgeld {row.CaseNumber ?? "(Entwurf)"} · {donor}",
            // the donor, so the ledger can be filtered by whose money it was
            BookedById = share.DonorAgentId,
            Timestamp = paidAt,
        }, actor, cancellationToken);

        // Compare-and-swap, not a tracked update: two tabs paying the same share in at once would otherwise book two
        // deposits (distinct ids, so the unique index cannot catch them) and the last writer would win, leaving one
        // orphaned booking and the money in twice. The loser matches no row and throws, rolling this back.
        var claimed = await db.FahndungKopfgeldAnteile
            .Where(k => k.Id == shareId && k.Status == BountyShareStatus.Zugesagt && k.KassenBuchungId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.Status, BountyShareStatus.Gesichert)
                .SetProperty(k => k.KassenBuchungId, booking.Id)
                .SetProperty(k => k.Account, (KassenKonto?)account), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Dieser Anteil wurde soeben von jemand anderem eingezahlt.");
        }

        // ExecuteUpdate bypasses the audit interceptor, so the money is recorded by hand
        db.AuditLogs.Add(ManualAudit.Row(nameof(FahndungKopfgeldAnteil), shareId, AuditAction.Modified, actor,
            new Dictionary<string, object?[]>
            {
                ["Status"] = [BountyShareStatusDisplay.Name(BountyShareStatus.Zugesagt),
                    BountyShareStatusDisplay.Name(BountyShareStatus.Gesichert)],
                ["Kassenbuchung"] = [null, booking.CaseNumber],
            }));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        // the advertised sum is unchanged — pledged and secured both count — so there is nothing to announce
        await wanted.InvalidatePublicViewAsync(cancellationToken);
    }

    public async Task WithdrawAsync(string shareId, string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireBountyWrite(actor);
        var text = (reason ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Ein Rückzug braucht eine Begründung.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var share = await db.FahndungKopfgeldAnteile.FirstOrDefaultAsync(k => k.Id == shareId, cancellationToken)
            ?? throw new InvalidOperationException(ShareNotFound);
        if (await VisibleNoticeAsync(db, share.WantedId, actor, cancellationToken) is null)
        {
            throw new InvalidOperationException(NotFound);
        }

        if (share.Status == BountyShareStatus.Gesichert)
        {
            // the money is in the till; giving it back is a withdrawal from the treasury, not an edit of a pledge
            throw new InvalidOperationException("Gesichertes Geld liegt in der Kasse; eine Rückzahlung an den "
                + "Stifter ist eine Kassen-Auszahlung und läuft nicht über den Kopfgeld-Rückzug.");
        }
        if (share.Status is BountyShareStatus.Ausgezahlt or BountyShareStatus.Zurueckgezogen)
        {
            throw new InvalidOperationException("Dieser Anteil ist bereits erledigt.");
        }
        RequireOwnOrLeadership(share, actor);

        var wasPending = share.Status == BountyShareStatus.Beantragt;
        share.Status = BountyShareStatus.Zurueckgezogen;
        share.WithdrawnReason = text.Length > MaxReason ? text[..MaxReason] : text;
        if (wasPending)
        {
            // a request nobody can decide any more is worse than none: the badge would keep counting it
            await CloseOpenRequestsAsync(db, shareId, cancellationToken);
        }
        // no push: a bounty going down is not announced, and the old post already stands uncorrectably
        await SaveAsync(db, share.WantedId, cancellationToken);
    }

    // ---- requests ----

    public async Task<IReadOnlyList<BountyRequestRow>> GetPendingRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await PendingRequests(db)
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .Join(db.FahndungKopfgeldAnteile, a => a.BountyShareId, k => k.Id, (a, k) => new { Request = a, Share = k })
            .Select(x => new BountyRequestRow(
                x.Request.Id,
                x.Share.Id,
                x.Share.Wanted!.DisplayName,
                x.Request.TargetDesignation,
                x.Share.Amount,
                x.Share.Account,
                x.Request.RequesterName,
                x.Request.Justification,
                x.Request.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetPendingRequestCountAsync(CancellationToken cancellationToken = default)
    {
        // the same query as the list, so the badge cannot count a request the inbox no longer shows
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await PendingRequests(db).CountAsync(cancellationToken);
    }

    public async Task ApproveRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the write guard first: RequireHighestClassification alone admits the read-only supervision and the demo
        // principal, which would commit agency money before the ReadOnlyBarrierInterceptor vetoes the save
        Permission.RequireBountyWrite(actor);
        Permission.RequireHighestClassification(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (request, share) = await PendingRequestAsync(db, requestId, cancellationToken);
        var row = await VisibleNoticeAsync(db, share.WantedId, actor, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        // the record is checked at decision time as well: it may have become a Verschlusssache since the request
        await RequireOpenRecordAsync(db, row, actor, cancellationToken);
        await RequireLiveModuleAsync(row, cancellationToken);

        var before = await AdvertisedAsync(db, share.WantedId, cancellationToken);

        // Claim the share before announcing: neither the request nor the share carries a concurrency token, so two
        // approvals both read Beantragt, both saved and both posted a "Kopfgeld erhoeht" message that cannot be
        // recalled. Pattern from PayInAsync; ExecuteUpdate bypasses the interceptor, hence the manual audit row.
        var claimed = await db.FahndungKopfgeldAnteile
            .Where(a => a.Id == share.Id && a.Status == BountyShareStatus.Beantragt)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, BountyShareStatus.Zugesagt), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Über dieses Kopfgeld wurde soeben entschieden.");
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(FahndungKopfgeldAnteil), share.Id, AuditAction.Modified, actor,
            ManualAudit.Change("Status", BountyShareStatusDisplay.Name(BountyShareStatus.Beantragt),
                BountyShareStatusDisplay.Name(BountyShareStatus.Zugesagt))));

        Decide(request, approved: true, note, actor);
        share.Status = BountyShareStatus.Zugesagt;
        await SaveAsync(db, share.WantedId, cancellationToken);

        await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
            "Kopfgeld genehmigt", "/fahndung?tab=oeffentlich", cancellationToken);
        await PushRaiseAsync(row, before, before + share.Amount, cancellationToken);
    }

    public async Task RejectRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireBountyWrite(actor);
        Permission.RequireHighestClassification(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (request, share) = await PendingRequestAsync(db, requestId, cancellationToken);

        Decide(request, approved: false, note, actor);
        share.Status = BountyShareStatus.Zurueckgezogen;
        share.WithdrawnReason = "Antrag abgelehnt";
        await SaveAsync(db, share.WantedId, cancellationToken);

        await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
            "Kopfgeld abgelehnt", "/fahndung?tab=oeffentlich", cancellationToken);
    }

    // ---- internals ----

    /// <summary>Open bounty requests whose share is still pending and whose notice still exists.</summary>
    /// <remarks>
    /// Count and list share it so they cannot disagree. The notice clause is what the publication inbox has too: a
    /// deleted notice makes its request undecidable — approving answers "not found" — while the badge would keep
    /// counting it, and a request nobody can decide is worse than none.
    /// </remarks>
    private static IQueryable<Request> PendingRequests(AppDbContext db)
        => db.Requests.Where(a => a.Type == RequestType.Kopfgeld
            && a.Status == RequestStatus.Requested
            && db.FahndungKopfgeldAnteile.Any(k => k.Id == a.BountyShareId
                && k.Status == BountyShareStatus.Beantragt
                && db.OeffentlicheFahndungen.Any(f => f.Id == k.WantedId)));

    private static Task CloseOpenRequestsAsync(AppDbContext db, string shareId, CancellationToken cancellationToken)
        => db.Requests
            .Where(a => a.Type == RequestType.Kopfgeld && a.BountyShareId == shareId
                && a.Status == RequestStatus.Requested)
            .ForEachAsync(a => a.Status = RequestStatus.Rejected, cancellationToken);

    private static async Task<(Request Request, FahndungKopfgeldAnteil Share)> PendingRequestAsync(
        AppDbContext db, string requestId, CancellationToken cancellationToken)
    {
        var request = await db.Requests.FirstOrDefaultAsync(
            a => a.Id == requestId && a.Type == RequestType.Kopfgeld, cancellationToken)
            ?? throw new InvalidOperationException(RequestNotFound);
        if (request.Status != RequestStatus.Requested)
        {
            throw new InvalidOperationException("Dieser Antrag wurde bereits entschieden.");
        }
        var share = await db.FahndungKopfgeldAnteile
            .FirstOrDefaultAsync(k => k.Id == request.BountyShareId, cancellationToken)
            ?? throw new InvalidOperationException(ShareNotFound);
        if (share.Status != BountyShareStatus.Beantragt)
        {
            throw new InvalidOperationException("Dieser Anteil wartet nicht mehr auf eine Entscheidung.");
        }
        return (request, share);
    }

    private static void Decide(Request request, bool approved, string? note, ClaimsPrincipal actor)
    {
        request.Status = approved ? RequestStatus.Approved : RequestStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = note.TrimToNull();
    }

    /// <summary>The notice, or null when the actor may not read the file behind it.</summary>
    /// <remarks>
    /// Not-found and not-allowed answer the same way on purpose: anything else makes the bounty panel an existence
    /// oracle for classified records. Internal rather than private so the reward service reaches the same gate instead
    /// of writing a second visibility predicate — precedent: <c>PublicWantedService.RequirePublishableRecordAsync</c>.
    /// </remarks>
    internal static async Task<OeffentlicheFahndung?> VisibleNoticeAsync(AppDbContext db, string wantedId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == wantedId, cancellationToken);
        if (row is null)
        {
            return null;
        }
        if (row.PersonId is null)
        {
            return row;
        }
        return await Visibility.IsRecordVisibleAsync(db, nameof(Person), row.PersonId, ViewerScope.From(actor),
            cancellationToken)
            ? row
            : null;
    }

    /// <summary>The record behind the notice, refusing the classified and the deleted — the publication gate itself.</summary>
    private static Task<Person> RequireOpenRecordAsync(AppDbContext db, OeffentlicheFahndung row,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => PublicWantedService.RequirePublishableRecordAsync(db, row, actor, cancellationToken);

    /// <summary>Money on a live notice changes what is outside, so it answers to the board's own switch.</summary>
    private Task RequireLiveModuleAsync(OeffentlicheFahndung row, CancellationToken cancellationToken)
        => row.Status == PublicWantedStatus.Veroeffentlicht
            ? modules.RequireEnabledAsync(PublicModules.Wanted, cancellationToken)
            : Task.CompletedTask;

    private static void RequireAmount(decimal amount)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Bitte einen Betrag größer 0 angeben.");
        }
        if (amount > MaxAmount)
        {
            throw new InvalidOperationException($"Ein Anteil ist auf {MaxAmount:N0} $ begrenzt.");
        }
    }

    private static void RequireOwnOrLeadership(FahndungKopfgeldAnteil share, ClaimsPrincipal actor)
    {
        if (share.DonorAgentId is { Length: > 0 } donor && donor == actor.GetAgentId())
        {
            return;
        }
        Permission.RequireLeadership(actor);
    }

    private static Task<decimal> AdvertisedAsync(AppDbContext db, string wantedId, CancellationToken cancellationToken)
        => db.FahndungKopfgeldAnteile
            .Where(k => k.WantedId == wantedId)
            .Where(BountyShares.Advertised)
            .SumAsync(k => k.Amount, cancellationToken);

    /// <summary>Every write of this table ends here: saving without dropping the public snapshot is the one bug worth preventing structurally.</summary>
    // one choke point for both consequences of a share write: the public snapshot and the inbox order of the
    // tips on this notice, which weigh the advertised sum
    private async Task SaveAsync(AppDbContext db, string wantedId, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        await wanted.InvalidatePublicViewAsync(cancellationToken);
        await tipPriority.StampForNoticeAsync(wantedId, cancellationToken);
    }

    /// <summary>Announces a raise in the public channel; silent on a drop, a draft, an expiry or a switched-off module.</summary>
    private async Task PushRaiseAsync(OeffentlicheFahndung row, decimal before, decimal after,
        CancellationToken cancellationToken)
    {
        if (after <= before || row.CaseNumber is null || row.Status != PublicWantedStatus.Veroeffentlicht)
        {
            return;
        }
        if (row.ExpiresAt is { } expires && expires <= DateTime.UtcNow)
        {
            return;
        }
        if (!await modules.IsEnabledAsync(PublicModules.Bounty, cancellationToken)
            || !await modules.IsEnabledAsync(PublicModules.Wanted, cancellationToken))
        {
            return;
        }

        // unlisted: nothing to link
        var board = await wanted.GetBoardAsync(cancellationToken);
        if (board.CardFor(row.CaseNumber) is not { } card)
        {
            return;
        }

        await discord.PushWantedCardAsync(NotificationType.PublicWantedBountyRaised,
            Compose(card, new PublicBounty(after, row.BountyIsCap), DateTime.UtcNow), cancellationToken);
    }

    /// <summary>The card. Takes only outward records, which structurally cannot carry a PersonId, the internal case number, a codename, a breakdown or the charge.</summary>
    private static WantedDiscordEmbed Compose(PublicWantedCard card, PublicBounty total, DateTime raisedAt)
        => WantedDiscordCard.BountyRaised(card, total, raisedAt);
}
