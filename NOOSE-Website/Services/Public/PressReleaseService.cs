using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IPressReleaseService" />
public class PressReleaseService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    ICaseNumberService caseNumbers,
    IDiscordWebhookService discord,
    IMemoryCache cache) : IPressReleaseService
{
    private const string CacheKey = "Pressemitteilungen";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Public file number prefix; PM was free against the prefixes already in use.</summary>
    public const string CaseNumberPrefix = "PM";

    private const string NotFound = "Diese Pressemitteilung existiert nicht mehr.";

    public async Task<PublicPressSnapshot> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        // the module switch is checked outside the content cache: caching "module is off" as an empty snapshot would
        // keep the pages dark for a whole cache window after someone turns the module back on
        if (!await modules.IsEnabledAsync(PublicModules.Press, cancellationToken))
        {
            return PublicPressSnapshot.Empty;
        }
        return await LoadAsync(cancellationToken);
    }

    public async Task<PublicPressView?> GetByCaseNumberAsync(string? caseNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            return null;
        }
        caseNumber = CaseNumbers.Normalize(caseNumber);
        // its own row read, NOT a lookup in the hub snapshot: that list is capped at HubLimit, so an older release
        // would answer "not published" at the permanent address its own Discord post points at
        if (!await modules.IsEnabledAsync(PublicModules.Press, cancellationToken))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Pressemitteilungen
            .AsNoTracking()
            .Where(p => p.Status == PressReleaseStatus.Veroeffentlicht && p.CaseNumber == caseNumber)
            .Select(p => new PublicPressView(p.CaseNumber!, p.ContentTitle ?? string.Empty,
                p.ContentTeaser ?? string.Empty, p.ContentHtml ?? string.Empty, p.PublishedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PressEdit>> GetAllAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // projected rather than Include'd: only the codename is wanted, and pulling the whole identity user would
        // carry the publisher's clear name into a panel the read-only supervision renders
        // The divergence flag is decided in memory, not in SQL: a comparison inside the projection runs under the
        // column collation, and the server default is case- AND accent-insensitive, so correcting a capital letter
        // or an umlaut in a published title read as "nothing to publish". Title and teaser are bounded (200/400),
        // so pulling them costs nothing; the body is compared by length plus SQL equality, because PressEdit
        // deliberately carries no HTML - a list with every draft attached would be megabytes per render. Residual
        // gap, knowingly: a body edit that changes only case or accents AND keeps the exact same length.
        var rows = await db.Pressemitteilungen
            .AsNoTracking()
            .OrderByDescending(p => p.PublishedAt ?? p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.CaseNumber,
                p.Title,
                p.Teaser,
                p.Status,
                BodyDiffers = (p.DraftHtml ?? string.Empty) != (p.ContentHtml ?? string.Empty)
                    || (p.DraftHtml ?? string.Empty).Length != (p.ContentHtml ?? string.Empty).Length,
                ContentTitle = p.ContentTitle ?? string.Empty,
                ContentTeaser = p.ContentTeaser ?? string.Empty,
                p.PublishedAt,
                PublishedByName = p.PublishedBy!.Codename,
                p.DiscordPushedAt,
                Touched = p.ModifiedAt ?? p.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(p => new PressEdit(
                p.Id,
                p.CaseNumber,
                p.Title,
                p.Teaser,
                p.Status,
                p.BodyDiffers
                    || !string.Equals(p.Title, p.ContentTitle, StringComparison.Ordinal)
                    || !string.Equals(p.Teaser, p.ContentTeaser, StringComparison.Ordinal),
                p.PublishedAt,
                p.PublishedByName,
                p.DiscordPushedAt,
                p.Touched))
            .ToList();
    }

    public async Task<PressDraft?> GetDraftAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Pressemitteilungen
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PressDraft(p.Title, p.Teaser, p.DraftHtml ?? string.Empty))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string> SaveDraftAsync(PressInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePressWrite(actor);

        var title = (input.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Die Pressemitteilung braucht einen Titel.");
        }
        var teaser = (input.Teaser ?? string.Empty).Trim();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        Pressemitteilung row;
        if (string.IsNullOrWhiteSpace(input.Id))
        {
            row = new Pressemitteilung();
            db.Pressemitteilungen.Add(row);
        }
        else
        {
            var id = input.Id;
            row = await db.Pressemitteilungen.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                  ?? throw new InvalidOperationException(NotFound);
        }

        row.Title = Cut(title, PressRules.MaxTitle);
        row.Teaser = Cut(teaser, PressRules.MaxTeaser);
        // null leaves the draft alone, an empty string clears it. Without that split a caller that only changes the
        // title would wipe the body, and the loss would be silent
        if (input.DraftHtml is not null)
        {
            row.DraftHtml = HtmlCleanup.Clean(input.DraftHtml);
        }

        await SaveAndInvalidateAsync(db, cancellationToken);
        return row.Id;
    }

    public async Task PublishAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePressWrite(actor);
        await modules.RequireEnabledAsync(PublicModules.Press, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Pressemitteilungen.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                  ?? throw new InvalidOperationException(NotFound);

        if (row.Teaser.Length == 0)
        {
            throw new InvalidOperationException("Die Pressemitteilung braucht einen Teaser für die Übersicht.");
        }

        // clean again rather than trust the stored draft: publishing is the moment the HTML becomes reachable anonymously
        var html = HtmlCleanup.Clean(row.DraftHtml);
        // empty means neither text nor picture; a release that is only an image is content, and PlainText alone
        // would have rejected it as an empty draft
        if (HtmlCleanup.PlainText(html).Length == 0 && !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ein leerer Entwurf lässt sich nicht veröffentlichen.");
        }

        // unconditional: the case-number service refuses to issue a number without an enclosing transaction
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the MINT, not the status: re-publishing is a legitimate flow here - it is how a corrected draft
        // goes out - so the row must stay publishable. What must happen once is the number, because two tabs both
        // saw CaseNumber == null, both minted, and the losing number was announced to Discord while no row carried
        // it. A row that already has one skips the claim and keeps it. Pattern from BountyService.PayInAsync.
        if (row.CaseNumber is null)
        {
            var minted = await caseNumbers.NextAsync(db, CaseNumberPrefix, cancellationToken);
            var claimed = await db.Pressemitteilungen
                .Where(p => p.Id == row.Id && p.CaseNumber == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CaseNumber, minted), cancellationToken);
            if (claimed == 0)
            {
                throw new InvalidOperationException("Diese Mitteilung hat soeben eine Nummer erhalten.");
            }
            row.CaseNumber = minted;
        }
        row.DraftHtml = html;
        row.ContentHtml = html;
        row.ContentTitle = row.Title;
        row.ContentTeaser = row.Teaser;
        row.Status = PressReleaseStatus.Veroeffentlicht;
        // minted once, like the case number, and for the same reason: the date is part of the dated statement the
        // outside world reads, and the hub sorts by it. Stamping it again would move a March release to today's top
        // spot because somebody fixed a typo in it. Retracting clears both, so a release that was off the air and
        // goes out again is honestly dated anew
        row.PublishedAt ??= DateTime.UtcNow;
        row.PublishedById ??= actor.GetAgentId();

        // stamped before the push, so the guarantee is at-most-once: a dead webhook loses one announcement, while
        // stamping afterwards would post twice whenever the process died in between. A message cannot be recalled,
        // a missing one can be seen — the panel shows whether a release was announced.
        // and claimed rather than merely stamped: two publishes both read null, both stamped, one save won and
        // BOTH pushed. The claim is what makes at-most-once true when they overlap.
        var announce = false;
        if (row.DiscordPushedAt is null)
        {
            var stamped = DateTime.UtcNow;
            announce = await db.Pressemitteilungen
                .Where(p => p.Id == row.Id && p.DiscordPushedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DiscordPushedAt, stamped), cancellationToken) == 1;
            if (announce)
            {
                row.DiscordPushedAt = stamped;
            }
        }
        var card = new PublicPressCard(row.CaseNumber, row.ContentTitle!, row.ContentTeaser!, row.PublishedAt);

        // the audit interceptor stamps the row itself, and the Status change reads as the action; no manual row needed
        await SaveAndInvalidateAsync(db, cancellationToken, tx);

        // after the commit: a Discord message cannot be recalled, and a rolled-back publication must not have announced
        if (announce)
        {
            await PushPublishedAsync(card, cancellationToken);
        }
    }

    public async Task RetractAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePressWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Pressemitteilungen.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                  ?? throw new InvalidOperationException(NotFound);

        // no module gate, here or in Delete: publishing needs a live module, depublishing never — otherwise the kill
        // switch would make retracting impossible, exactly the wrong way round.
        // ContentHtml and the case number stay: visibility hangs on Status alone, so going back online is one click on
        // the same address, and a counter number is never reused anyway. The publication date does not: it says since
        // when this stands outside, and after a retraction the answer is "it does not"
        row.Status = PressReleaseStatus.Entwurf;
        row.PublishedAt = null;
        row.PublishedById = null;

        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePressWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Pressemitteilungen.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                  ?? throw new InvalidOperationException(NotFound);

        if (row.Status == PressReleaseStatus.Veroeffentlicht)
        {
            // otherwise deleting would be a silent depublication with no reason on the record
            throw new InvalidOperationException("Zuerst zurückziehen, dann löschen.");
        }

        db.Pressemitteilungen.Remove(row);
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public async Task CreateCaptureDraftAsync(PublicWantedCard notice, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the guard that authorised the capture, not RequirePressWrite: RequirePublicWantedWrite has no rank floor, so
        // any write-capable agent may close a notice, and demanding leadership here would silently drop the automatism
        // for every capture below rank 4 — the hook is swallowed by design, so nobody would be told. The draft stays
        // internal and only leadership can publish it, which is exactly the intent.
        Permission.RequirePublicWantedWrite(actor);

        // the stored choice alone, not RequireEnabledAsync: the kill switch is a temporary outage of the public pages
        // and no reason to lose an internal draft. With the module off for good no draft is written at all — a draft
        // exists to be published, and one per capture that nobody can publish is noise, not a safety net
        var snapshot = await modules.GetAsync(cancellationToken);
        if (snapshot.Find(PublicModules.Press)?.IsEnabled != true)
        {
            return;
        }

        var (title, teaser, html) = PressDraftText.ForCapture(notice);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Pressemitteilungen.Add(new Pressemitteilung
        {
            Title = Cut(title, PressRules.MaxTitle),
            Teaser = Cut(teaser, PressRules.MaxTeaser),
            DraftHtml = HtmlCleanup.Clean(html),
        });

        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    public async Task<List<Pressemitteilung>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Pressemitteilungen
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePressWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Pressemitteilungen
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Diese Pressemitteilung liegt nicht im Papierkorb.");

        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        // a restore comes back as a draft: nothing goes public again as a side effect of undoing a delete. The address
        // needs no re-check, unlike an editorial page: the case number is unique and never reused
        row.Status = PressReleaseStatus.Entwurf;

        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    private async Task<PublicPressSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out PublicPressSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        PublicPressSnapshot snapshot;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // a published row without a case number cannot exist (publishing mints one), but the filter is what makes
            // the dictionary key non-null rather than an assumption about it. No GroupBy either, unlike the editorial
            // pages: their slug carries no unique index because a deleted page keeps its address, while a counter
            // number is never reused — so the index rules duplicates out and ToDictionary cannot throw.
            var rows = await db.Pressemitteilungen
                .AsNoTracking()
                .Where(p => p.Status == PressReleaseStatus.Veroeffentlicht && p.CaseNumber != null)
                .OrderByDescending(p => p.PublishedAt)
                .Take(PressRules.HubLimit)
                .Select(p => new PublicPressView(p.CaseNumber!, p.ContentTitle ?? string.Empty,
                    p.ContentTeaser ?? string.Empty, p.ContentHtml ?? string.Empty, p.PublishedAt))
                .ToListAsync(cancellationToken);

            snapshot = new PublicPressSnapshot(
                Cards: rows.Select(p => new PublicPressCard(p.CaseNumber, p.Title, p.Teaser, p.PublishedAt)).ToList(),
                ByCaseNumber: rows.ToDictionary(p => p.CaseNumber, p => p, StringComparer.OrdinalIgnoreCase),
                // stripped once per cache fill, not once per anonymous search request
                SearchText: rows.ToDictionary(p => p.CaseNumber, p => HtmlCleanup.PlainText(p.Html),
                    StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // an unreachable database shows no releases rather than a stack trace to an anonymous visitor
            return PublicPressSnapshot.Empty;
        }

        cache.Set(CacheKey, snapshot, CacheDuration);
        return snapshot;
    }

    /// <summary>Announces a fresh release in the public channel.</summary>
    /// <remarks>
    /// Takes a <see cref="PublicPressCard"/> and nothing else, same argument as the wanted push: that record cannot
    /// carry an author, a record or an internal id, so the message cannot either. Fire and forget — PushCustomAsync
    /// swallows its own failures, and a dead webhook must never fail a publication.
    /// </remarks>
    private Task PushPublishedAsync(PublicPressCard card, CancellationToken cancellationToken)
        => discord.PushCustomAsync(NotificationType.PublicPressPublished,
            $"📰 **Neue Pressemitteilung** — {card.Title} ({card.CaseNumber})",
            $"/presse/{card.CaseNumber}", cancellationToken);

    /// <summary>The one save path of this table: nothing writes it without dropping the snapshot.</summary>
    /// <remarks>A file scan holds this shape (<c>PressCacheDisciplineTests</c>).</remarks>
    private async Task SaveAndInvalidateAsync(AppDbContext db, CancellationToken cancellationToken,
        IDbContextTransaction? transaction = null)
    {
        await db.SaveChangesAsync(cancellationToken);
        // commit before the drop: invalidating first lets a concurrent read cache a pre-commit snapshot for 10 s
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        cache.Remove(CacheKey);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
