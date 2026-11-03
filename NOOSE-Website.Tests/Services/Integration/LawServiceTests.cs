using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LawService"/> against in-memory SQLite.</summary>
public sealed class LawServiceTests
{
    private static LawService NewService(SqliteTestContext ctx) => new(ctx.Factory, Substitute.For<IPublicLawService>());

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static Law NewLaw(string id, string book, string paragraph, string title, string text = "Text", string? sentence = null)
        => new() { Id = id, LawBook = book, Paragraph = paragraph, Title = title, Text = text, Sentence = sentence };

    private static PartnerShare LawShare(string lawId, PartnerAgency agency, string? partnerAgentId = null)
        => new() { EntityType = nameof(Law), EntityId = lawId, Agency = agency, PartnerAgentId = partnerAgentId };

    private static LawInput ValidInput() => new()
    {
        LawBook = "StGB",
        Paragraph = "§ 1",
        Title = "Titel",
        Text = "Volltext",
        Sentence = "1 Jahr",
    };

    // ---- GetListAsync ------------------------------------------------------

    [Fact]
    public async Task GetListAsync_NoPartner_ReturnsAllLaws_OrderedByBookParagraphTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l3", "StVO", "§ 1", "Ampel"));
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.Laws.Add(NewLaw("l2", "StGB", "§ 2", "Diebstahl"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync();

        Assert.Equal(new[] { "l1", "l2", "l3" }, result.Select(l => l.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_Partner_ReturnsOnlyLawsSharedToTheirAgency()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.Laws.Add(NewLaw("l2", "StGB", "§ 2", "Diebstahl"));
            db.Laws.Add(NewLaw("l3", "StGB", "§ 3", "Raub"));
            // l1 shared to DoJ (agency-wide), l2 shared to LSPD only, l3 not shared.
            db.PartnerShares.Add(LawShare("l1", PartnerAgency.DoJ));
            db.PartnerShares.Add(LawShare("l2", PartnerAgency.LSPD));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(partnerAgency: PartnerAgency.DoJ);

        var law = Assert.Single(result);
        Assert.Equal("l1", law.Id);
    }

    [Fact]
    public async Task GetListAsync_Partner_WithAgentId_ReturnsAgencyWideAndOwnAccountShares()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Agencywide"));
            db.Laws.Add(NewLaw("l2", "StGB", "§ 2", "Mine"));
            db.Laws.Add(NewLaw("l3", "StGB", "§ 3", "Other"));
            db.PartnerShares.Add(LawShare("l1", PartnerAgency.DoJ, partnerAgentId: null));  // whole agency
            db.PartnerShares.Add(LawShare("l2", PartnerAgency.DoJ, partnerAgentId: "pa1")); // my account
            db.PartnerShares.Add(LawShare("l3", PartnerAgency.DoJ, partnerAgentId: "pa2")); // someone else
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(partnerAgency: PartnerAgency.DoJ, partnerAgentId: "pa1");

        Assert.Equal(new[] { "l1", "l2" }, result.Select(l => l.Id).OrderBy(x => x).ToArray());
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_NoPartner_ReturnsLaw()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("l1");

        Assert.NotNull(result);
        Assert.Equal("Betrug", result!.Title);
    }

    [Fact]
    public async Task GetAsync_NoPartner_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetAsync("ghost");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_Partner_ReturnsLaw_WhenSharedToAgency()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.PartnerShares.Add(LawShare("l1", PartnerAgency.DoJ));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("l1", partnerAgency: PartnerAgency.DoJ);

        Assert.NotNull(result);
        Assert.Equal("l1", result!.Id);
    }

    [Fact]
    public async Task GetAsync_Partner_ReturnsNull_WhenNotShared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            // shared to a different agency only.
            db.PartnerShares.Add(LawShare("l1", PartnerAgency.LSPD));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("l1", partnerAgency: PartnerAgency.DoJ);

        Assert.Null(result);
    }

    // ---- SearchAsync -------------------------------------------------------

    [Fact]
    public async Task SearchAsync_FiltersByTitleParagraphOrBook()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 263", "Betrug"));
            db.Laws.Add(NewLaw("l2", "StGB", "§ 242", "Diebstahl"));
            db.Laws.Add(NewLaw("l3", "StVO", "§ 1", "Ampel"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var byTitle = await svc.SearchAsync("Betrug");
        Assert.Equal("l1", Assert.Single(byTitle).Id);

        var byParagraph = await svc.SearchAsync("242");
        Assert.Equal("l2", Assert.Single(byParagraph).Id);

        var byBook = await svc.SearchAsync("StVO");
        Assert.Equal("l3", Assert.Single(byBook).Id);
    }

    [Fact]
    public async Task SearchAsync_EmptyText_ReturnsAll_OrderedByBookThenParagraph()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l2", "StGB", "§ 2", "B"));
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "A"));
            db.Laws.Add(NewLaw("l3", "StVO", "§ 1", "C"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("   ");

        Assert.Equal(new[] { "l1", "l2", "l3" }, result.Select(l => l.Id).ToArray());
    }

    [Fact]
    public async Task SearchAsync_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.Laws.Add(NewLaw($"l{i}", "StGB", $"§ {i}", $"T{i}"));
            }
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync(null, max: 2);

        Assert.Equal(2, result.Count);
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsLaw_AndTrimsFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new LawInput
        {
            LawBook = "  StGB  ",
            Paragraph = "  § 263  ",
            Title = "  Betrug  ",
            Text = "  Volltext  ",
            Sentence = "  6 Monate  ",
        };

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Laws.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("StGB", stored.LawBook);
        Assert.Equal("§ 263", stored.Paragraph);
        Assert.Equal("Betrug", stored.Title);
        Assert.Equal("Volltext", stored.Text);
        Assert.Equal("6 Monate", stored.Sentence);
    }

    [Fact]
    public async Task CreateAsync_NullsBlankSentence()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Sentence = "   ";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Laws.SingleAsync();
        Assert.Null(stored.Sentence);
    }

    [Fact]
    public async Task CreateAsync_KeepsSentence_WhenProvided()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Sentence = "2 Jahre";

        await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Laws.SingleAsync();
        Assert.Equal("2 Jahre", stored.Sentence);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.Laws.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenRequiredFieldMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Text = "   "; // blank required field

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Leader()));
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Alt", "AlterText", "alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new LawInput
        {
            LawBook = "StVO",
            Paragraph = "§ 99",
            Title = "Neu",
            Text = "NeuerText",
            Sentence = "  ", // becomes null
        };

        await svc.RefreshAsync("l1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Laws.SingleAsync(l => l.Id == "l1");
        Assert.Equal("StVO", stored.LawBook);
        Assert.Equal("§ 99", stored.Paragraph);
        Assert.Equal("Neu", stored.Title);
        Assert.Equal("NeuerText", stored.Text);
        Assert.Null(stored.Sentence);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("ghost", ValidInput(), Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("l1", ValidInput(), NonLeader()));

        using var check = ctx.NewContext();
        var stored = await check.Laws.SingleAsync(l => l.Id == "l1");
        Assert.Equal("Alt", stored.Title); // untouched
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenValidationFails()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = ValidInput();
        input.Title = ""; // blank required field, actor passes leadership guard

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("l1", input, Leader()));
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_RemovesLaw()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("l1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.Laws.AnyAsync(l => l.Id == "l1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("ghost", Leader()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(NewLaw("l1", "StGB", "§ 1", "Betrug"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("l1", NonLeader()));

        using var check = ctx.NewContext();
        Assert.True(await check.Laws.AnyAsync(l => l.Id == "l1"));
    }
}
