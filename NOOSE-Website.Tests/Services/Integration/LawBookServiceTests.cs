using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Law books, rich-text paragraphs and the trash, with the audit interceptor attached as in production.</summary>
public sealed class LawBookServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

    // Director rank, but read-only: the write check must come first
    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    // the interceptor turns Remove into a soft delete, which the trash tests need
    private static LawService NewService(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new LawService(factory, new PublicLawService(factory, new PublicModuleService(factory, cache), cache));
    }

    private static LawInput Paragraph(string book, string paragraph, string text = "<p>Wer …</p>") => new()
    {
        LawBook = book,
        Paragraph = paragraph,
        Title = "Titel " + paragraph,
        Text = text,
    };

    private static LawBookInput Book(string abbreviation, string name = "Ein Gesetzbuch") => new()
    {
        Abbreviation = abbreviation,
        Name = name,
    };

    // ---- rich text ----

    [Fact]
    public async Task The_text_is_stored_sanitized_and_keeps_its_formatting()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.CreateAsync(Paragraph("StGB", "§ 1",
            "<p>(1) <strong>Wer</strong> tötet,</p><ol><li>heimtückisch</li></ol><script>alert(1)</script>"), Leader());

        using var check = ctx.NewContext();
        var text = (await check.Laws.SingleAsync()).Text;
        Assert.Contains("<strong>Wer</strong>", text);
        Assert.Contains("<li>heimtückisch</li>", text);
        Assert.DoesNotContain("script", text);
    }

    [Fact]
    public async Task An_empty_editor_is_no_text()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Paragraph("StGB", "§ 1", "<p><br></p>"), Leader()));
    }

    [Fact]
    public async Task The_read_only_supervision_writes_nothing_despite_its_rank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Paragraph("StGB", "§ 1"), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateBookAsync(Book("StGB"), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateBookAsync(Book("StGB"), NonLeader()));
    }

    // ---- catalog ----

    [Fact]
    public async Task A_paragraph_in_an_unknown_book_puts_the_book_in_the_catalog()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.CreateAsync(Paragraph("WaffG", "§ 1"), Leader());

        var book = Assert.Single(await svc.GetBooksAsync());
        Assert.NotNull(book.Id);
        Assert.Equal("WaffG", book.Abbreviation);
        Assert.Equal(1, book.ParagraphCount);
    }

    [Fact]
    public async Task A_paragraph_takes_the_catalog_spelling_of_its_book()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        await svc.CreateBookAsync(Book("BtmG"), Leader());

        var law = await svc.CreateAsync(Paragraph("btmg", "§ 1"), Leader());

        Assert.Equal("BtmG", law.LawBook);
        Assert.Single(await svc.GetBooksAsync());
    }

    [Fact]
    public async Task New_paragraphs_append_and_the_book_reads_in_order()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        await svc.CreateAsync(Paragraph("StGB", "§ 10"), Leader());
        await svc.CreateAsync(Paragraph("StGB", "§ 2"), Leader());
        var first = Paragraph("StGB", "Präambel");
        first.SortOrder = -1;
        await svc.CreateAsync(first, Leader());

        var content = await svc.GetBookAsync("stgb");

        Assert.NotNull(content);
        Assert.Equal(["§ 10", "§ 2", "Präambel"], content!.Paragraphs.Select(l => l.Paragraph));
        Assert.Equal([1, 2, 3], content.Paragraphs.Select(l => l.SortOrder));
    }

    [Fact]
    public async Task A_book_only_named_by_its_paragraphs_still_shows()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(new Law { Id = "l1", LawBook = "StVO", Paragraph = "§ 1", Title = "Vorfahrt", Text = "<p>x</p>" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var book = Assert.Single(await svc.GetBooksAsync());
        Assert.Null(book.Id);
        Assert.Equal("StVO", book.Abbreviation);
        Assert.NotNull(await svc.GetBookAsync("StVO"));
    }

    [Fact]
    public async Task A_partner_sees_only_books_with_a_paragraph_released_to_them()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var shared = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());
        await svc.CreateAsync(Paragraph("StGB", "§ 2"), Leader());
        await svc.CreateAsync(Paragraph("WaffG", "§ 1"), Leader());
        await svc.CreateBookAsync(Book("LBG"), Leader());
        using (var db = ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Law), EntityId = shared.Id, Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }

        var book = Assert.Single(await svc.GetBooksAsync(partnerAgency: PartnerAgency.DoJ));
        Assert.Equal("StGB", book.Abbreviation);
        Assert.Equal(1, book.ParagraphCount);
        Assert.Null(await svc.GetBookAsync("WaffG", partnerAgency: PartnerAgency.DoJ));
        Assert.Null(await svc.GetBookAsync("LBG", partnerAgency: PartnerAgency.DoJ));
        var content = await svc.GetBookAsync("StGB", partnerAgency: PartnerAgency.DoJ);
        Assert.Equal([shared.Id], content!.Paragraphs.Select(l => l.Id));
    }

    [Theory]
    [InlineData("stgb")]
    [InlineData("StGB")]
    public async Task An_abbreviation_is_taken_once_regardless_of_case(string second)
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        await svc.CreateBookAsync(Book("StGB"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateBookAsync(Book(second), Leader()));
    }

    [Fact]
    public async Task An_abbreviation_cannot_break_the_address()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateBookAsync(Book("St/GB"), Leader()));
    }

    [Fact]
    public async Task Renaming_a_book_moves_its_paragraphs_including_the_deleted_ones()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var book = await svc.CreateBookAsync(Book("StGB"), Leader());
        var kept = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());
        var binned = await svc.CreateAsync(Paragraph("StGB", "§ 2"), Leader());
        await svc.DeleteAsync(binned.Id, Leader());

        await svc.UpdateBookAsync(book.Id, Book("SAStGB", "Strafgesetzbuch"), Leader());

        using var check = ctx.NewContext();
        var books = await check.Laws.IgnoreQueryFilters().Select(l => l.LawBook).Distinct().ToListAsync();
        Assert.Equal(["SAStGB"], books);
        Assert.Null(await svc.GetBookAsync("StGB"));
        Assert.Equal("Strafgesetzbuch", (await svc.GetBookAsync("SAStGB"))!.Book.Name);
        Assert.Equal(kept.Id, Assert.Single((await svc.GetBookAsync("SAStGB"))!.Paragraphs).Id);
    }

    [Fact]
    public async Task Only_an_empty_book_can_be_deleted()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var book = await svc.CreateBookAsync(Book("StGB"), Leader());
        var law = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteBookAsync(book.Id, Leader()));

        await svc.DeleteAsync(law.Id, Leader());
        await svc.DeleteBookAsync(book.Id, Leader());

        Assert.Empty(await svc.GetBooksAsync());
        Assert.Equal(book.Id, Assert.Single(await svc.GetBookTrashAsync()).Id);
    }

    // ---- trash ----

    [Fact]
    public async Task A_restored_paragraph_comes_back_internal()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var law = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());
        using (var db = ctx.NewContext())
        {
            (await db.Laws.SingleAsync()).IsPublic = true;
            await db.SaveChangesAsync();
        }
        await svc.DeleteAsync(law.Id, Leader());
        Assert.Equal(law.Id, Assert.Single(await svc.GetTrashAsync()).Id);

        await svc.RestoreAsync(law.Id, Leader());

        var restored = await svc.GetAsync(law.Id);
        Assert.NotNull(restored);
        Assert.False(restored!.IsPublic);
        Assert.Empty(await svc.GetTrashAsync());
    }

    [Fact]
    public async Task A_paragraph_does_not_come_back_next_to_its_successor()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var old = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());
        await svc.DeleteAsync(old.Id, Leader());
        await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(old.Id, Leader()));
    }

    [Fact]
    public async Task A_paragraph_waits_for_its_book_to_come_back()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var book = await svc.CreateBookAsync(Book("StGB"), Leader());
        var law = await svc.CreateAsync(Paragraph("StGB", "§ 1"), Leader());
        await svc.DeleteAsync(law.Id, Leader());
        await svc.DeleteBookAsync(book.Id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(law.Id, Leader()));

        await svc.RestoreBookAsync(book.Id, Leader());
        await svc.RestoreAsync(law.Id, Leader());
        Assert.Single((await svc.GetBookAsync("StGB"))!.Paragraphs);
    }

    [Fact]
    public async Task A_book_does_not_come_back_onto_a_taken_abbreviation()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var old = await svc.CreateBookAsync(Book("StGB"), Leader());
        await svc.DeleteBookAsync(old.Id, Leader());
        await svc.CreateBookAsync(Book("StGB"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreBookAsync(old.Id, Leader()));
    }
}
