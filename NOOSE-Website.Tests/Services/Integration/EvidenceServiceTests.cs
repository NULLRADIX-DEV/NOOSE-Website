using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="EvidenceService"/> over in-memory SQLite.</summary>
public sealed class EvidenceServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.SpecialAgent).AsTeamLead().Build();

    private static EvidenceService Build(SqliteTestContext ctx) => Build(ctx, out _);

    private static EvidenceService Build(SqliteTestContext ctx, out ICaseNumberService caseNumber)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var seq = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>())
            .Returns(_ => $"NOOSE-ASS-2026-{++seq:0000}");
        caseNumber = caseNo;
        // real suggestion service: category learning is part of what these tests assert
        return new EvidenceService(ctx.Factory, caseNo, Substitute.For<IEvidenceImageStorageService>(),
            new ProfileSuggestionService(ctx.Factory));
    }

    /// <summary>Like <see cref="Build(SqliteTestContext)"/> but with a storage stub that accepts uploads, so image paths run through.</summary>
    private static EvidenceService BuildWithImages(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var seq = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>())
            .Returns(_ => $"NOOSE-ASS-2026-{++seq:0000}");

        var storage = Substitute.For<IEvidenceImageStorageService>();
        storage.IsAllowedType(Arg.Any<string>()).Returns(true);
        var files = 0;
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult($"bild-{++files}.png"));

        return new EvidenceService(ctx.Factory, caseNo, storage, new ProfileSuggestionService(ctx.Factory));
    }

    /// <summary>Like <see cref="Build(SqliteTestContext)"/> but rejects allocation outside a transaction, as the real service does.</summary>
    private static EvidenceService BuildStrict(SqliteTestContext ctx, out ICaseNumberService caseNumber)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var seq = 0;
        // own number range: the loose builder that seeded the fixture already burned the 0001 series
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<AppDbContext>().Database.CurrentTransaction is null
                ? throw new InvalidOperationException("Aktenzeichen-Vergabe erfordert eine umschließende Transaktion.")
                : $"NOOSE-ASS-2026-5{++seq:000}");
        caseNumber = caseNo;
        // real suggestion service: category learning is part of what these tests assert
        return new EvidenceService(ctx.Factory, caseNo, Substitute.For<IEvidenceImageStorageService>(),
            new ProfileSuggestionService(ctx.Factory));
    }

    /// <summary>Writes a ledger row straight to the DB, bypassing the stock guard — the only way to fixture a negative balance.</summary>
    private static void SeedRawEntry(SqliteTestContext ctx, string entryId, EvidenceEntryType type, string itemId, int quantity, bool deleted = false)
    {
        using var db = ctx.NewContext();
        db.EvidenceEntries.Add(new EvidenceEntry
        {
            Id = entryId,
            CaseNumber = $"NOOSE-ASS-2026-9{Math.Abs(entryId.GetHashCode()) % 1000:000}",
            Type = type,
            OwnerType = EvidenceService.NooseOwner,
            HandlerAgentId = "lead",
            Timestamp = T0,
            IsDeleted = deleted,
            DeletedAt = deleted ? T0 : null,
        });
        db.EvidenceEntryLines.Add(new EvidenceEntryLine { EntryId = entryId, ItemId = itemId, Quantity = quantity });
        db.SaveChanges();
    }

    private static async Task<string> DepositAsync(EvidenceService svc, string itemName, int quantity)
    {
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = itemName, Quantity = quantity })), Leader());
        return (await svc.GetItemByNameAsync(itemName))!.Id;
    }

    private static EvidenceEntryInput Entry(EvidenceEntryType type, Action<EvidenceEntryInput>? cfg = null)
    {
        var i = new EvidenceEntryInput
        {
            Type = type,
            OwnerType = EvidenceService.NooseOwner,
            Timestamp = T0,
            HandlerAgentId = "lead",
        };
        cfg?.Invoke(i);
        return i;
    }

    [Fact]
    public async Task CreateEntry_AssignsCaseNumber_AutoCreatesItem_AndPersistsLines()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var created = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.Lines.Add(new EvidenceLineInput { ItemName = "Twisted Burger", Quantity = 367 });
            i.Lines.Add(new EvidenceLineInput { ItemName = "Ripper Roo", Quantity = 367 });
        }), Leader());

        Assert.Equal("NOOSE-ASS-2026-0001", created.CaseNumber);
        using var db = ctx.NewContext();
        Assert.Equal(2, db.EvidenceItems.Count());
        Assert.Equal(2, db.EvidenceEntryLines.Count());
    }

    [Fact]
    public async Task CreateEntry_ReusesExistingItem_CaseInsensitive()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 5 })), Leader());
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "zyanid", Quantity = 2 })), Leader());

        using var db = ctx.NewContext();
        Assert.Equal(1, db.EvidenceItems.Count());
    }

    [Fact]
    public async Task OnHand_IsDepositsMinusWithdrawals()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 10 })), Leader());
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 3 })), Leader());

        var items = await svc.GetItemsAsync();
        Assert.Equal(7, items.Single(x => x.Item.Name == "Zyanid").OnHand);
    }

    [Fact]
    public async Task OnHand_ExcludesDeletedEntries()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 10 })), Leader());
        var itemId = (await svc.GetItemsAsync()).Single().Item.Id;

        // a soft-deleted deposit must not count toward on-hand (global query filter)
        using (var db = ctx.NewContext())
        {
            db.EvidenceEntries.Add(new EvidenceEntry
            {
                Id = "ghost", CaseNumber = "NOOSE-ASS-2026-9001", Type = EvidenceEntryType.Deposit,
                OwnerType = EvidenceService.NooseOwner, HandlerAgentId = "lead", Timestamp = T0,
                IsDeleted = true, DeletedAt = T0,
            });
            db.EvidenceEntryLines.Add(new EvidenceEntryLine { EntryId = "ghost", ItemId = itemId, Quantity = 100 });
            db.SaveChanges();
        }

        Assert.Equal(10, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task GetEntryDisplay_ResolvesNooseOwner()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var e = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Food", Quantity = 1 })), Leader());

        var display = await svc.GetEntryDisplayAsync(e.Id);
        Assert.Equal("NOOSE", display!.OwnerDisplay);
    }

    [Fact]
    public async Task GetEntryDisplay_ResolvesAgentOwner_ByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            var a = Seed.Agent("a1");
            a.Codename = "Falcon";
            db.Users.Add(a);
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var e = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Agent);
            i.OwnerId = "a1";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Food", Quantity = 1 });
        }), Leader());

        var display = await svc.GetEntryDisplayAsync(e.Id);
        Assert.Equal("Falcon", display!.OwnerDisplay);
    }

    [Fact]
    public async Task GetEntryDisplay_ResolvesPersonOwner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            var p = Seed.Person("p1");
            p.Name = "John Doe";
            db.People.Add(p);
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var e = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Person);
            i.OwnerId = "p1";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Waffe", Quantity = 1 });
        }), Leader());

        var display = await svc.GetEntryDisplayAsync(e.Id);
        Assert.Contains("John Doe", display!.OwnerDisplay);
    }

    [Fact]
    public async Task UpdateEntry_ReplacesLines()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var e = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.Lines.Add(new EvidenceLineInput { ItemName = "A", Quantity = 1 });
            i.Lines.Add(new EvidenceLineInput { ItemName = "B", Quantity = 2 });
        }), Leader());

        await svc.UpdateEntryAsync(e.Id, Entry(EvidenceEntryType.Deposit, i =>
        {
            i.Lines.Add(new EvidenceLineInput { ItemName = "A", Quantity = 5 });
        }), Leader());

        using var db = ctx.NewContext();
        var lines = db.EvidenceEntryLines.Where(l => l.EntryId == e.Id).ToList();
        Assert.Single(lines);
        Assert.Equal(5, lines[0].Quantity);
    }

    [Fact]
    public async Task EntryTrash_ReturnsOnlyDeleted_AndRestoreClearsFlag()
    {
        // the audit/soft-delete interceptor is not wired in the test context, so seed the deleted row directly
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.EvidenceEntries.Add(new EvidenceEntry
            {
                Id = "del", CaseNumber = "NOOSE-ASS-2026-9001", Type = EvidenceEntryType.Deposit,
                OwnerType = EvidenceService.NooseOwner, HandlerAgentId = "lead", Timestamp = T0,
                IsDeleted = true, DeletedAt = T0,
            });
            db.EvidenceEntries.Add(new EvidenceEntry
            {
                Id = "live", CaseNumber = "NOOSE-ASS-2026-9002", Type = EvidenceEntryType.Deposit,
                OwnerType = EvidenceService.NooseOwner, HandlerAgentId = "lead", Timestamp = T0,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var trash = await svc.GetEntryTrashAsync();
        Assert.Single(trash);
        Assert.Equal("del", trash[0].Id);
        Assert.Single(await svc.GetEntriesAsync());

        await svc.RestoreEntryAsync("del", Leader());
        Assert.Empty(await svc.GetEntryTrashAsync());
        Assert.Equal(2, (await svc.GetEntriesAsync()).Count);
    }

    [Fact]
    public async Task Withdrawal_ExceedingStock_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 5 })), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 6 })), Leader()));
    }

    [Fact]
    public async Task Withdrawal_WithinStock_Succeeds()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 10 })), Leader());
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 4 })), Leader());

        var itemId = (await svc.GetItemsAsync()).Single().Item.Id;
        Assert.Equal(6, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task Withdrawal_OfUnstockedItem_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "Nichts", Quantity = 1 })), Leader()));
    }

    [Fact]
    public async Task Withdrawal_AggregatesLinesPerItem()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 5 })), Leader());

        // two lines of the same item summing to 6 > 5 must be rejected
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal, i =>
            {
                i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 3 });
                i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 3 });
            }), Leader()));
    }

    [Fact]
    public async Task UpdateWithdrawal_ExcludesOwnEntry_FromAvailability()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 10 })), Leader());
        var wd = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 6 })), Leader());

        // editing this withdrawal up to 9 is allowed: available excluding itself is 10
        await svc.UpdateEntryAsync(wd.Id, Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Zyanid", Quantity = 9 })), Leader());

        var itemId = (await svc.GetItemsAsync()).Single().Item.Id;
        Assert.Equal(1, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task GetEntriesForOwner_ReturnsOnlyThatOwner()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Person); i.OwnerId = "p1";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Waffe", Quantity = 1 });
        }), Leader());
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Person); i.OwnerId = "p2";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Geld", Quantity = 1 });
        }), Leader());

        var rows = await svc.GetEntriesForOwnerAsync(nameof(Person), "p1");
        Assert.Single(rows);
        Assert.Equal("p1", rows[0].Entry.OwnerId);
    }

    [Fact]
    public async Task GetEntriesForFactionMembers_IncludesMemberOwnedEntriesOnly()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("fac"));
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "fac", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = Build(ctx);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Person); i.OwnerId = "p1";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Waffe", Quantity = 1 });
        }), Leader());
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
        {
            i.OwnerType = nameof(Person); i.OwnerId = "p2";
            i.Lines.Add(new EvidenceLineInput { ItemName = "Geld", Quantity = 1 });
        }), Leader());

        var rows = await svc.GetEntriesForFactionMembersAsync("fac");
        Assert.Single(rows);
        Assert.Equal("p1", rows[0].Entry.OwnerId);
    }

    [Fact]
    public async Task CreateItem_RejectsDuplicateName()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Zyanid" }, null, null, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateItemAsync(new EvidenceItemInput { Name = "zyanid" }, null, null, Leader()));
    }

    [Fact]
    public async Task NonLeadership_CannotWrite()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateItemAsync(new EvidenceItemInput { Name = "X" }, null, null, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "X", Quantity = 1 })), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "X", Quantity = 1 })), Junior()));
    }

    // ---- deposit is open, withdrawal is not ----

    [Fact]
    public async Task CreateEntry_Deposit_NonLeadership_IsBooked()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 3 })), Junior());

        var item = await svc.GetItemByNameAsync("Pistole");
        Assert.Equal(3, await svc.GetOnHandAsync(item!.Id));
    }

    [Fact]
    public async Task CreateEntry_Deposit_NonLeadership_AutoCreatesUnknownItem()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // depositing an unknown name creates the catalog item, even though CreateItemAsync stays leadership
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Fundstück", Quantity = 1 })), Junior());

        Assert.NotNull(await svc.GetItemByNameAsync("Fundstück"));
    }

    [Fact]
    public async Task CreateEntry_Withdrawal_SeniorSpecialAgent_Succeeds()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await DepositAsync(svc, "Pistole", 10);
        ClaimsPrincipal senior = ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build();

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 3 })), senior);

        var itemId = (await svc.GetItemByNameAsync("Pistole"))!.Id;
        Assert.Equal(7, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task CreateEntry_Withdrawal_BelowSenior_Throws_AndWritesNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await DepositAsync(svc, "Pistole", 10);

        int entriesBefore, itemsBefore;
        using (var db = ctx.NewContext())
        {
            entriesBefore = db.EvidenceEntries.Count();
            itemsBefore = db.EvidenceItems.Count();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 1 })), Junior()));

        // the guard runs before the stock check and before the transaction, so no case number is burned
        using var after = ctx.NewContext();
        Assert.Equal(entriesBefore, after.EvidenceEntries.Count());
        Assert.Equal(itemsBefore, after.EvidenceItems.Count());
    }

    [Fact]
    public async Task CreateEntry_Deposit_Demo_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        // demo carries Director rank; without interceptors here, only the Permission guard can stop it
        ClaimsPrincipal demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "X", Quantity = 1 })), demo));
    }

    [Fact]
    public async Task UpdateEntry_NonLeadership_Throws_EvenWhenFlippingDepositToWithdrawal()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var own = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 10 })), Junior());
        var itemId = (await svc.GetItemByNameAsync("Pistole"))!.Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateEntryAsync(own.Id, Entry(EvidenceEntryType.Withdrawal,
                i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 10 })), Junior()));

        Assert.Equal(10, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task DeleteEntry_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var own = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 10 })), Junior());
        var itemId = (await svc.GetItemByNameAsync("Pistole"))!.Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteEntryAsync(own.Id, Junior()));
        Assert.Equal(10, await svc.GetOnHandAsync(itemId));
    }

    [Fact]
    public async Task RestoreEntry_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreEntryAsync("any", Junior()));
    }

    [Fact]
    public async Task SetItemImage_FirstPicture_NonLeadership_Succeeds()
    {
        using var ctx = new SqliteTestContext();
        var svc = BuildWithImages(ctx);
        await DepositAsync(svc, "Pistole", 1);
        var itemId = (await svc.GetItemByNameAsync("Pistole"))!.Id;

        using var image = new MemoryStream(new byte[] { 1, 2, 3 });
        await svc.SetItemImageAsync(itemId, image, "image/png", Junior());

        using var db = ctx.NewContext();
        Assert.False(string.IsNullOrEmpty(db.EvidenceItems.Single(i => i.Id == itemId).ImageFileName));
    }

    [Fact]
    public async Task SetItemImage_Replacement_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = BuildWithImages(ctx);
        await DepositAsync(svc, "Pistole", 1);
        var itemId = (await svc.GetItemByNameAsync("Pistole"))!.Id;
        using (var first = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            await svc.SetItemImageAsync(itemId, first, "image/png", Leader());
        }

        using var second = new MemoryStream(new byte[] { 4, 5, 6 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetItemImageAsync(itemId, second, "image/png", Junior()));
    }

    // ---- clearing ----

    [Fact]
    public async Task ClearStock_PositiveStock_BooksOneWithdrawalWithFullQuantities()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var a = await DepositAsync(svc, "Pistole", 10);
        var b = await DepositAsync(svc, "Messer", 4);

        var result = await svc.ClearStockAsync(new[] { a, b }, null, Leader());

        Assert.Equal(2, result.ClearedItems);
        Assert.Equal(14, result.ClearedPieces);
        Assert.Equal(0, result.CorrectedItems);
        Assert.Null(result.CorrectionCaseNumber);
        Assert.Equal(0, await svc.GetOnHandAsync(a));
        Assert.Equal(0, await svc.GetOnHandAsync(b));

        using var db = ctx.NewContext();
        var booked = db.EvidenceEntries.Single(e => e.Id == result.WithdrawalEntryId);
        Assert.Equal(EvidenceEntryType.Withdrawal, booked.Type);
        Assert.Equal(EvidenceService.NooseOwner, booked.OwnerType);
        Assert.Null(booked.OwnerId);
        var lines = db.EvidenceEntryLines.Where(l => l.EntryId == booked.Id).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(10, lines.Single(l => l.ItemId == a).Quantity);
        Assert.Equal(4, lines.Single(l => l.ItemId == b).Quantity);
    }

    [Fact]
    public async Task ClearStock_NegativeStock_BooksCorrectingDeposit()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Zyanid", 2);
        // a raw withdrawal of 5 drives the balance to -3; the service itself would reject it
        SeedRawEntry(ctx, "raw", EvidenceEntryType.Withdrawal, id, 5);
        Assert.Equal(-3, await svc.GetOnHandAsync(id));

        var result = await svc.ClearStockAsync(new[] { id }, null, Leader());

        Assert.Equal(0, result.ClearedItems);
        Assert.Equal(1, result.CorrectedItems);
        Assert.Equal(3, result.CorrectedPieces);
        Assert.Null(result.WithdrawalCaseNumber);
        Assert.Equal(0, await svc.GetOnHandAsync(id));

        using var db = ctx.NewContext();
        var booked = db.EvidenceEntries.Single(e => e.Id == result.CorrectionEntryId);
        Assert.Equal(EvidenceEntryType.Deposit, booked.Type);
        Assert.Equal(3, db.EvidenceEntryLines.Single(l => l.EntryId == booked.Id).Quantity);
    }

    [Fact]
    public async Task ClearStock_MixedBalances_BooksBothEntries_SharingTimestamp()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var a = await DepositAsync(svc, "Pistole", 10);
        var b = await DepositAsync(svc, "Zyanid", 1);
        SeedRawEntry(ctx, "raw", EvidenceEntryType.Withdrawal, b, 4);

        var result = await svc.ClearStockAsync(new[] { a, b }, null, Leader());

        Assert.Equal(1, result.ClearedItems);
        Assert.Equal(1, result.CorrectedItems);
        Assert.Equal(3, result.CorrectedPieces);
        Assert.Equal(0, await svc.GetOnHandAsync(a));
        Assert.Equal(0, await svc.GetOnHandAsync(b));

        using var db = ctx.NewContext();
        var withdrawal = db.EvidenceEntries.Single(e => e.Id == result.WithdrawalEntryId);
        var correction = db.EvidenceEntries.Single(e => e.Id == result.CorrectionEntryId);
        Assert.Equal(withdrawal.Timestamp, correction.Timestamp);
        Assert.NotEqual(withdrawal.CaseNumber, correction.CaseNumber);
        Assert.StartsWith("Räumung der Asservatenkammer", withdrawal.Notes);
        Assert.Contains("Korrektur Negativbestand", correction.Notes);
    }

    [Fact]
    public async Task ClearStock_ZeroStockItem_IsSkipped()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var a = await DepositAsync(svc, "Pistole", 5);
        var b = await DepositAsync(svc, "Messer", 2);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 5 })), Leader());

        var result = await svc.ClearStockAsync(new[] { a, b }, null, Leader());

        Assert.Equal(1, result.ClearedItems);
        Assert.Equal(1, result.SkippedItems);
        using var db = ctx.NewContext();
        var line = db.EvidenceEntryLines.Single(l => l.EntryId == result.WithdrawalEntryId);
        Assert.Equal(b, line.ItemId);
    }

    [Fact]
    public async Task ClearStock_RecomputesOnHand_AfterConcurrentWithdrawal()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 10);
        // another agent takes 4 between dialog open and apply
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 4 })), Leader());

        var result = await svc.ClearStockAsync(new[] { id }, null, Leader());

        Assert.Equal(6, result.ClearedPieces);
        Assert.Equal(0, await svc.GetOnHandAsync(id));
    }

    [Fact]
    public async Task ClearStock_ItemDrainedConcurrently_BooksNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 10);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 10 })), Leader());
        var before = CountEntries(ctx);

        var result = await svc.ClearStockAsync(new[] { id }, null, Leader());

        Assert.False(result.Booked);
        Assert.Equal(1, result.SkippedItems);
        Assert.Equal(before, CountEntries(ctx));
    }

    [Fact]
    public async Task ClearStock_EmptySelection_BooksNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, out var caseNo);

        var result = await svc.ClearStockAsync(Array.Empty<string>(), null, Leader());

        Assert.Equal(EvidenceClearingResult.Empty, result);
        Assert.Equal(0, CountEntries(ctx));
        await caseNo.DidNotReceive().NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearStock_UnknownAndDeletedItemIds_AreSkipped()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 5);
        // soft-delete the item but leave its stock intact
        using (var db = ctx.NewContext())
        {
            var item = db.EvidenceItems.Single(i => i.Id == id);
            item.IsDeleted = true;
            item.DeletedAt = T0;
            db.SaveChanges();
        }
        var before = CountEntries(ctx);

        var result = await svc.ClearStockAsync(new[] { id, "does-not-exist" }, null, Leader());

        Assert.False(result.Booked);
        Assert.Equal(2, result.SkippedItems);
        Assert.Equal(before, CountEntries(ctx));
    }

    [Fact]
    public async Task ClearStock_ExcludesDeletedEntriesFromQuantity()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 10);
        SeedRawEntry(ctx, "ghost", EvidenceEntryType.Deposit, id, 100, deleted: true);

        var result = await svc.ClearStockAsync(new[] { id }, null, Leader());

        Assert.Equal(10, result.ClearedPieces);
    }

    [Fact]
    public async Task ClearStock_DuplicateIds_BookOnePositionPerItem()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 7);

        var result = await svc.ClearStockAsync(new[] { id, id }, null, Leader());

        Assert.Equal(1, result.ClearedItems);
        Assert.Equal(7, result.ClearedPieces);
        using var db = ctx.NewContext();
        Assert.Single(db.EvidenceEntryLines.Where(l => l.EntryId == result.WithdrawalEntryId));
    }

    [Fact]
    public async Task ClearStock_NonLeadership_Throws_AndWritesNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 5);
        var before = CountEntries(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ClearStockAsync(new[] { id }, null, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ClearStockAsync(new[] { id }, null, OnlyReader()));

        Assert.Equal(before, CountEntries(ctx));
        Assert.Equal(5, await svc.GetOnHandAsync(id));
    }

    [Fact]
    public async Task ClearStock_AppendsFreeTextNote_ToBothHalves()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var a = await DepositAsync(svc, "Pistole", 3);
        var b = await DepositAsync(svc, "Zyanid", 1);
        SeedRawEntry(ctx, "raw", EvidenceEntryType.Withdrawal, b, 2);

        var result = await svc.ClearStockAsync(new[] { a, b }, "Abtransport LSPD", Leader());

        using var db = ctx.NewContext();
        var withdrawal = db.EvidenceEntries.Single(e => e.Id == result.WithdrawalEntryId);
        var correction = db.EvidenceEntries.Single(e => e.Id == result.CorrectionEntryId);
        Assert.Contains("Räumung der Asservatenkammer", withdrawal.Notes);
        Assert.Contains("Abtransport LSPD", withdrawal.Notes);
        Assert.Contains("Korrektur Negativbestand", correction.Notes);
        Assert.Contains("Abtransport LSPD", correction.Notes);
    }

    [Fact]
    public async Task ClearStock_AllocatesBothCaseNumbers_InsideOneTransaction()
    {
        using var ctx = new SqliteTestContext();
        var loose = Build(ctx);
        var a = await DepositAsync(loose, "Pistole", 3);
        var b = await DepositAsync(loose, "Zyanid", 1);
        SeedRawEntry(ctx, "raw", EvidenceEntryType.Withdrawal, b, 2);

        var svc = BuildStrict(ctx, out var caseNo);
        var result = await svc.ClearStockAsync(new[] { a, b }, null, Leader());

        Assert.True(result.Booked);
        await caseNo.Received(2).NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearStock_SetsActorAsHandler()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var id = await DepositAsync(svc, "Pistole", 3);

        var result = await svc.ClearStockAsync(new[] { id }, null, Leader());

        using var db = ctx.NewContext();
        Assert.Equal("lead", db.EvidenceEntries.Single(e => e.Id == result.WithdrawalEntryId).HandlerAgentId);
    }

    // ---------- categories ----------

    [Fact]
    public async Task CreateItem_PersistsCategory_AndLearnsSuggestion()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var item = await svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = "  Drogen  " }, null, null, Leader());

        using var db = ctx.NewContext();
        Assert.Equal("Drogen", db.EvidenceItems.Single(i => i.Id == item.Id).Category);
        Assert.Equal("Drogen", db.ProfileSuggestions
            .Single(v => v.Type == SuggestionType.EvidenceCategory).Value);
    }

    [Fact]
    public async Task CreateItem_WithoutCategory_LearnsNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var item = await svc.CreateItemAsync(new EvidenceItemInput { Name = "Aktentasche" }, null, null, Leader());

        using var db = ctx.NewContext();
        Assert.Null(db.EvidenceItems.Single(i => i.Id == item.Id).Category);
        Assert.Empty(db.ProfileSuggestions.Where(v => v.Type == SuggestionType.EvidenceCategory));
    }

    [Fact]
    public async Task UpdateItem_ChangesCategory_AndLearnsSuggestion()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var item = await svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());

        await svc.UpdateItemAsync(item.Id,
            new EvidenceItemInput { Name = "Kokainpaket", Category = "Betäubungsmittel" }, Leader());

        using var db = ctx.NewContext();
        Assert.Equal("Betäubungsmittel", db.EvidenceItems.Single(i => i.Id == item.Id).Category);
        // the old value stays in the catalog; only the admin panel removes values
        Assert.Equal(2, db.ProfileSuggestions.Count(v => v.Type == SuggestionType.EvidenceCategory));
    }

    [Fact]
    public async Task UpdateItem_ClearingCategory_NullsIt()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var item = await svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());

        await svc.UpdateItemAsync(item.Id, new EvidenceItemInput { Name = "Kokainpaket", Category = "   " }, Leader());

        using var db = ctx.NewContext();
        Assert.Null(db.EvidenceItems.Single(i => i.Id == item.Id).Category);
    }

    [Fact]
    public async Task GetItems_FiltersByCategory()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Pistole", Category = "Waffen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Aktentasche" }, null, null, Leader());

        var rows = await svc.GetItemsAsync(category: "Drogen");

        Assert.Equal("Kokainpaket", Assert.Single(rows).Item.Name);
    }

    [Fact]
    public async Task GetItems_NoneSentinel_ReturnsOnlyUncategorised()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Aktentasche" }, null, null, Leader());

        var rows = await svc.GetItemsAsync(category: EvidenceCategories.None);

        Assert.Equal("Aktentasche", Assert.Single(rows).Item.Name);
    }

    [Fact]
    public async Task GetItems_NullCategory_ReturnsEverything()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Aktentasche" }, null, null, Leader());

        Assert.Equal(2, (await svc.GetItemsAsync()).Count);
    }

    [Fact]
    public async Task GetItems_CombinesSearchAndCategory()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainwaage", Category = "Werkzeug" }, null, null, Leader());

        var rows = await svc.GetItemsAsync("Kokain", "Drogen");

        Assert.Equal("Kokainpaket", Assert.Single(rows).Item.Name);
    }

    [Fact]
    public async Task GetItems_CategoryWithoutMatches_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await DepositAsync(svc, "Kokainpaket", 5);

        // an empty match set must not fall back to reading the whole ledger
        Assert.Empty(await svc.GetItemsAsync(category: "Waffen"));
    }

    [Fact]
    public async Task CreateItem_OverlongCategory_Throws_AndWritesNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = new string('x', 301) }, null, null, Leader()));

        using var db = ctx.NewContext();
        Assert.Empty(db.EvidenceItems);
    }

    [Fact]
    public async Task UpdateItem_OverlongCategory_Throws_AndKeepsOldValue()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var item = await svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateItemAsync(item.Id,
            new EvidenceItemInput { Name = "Kokainpaket", Category = new string('x', 301) }, Leader()));

        using var db = ctx.NewContext();
        Assert.Equal("Drogen", db.EvidenceItems.Single().Category);
    }

    [Fact]
    public async Task CreateItem_MaximumLengthCategory_IsAccepted()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var category = new string('x', 300);

        var item = await svc.CreateItemAsync(
            new EvidenceItemInput { Name = "Kokainpaket", Category = category }, null, null, Leader());

        using var db = ctx.NewContext();
        Assert.Equal(category, db.EvidenceItems.Single(i => i.Id == item.Id).Category);
    }

    [Fact]
    public async Task CreateEntry_AutoCreatedItem_TakesLineCategory_AndLearnsSuggestion()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
            i.Lines.Add(new EvidenceLineInput { ItemName = "Handgranate", Quantity = 2, NewItemCategory = "Waffen" })),
            Leader());

        using var db = ctx.NewContext();
        Assert.Equal("Waffen", db.EvidenceItems.Single(i => i.Name == "Handgranate").Category);
        Assert.Equal("Waffen", db.ProfileSuggestions
            .Single(v => v.Type == SuggestionType.EvidenceCategory).Value);
    }

    [Fact]
    public async Task CreateEntry_ExistingItem_KeepsCategory_WhenLineSuppliesAnother()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Pistole", Category = "Waffen" }, null, null, Leader());

        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit, i =>
            i.Lines.Add(new EvidenceLineInput { ItemName = "pistole", Quantity = 1, NewItemCategory = "Spielzeug" })),
            Leader());

        using var db = ctx.NewContext();
        Assert.Equal("Waffen", db.EvidenceItems.Single().Category);
    }

    [Fact]
    public async Task GetEntries_FiltersByPositionCategory()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Pistole", Category = "Waffen" }, null, null, Leader());
        await DepositAsync(svc, "Kokainpaket", 5);
        await DepositAsync(svc, "Pistole", 1);

        var rows = await svc.GetEntriesAsync(category: "Drogen");

        Assert.Equal("Kokainpaket", Assert.Single(Assert.Single(rows).Lines).ItemName);
    }

    [Fact]
    public async Task GetEntries_NoneSentinel_ReturnsEntriesWithUncategorisedPositions()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await DepositAsync(svc, "Kokainpaket", 5);
        await DepositAsync(svc, "Aktentasche", 1);

        var rows = await svc.GetEntriesAsync(category: EvidenceCategories.None);

        Assert.Equal("Aktentasche", Assert.Single(Assert.Single(rows).Lines).ItemName);
    }

    [Fact]
    public async Task GetItems_NoneSentinel_MatchesEmptyStringToo()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        using (var db = ctx.NewContext())
        {
            // the write paths trim to null, but the SQL branch has to cover "" as well
            db.EvidenceItems.Add(new EvidenceItem { Name = "Aktentasche", Category = string.Empty });
            db.SaveChanges();
        }
        await DepositAsync(svc, "Aktentasche", 1);

        Assert.Equal("Aktentasche",
            Assert.Single(await svc.GetItemsAsync(category: EvidenceCategories.None)).Item.Name);
    }

    [Fact]
    public async Task GetEntries_UnknownCategory_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await DepositAsync(svc, "Kokainpaket", 5);

        Assert.Empty(await svc.GetEntriesAsync(category: "Waffen"));
    }

    [Fact]
    public async Task UpdateEntry_ReplacingLines_KeepsThemOnOneCommit()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var created = await svc.CreateEntryAsync(Entry(EvidenceEntryType.Deposit,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 3 })), Leader());

        await svc.UpdateEntryAsync(created.Id, Entry(EvidenceEntryType.Deposit, i =>
        {
            i.Lines.Add(new EvidenceLineInput { ItemName = "Pistole", Quantity = 1 });
            i.Lines.Add(new EvidenceLineInput { ItemName = "Handgranate", Quantity = 2, NewItemCategory = "Waffen" });
        }), Leader());

        using var db = ctx.NewContext();
        Assert.Equal(2, db.EvidenceEntryLines.Count(l => l.EntryId == created.Id));
        Assert.Equal("Waffen", db.EvidenceItems.Single(i => i.Name == "Handgranate").Category);
    }

    [Fact]
    public async Task GetEntries_CombinesTypeAndCategory()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateItemAsync(new EvidenceItemInput { Name = "Kokainpaket", Category = "Drogen" }, null, null, Leader());
        await DepositAsync(svc, "Kokainpaket", 5);
        await svc.CreateEntryAsync(Entry(EvidenceEntryType.Withdrawal,
            i => i.Lines.Add(new EvidenceLineInput { ItemName = "Kokainpaket", Quantity = 2 })), Leader());

        var rows = await svc.GetEntriesAsync(EvidenceEntryType.Withdrawal, null, "Drogen");

        Assert.Equal(EvidenceEntryType.Withdrawal, Assert.Single(rows).Entry.Type);
    }

    private static int CountEntries(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        return db.EvidenceEntries.Count();
    }
}
