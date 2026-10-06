using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IPublicLawService" />
public class PublicLawService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    IMemoryCache cache) : IPublicLawService
{
    private const string CacheKey = "OeffentlichesRecht";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<PublicLawSnapshot> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        // the module switch is checked outside the content cache: caching "module is off" as an empty snapshot would
        // keep the page dark for a whole cache window after someone turns the module back on
        if (!await modules.IsEnabledAsync(PublicModules.Law, cancellationToken))
        {
            return PublicLawSnapshot.Empty;
        }
        return await LoadAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LawReleaseRow>> GetAllAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the panel decides what goes out, so it lists every paragraph — including the ones that stay in
        return await db.Laws
            .AsNoTracking()
            .OrderBy(l => l.LawBook).ThenBy(l => l.SortOrder).ThenBy(l => l.Paragraph).ThenBy(l => l.Title)
            .Select(l => new LawReleaseRow(l.Id, l.LawBook, l.Paragraph, l.Title, l.IsPublic))
            .ToListAsync(cancellationToken);
    }

    public async Task SetPublicAsync(string lawId, bool isPublic, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLawReleaseWrite(actor);
        if (isPublic)
        {
            // releasing needs a live module, withdrawing never — otherwise the kill switch would pin a paragraph
            // outside, exactly the wrong way round
            await modules.RequireEnabledAsync(PublicModules.Law, cancellationToken);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var law = await db.Laws.FirstOrDefaultAsync(l => l.Id == lawId, cancellationToken)
                  ?? throw new InvalidOperationException("Dieser Paragraf existiert nicht mehr.");

        law.IsPublic = isPublic;
        await SaveAndInvalidateAsync(db, cancellationToken);
    }

    // through the same choke point with nothing to save, so this file keeps exactly one cache.Remove and the scan
    // stays a scan rather than a list of allowed exceptions
    public Task InvalidatePublicViewAsync() => SaveAndInvalidateAsync(null, CancellationToken.None);

    private async Task<PublicLawSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out PublicLawSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        PublicLawSnapshot snapshot;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // read here rather than through ILawService: an internal list service answers a different question and
            // may widen without anybody thinking about the outside
            var rows = await db.Laws
                .AsNoTracking()
                .Where(l => l.IsPublic)
                .OrderBy(l => l.LawBook).ThenBy(l => l.SortOrder).ThenBy(l => l.Paragraph).ThenBy(l => l.Title)
                .Select(l => new { l.LawBook, l.Paragraph, l.Title, l.Text, l.Sentence })
                .ToListAsync(cancellationToken);
            // only name and order leave the catalog, and only above a released paragraph
            var catalog = (await db.LawBooks
                    .AsNoTracking()
                    .Select(b => new { b.Abbreviation, b.Name, b.SortOrder })
                    .ToListAsync(cancellationToken))
                .GroupBy(b => b.Abbreviation, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            snapshot = new PublicLawSnapshot(rows
                .GroupBy(r => r.LawBook, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Group: g, Book: catalog.GetValueOrDefault(g.Key)))
                .OrderBy(x => x.Book?.SortOrder ?? int.MaxValue)
                .ThenBy(x => x.Group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new PublicLawBook(
                    x.Group.Key,
                    x.Group.Select(r => Entry(r.Paragraph, r.Title, r.Text, r.Sentence)).ToList(),
                    x.Book is { } b && !string.Equals(b.Name, x.Group.Key, StringComparison.OrdinalIgnoreCase) ? b.Name : null))
                .ToList());
        }
        catch (Exception)
        {
            // an unreachable database shows no paragraphs rather than a stack trace to an anonymous visitor
            return PublicLawSnapshot.Empty;
        }

        cache.Set(CacheKey, snapshot, CacheDuration);
        return snapshot;
    }

    // mentions stripped before the markup goes out: the public page never resolves them, so a token would print raw
    private static PublicLawEntry Entry(string paragraph, string title, string text, string? sentence)
    {
        var html = HtmlCleanup.Clean(MentionParser.Strip(text));
        return new PublicLawEntry(paragraph, title, html, sentence, HtmlCleanup.PlainText(html));
    }

    /// <summary>The one save path of this service; the law table has another writer, which drops the snapshot too.</summary>
    /// <remarks>
    /// <see cref="ILawService"/> edits and deletes paragraphs, so it calls <see cref="InvalidatePublicViewAsync"/>
    /// after every write — otherwise a corrected or deleted paragraph would stand outside for a whole cache window.
    /// A file scan holds that (<c>PublicLawCacheDisciplineTests</c>).
    /// </remarks>
    private async Task SaveAndInvalidateAsync(AppDbContext? db, CancellationToken cancellationToken)
    {
        if (db is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        cache.Remove(CacheKey);
    }
}
