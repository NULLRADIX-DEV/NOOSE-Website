using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ILawService" />
public class LawService(IDbContextFactory<AppDbContext> dbFactory, IPublicLawService publicLaws) : ILawService
{
    private const int MaxAbbreviationLength = 128;
    private const int MaxNameLength = 256;
    private const int MaxDescriptionLength = 1024;
    private const int MaxParagraphLength = 32;
    private const int MaxTitleLength = 256;
    private const int MaxSectionLength = 256;
    private const int MaxSentenceLength = 512;

    // the abbreviation is a path segment of the book page
    private static readonly char[] ForbiddenAbbreviationChars = ['/', '\\', '?', '#', '%'];

    public async Task<List<Law>> GetListAsync(CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, partnerAgency, partnerAgentId)
            .OrderBy(g => g.LawBook).ThenBy(g => g.SortOrder).ThenBy(g => g.Paragraph).ThenBy(g => g.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<Law?> GetAsync(string id, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // partners: only released laws
        if (partnerAgency is { } agency && !await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Law), id, agency, partnerAgentId, cancellationToken))
        {
            return null;
        }
        return await db.Laws.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
    }

    public async Task<List<Law>> SearchAsync(string? searchText, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Laws.AsQueryable();

        var s = searchText?.Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            query = query.Where(g => g.Title.Contains(s) || g.Paragraph.Contains(s) || g.LawBook.Contains(s));
        }

        return await query
            .OrderBy(g => g.LawBook).ThenBy(g => g.SortOrder).ThenBy(g => g.Paragraph)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<Law> CreateAsync(LawInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);
        var text = Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var book = await EnsureBookAsync(db, input.LawBook.Trim(), cancellationToken);
        var law = new Law
        {
            LawBook = book,
            Paragraph = input.Paragraph.Trim(),
            Title = input.Title.Trim(),
            Text = text,
            Sentence = NullIfBlank(input.Sentence),
            Section = NullIfBlank(input.Section),
            SortOrder = input.SortOrder > 0 ? input.SortOrder : await NextSortOrderAsync(db, book, cancellationToken),
        };

        db.Laws.Add(law);
        await db.SaveChangesAsync(cancellationToken);
        // after every write, not only after an obviously released one: a corrected or deleted paragraph would
        // otherwise stand outside for a whole cache window, and "this one cannot be public" is a fact about today
        await publicLaws.InvalidatePublicViewAsync();
        return law;
    }

    public async Task RefreshAsync(string id, LawInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);
        var text = Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var law = await db.Laws.FirstOrDefaultAsync(g => g.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Paragraf nicht gefunden.");

        var book = await EnsureBookAsync(db, input.LawBook.Trim(), cancellationToken);
        var moved = !string.Equals(law.LawBook, book, StringComparison.OrdinalIgnoreCase);
        law.LawBook = book;
        law.Paragraph = input.Paragraph.Trim();
        law.Title = input.Title.Trim();
        law.Text = text;
        law.Sentence = NullIfBlank(input.Sentence);
        law.Section = NullIfBlank(input.Section);
        if (input.SortOrder > 0)
        {
            law.SortOrder = input.SortOrder;
        }
        else if (moved)
        {
            law.SortOrder = await NextSortOrderAsync(db, book, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var law = await db.Laws.FirstOrDefaultAsync(g => g.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Paragraf nicht gefunden.");

        // withdrawn from the public page on the way out: a restored paragraph comes back internal
        law.IsPublic = false;
        // soft-delete (interceptor rewrites Remove)
        db.Laws.Remove(law);
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    public async Task<List<Law>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Laws.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.IsDeleted)
            .OrderByDescending(l => l.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var law = await db.Laws.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == id && l.IsDeleted, cancellationToken);
        if (law is null)
        {
            return;
        }

        var key = law.LawBook.ToLower();
        var bookIsActive = await db.LawBooks.AnyAsync(b => b.Abbreviation.ToLower() == key, cancellationToken);
        if (!bookIsActive && await db.LawBooks.IgnoreQueryFilters().AnyAsync(b => b.Abbreviation.ToLower() == key && b.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Das Gesetzbuch „{law.LawBook}“ liegt im Papierkorb. Stelle zuerst das Gesetzbuch wieder her.");
        }
        var paragraph = law.Paragraph.ToLower();
        if (await db.Laws.AnyAsync(l => l.LawBook.ToLower() == key && l.Paragraph.ToLower() == paragraph, cancellationToken))
        {
            throw new InvalidOperationException(
                $"{law.LawBook} {law.Paragraph} gibt es inzwischen wieder. Lösche oder benenne den bestehenden Paragrafen zuerst um.");
        }
        if (!bookIsActive)
        {
            law.LawBook = await EnsureBookAsync(db, law.LawBook, cancellationToken);
        }

        law.IsDeleted = false;
        law.DeletedAt = null;
        law.DeletedById = null;
        law.IsPublic = false;
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    public async Task<List<LawBookSummary>> GetBooksAsync(CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var counts = await Visible(db, partnerAgency, partnerAgentId)
            .GroupBy(l => l.LawBook)
            .Select(g => new { Book = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var catalog = await db.LawBooks.AsNoTracking().ToListAsync(cancellationToken);

        // case folded like the database collation, so "BtMG" and "BtmG" stay one book
        var countByBook = counts
            .GroupBy(c => c.Book, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.OrdinalIgnoreCase);

        var books = catalog
            .Select(b => Summary(b, countByBook.GetValueOrDefault(b.Abbreviation)))
            .ToList();
        // a book only named by its paragraphs still gets a card
        var listed = new HashSet<string>(catalog.Select(b => b.Abbreviation), StringComparer.OrdinalIgnoreCase);
        books.AddRange(countByBook
            .Where(c => !listed.Contains(c.Key))
            .Select(c => new LawBookSummary(null, c.Key, c.Key, null, 0, c.Value)));

        if (partnerAgency is not null)
        {
            books.RemoveAll(b => b.ParagraphCount == 0);
        }
        return books
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Abbreviation, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<LawBookContent?> GetBookAsync(string abbreviation, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        var key = abbreviation?.Trim().ToLower();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var book = await db.LawBooks.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Abbreviation.ToLower() == key, cancellationToken);
        var paragraphs = await Visible(db, partnerAgency, partnerAgentId)
            .Where(l => l.LawBook.ToLower() == key)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Paragraph).ThenBy(l => l.Title)
            .ToListAsync(cancellationToken);

        // a partner learns nothing about a book none of whose paragraphs were released to them
        if (paragraphs.Count == 0 && (book is null || partnerAgency is not null))
        {
            return null;
        }
        var summary = book is not null
            ? Summary(book, paragraphs.Count)
            : new LawBookSummary(null, paragraphs[0].LawBook, paragraphs[0].LawBook, null, 0, paragraphs.Count);
        return new LawBookContent(summary, paragraphs);
    }

    public async Task<LawBook> CreateBookAsync(LawBookInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);
        var (abbreviation, name, description) = ValidateBook(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireFreeAbbreviationAsync(db, abbreviation, null, cancellationToken);
        var book = new LawBook
        {
            Abbreviation = abbreviation,
            Name = name,
            Description = description,
            SortOrder = input.SortOrder > 0 ? input.SortOrder : await NextBookSortOrderAsync(db, cancellationToken),
        };
        db.LawBooks.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
        return book;
    }

    public async Task UpdateBookAsync(string id, LawBookInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);
        var (abbreviation, name, description) = ValidateBook(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var book = await db.LawBooks.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Gesetzbuch nicht gefunden.");
        await RequireFreeAbbreviationAsync(db, abbreviation, id, cancellationToken);

        if (!string.Equals(book.Abbreviation, abbreviation, StringComparison.Ordinal))
        {
            // deleted paragraphs too, or a restore would land in a book that no longer exists; tracked, so the
            // search stems and the audit follow
            var old = book.Abbreviation.ToLower();
            var paragraphs = await db.Laws.IgnoreQueryFilters()
                .Where(l => l.LawBook.ToLower() == old)
                .ToListAsync(cancellationToken);
            foreach (var law in paragraphs)
            {
                law.LawBook = abbreviation;
            }
        }
        book.Abbreviation = abbreviation;
        book.Name = name;
        book.Description = description;
        if (input.SortOrder > 0)
        {
            book.SortOrder = input.SortOrder;
        }
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    public async Task DeleteBookAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var book = await db.LawBooks.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Gesetzbuch nicht gefunden.");
        var key = book.Abbreviation.ToLower();
        var remaining = await db.Laws.CountAsync(l => l.LawBook.ToLower() == key, cancellationToken);
        if (remaining > 0)
        {
            throw new InvalidOperationException(
                $"„{book.Abbreviation}“ enthält noch {remaining} Paragrafen. Lösche oder verschiebe sie zuerst.");
        }

        db.LawBooks.Remove(book);
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    public async Task<List<LawBook>> GetBookTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.LawBooks.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.IsDeleted)
            .OrderByDescending(b => b.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreBookAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireLawWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var book = await db.LawBooks.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == id && b.IsDeleted, cancellationToken);
        if (book is null)
        {
            return;
        }
        await RequireFreeAbbreviationAsync(db, book.Abbreviation, id, cancellationToken);
        book.IsDeleted = false;
        book.DeletedAt = null;
        book.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
        await publicLaws.InvalidatePublicViewAsync();
    }

    // write check ahead of the rank: the read-only supervision carries Director and would otherwise reach the save
    private static void RequireLawWrite(ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
    }

    private static IQueryable<Law> Visible(AppDbContext db, PartnerAgency? partnerAgency, string? partnerAgentId)
        => partnerAgency is { } agency ? db.Laws.OnlyPartnerVisible(db, agency, partnerAgentId) : db.Laws.AsQueryable();

    private static LawBookSummary Summary(LawBook book, int count)
        => new(book.Id, book.Abbreviation, book.Name, book.Description, book.SortOrder, count);

    /// <summary>Catalog spelling of the book, created on first use.</summary>
    private static async Task<string> EnsureBookAsync(AppDbContext db, string abbreviation, CancellationToken cancellationToken)
    {
        var key = abbreviation.ToLower();
        var existing = await db.LawBooks
            .Where(b => b.Abbreviation.ToLower() == key)
            .Select(b => b.Abbreviation)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
        if (abbreviation.IndexOfAny(ForbiddenAbbreviationChars) >= 0)
        {
            throw new InvalidOperationException("Das Kürzel darf weder / noch \\, ?, # oder % enthalten.");
        }
        db.LawBooks.Add(new LawBook
        {
            Abbreviation = abbreviation,
            Name = abbreviation,
            SortOrder = await NextBookSortOrderAsync(db, cancellationToken),
        });
        return abbreviation;
    }

    private static async Task<int> NextSortOrderAsync(AppDbContext db, string book, CancellationToken cancellationToken)
    {
        var key = book.ToLower();
        var max = await db.Laws.Where(l => l.LawBook.ToLower() == key)
            .MaxAsync(l => (int?)l.SortOrder, cancellationToken);
        return (max ?? 0) + 1;
    }

    private static async Task<int> NextBookSortOrderAsync(AppDbContext db, CancellationToken cancellationToken)
        => (await db.LawBooks.MaxAsync(b => (int?)b.SortOrder, cancellationToken) ?? 0) + 1;

    private static async Task RequireFreeAbbreviationAsync(AppDbContext db, string abbreviation, string? exceptId, CancellationToken cancellationToken)
    {
        var key = abbreviation.ToLower();
        if (await db.LawBooks.AnyAsync(b => b.Abbreviation.ToLower() == key && b.Id != exceptId, cancellationToken))
        {
            throw new InvalidOperationException($"Ein Gesetzbuch mit dem Kürzel „{abbreviation}“ gibt es schon.");
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Checks the required fields and returns the sanitized text.</summary>
    private static string Validate(LawInput input)
    {
        var text = HtmlCleanup.Clean(input.Text).Trim();
        if (string.IsNullOrWhiteSpace(input.LawBook)
            || string.IsNullOrWhiteSpace(input.Paragraph)
            || string.IsNullOrWhiteSpace(input.Title)
            // an empty editor still sends <p><br></p>
            || HtmlCleanup.PlainText(text).Length == 0)
        {
            throw new InvalidOperationException("Gesetzbuch, Paragraf, Titel und Text sind Pflichtfelder.");
        }
        if (input.LawBook.Trim().Length > MaxAbbreviationLength
            || input.Paragraph.Trim().Length > MaxParagraphLength
            || input.Title.Trim().Length > MaxTitleLength
            || (input.Section?.Trim().Length ?? 0) > MaxSectionLength
            || (input.Sentence?.Trim().Length ?? 0) > MaxSentenceLength)
        {
            throw new InvalidOperationException(
                $"Zu lang: Paragraf höchstens {MaxParagraphLength}, Titel und Abschnitt höchstens {MaxTitleLength}, Strafmaß höchstens {MaxSentenceLength} Zeichen.");
        }
        return text;
    }

    private static (string Abbreviation, string Name, string? Description) ValidateBook(LawBookInput input)
    {
        var abbreviation = input.Abbreviation?.Trim() ?? string.Empty;
        var name = input.Name?.Trim() ?? string.Empty;
        if (abbreviation.Length == 0 || name.Length == 0)
        {
            throw new InvalidOperationException("Kürzel und Name sind Pflichtfelder.");
        }
        if (abbreviation.IndexOfAny(ForbiddenAbbreviationChars) >= 0)
        {
            throw new InvalidOperationException("Das Kürzel darf weder / noch \\, ?, # oder % enthalten.");
        }
        var description = NullIfBlank(input.Description);
        if (abbreviation.Length > MaxAbbreviationLength || name.Length > MaxNameLength
            || (description?.Length ?? 0) > MaxDescriptionLength)
        {
            throw new InvalidOperationException(
                $"Zu lang: Kürzel höchstens {MaxAbbreviationLength}, Name höchstens {MaxNameLength}, Beschreibung höchstens {MaxDescriptionLength} Zeichen.");
        }
        return (abbreviation, name, description);
    }
}
