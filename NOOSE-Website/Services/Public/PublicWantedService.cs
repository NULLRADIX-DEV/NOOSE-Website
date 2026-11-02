using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IPublicWantedService" />
/// <remarks>
/// Takes no <c>IPersonService</c> on purpose: the dependency runs the other way, because a person file that becomes a
/// Verschlusssache has to pull its own notice offline.
/// </remarks>
public class PublicWantedService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    ICaseNumberService caseNumbers,
    IFileStorageService peopleFiles,
    IPublicWantedPhotoStorageService publicFiles,
    INotificationService notifications,
    ITipPriorityService tipPriority,
    IDiscordWebhookService discord,
    IPressReleaseService press,
    IMemoryCache cache) : IPublicWantedService
{
    private const string CacheKey = "OeffentlicheFahndungen";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>How many captures the anonymous archive shows; the rest stays internal.</summary>
    private const int ArchiveLimit = 100;

    /// <summary>How many expiries one sweep handles, so a backlog cannot become one huge write.</summary>
    private const int ExpiryBatch = 200;

    private static readonly IReadOnlyList<PublicWantedHint> NoHints = [];

    /// <summary>
    /// The states in which a notice says something about a record outside — the set the retraction hook works on.
    /// </summary>
    /// <remarks>
    /// Gefasst belongs here since the archive exists: a person caught in August and classified in September would
    /// otherwise keep photo, name and date on /gefasst, because the hook only ever looked at live notices.
    /// </remarks>
    /// <remarks>Internal rather than private: the objection service names this set instead of copying it.</remarks>
    internal static readonly PublicWantedStatus[] PubliclyVisible =
    [
        PublicWantedStatus.Veroeffentlicht,
        PublicWantedStatus.Beantragt,
        PublicWantedStatus.Gefasst,
    ];

    /// <summary>Own case-number counter: "F" belongs to factions, and one number must name one kind of record.</summary>
    public const string CaseNumberPrefix = "FA";

    // the four outward length limits live in PublicWantedRules so the editor can state them
    private const int MaxDisplayName = PublicWantedRules.MaxDisplayName;
    private const int MaxAliasText = PublicWantedRules.MaxAliasText;
    private const int MaxLastArea = PublicWantedRules.MaxLastArea;
    private const int MaxVehicleText = PublicWantedRules.MaxVehicleText;

    /// <summary>The states in which a notice occupies its subject; a second one for the same subject is refused.</summary>
    private static readonly PublicWantedStatus[] LiveStates =
    [
        PublicWantedStatus.Entwurf,
        PublicWantedStatus.Beantragt,
        PublicWantedStatus.Veroeffentlicht,
    ];

    private const string NotFound = "Ausschreibung nicht gefunden.";
    private const string SourceNotFound = "Der Eintrag aus dem Steckbrief ist nicht mehr vorhanden.";
    private const string RecordNotFound = "Akte nicht gefunden.";
    private const string Classified = "Eine Verschlusssache wird nicht öffentlich ausgeschrieben.";

    // ---- outward reads ----

    public async Task<PublicWantedBoard> GetBoardAsync(CancellationToken cancellationToken = default)
    {
        // the module switch is read outside the content cache: caching "module is off" as an empty board would keep
        // the board dark for a whole cache window after someone turns it back on
        if (!await modules.IsEnabledAsync(PublicModules.Wanted, cancellationToken))
        {
            return PublicWantedBoard.Empty;
        }
        var board = await LoadAsync(cancellationToken);
        // the item switch is read outside the content cache for the same reason as the board's own, and it is a
        // sub-switch: turning the vehicles off has to leave the person notices standing, while the board switch
        // above takes everything with it
        if (!await modules.IsEnabledAsync(PublicModules.WantedVehicles, cancellationToken))
        {
            board = board.WithoutItems();
        }
        // the bounty switch is read outside the content cache for the same reason as the board's own; dropping the
        // dictionary is one assignment because the amounts live on the board rather than on each card
        return await modules.IsEnabledAsync(PublicModules.Bounty, cancellationToken)
            ? board
            : board with { BountyByCaseNumber = PublicWantedBoard.NoBounties };
    }

    public async Task<PublicWantedDetail?> GetByCaseNumberAsync(string? caseNumber, CancellationToken cancellationToken = default)
        => (await GetBoardAsync(cancellationToken)).Find(caseNumber);

    public async Task<PublicBounty?> GetBountyAsync(string? caseNumber, CancellationToken cancellationToken = default)
        => (await GetBoardAsync(cancellationToken)).BountyFor(caseNumber);

    public async Task<WantedPosterLink?> GetPosterLinkAsync(string? caseNumber, CancellationToken cancellationToken = default)
    {
        // gated by the board
        var entry = await GetByCaseNumberAsync(caseNumber, cancellationToken);
        if (entry is null)
        {
            return null;
        }
        var tipsOn = await modules.IsEnabledAsync(PublicModules.Tips, cancellationToken);
        // configured site only
        var config = await discord.GetConfigAsync(cancellationToken);
        return WantedShortLink.Poster(config.SiteBaseUrl, entry.CaseNumber, tipsOn);
    }

    public async Task<string?> ResolveShortLinkAsync(string? code, CancellationToken cancellationToken = default)
    {
        var entry = await GetByCaseNumberAsync(WantedShortLink.CaseNumberFor(code), cancellationToken);
        return entry is null ? null : WantedShortLink.ProfilePath(entry.CaseNumber);
    }

    public async Task<IReadOnlyList<PublicWantedArchiveCard>> GetArchiveAsync(CancellationToken cancellationToken = default)
    {
        // its own switch, read outside the content cache for the same reason as the board: the archive has to stay
        // online while the board is off, and vice versa
        if (!await modules.IsEnabledAsync(PublicModules.WantedArchive, cancellationToken))
        {
            return [];
        }
        var board = await LoadAsync(cancellationToken);
        if (!await modules.IsEnabledAsync(PublicModules.WantedVehicles, cancellationToken))
        {
            board = board.WithoutItems();
        }
        return board.Archive;
    }

    public async Task<int> GetCapturedTotalAsync(CancellationToken cancellationToken = default)
    {
        // same two switches as the archive list it heads, read outside the content cache for the same reason
        if (!await modules.IsEnabledAsync(PublicModules.WantedArchive, cancellationToken))
        {
            return 0;
        }
        var board = await LoadAsync(cancellationToken);
        if (!await modules.IsEnabledAsync(PublicModules.WantedVehicles, cancellationToken))
        {
            board = board.WithoutItems();
        }
        return board.CapturedTotal;
    }

    public async Task<PublicWantedPhoto?> GetPublishedPhotoAsync(string? caseNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            return null;
        }

        // per-entry gate, not "either module is on": with the archive off a captured photo must 404, and with the
        // board off a live one must, or turning a module off would leave its pictures downloadable
        var boardOn = await modules.IsEnabledAsync(PublicModules.Wanted, cancellationToken);
        var archiveOn = await modules.IsEnabledAsync(PublicModules.WantedArchive, cancellationToken);
        if (!boardOn && !archiveOn)
        {
            return null;
        }

        // read through the same snapshot the pages use, so a notice hidden there has no picture either
        var snapshot = await LoadAsync(cancellationToken);
        var live = boardOn ? snapshot.Find(caseNumber) : null;
        var captured = live is null && archiveOn ? snapshot.FindCaptured(caseNumber) : null;
        if (!(live?.HasPhoto ?? captured?.HasPhoto ?? false))
        {
            return null;
        }
        // the module of the set the row was found in, like the two gates above. An item notice cannot carry a photo
        // today; an endpoint that relies on a rule enforced in another file is exactly the coupling that rots.
        if (WantedKinds.IsItem(live?.Kind ?? captured!.Kind)
            && !await modules.IsEnabledAsync(PublicModules.WantedVehicles, cancellationToken))
        {
            return null;
        }

        var name = live?.CaseNumber ?? captured!.CaseNumber;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // widened by one status, but only for a case number the snapshot already handed out — the belt above still
        // decides, this query only resolves the file name
        var row = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .Where(f => f.CaseNumber == name
                && (f.Status == PublicWantedStatus.Veroeffentlicht || f.Status == PublicWantedStatus.Gefasst))
            .Select(f => new { f.PhotoFileName, f.PhotoContentType })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.PhotoFileName is { Length: > 0 } file
            ? new PublicWantedPhoto(file, row.PhotoContentType ?? "application/octet-stream")
            : null;
    }

    /// <summary>Every notice answers to the board switch; an item notice answers to its own on top.</summary>
    /// <remarks>
    /// Without the second gate a licence plate could go live while its module is off: the row would say published,
    /// the board would strip it, and the Discord post — which cannot be recalled — would link to a 404.
    /// </remarks>
    private async Task RequireModulesAsync(PublicWantedKind kind, CancellationToken cancellationToken)
    {
        await modules.RequireEnabledAsync(PublicModules.Wanted, cancellationToken);
        if (WantedKinds.IsItem(kind))
        {
            await modules.RequireEnabledAsync(PublicModules.WantedVehicles, cancellationToken);
        }
    }

    // ---- internal reads ----

    public async Task<PublicWantedBanner?> GetBannerForPersonAsync(string personId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(personId))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.OeffentlicheFahndungen
            .AsNoTracking()
            // person kinds only: the banner says this PERSON is publicly wanted, which an advertised plate on the
            // same file does not make true
            .Where(WantedKinds.PersonRows)
            .Where(f => f.PersonId == personId
                && (f.Status == PublicWantedStatus.Veroeffentlicht || f.Status == PublicWantedStatus.Beantragt))
            .OrderByDescending(f => f.PublishedAt ?? f.CreatedAt)
            .Select(f => new PublicWantedBanner(f.CaseNumber, f.Status, f.PublishedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublicWantedEdit>> GetAllAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // projected rather than Include'd: only the codename is wanted, and pulling the whole identity user would
        // carry the publisher's clear name into a panel the read-only supervision renders
        var rows = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .OrderByDescending(f => f.PublishedAt ?? f.CreatedAt)
            .Select(f => new { f.PersonId, Row = new PublicWantedEdit(
                f.Id,
                f.CaseNumber,
                f.Kind,
                f.Status,
                f.DisplayName,
                f.Person!.CaseNumber,
                f.PublicHazardLevel,
                f.PhotoFileName != null,
                f.PublishedAt,
                f.PublishedBy!.Codename,
                f.ExpiresAt,
                f.ModifiedAt ?? f.CreatedAt,
                f.ViewCount,
                0m) })
            .ToListAsync(cancellationToken);

        // a notice carries the file's content, so it answers to the file's read gate — otherwise the list would show
        // the name, accusation and Aktenzeichen of a Verschlusssache to every rank-3 agent
        var visible = await VisibleRecordsAsync(db, rows.Select(r => r.PersonId), actor, cancellationToken);
        var kept = rows.Where(r => r.PersonId is null || visible.Contains(r.PersonId)).Select(r => r.Row).ToList();

        // summed after the gate and in one query, not as a correlated subquery per row
        var bounties = await BountiesAsync(db, kept.Select(r => r.Id), cancellationToken);
        return kept
            .Select(r => bounties.TryGetValue(r.Id, out var total) ? r with { Bounty = total } : r)
            .ToList();
    }

    public async Task<PublicWantedDraft?> GetDraftAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new { f.PersonId, Draft = new PublicWantedDraft(f.Id, f.CaseNumber, f.Kind, f.Status,
                f.DisplayName, f.AliasText, f.LastArea, f.VehicleText, f.PhotoSourceId, f.ExpiresAt, f.ChargeHtml,
                f.BountyIsCap, f.PublicHazardLevel, f.PublicPaidOut) })
            .FirstOrDefaultAsync(cancellationToken);

        return row is not null && await IsRecordVisibleAsync(db, row.PersonId, actor, cancellationToken)
            ? row.Draft
            : null;
    }

    public async Task<PublicWantedOptions> GetOptionsAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new { f.PersonId, f.Kind })
            .FirstOrDefaultAsync(cancellationToken);
        var personId = row?.PersonId;
        // these are LIVE rows of the file, not snapshot fields — without the gate the editor hands a rank-3 agent
        // the current whereabouts of a file he may not open anywhere else
        if (personId is null || !await IsRecordVisibleAsync(db, personId, actor, cancellationToken))
        {
            return PublicWantedOptions.Empty;
        }

        // the only photo store in the house holds mugshots, and this notice hangs off the owner's file — offering
        // them here would put her portrait on a licence plate. Areas stay: where a vehicle was last seen is its own.
        var photos = WantedKinds.IsItem(row!.Kind)
            ? []
            : await db.PersonPhotos
            .AsNoTracking()
            .Where(f => f.PersonId == personId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new PublicWantedPhotoOption(f.Id, f.CreatedAt.ToString("dd.MM.yyyy")))
            .ToListAsync(cancellationToken);
        var areas = await db.PersonLocations
            .AsNoTracking()
            .Where(o => o.PersonId == personId)
            .Select(o => o.Text)
            .ToListAsync(cancellationToken);

        return new PublicWantedOptions(photos, areas);
    }

    public async Task<PublicWantedEdit?> GetForPersonAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsRecordVisibleAsync(db, personId, actor, cancellationToken))
        {
            return null;
        }

        var row = await db.OeffentlicheFahndungen
            .AsNoTracking()
            // the file page shows the person notice here; the vehicles and weapons have their own panel
            .Where(WantedKinds.PersonRows)
            .Where(f => f.PersonId == personId)
            .OrderByDescending(f => f.PublishedAt ?? f.CreatedAt)
            .Select(f => new PublicWantedEdit(
                f.Id, f.CaseNumber, f.Kind, f.Status, f.DisplayName, f.Person!.CaseNumber, f.PublicHazardLevel,
                f.PhotoFileName != null, f.PublishedAt, f.PublishedBy!.Codename, f.ExpiresAt,
                f.ModifiedAt ?? f.CreatedAt, f.ViewCount, 0m))
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var bounties = await BountiesAsync(db, [row.Id], cancellationToken);
        return bounties.TryGetValue(row.Id, out var total) ? row with { Bounty = total } : row;
    }

    public async Task<IReadOnlyList<PublicWantedEdit>> GetItemsForPersonAsync(string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsRecordVisibleAsync(db, personId, actor, cancellationToken))
        {
            return [];
        }

        var rows = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .Where(WantedKinds.ItemRows)
            .Where(f => f.PersonId == personId)
            .OrderByDescending(f => f.PublishedAt ?? f.CreatedAt)
            .Select(f => new PublicWantedEdit(
                f.Id, f.CaseNumber, f.Kind, f.Status, f.DisplayName, f.Person!.CaseNumber, f.PublicHazardLevel,
                f.PhotoFileName != null, f.PublishedAt, f.PublishedBy!.Codename, f.ExpiresAt,
                f.ModifiedAt ?? f.CreatedAt, f.ViewCount, 0m))
            .ToListAsync(cancellationToken);

        var bounties = await BountiesAsync(db, rows.Select(r => r.Id), cancellationToken);
        return rows
            .Select(r => bounties.TryGetValue(r.Id, out var total) ? r with { Bounty = total } : r)
            .ToList();
    }

    public async Task<IReadOnlyList<PublicWantedItemSource>> GetItemSourcesAsync(string personId,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // LIVE rows of the file, like the photo and area choices: without the gate the panel would hand a rank-3
        // agent the vehicles of a file he may not open anywhere else
        if (!await IsRecordVisibleAsync(db, personId, actor, cancellationToken))
        {
            return [];
        }

        var vehicles = await db.PersonVehicles
            .AsNoTracking()
            .Where(v => v.PersonId == personId)
            .OrderBy(v => v.Designation)
            .Select(v => new { v.Id, v.Designation, v.LicensePlate })
            .ToListAsync(cancellationToken);
        var weapons = await db.PersonWeapons
            .AsNoTracking()
            .Where(w => w.PersonId == personId)
            .OrderBy(w => w.Text)
            .Select(w => new { w.Id, w.Text })
            .ToListAsync(cancellationToken);

        var taken = (await db.OeffentlicheFahndungen
                .AsNoTracking()
                .Where(WantedKinds.ItemRows)
                .Where(f => f.PersonId == personId && LiveStates.Contains(f.Status))
                .Select(f => new { f.Kind, f.DisplayName })
                .ToListAsync(cancellationToken))
            .Select(f => SubjectKey(f.Kind, f.DisplayName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sources = vehicles
            .Select(v => new PublicWantedItemSource(v.Id, PublicWantedKind.Fahrzeug,
                VehicleLabel(v.Designation, v.LicensePlate),
                taken.Contains(SubjectKey(PublicWantedKind.Fahrzeug,
                    VehicleSubject(v.Designation, v.LicensePlate)))))
            .Concat(weapons.Select(w => new PublicWantedItemSource(w.Id, PublicWantedKind.Waffe, w.Text,
                taken.Contains(SubjectKey(PublicWantedKind.Waffe, w.Text)))))
            .Where(o => o.Label.Trim().Length > 0)
            .ToList();
        return sources;
    }

    // ---- writes ----

    public async Task<string> CreateDraftFromPersonAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // resolved through the canonical visibility gate first: an invisible file must read as "not found", never as
        // "not allowed", or the create button becomes an existence oracle for classified records
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, ViewerScope.From(actor), cancellationToken))
        {
            throw new InvalidOperationException(RecordNotFound);
        }

        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException(RecordNotFound);
        RequireNotClassified(person, actor);

        // person kinds only: an advertised licence plate hangs off the same file and must not block the manhunt
        var live = await db.OeffentlicheFahndungen
            .Where(WantedKinds.PersonRows)
            .AnyAsync(f => f.PersonId == personId && LiveStates.Contains(f.Status), cancellationToken);
        if (live)
        {
            throw new InvalidOperationException("Für diese Akte gibt es bereits eine Ausschreibung.");
        }

        var row = new OeffentlicheFahndung
        {
            PersonId = person.Id,
            Kind = PublicWantedKind.Fahndung,
            Status = PublicWantedStatus.Entwurf,
            DisplayName = Cut(person.Name, MaxDisplayName),
            // only name and accusation are pulled: a licence plate correlates live, and an alias may be informant-
            // sourced, so the author picks those two by hand in the editor
            ChargeHtml = ChargeFrom(person.WantedReason),
        };
        db.OeffentlicheFahndungen.Add(row);
        // through the one save path even though a draft was never on the board: an exception here would have to be
        // whitelisted in the guard, and an unnecessary drop costs one rebuild inside a 10 s window
        await SaveAndInvalidateAsync(db, cancellationToken);
        return row.Id;
    }

    public async Task<string> CreateDraftFromVehicleAsync(string vehicleId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.PersonVehicles
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == vehicleId, cancellationToken)
            ?? throw new InvalidOperationException(SourceNotFound);

        // the plate is what identifies the vehicle outside; the model goes into the description line beside it, and
        // only when the plate is actually known — otherwise both fields would say the same thing
        var plateKnown = !string.IsNullOrWhiteSpace(source.LicensePlate);
        return await ItemDraftAsync(db, PublicWantedKind.Fahrzeug, source.PersonId,
            VehicleSubject(source.Designation, source.LicensePlate),
            plateKnown ? source.Designation : null, actor, cancellationToken);
    }

    public async Task<string> CreateDraftFromWeaponAsync(string weaponId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.PersonWeapons
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == weaponId, cancellationToken)
            ?? throw new InvalidOperationException(SourceNotFound);

        return await ItemDraftAsync(db, PublicWantedKind.Waffe, source.PersonId, source.Text, null, actor,
            cancellationToken);
    }

    /// <summary>The one body behind both item entry points; the source row is read for the prefill and dropped.</summary>
    private async Task<string> ItemDraftAsync(AppDbContext db, PublicWantedKind kind, string personId,
        string subject, string? vehicleText, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        // resolved through the canonical visibility gate first, exactly as the person path does: an invisible file
        // must read as "not found", never as "not allowed"
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, ViewerScope.From(actor),
                cancellationToken))
        {
            throw new InvalidOperationException(RecordNotFound);
        }

        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException(RecordNotFound);
        RequireNotClassified(person, actor);

        var name = Cut(subject.Trim(), MaxDisplayName);
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Der Eintrag trägt keine Bezeichnung, aus der sich etwas ausschreiben lässt.");
        }

        // deduplicated on the text, not on the source row: the file's profile children are replaced wholesale on
        // every save, so their ids are worthless a moment later — and the plate is what names the notice outside
        var live = await db.OeffentlicheFahndungen
            .AnyAsync(f => f.PersonId == personId && f.Kind == kind && f.DisplayName == name
                && LiveStates.Contains(f.Status), cancellationToken);
        if (live)
        {
            throw new InvalidOperationException("Dafür gibt es an dieser Akte bereits eine Ausschreibung.");
        }

        var row = new OeffentlicheFahndung
        {
            PersonId = person.Id,
            Kind = kind,
            Status = PublicWantedStatus.Entwurf,
            DisplayName = name,
            VehicleText = CutOrNull(vehicleText, MaxVehicleText),
            // the accusation is deliberately not prefilled from the file: WantedReason is an allegation against the
            // person and usually names her, which is the very reference an item notice promises not to publish
        };
        db.OeffentlicheFahndungen.Add(row);
        await SaveAndInvalidateAsync(db, cancellationToken);
        return row.Id;
    }

    public async Task UpdateSnapshotAsync(PublicWantedInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == input.Id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        // the same gate the read path holds, and unconditionally like the sibling write paths: without it an agent
        // whose circuit predates a classification could write against a Verschlusssache and audit it
        if (!await IsRecordVisibleAsync(db, row.PersonId, actor, cancellationToken))
        {
            throw new InvalidOperationException(NotFound);
        }

        var live = row.Status == PublicWantedStatus.Veroeffentlicht;
        if (live)
        {
            // editing a live notice is a publication, so it answers to the same gates
            await RequireModulesAsync(row.Kind, cancellationToken);
            await RequirePublishableRecordAsync(db, row, actor, cancellationToken);
        }

        var name = (input.DisplayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Die Ausschreibung braucht einen Anzeigenamen.");
        }

        row.DisplayName = Cut(name, MaxDisplayName);
        row.AliasText = CutOrNull(input.AliasText, MaxAliasText);
        row.LastArea = CutOrNull(input.LastArea, MaxLastArea);
        row.VehicleText = CutOrNull(input.VehicleText, MaxVehicleText);
        row.ExpiresAt = PublicExpiry.From(input.ExpiresAt);

        // only a real change flips the flag: saving an untouched editor must leave the level on the score, while an
        // author who picks one keeps it through every later publication
        var levelChanged = input.HazardLevel is { } level && level != row.PublicHazardLevel;
        if (levelChanged)
        {
            row.PublicHazardLevel = input.HazardLevel!.Value;
            row.HazardLevelIsManual = true;
        }

        // refused rather than quietly nulled: the editor offers no picker for an item notice, so a value here can
        // only come from a manipulated post — and the only photo store in the house holds the owner's mugshots
        if (WantedKinds.IsItem(row.Kind) && !string.IsNullOrEmpty(input.PhotoSourceId))
        {
            throw new InvalidOperationException("Eine Fahrzeug- oder Waffen-Ausschreibung trägt kein Foto.");
        }

        var previousPhoto = row.PhotoFileName;
        var previousSource = row.PhotoSourceId;
        await PhotoSourceSetAsync(db, row, input.PhotoSourceId, cancellationToken);
        if (live)
        {
            // the copy has to follow the choice here, not only at publish time: otherwise clearing the photo of a
            // published notice reports success while the mugshot stays anonymously downloadable
            await PhotoCopyAsync(db, row, cancellationToken);
        }
        else if (row.Status == PublicWantedStatus.Gefasst)
        {
            // an archive row is still outside: /gefasst renders it and the photo endpoint widens to Gefasst, so a
            // removal has to reach the copy here too. A swap is refused rather than copied, because a fresh
            // mugshot must not go outside without passing the publication gates above.
            if (row.PhotoSourceId is not { Length: > 0 })
            {
                row.PhotoFileName = null;
                row.PhotoContentType = null;
            }
            else if (!string.Equals(row.PhotoSourceId, previousSource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Das Foto einer gefassten Ausschreibung lässt sich nicht "
                    + "tauschen — dazu zurückziehen und neu veröffentlichen.");
            }
        }

        // null means "leave the stored accusation alone", "" means "clear it" — without the split, saving a renamed
        // notice would silently drop its text
        if (input.ChargeHtml is not null)
        {
            row.ChargeHtml = HtmlCleanup.Clean(input.ChargeHtml);
        }

        if (live)
        {
            RequirePublishableCharge(row.ChargeHtml);
        }

        await SaveAndInvalidateAsync(db, cancellationToken);

        // the level is a factor of the inbox order of every tip on this notice, so changing it here has to reach
        // the same stamp the publish body and the refresh action call
        if (levelChanged)
        {
            await tipPriority.StampForNoticeAsync(row.Id, cancellationToken);
        }

        // after the save, for the same reason as in the publish body. Gefasst counts as outside, so its stale
        // copy has to go too.
        if ((live || row.Status == PublicWantedStatus.Gefasst) && previousPhoto != row.PhotoFileName)
        {
            DeleteCopy(previousPhoto);
        }
    }

    /// <summary>A date picked in the editor is local midnight; outside it must mean "to the end of that day".</summary>
    public async Task<PublicWantedPublishOutcome> PublishAsync(string id, string? justification, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // first, so a read-only supervisor does not even learn whether any module is on
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        // after the row, not before it: which module has to be live depends on the kind
        await RequireModulesAsync(row.Kind, cancellationToken);
        if (row.Status == PublicWantedStatus.Veroeffentlicht)
        {
            throw new InvalidOperationException("Die Ausschreibung ist bereits veröffentlicht.");
        }
        // fail closed: every notice is drawn from a record so the suppression belt has something to grip. A row
        // without one would be the only public entry no belt protects.
        if (row.PersonId is null)
        {
            throw new InvalidOperationException("Eine Ausschreibung ohne Aktenbezug lässt sich nicht veröffentlichen.");
        }

        var person = await RequirePublishableRecordAsync(db, row, actor, cancellationToken);
        // checked here as well so a request is never filed for content that approval would have to refuse; the
        // authoritative run is inside PublishRowAsync, which both entry points share
        RequirePublishableContent(row);

        // rank 1-2 may prepare a notice but not put it outside; the request is filed after the classification block
        // above, so nobody files one that could never be approved
        if (!actor.MayHighestClassification())
        {
            if (string.IsNullOrWhiteSpace(justification))
            {
                throw new InvalidOperationException("Ein Veröffentlichungsantrag braucht eine Begründung.");
            }
            var open = await db.Requests.AnyAsync(a => a.Type == RequestType.Veroeffentlichung
                && a.PublicationWantedId == row.Id && a.Status == RequestStatus.Requested, cancellationToken);
            if (open)
            {
                throw new InvalidOperationException("Für diese Ausschreibung läuft bereits ein Veröffentlichungsantrag.");
            }

            db.Requests.Add(new Request
            {
                Type = RequestType.Veroeffentlichung,
                TargetType = nameof(Person),
                TargetId = person.Id,
                TargetDesignation = $"{person.Name} ({person.CaseNumber})",
                TargetClassification = person.Classification,
                Justification = justification!.Trim(),
                RequesterName = actor.GetCodename(),
                PublicationWantedId = row.Id,
            });
            row.Status = PublicWantedStatus.Beantragt;
            await SaveAndInvalidateAsync(db, cancellationToken);
            return PublicWantedPublishOutcome.Requested;
        }

        await PublishRowAsync(db, row, person, actor, cancellationToken);
        return PublicWantedPublishOutcome.Published;
    }

    public async Task RetractAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Zum Zurückziehen gehört ein Grund.");
        }

        // deliberately no module gate: publishing needs a live module, taking something offline never does — otherwise
        // the kill switch would make it impossible to pull an entry, exactly backwards
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        // the same set the hook uses: a captured notice is still outside, in the archive, so it must be retractable
        // — otherwise the only way off /gefasst would be deleting it, a silent depublication with no reason
        if (!PubliclyVisible.Contains(row.Status))
        {
            throw new InvalidOperationException("Nur eine öffentlich sichtbare Ausschreibung lässt sich zurückziehen.");
        }

        await RetractRowAsync(db, row, reason.Trim(), cancellationToken);
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public async Task CapturedAsync(string id, ClaimsPrincipal actor, decimal? publicPaidOut = null,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);
        // before the row is touched, so a rejected figure cannot leave a half-captured notice behind
        RequireShowableAmount(publicPaidOut);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        if (row.Status != PublicWantedStatus.Veroeffentlicht)
        {
            throw new InvalidOperationException("Nur eine veröffentlichte Ausschreibung lässt sich auf gefasst setzen.");
        }

        row.Status = PublicWantedStatus.Gefasst;
        row.CapturedAt = DateTime.UtcNow;
        row.PublicPaidOut = publicPaidOut;
        var card = new PublicWantedCard(row.CaseNumber!, row.Kind, row.DisplayName, row.AliasText,
            row.PhotoFileName != null, row.PublicHazardLevel, row.PublishedAt, NoHints);
        await SaveAndInvalidateAsync(db, cancellationToken);

        // after the commit, and never fatal: a press draft is a convenience, and losing it must not undo the capture.
        // The card is all the draft may know — it cannot carry a PersonId, the internal case number or a score
        try { await press.CreateCaptureDraftAsync(card, actor, cancellationToken); } catch { /* best effort */ }
    }

    public async Task SetPublicPaidOutAsync(string id, decimal? publicPaidOut, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);
        RequireShowableAmount(publicPaidOut);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        // Gefasst, or any notice that already carries a figure: a retraction leaves the figure in the public total
        // (it does not unpay a reward), so locking the correction to Gefasst would freeze a wrong number for good
        if (row.Status != PublicWantedStatus.Gefasst && row.PublicPaidOut is null)
        {
            throw new InvalidOperationException(
                "Die angezeigte Belohnung gehört zu einer Ergreifung — erst auf gefasst setzen.");
        }

        row.PublicPaidOut = publicPaidOut;
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    /// <summary>The two rules a shown figure has to obey; it is a display value, so nothing else applies.</summary>
    private static void RequireShowableAmount(decimal? amount)
    {
        if (amount is not { } value)
        {
            return;
        }
        if (value < 0m)
        {
            throw new InvalidOperationException("Die angezeigte Belohnung darf nicht negativ sein.");
        }
        // the column holds two decimals; MySQL would swallow a third without a word
        if (decimal.Round(value, 2) != value)
        {
            throw new InvalidOperationException("Die angezeigte Belohnung hat höchstens zwei Dezimalstellen.");
        }
    }

    public async Task RefreshHazardLevelAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        var person = await RequirePublishableRecordAsync(db, row, actor, cancellationToken);

        // the one action that overrules the author again — that is what the button is for
        row.PublicHazardLevel = HazardLevelLogic.From(person.ThreatScore);
        row.HazardLevelIsManual = false;
        await SaveAndInvalidateAsync(db, cancellationToken);
        await tipPriority.StampForNoticeAsync(row.Id, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        // Gefasst is outside too — /gefasst renders it — so deleting it would be the same silent depublication.
        // Beantragt stays deletable: CloseOpenRequestsAsync below closes its request, and the editor offers
        // Zurückziehen for it as the alternative.
        if (row.Status is PublicWantedStatus.Veroeffentlicht or PublicWantedStatus.Gefasst)
        {
            // otherwise deleting would be a silent depublication with no reason on the record
            throw new InvalidOperationException("Zuerst zurückziehen, dann löschen.");
        }

        // a request pointing at a deleted notice can never be decided again: the inbox resolves the notice through the
        // soft-delete filter and would not find it, while the badge counts the request row forever
        await CloseOpenRequestsAsync(db, row.Id, cancellationToken);

        // the audit interceptor rewrites this into a soft delete; the photo copy stays so a restore is not a broken poster
        db.OeffentlicheFahndungen.Remove(row);
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public async Task RetractForRecordAsync(string personId, string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // no rights guard: the caller already passed one, and a classification upgrade must never fail because this
        // guard said no — that would leave the poster up, the exact outcome it exists to prevent
        if (string.IsNullOrEmpty(personId))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.OeffentlicheFahndungen
            .Where(f => f.PersonId == personId && PubliclyVisible.Contains(f.Status))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            await RetractRowAsync(db, row, reason, cancellationToken);
        }
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    // ---- warning chips ----

    public async Task<IReadOnlyList<string>> GetHintIdsAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedRecordRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var personId = await db.OeffentlicheFahndungen
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => f.PersonId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!await IsRecordVisibleAsync(db, personId, actor, cancellationToken))
        {
            return [];
        }

        return await db.FahndungWarnhinweise
            .AsNoTracking()
            .Where(z => z.FahndungId == id)
            .Select(z => z.WarnhinweisId)
            .ToListAsync(cancellationToken);
    }

    public async Task SetHintsAsync(string id, IEnumerable<string> hintIds, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        // the same gate the read path holds: without it an agent who knows an id could chip a notice whose file he
        // may not open, and write an audit row against that file
        if (!await IsRecordVisibleAsync(db, row.PersonId, actor, cancellationToken))
        {
            throw new InvalidOperationException(NotFound);
        }
        if (row.Status == PublicWantedStatus.Veroeffentlicht)
        {
            // chipping a live notice changes what is outside, so it answers to the same gates as an edit
            await RequireModulesAsync(row.Kind, cancellationToken);
            await RequirePublishableRecordAsync(db, row, actor, cancellationToken);
        }

        var requested = hintIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.FahndungWarnhinweise
            .Where(z => z.FahndungId == id)
            .ToListAsync(cancellationToken);
        var existingIds = existing.Select(z => z.WarnhinweisId).ToHashSet(StringComparer.Ordinal);

        // Narrowed to rows that exist and are either active or already assigned. Active-only would silently drop a
        // warning that was deactivated after it was assigned: the picker does not offer it, the editor does not show
        // it, so pressing "Übernehmen" without touching anything would destroy the assignment unseen. Adding one is
        // still restricted to active rows, which is what keeps a tampered dialog post out.
        var selectable = requested.Count == 0
            ? []
            : await db.Warnhinweise
                .Where(w => requested.Contains(w.Id) && (w.IsActive || existingIds.Contains(w.Id)))
                .Select(w => w.Id)
                .ToListAsync(cancellationToken);
        var wanted = selectable.ToHashSet(StringComparer.Ordinal);

        var toRemove = existing.Where(z => !wanted.Contains(z.WarnhinweisId)).ToList();
        var toSupplement = wanted
            .Where(x => !existingIds.Contains(x))
            .Select(x => new FahndungWarnhinweis { FahndungId = id, WarnhinweisId = x })
            .ToList();
        if (toRemove.Count == 0 && toSupplement.Count == 0)
        {
            return;
        }

        db.FahndungWarnhinweise.RemoveRange(toRemove);
        db.FahndungWarnhinweise.AddRange(toSupplement);

        // FahndungWarnhinweis is not IAuditable, so the interceptor would write nothing — log the diff against the
        // notice, which is the row that carries the change outside
        var touched = toRemove.Select(z => z.WarnhinweisId).Concat(toSupplement.Select(z => z.WarnhinweisId))
            .Distinct(StringComparer.Ordinal).ToList();
        var names = await db.Warnhinweise
            .Where(w => touched.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);
        var changes = new Dictionary<string, object?[]>();
        if (toSupplement.Count > 0)
        {
            changes["Warnhinweise hinzugefügt"] =
                [null, string.Join(", ", toSupplement.Select(z => names.GetValueOrDefault(z.WarnhinweisId, z.WarnhinweisId)))];
        }
        if (toRemove.Count > 0)
        {
            changes["Warnhinweise entfernt"] =
                [null, string.Join(", ", toRemove.Select(z => names.GetValueOrDefault(z.WarnhinweisId, z.WarnhinweisId)))];
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(OeffentlicheFahndung), row.Id, AuditAction.Modified, actor, changes));

        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    // ---- bounty ----

    public async Task SetBountyIsCapAsync(string id, bool isCap, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        // the same gate as the chips: the flag is snapshot data on the notice and reads "bis X" outside
        if (!await IsRecordVisibleAsync(db, row.PersonId, actor, cancellationToken))
        {
            throw new InvalidOperationException(NotFound);
        }
        if (row.BountyIsCap == isCap)
        {
            return;
        }
        if (row.Status == PublicWantedStatus.Veroeffentlicht)
        {
            await RequireModulesAsync(row.Kind, cancellationToken);
            await RequirePublishableRecordAsync(db, row, actor, cancellationToken);
        }

        row.BountyIsCap = isCap;
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public Task InvalidatePublicViewAsync(CancellationToken cancellationToken = default)
        => SaveAndInvalidateAsync(null, cancellationToken);

    // ---- counter and expiry ----

    public async Task CountViewAsync(string? caseNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            return;
        }

        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The whole publication predicate sits in the Where rather than in a preceding read: a counting write must be
        // unable to touch a row that is not outside. No IgnoreQueryFilters — the global !IsDeleted filter applies here
        // and is exactly the guard wanted. ExecuteUpdate because the row is IAuditable: a tracked increment would
        // stamp GeaendertAm, write one AuditLog row per anonymous view, and push the notice onto the person file's
        // timeline on every page load. No Permission guard either — there is no actor, the caller is an anonymous
        // visitor, and the one integer this touches never leaves the house. Third documented exception to
        // "bulk write ⇒ call the guard yourself", next to score writes and FactionRecency.StampAsync.
        await db.OeffentlicheFahndungen
            .Where(f => f.CaseNumber == caseNumber
                && f.Status == PublicWantedStatus.Veroeffentlicht
                && (f.ExpiresAt == null || f.ExpiresAt > now))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ViewCount, f => f.ViewCount + 1), cancellationToken);
    }

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        // UtcNow, never Now: PublicExpiry stores the end of the chosen local day converted to UTC, and MySQL datetime
        // carries no offset that would catch the mistake
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Veroeffentlicht only, deliberately not "everything but Gefasst": a Beantragt row with an expiry date would
        // otherwise die silently, without CloseOpenRequestsAsync running, and the nav badge would keep counting a
        // request the inbox can no longer find. No IgnoreQueryFilters — a soft-deleted row belongs to the bin.
        var rows = await db.OeffentlicheFahndungen
            .Where(f => f.Status == PublicWantedStatus.Veroeffentlicht
                && f.ExpiresAt != null && f.ExpiresAt <= now)
            .OrderBy(f => f.ExpiresAt)
            .Take(ExpiryBatch)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            // the status change is the idempotency token: a second sweep selects nothing, so no NotifiedAt column
            // is needed the way Followup needs one. The photo copy stays — expiry is reversible like a retraction.
            row.Status = PublicWantedStatus.Abgelaufen;
        }
        await SaveAndInvalidateAsync(db, cancellationToken);

        await NotifyExpiredAsync(db, rows, cancellationToken);
        return rows.Count;
    }

    /// <summary>One bell per sweep, not one per notice; a failure here must not roll the status back.</summary>
    private async Task NotifyExpiredAsync(AppDbContext db, List<OeffentlicheFahndung> rows,
        CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await db.Users
                .AsNoTracking()
                .OnlySelectable()
                .Where(u => u.IsAdmin || (u.Rank != null && u.Rank >= Rank.SupervisorySpecialAgent))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            if (recipients.Count == 0)
            {
                return;
            }

            var title = rows.Count == 1
                ? $"Ausschreibung abgelaufen: {rows[0].DisplayName} ({rows[0].CaseNumber})"
                : $"{rows.Count} öffentliche Ausschreibungen abgelaufen";
            // PublicWantedExpired is not Discord-routable: this is an internal fact, and NotifyManyAsync pushes
            // every routable category into its channel on its own
            await notifications.NotifyManyAsync(recipients, NotificationType.PublicWantedExpired, title,
                "/fahndung?tab=oeffentlich", null, cancellationToken);
        }
        catch (Exception)
        {
            /* best effort */
        }
    }

    // ---- trash ----

    public async Task<List<OeffentlicheFahndung>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.OeffentlicheFahndungen
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.IsDeleted)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFahndungen
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == id && f.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Die Ausschreibung liegt nicht im Papierkorb.");

        // the file may have become a Verschlusssache while the notice sat in the bin
        await RequirePublishableRecordAsync(db, row, actor, cancellationToken);

        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        // back as a draft: undoing a delete must not republish anything on the way
        row.Status = PublicWantedStatus.Entwurf;
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    // ---- publication requests ----

    public async Task<IReadOnlyList<PublicWantedRequestRow>> GetPendingRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await PendingRequests(db)
            .OrderBy(a => a.CreatedAt)
            .Join(db.OeffentlicheFahndungen, a => a.PublicationWantedId, f => f.Id,
                (a, f) => new PublicWantedRequestRow(a.Id, f.Id, f.DisplayName, a.TargetDesignation,
                    a.RequesterName, a.Justification, a.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetPendingRequestCountAsync(CancellationToken cancellationToken = default)
    {
        // the same join as the list, not a bare count over Antraege: a request whose notice is gone must not keep the
        // badge at one while the inbox says there is nothing to decide
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await PendingRequests(db).CountAsync(cancellationToken);
    }

    public async Task ApprovePublicationRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the write guard first: approving publishes, and RequireHighestClassification alone admits the read-only
        // supervision and the demo principal, which would mint a case number and copy a photo before the
        // ReadOnlyBarrierInterceptor vetoes the save
        Permission.RequirePublicWantedWrite(actor);
        Permission.RequireHighestClassification(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (request, row) = await PendingRequestAsync(db, requestId, cancellationToken);
        // after the row for the same reason as the direct path: the kind decides which module has to be live
        await RequireModulesAsync(row.Kind, cancellationToken);
        var person = await RequirePublishableRecordAsync(db, row, actor, cancellationToken);

        DecideRequest(request, approved: true, note, actor);
        await PublishRowAsync(db, row, person, actor, cancellationToken);
        await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
            "Veröffentlichung genehmigt", "/fahndung?tab=oeffentlich", cancellationToken);
    }

    public async Task RejectPublicationRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePublicWantedWrite(actor);
        Permission.RequireHighestClassification(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (request, row) = await PendingRequestAsync(db, requestId, cancellationToken);

        DecideRequest(request, approved: false, note, actor);
        // only the state the request asked about: PendingRequestAsync validates the request, never the notice, so an
        // unconditional write would take a live notice offline with no reason and past RetractAsync
        if (row.Status == PublicWantedStatus.Beantragt)
        {
            row.Status = PublicWantedStatus.Entwurf;
        }
        await SaveAndInvalidateAsync(db, cancellationToken);
        await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
            "Veröffentlichung abgelehnt", "/fahndung?tab=oeffentlich", cancellationToken);
    }

    // ---- internals ----

    /// <summary>The one publish body; the direct and the approved path share it so they cannot drift apart.</summary>
    private async Task PublishRowAsync(AppDbContext db, OeffentlicheFahndung row, Person person, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        // cleaned before the state check, and the content rules run here rather than only in PublishAsync: the
        // approval path reaches this body too, and a Beantragt row can be edited between filing and decision
        row.ChargeHtml = HtmlCleanup.Clean(row.ChargeHtml);
        RequirePublishableContent(row);

        // unconditional: the case-number service refuses to issue a number without an enclosing transaction
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the row before minting anything. The table carries no concurrency token, so two tabs both saw
        // Status != Veroeffentlicht, both minted a case number, both copied the photo and both posted to Discord -
        // and a Discord post cannot be recalled, so one of them pointed at a number no row carries. Inside the
        // transaction, both because the number service demands one and because the claim must roll back with it.
        // Pattern from BountyService.PayInAsync.
        var claimed = await db.OeffentlicheFahndungen
            .Where(f => f.Id == row.Id && f.Status != PublicWantedStatus.Veroeffentlicht)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, PublicWantedStatus.Veroeffentlicht),
                cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Diese Ausschreibung ist bereits veröffentlicht.");
        }

        row.CaseNumber ??= await caseNumbers.NextAsync(db, CaseNumberPrefix, cancellationToken);

        var previousPhoto = row.PhotoFileName;
        await PhotoCopyAsync(db, row, cancellationToken);
        var freshPhoto = row.PhotoFileName != previousPhoto ? row.PhotoFileName : null;

        // the score is the default, not the verdict: an author who set the level keeps it, or every publication
        // would silently pull the poster back to whatever the sweep last computed
        if (!row.HazardLevelIsManual)
        {
            row.PublicHazardLevel = HazardLevelLogic.From(person.ThreatScore);
        }
        row.Status = PublicWantedStatus.Veroeffentlicht;
        row.PublishedAt = DateTime.UtcNow;
        row.PublishedById = actor.GetAgentId();
        row.RetractedAt = null;
        row.RetractedReason = null;
        row.CapturedAt = null;

        try
        {
            await SaveAndInvalidateAsync(db, cancellationToken, tx);
        }
        catch
        {
            // the copy is the one side effect a rollback cannot undo; without this every refused publish leaves an
            // unreferenced image behind, and a read-only account can repeat the attempt
            DeleteCopy(freshPhoto);
            throw;
        }

        // after the commit: a rollback would otherwise have thrown away the row that still points at this file
        DeleteCopy(previousPhoto is { Length: > 0 } && previousPhoto != row.PhotoFileName ? previousPhoto : null);

        // the hazard level is a factor of the inbox order of every tip on this notice
        await tipPriority.StampForNoticeAsync(row.Id, cancellationToken);

        // and after the commit for the same reason: a Discord post cannot be recalled, so it must never announce a
        // notice that never went live. Both entry points share this body, so it can neither fire twice nor fire for
        // a rank-2 request, which returns before ever getting here.
        await PushPublishedAsync(new PublicWantedCard(row.CaseNumber!, row.Kind, row.DisplayName, row.AliasText,
            row.PhotoFileName != null, row.PublicHazardLevel, row.PublishedAt, NoHints), cancellationToken);
    }

    /// <summary>Announces a fresh notice in the public channel as a card.</summary>
    /// <remarks>
    /// Takes a <see cref="PublicWantedCard"/> and nothing else on purpose — that record structurally cannot carry a
    /// PersonId, the internal NOOSE-P case number, a codename or a score, so the message cannot either. No accusation
    /// text: after a retraction the post is then a dead link rather than a standing allegation. Chips and money come
    /// from the outward board, and a notice the board does not list is not posted: the link would answer "not found"
    /// and a post cannot be recalled. PushWantedCardAsync swallows its own failures.
    /// </remarks>
    private async Task PushPublishedAsync(PublicWantedCard card, CancellationToken cancellationToken)
    {
        // fresh after commit
        var board = await GetBoardAsync(cancellationToken);
        if (board.CardFor(card.CaseNumber) is not { } listed)
        {
            return;
        }
        await discord.PushWantedCardAsync(NotificationType.PublicWantedPublished,
            WantedDiscordCard.Published(listed, board.BountyFor(card.CaseNumber)), cancellationToken);
    }

    /// <summary>The one save path of this table: nothing writes it without dropping the snapshot.</summary>
    /// <remarks>
    /// A file scan holds this shape (<c>PublicWantedCacheDisciplineTests</c>). Board and archive share one cache key
    /// for the same reason: two keys would double every one of these call sites and create a new failure class where
    /// one is dropped and the other stays.
    /// </remarks>
    private async Task SaveAndInvalidateAsync(AppDbContext? db, CancellationToken cancellationToken,
        IDbContextTransaction? transaction = null)
    {
        if (db is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        // commit before the drop: invalidating first lets a concurrent read cache a pre-commit board for 10 s
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        cache.Remove(CacheKey);
    }

    private void DeleteCopy(string? fileName)
    {
        if (fileName is not { Length: > 0 })
        {
            return;
        }
        try { publicFiles.Delete(fileName); } catch { /* best effort */ }
    }

    /// <summary>Copies the chosen file photo into the public folder; the notice never points at an internal file.</summary>
    private async Task PhotoCopyAsync(AppDbContext db, OeffentlicheFahndung row, CancellationToken cancellationToken)
    {
        // the last of the three layers: an item notice hangs off the owner's file, so a mugshot would resolve here
        if (WantedKinds.IsItem(row.Kind))
        {
            row.PhotoSourceId = null;
            row.PhotoFileName = null;
            row.PhotoContentType = null;
            return;
        }

        if (row.PhotoSourceId is not { Length: > 0 } sourceId)
        {
            row.PhotoFileName = null;
            row.PhotoContentType = null;
            return;
        }

        var source = await db.PersonPhotos
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == sourceId && f.PersonId == row.PersonId, cancellationToken);
        if (source is null)
        {
            row.PhotoSourceId = null;
            row.PhotoFileName = null;
            row.PhotoContentType = null;
            return;
        }

        try
        {
            await using var stream = peopleFiles.OpenRead(source.FileNameSaved);
            row.PhotoFileName = await publicFiles.SaveAsync(stream, source.ContentType, cancellationToken);
            row.PhotoContentType = source.ContentType;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // a missing source file is a poster without a picture, not a failed publication
            row.PhotoFileName = null;
            row.PhotoContentType = null;
        }
    }

    private async Task PhotoSourceSetAsync(AppDbContext db, OeffentlicheFahndung row, string? photoSourceId,
        CancellationToken cancellationToken)
    {
        if (photoSourceId is not { Length: > 0 })
        {
            row.PhotoSourceId = null;
            return;
        }

        var known = await db.PersonPhotos
            .AnyAsync(f => f.Id == photoSourceId && f.PersonId == row.PersonId, cancellationToken);
        row.PhotoSourceId = known ? photoSourceId : null;
    }

    private static void DecideRequest(Request request, bool approved, string? note, ClaimsPrincipal actor)
    {
        request.Status = approved ? RequestStatus.Approved : RequestStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    private static async Task<(Request Request, OeffentlicheFahndung Row)> PendingRequestAsync(
        AppDbContext db, string requestId, CancellationToken cancellationToken)
    {
        var request = await db.Requests.FirstOrDefaultAsync(a => a.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Antrag nicht gefunden.");
        if (request.Type != RequestType.Veroeffentlichung)
        {
            throw new InvalidOperationException("Ungültiger Antragstyp.");
        }
        if (request.Status != RequestStatus.Requested)
        {
            throw new InvalidOperationException("Der Antrag ist bereits entschieden.");
        }

        var row = await db.OeffentlicheFahndungen
            .FirstOrDefaultAsync(f => f.Id == request.PublicationWantedId, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        return (request, row);
    }

    /// <summary>Loads the file behind a notice and refuses a classified or deleted one, rank-independently.</summary>
    /// <remarks>Internal rather than private: the bounty service names this gate instead of copying its predicate.</remarks>
    internal static async Task<Person> RequirePublishableRecordAsync(AppDbContext db, OeffentlicheFahndung row,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (row.PersonId is null)
        {
            throw new InvalidOperationException(RecordNotFound);
        }

        // no IgnoreQueryFilters: a soft-deleted file blocks publication the same way a missing one does
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.PersonId, cancellationToken)
            ?? throw new InvalidOperationException(RecordNotFound);
        RequireNotClassified(person, actor);
        return person;
    }

    /// <summary>All three secrecy flags block, admin included; the message depends on who is asking.</summary>
    private static void RequireNotClassified(Person person, ClaimsPrincipal actor)
    {
        if (!person.IsClassified && !person.IsTRUClassified && !person.IsHRBClassified)
        {
            return;
        }
        // to someone who may not read classified records, the refusal reads exactly like a missing file — otherwise
        // pressing publish would tell a junior agent that the record has since become a Verschlusssache
        throw new InvalidOperationException(actor.MayClassifiedRead() ? Classified : RecordNotFound);
    }

    /// <summary>Everything a notice must satisfy before it may go outside.</summary>
    private static void RequirePublishableContent(OeffentlicheFahndung row)
    {
        if (string.IsNullOrWhiteSpace(row.DisplayName))
        {
            throw new InvalidOperationException("Die Ausschreibung braucht einen Anzeigenamen.");
        }
        RequirePublishableCharge(row.ChargeHtml);
    }

    /// <summary>Refuses, never strips: silently removing part of an accusation changes what it says.</summary>
    private static void RequirePublishableCharge(string? html)
    {
        var text = HtmlCleanup.PlainText(html);
        if (text.Length == 0 && !(html ?? string.Empty).Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Die Ausschreibung braucht einen Vorwurfstext.");
        }
        if (MentionParser.Parse(html).Count > 0)
        {
            throw new InvalidOperationException(
                "Der Vorwurfstext enthält eine Erwähnung; öffentlicher Text darf keine Akte verlinken.");
        }
        if ((html ?? string.Empty).Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Der Vorwurfstext enthält einen Platzhalter.");
        }
    }

    private static Task RetractRowAsync(AppDbContext db, OeffentlicheFahndung row, string reason,
        CancellationToken cancellationToken)
    {
        // case number, accusation and the photo copy stay: visibility hangs on the status, so going back online is one
        // click on the same address
        row.Status = PublicWantedStatus.Zurueckgezogen;
        row.RetractedAt = DateTime.UtcNow;
        row.RetractedReason = reason;

        return CloseOpenRequestsAsync(db, row.Id, cancellationToken);
    }

    private async Task<PublicWantedBoard> LoadAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out PublicWantedBoard? cached) && cached is not null)
        {
            return cached;
        }

        PublicWantedBoard board;
        try
        {
            var now = DateTime.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.OeffentlicheFahndungen
                .AsNoTracking()
                .Where(f => f.Status == PublicWantedStatus.Veroeffentlicht
                    && f.CaseNumber != null
                    && (f.ExpiresAt == null || f.ExpiresAt > now))
                .OrderByDescending(f => f.PublishedAt)
                .Select(f => new
                {
                    f.Id,
                    f.PersonId,
                    CaseNumber = f.CaseNumber!,
                    f.Kind,
                    f.DisplayName,
                    f.AliasText,
                    HasPhoto = f.PhotoFileName != null,
                    f.PublicHazardLevel,
                    f.PublishedAt,
                    f.ChargeHtml,
                    f.LastArea,
                    f.VehicleText,
                    f.ExpiresAt,
                    f.BountyIsCap,
                })
                .ToListAsync(cancellationToken);

            // Uncapped on purpose, the cap is applied AFTER the belt below: taking the newest hundred first lets
            // suppressed rows consume slots, so a visible capture behind them could never be shown at all.
            var captured = await db.OeffentlicheFahndungen
                .AsNoTracking()
                .Where(f => f.Status == PublicWantedStatus.Gefasst && f.CaseNumber != null && f.CapturedAt != null)
                .OrderByDescending(f => f.CapturedAt)
                .Select(f => new
                {
                    f.PersonId,
                    CaseNumber = f.CaseNumber!,
                    f.Kind,
                    f.DisplayName,
                    HasPhoto = f.PhotoFileName != null,
                    f.CapturedAt,
                })
                .ToListAsync(cancellationToken);

            // Counted apart from the cards above, which are capped for page weight: a figure that silently stops at
            // the display limit reads as completeness. Three columns, because that is all the belt and the item
            // switch need to decide what may be counted.
            var capturedAll = await db.OeffentlicheFahndungen
                .AsNoTracking()
                .Where(f => f.Status == PublicWantedStatus.Gefasst && f.CaseNumber != null && f.CapturedAt != null)
                .Select(f => new { CaseNumber = f.CaseNumber!, f.PersonId, f.Kind })
                .ToListAsync(cancellationToken);

            // The suppression belt, as a second query rather than a subquery: IgnoreQueryFilters is compilation-scoped,
            // so a subquery using it strips !IsDeleted from the OUTER set as well and a deleted notice goes live —
            // measured, not assumed. Standalone it can only do what it says. One call over all three lists, because
            // the archive and its counter answer to the same belt as the board.
            var open = await OpenRecordsAsync(db,
                rows.Select(r => r.PersonId).Concat(capturedAll.Select(r => r.PersonId)), cancellationToken);

            var visible = rows.Where(r => r.PersonId is null || open.Contains(r.PersonId)).ToList();
            // after the belt, so a suppressed notice does not even get its chips queried
            var hints = await HintsAsync(db, visible.Select(r => r.Id), cancellationToken);
            // and the money for the same reason — a notice nobody may see must not have its bounty summed either
            var bounties = await BountiesAsync(db, visible.Select(r => r.Id), cancellationToken);

            // Deduplicated before the dictionary: a throwing ToDictionary inside this try would blank the whole board.
            // Chosen once and reused below, so card, chips and money always describe the same notice — picking the row
            // a second time would let a duplicated case number pair one notice's card with another's bounty.
            var chosen = visible
                .GroupBy(r => r.CaseNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var byCaseNumber = chosen.ToDictionary(
                r => r.CaseNumber,
                r => new PublicWantedDetail(r.CaseNumber, r.Kind, r.DisplayName, r.AliasText, r.HasPhoto,
                    r.PublicHazardLevel, r.PublishedAt, r.ChargeHtml, r.LastArea, r.VehicleText, r.ExpiresAt,
                    hints.GetValueOrDefault(r.Id, NoHints)),
                StringComparer.OrdinalIgnoreCase);
            var cards = byCaseNumber.Values
                .OrderByDescending(f => f.PublishedAt)
                .Select(f => new PublicWantedCard(f.CaseNumber, f.Kind, f.DisplayName, f.AliasText, f.HasPhoto,
                    f.HazardLevel, f.PublishedAt, f.Hints))
                .ToList();

            var capturedByCaseNumber = captured
                .Where(r => r.PersonId is null || open.Contains(r.PersonId))
                .Select(r => new PublicWantedArchiveCard(r.CaseNumber, r.Kind, r.DisplayName, r.HasPhoto,
                    r.CapturedAt!.Value))
                .GroupBy(f => f.CaseNumber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // the archive is a record of recent results, not a dump: an anonymous page rendering every capture since
            // launch is a page-weight problem and a scraping target. Capped here, behind the belt, so only visible
            // rows fill the window.
            var archive = capturedByCaseNumber.Values
                .OrderByDescending(f => f.CapturedAt)
                .Take(ArchiveLimit)
                .ToList();

            // keyed by case number like everything else the outside addresses; a notice whose shares add up to
            // nothing gets no entry at all rather than an advertised "0 $"
            var bountyByCaseNumber = chosen
                .Where(r => bounties.ContainsKey(r.Id))
                .ToDictionary(r => r.CaseNumber, r => new PublicBounty(bounties[r.Id], r.BountyIsCap),
                    StringComparer.OrdinalIgnoreCase);

            // deduplicated by case number first, like the archive itself: the unique index makes that defensive,
            // and a counter that disagreed with the list it heads would be worse than either
            var capturedTotals = capturedAll
                .Where(r => r.PersonId is null || open.Contains(r.PersonId))
                .GroupBy(r => r.CaseNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .GroupBy(r => r.Kind)
                .ToDictionary(g => g.Key, g => g.Count());

            // stripped once per cache fill, not once per anonymous search request. Built from the same chosen rows
            // as the cards, so a notice and its searchable text can never describe different rows.
            var searchText = byCaseNumber.ToDictionary(
                e => e.Key,
                e => string.Join(" · ", new[]
                    {
                        e.Value.DisplayName, e.Value.AliasText, e.Value.LastArea, e.Value.VehicleText,
                        HtmlCleanup.PlainText(e.Value.ChargeHtml),
                    }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                StringComparer.OrdinalIgnoreCase);

            board = new PublicWantedBoard(cards, byCaseNumber, archive, capturedByCaseNumber, bountyByCaseNumber,
                capturedTotals, searchText);
        }
        catch (Exception)
        {
            // never cache a failure: the next request should try again rather than sit on an empty board
            return PublicWantedBoard.Empty;
        }

        cache.Set(CacheKey, board, CacheDuration);
        return board;
    }

    /// <summary>The active warning chips of the given notices, keyed by notice, in display order.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<PublicWantedHint>>> HintsAsync(
        AppDbContext db, IEnumerable<string> wantedIds, CancellationToken cancellationToken)
    {
        var ids = wantedIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<PublicWantedHint>>(StringComparer.Ordinal);
        }

        // IsActive is read here rather than copied onto the assignment: switching a warning off has to clear it from
        // every live notice within one cache window, without editing forty notices by hand
        var rows = await db.FahndungWarnhinweise
            .AsNoTracking()
            .Where(z => ids.Contains(z.FahndungId) && z.Warnhinweis!.IsActive)
            .OrderBy(z => z.Warnhinweis!.SortOrder).ThenBy(z => z.Warnhinweis!.Name)
            .Select(z => new { z.FahndungId, Label = z.Warnhinweis!.Name, z.Warnhinweis.Colour })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(z => z.FahndungId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PublicWantedHint>)g
                    .Select(z => new PublicWantedHint(z.Label, z.Colour ?? string.Empty)).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>The advertised bounty of the given notices, keyed by notice; nothing for a notice without money.</summary>
    /// <remarks>
    /// Summed here rather than kept as a column on the notice: a denormalised total drifts silently, and a wrong
    /// number about money is worse than a ten-second-old one. Only pledged and secured shares count — a share still
    /// awaiting a decision is not money yet, and advertising it would leak an open internal decision.
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> BountiesAsync(
        AppDbContext db, IEnumerable<string> wantedIds, CancellationToken cancellationToken)
    {
        var ids = wantedIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }

        var rows = await db.FahndungKopfgeldAnteile
            .AsNoTracking()
            .Where(k => ids.Contains(k.WantedId))
            // named, not spelled out again: the same rule decides the internal breakdown and the raise announcement
            .Where(BountyShares.Advertised)
            .GroupBy(k => k.WantedId)
            .Select(g => new { WantedId = g.Key, Total = g.Sum(k => k.Amount) })
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => r.Total > 0m)
            .ToDictionary(r => r.WantedId, r => r.Total, StringComparer.Ordinal);
    }

    /// <summary>Open publication requests whose notice still exists; count and list share it so they cannot disagree.</summary>
    private static IQueryable<Request> PendingRequests(AppDbContext db)
        => db.Requests.Where(a => a.Type == RequestType.Veroeffentlichung
            && a.Status == RequestStatus.Requested
            && db.OeffentlicheFahndungen.Any(f => f.Id == a.PublicationWantedId));

    /// <summary>Closes every open publication request of a notice; a request nobody can decide is worse than none.</summary>
    private static Task CloseOpenRequestsAsync(AppDbContext db, string wantedId, CancellationToken cancellationToken)
        => db.Requests
            .Where(a => a.Type == RequestType.Veroeffentlichung && a.PublicationWantedId == wantedId
                && a.Status == RequestStatus.Requested)
            .ForEachAsync(a => a.Status = RequestStatus.Rejected, cancellationToken);

    /// <summary>Of the given person files, the ones that may appear outside at all: alive, undeleted, no secrecy flag.</summary>
    private static async Task<HashSet<string>> OpenRecordsAsync(AppDbContext db, IEnumerable<string?> personIds,
        CancellationToken cancellationToken)
    {
        var ids = Ids(personIds);
        if (ids.Count == 0)
        {
            return Empty();
        }

        return (await db.People
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id) && !p.IsDeleted
                    && !p.IsClassified && !p.IsTRUClassified && !p.IsHRBClassified)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Of the given person files, the ones this actor may read.</summary>
    /// <remarks>
    /// A deleted file still resolves its secrecy level here, so a leftover notice stays manageable by whoever may see
    /// that level. A file that does not resolve at all is not visible — fail closed.
    /// </remarks>
    private static async Task<HashSet<string>> VisibleRecordsAsync(AppDbContext db, IEnumerable<string?> personIds,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var ids = Ids(personIds);
        if (ids.Count == 0)
        {
            return Empty();
        }

        var scope = ViewerScope.From(actor);
        var rows = await db.People
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified })
            .ToListAsync(cancellationToken);

        return rows
            .Where(p => RecordVisibility.IsVisible(scope, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True when the notice's record is readable for the actor; a notice without a record has no gate yet.</summary>
    private static async Task<bool> IsRecordVisibleAsync(AppDbContext db, string? personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
        => personId is null
            || (await VisibleRecordsAsync(db, [personId], actor, cancellationToken)).Contains(personId);

    private static List<string> Ids(IEnumerable<string?> personIds)
        => personIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToList();

    private static HashSet<string> Empty() => new(StringComparer.Ordinal);

    private static string ChargeFrom(string? wantedReason)
    {
        var bare = MentionParser.Strip(wantedReason);
        return bare.Length == 0 ? string.Empty : HtmlCleanup.Clean($"<p>{WebUtility.HtmlEncode(bare)}</p>");
    }

    /// <summary>What a vehicle is called outside: the plate when there is one, otherwise the model.</summary>
    private static string VehicleSubject(string designation, string? plate)
        => string.IsNullOrWhiteSpace(plate) ? designation : plate.Trim();

    /// <summary>How a source reads in the picker; the same shape the file's own profile list uses.</summary>
    private static string VehicleLabel(string designation, string? plate)
        => string.IsNullOrWhiteSpace(plate) ? designation : $"{designation} – {plate}";

    /// <summary>Kind and display name together — one live notice per subject of a file.</summary>
    private static string SubjectKey(PublicWantedKind kind, string displayName)
        => $"{(int)kind}|{displayName.Trim()}";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? CutOrNull(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : Cut(trimmed, max);
    }
}
