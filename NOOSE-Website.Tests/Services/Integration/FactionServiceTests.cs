using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Factions;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FactionService"/> against in-memory SQLite.</summary>
public sealed class FactionServiceTests
{
    // --- actors ---
    // Director: leadership + highest-classification.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not highest-classification, may not read classified.
    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // TeamLead without admin => read-only supervisor (fails RequireWriteAccess).
    private static ClaimsPrincipal ReadOnly(string id = "ro")
        => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().Build();

    // --- collaborator factories ---
    private static ICaseNumberService CaseNo(string value = "FIB-F-2026-0001")
    {
        var c = Substitute.For<ICaseNumberService>();
        c.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(value);
        return c;
    }

    private static IFactionPhotoStorageService PhotoStorage(
        bool allowed = true, long maxBytes = 10 * 1024 * 1024, string saved = "saved.jpg")
    {
        var s = Substitute.For<IFactionPhotoStorageService>();
        s.IsAllowedType(Arg.Any<string>()).Returns(allowed);
        s.MaxBytes.Returns(maxBytes);
        s.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(saved);
        return s;
    }

    private static FactionService NewService(
        SqliteTestContext ctx,
        IThreatScoreService? threat = null,
        IPersonService? personService = null,
        IFactionPhotoStorageService? photo = null,
        ICaseNumberService? caseNo = null,
        IProfileSuggestionService? suggestion = null)
        => new(
            ctx.Factory,
            caseNo ?? CaseNo(),
            suggestion ?? Substitute.For<IProfileSuggestionService>(),
            personService ?? Substitute.For<IPersonService>(),
            photo ?? PhotoStorage(),
            threat ?? Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<IPublicFactionProfileService>());

    // ==================== GetListAsync ====================

    [Fact]
    public async Task GetListAsync_ReturnsAllVisible_ForLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => { f.IsClassified = true; f.CaseNumber = "F-1"; }));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos", configure: f => f.CaseNumber = "F-2"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(Leader()));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetListAsync_ExcludesClassified_ForNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => { f.IsClassified = true; f.CaseNumber = "F-1"; }));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos", configure: f => f.CaseNumber = "F-2"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(LowRank()));

        Assert.Single(result);
        Assert.Equal("f2", result[0].Id);
    }

    // ==================== GetDetailAsync ====================

    [Fact]
    public async Task GetDetailAsync_ReturnsFaction_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("f1", ViewerScope.From(LowRank()));

        Assert.NotNull(result);
        Assert.Equal("Ballas", result!.Name);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("f1", ViewerScope.From(LowRank())));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("nope", ViewerScope.From(Leader())));
    }

    // ==================== GetTrashAsync ====================

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "live", name: "Live", configure: f => f.CaseNumber = "F-live"));
            db.Factions.Add(Seed.Faction(id: "dead", name: "Dead", configure: f =>
            {
                f.CaseNumber = "F-dead";
                f.IsDeleted = true;
                f.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetTrashAsync();

        Assert.Single(result);
        Assert.Equal("dead", result[0].Id);
    }

    // ==================== SearchAsync ====================

    [Fact]
    public async Task SearchAsync_FiltersByName_AndExcludesClassified_ForNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas Gang", configure: f => f.CaseNumber = "F-1"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Ballas Secret", configure: f => { f.IsClassified = true; f.CaseNumber = "F-2"; }));
            db.Factions.Add(Seed.Faction(id: "f3", name: "Vagos", configure: f => f.CaseNumber = "F-3"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("Ballas", isLeadership: false);

        // classified "Ballas Secret" excluded for non-leadership.
        Assert.Single(result);
        Assert.Equal("f1", result[0].Id);
    }

    [Fact]
    public async Task SearchAsync_LeadershipSeesClassifiedMatches()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas Gang", configure: f => f.CaseNumber = "F-1"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Ballas Secret", configure: f => { f.IsClassified = true; f.CaseNumber = "F-2"; }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("Ballas", isLeadership: true);

        Assert.Equal(2, result.Count);
    }

    // ==================== CreateAsync ====================

    [Fact]
    public async Task CreateAsync_PersistsFaction_WithCaseNumber_CreatorLead_AndHistory()
    {
        using var ctx = new SqliteTestContext();
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);
        var input = new FactionInput { Name = "  Ballas  ", Classification = Classification.ReviewCase };

        var faction = await svc.CreateAsync(input, Leader("lead"));

        Assert.Equal("FIB-F-2026-0001", faction.CaseNumber);
        Assert.Equal("Ballas", faction.Name);

        using var db = ctx.NewContext();
        Assert.True(await db.Factions.AnyAsync(f => f.Id == faction.Id));
        // creator auto-assigned as investigation lead
        var agent = await db.FactionAgents.SingleAsync(a => a.FactionId == faction.Id);
        Assert.Equal("lead", agent.AgentId);
        Assert.True(agent.IsInvestigationLead);
        // classification history written
        Assert.True(await db.ClassificationHistory.AnyAsync(
            h => h.EntityType == nameof(Faction) && h.EntityId == faction.Id && h.Value == Classification.ReviewCase));
        await threat.Received().NewCalculateAsync(faction.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithExistingMember_CreatesMembership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new FactionInput
        {
            Name = "Ballas",
            Members = { new MemberInput { PersonId = "p1", Rank = "Boss", IsLead = true } },
        };

        var faction = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var member = await check.FactionMembers.SingleAsync(m => m.FactionId == faction.Id);
        Assert.Equal("p1", member.PersonId);
        Assert.True(member.IsLead);
    }

    [Fact]
    public async Task CreateAsync_Throws_OnSecuredStateThreatening_ForLowRank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new FactionInput { Name = "Ballas", Classification = Classification.SecuredStateThreatening };

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, LowRank()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenAssigningSecrecy_WithoutPermission()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new FactionInput { Name = "Ballas", SecrecyLevel = DocumentClassification.Leadership };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, LowRank()));
    }

    // ==================== RefreshAsync ====================

    [Fact]
    public async Task RefreshAsync_UpdatesMasterData_AndPropagatesRankRename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Old"));
            db.FactionRanks.Add(new FactionRank { Id = "r1", FactionId = "f1", Designation = "Soldat", Order = 0 });
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1", Rank = "Soldat" });
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);
        var input = new FactionInput
        {
            Name = "New Name",
            Ranks = { new RankInput { Id = "r1", Designation = "Boss" } },
        };

        await svc.RefreshAsync("f1", input, Leader());

        using var check = ctx.NewContext();
        var faction = await check.Factions.SingleAsync(f => f.Id == "f1");
        Assert.Equal("New Name", faction.Name);
        var member = await check.FactionMembers.SingleAsync(m => m.Id == "m1");
        Assert.Equal("Boss", member.Rank);
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("nope", new FactionInput { Name = "X" }, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassified_AndActorLacksAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("f1", new FactionInput { Name = "X" }, LowRank()));
    }

    // ==================== DeleteAsync ====================

    [Fact]
    public async Task DeleteAsync_RemovesFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("f1", "Dublette", Leader());

        // interceptor absent in tests => hard delete; row gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.Factions.AnyAsync(f => f.Id == "f1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("f1", "Dublette", LowRank()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync("nope", "Dublette", Leader()));
    }

    // ==================== RestoreAsync ====================

    [Fact]
    public async Task RestoreAsync_ClearsDeletedFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f =>
            {
                f.IsDeleted = true;
                f.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
                f.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("f1", Leader());

        using var check = ctx.NewContext();
        var faction = await check.Factions.IgnoreQueryFilters().SingleAsync(f => f.Id == "f1");
        Assert.False(faction.IsDeleted);
        Assert.Null(faction.DeletedAt);
        Assert.Null(faction.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("f1", LowRank()));
    }

    // ==================== ClassificationSetAsync ====================

    [Fact]
    public async Task ClassificationSetAsync_UpdatesClassification_AndWritesHistory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.ClassificationSetAsync("f1", Classification.SuspicionCase, "Grund", Leader());

        using var check = ctx.NewContext();
        var faction = await check.Factions.SingleAsync(f => f.Id == "f1");
        Assert.Equal(Classification.SuspicionCase, faction.Classification);
        Assert.True(await check.ClassificationHistory.AnyAsync(
            h => h.EntityId == "f1" && h.Value == Classification.SuspicionCase && h.Justification == "Grund"));
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_OnSecuredStateThreatening_ForLowRank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("f1", Classification.SecuredStateThreatening, null, LowRank()));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("nope", Classification.ReviewCase, null, Leader()));
    }

    // ==================== GetClassificationHistoryAsync ====================

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEntries_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Faction), EntityId = "f1", Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Faction), EntityId = "f1", Value = Classification.SuspicionCase,
                Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetClassificationHistoryAsync("f1", ViewerScope.From(Leader()));

        Assert.Equal(2, result.Count);
        Assert.Equal(Classification.SuspicionCase, result[0].Value);
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Faction), EntityId = "f1", Value = Classification.ReviewCase,
                Timestamp = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetClassificationHistoryAsync("f1", ViewerScope.From(LowRank()));

        Assert.Empty(result);
    }

    // ==================== GetMembersAsync ====================

    [Fact]
    public async Task GetMembersAsync_ReturnsVisibleMembers_LeadFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "pa", name: "Alpha", configure: p => p.CaseNumber = "P-a"));
            db.People.Add(Seed.Person(id: "pz", name: "Zeta", configure: p => p.CaseNumber = "P-z"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pa", IsLead = false });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pz", IsLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetMembersAsync("f1", ViewerScope.From(Leader()));

        Assert.Equal(2, result.Count);
        // IsLead desc first, regardless of person name.
        Assert.Equal("pz", result[0].PersonId);
    }

    [Fact]
    public async Task GetMembersAsync_OrdersByRankHierarchy()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionRanks.Add(new FactionRank { FactionId = "f1", Designation = "Boss", Order = 0 });
            db.FactionRanks.Add(new FactionRank { FactionId = "f1", Designation = "Lieutenant", Order = 1 });
            db.People.Add(Seed.Person(id: "pb", name: "Beta", configure: p => p.CaseNumber = "P-b"));
            db.People.Add(Seed.Person(id: "pl", name: "Lambda", configure: p => p.CaseNumber = "P-l"));
            db.People.Add(Seed.Person(id: "pn", name: "Noro", configure: p => p.CaseNumber = "P-n"));
            db.People.Add(Seed.Person(id: "px", name: "Xray", configure: p => p.CaseNumber = "P-x"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pl", Rank = "Lieutenant" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pn" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pb", Rank = "Boss" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "px", Rank = "Alt-Bezeichnung" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetMembersAsync("f1", ViewerScope.From(Leader()));

        // rank hierarchy first (Boss before Lieutenant); unknown ("Alt-Bezeichnung") and no rank last, then by name
        Assert.Equal(new[] { "pb", "pl", "pn", "px" }, result.Select(m => m.PersonId));
    }

    [Fact]
    public async Task GetMembersAsync_ExcludesClassifiedPerson_ForNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "pub", name: "Public", configure: p => p.CaseNumber = "P-pub"));
            db.People.Add(Seed.Person(id: "sec", name: "Secret", configure: p => { p.IsClassified = true; p.CaseNumber = "P-sec"; }));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "pub" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "sec" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetMembersAsync("f1", ViewerScope.From(LowRank()));

        Assert.Single(result);
        Assert.Equal("pub", result[0].PersonId);
    }

    // ==================== MemberAddAsync ====================

    [Fact]
    public async Task MemberAddAsync_AddsMembership_AndRecomputesScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.MemberAddAsync("f1", new MemberInput { PersonId = "p1", Rank = "Boss" }, Leader());

        using var check = ctx.NewContext();
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f1" && m.PersonId == "p1"));
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
        await threat.Received().NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberAddAsync_Throws_OnDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "p1"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MemberAddAsync("f1", new MemberInput { PersonId = "p1" }, Leader()));
    }

    [Fact]
    public async Task MemberAddAsync_Throws_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MemberAddAsync("f1", new MemberInput { PersonId = "p1" }, LowRank()));
    }

    // ==================== MembersBulkApplyAsync ====================

    [Fact]
    public async Task MembersBulkApplyAsync_AddsRemoves_ReturnsCounts()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "p1", configure: p => p.CaseNumber = "P-1"));
            db.People.Add(Seed.Person(id: "p2", configure: p => p.CaseNumber = "P-2"));
            db.People.Add(Seed.Person(id: "p3", configure: p => p.CaseNumber = "P-3"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });        // already a member
            db.FactionMembers.Add(new FactionMember { Id = "m3", FactionId = "f1", PersonId = "p3" }); // to remove
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.MembersBulkApplyAsync(
            "f1",
            new List<MemberInput> { new() { PersonId = "p1" }, new() { PersonId = "p2" } },
            new List<string> { "m3" },
            Leader());

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.AddedExisting);
        Assert.Equal(1, result.AlreadyMembers);
        Assert.Equal(1, result.Removed);

        using var check = ctx.NewContext();
        var ids = await check.FactionMembers.Where(m => m.FactionId == "f1").Select(m => m.PersonId).ToListAsync();
        Assert.Equal(new HashSet<string> { "p1", "p2" }, ids.ToHashSet());
    }

    [Fact]
    public async Task MembersBulkApplyAsync_CreatesNewPerson_ViaPersonService()
    {
        using var ctx = new SqliteTestContext();
        var newPerson = Seed.Person(id: "new1", name: "Neu");
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            // mirror what the real IPersonService.CreateAsync would persist
            db.People.Add(newPerson);
            db.SaveChanges();
        }
        var personService = Substitute.For<IPersonService>();
        personService.CreateAsync(Arg.Any<PersonInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(newPerson);
        var svc = NewService(ctx, personService: personService);

        var result = await svc.MembersBulkApplyAsync(
            "f1",
            new List<MemberInput> { new() { NewPersonName = "Neu" } },
            new List<string>(),
            Leader());

        Assert.Equal(1, result.Created);
        using var check = ctx.NewContext();
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f1" && m.PersonId == "new1"));
    }

    [Fact]
    public async Task MembersBulkApplyAsync_Throws_ForReadOnlyActor()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MembersBulkApplyAsync("f1", new List<MemberInput>(), new List<string>(), ReadOnly()));
    }

    [Fact]
    public async Task MemberAddAsync_Throws_ForReadOnlyActor()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MemberAddAsync("f1", new MemberInput { PersonId = "p1" }, ReadOnly()));
    }

    // ==================== RemoveFromOtherFactions ====================

    [Fact]
    public async Task MemberAddAsync_EndsOtherFactionMemberships_WhenRequested()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.Factions.Add(Seed.Faction(id: "f3"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        var ended = await svc.MemberAddAsync("f3",
            new MemberInput { PersonId = "p1", RemoveFromOtherFactions = true }, Leader());

        Assert.Equal(2, ended);
        using var check = ctx.NewContext();
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f3" && m.PersonId == "p1"));
        Assert.False(await check.FactionMembers.AnyAsync(m => m.FactionId == "f1" && m.PersonId == "p1"));
        Assert.False(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p1"));
        // only the new membership is left
        Assert.Equal(1, await check.FactionMembers.CountAsync(m => m.PersonId == "p1"));
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
        await threat.Received().NewCalculateAsync("f2", Arg.Any<CancellationToken>());
        await threat.Received().NewCalculateAsync("f3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberAddAsync_KeepsOtherFactions_ByDefault()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var ended = await svc.MemberAddAsync("f2", new MemberInput { PersonId = "p1" }, Leader());

        Assert.Equal(0, ended);
        using var check = ctx.NewContext();
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f1" && m.PersonId == "p1"));
    }

    [Fact]
    public async Task MemberAddAsync_SkipsClassifiedFaction_NotInAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2", configure: f => f.SecrecyLevel = DocumentClassification.Hrb));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var ended = await svc.MemberAddAsync("f1",
            new MemberInput { PersonId = "p1", RemoveFromOtherFactions = true }, LowRank());

        Assert.Equal(0, ended);
        using var check = ctx.NewContext();
        // the invisible membership stays; no hint in the result either
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p1"));
    }

    [Fact]
    public async Task MemberAddAsync_EndsClassifiedFaction_ForItsAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2", configure: f => f.SecrecyLevel = DocumentClassification.Hrb));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var hrb = ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.Trainee).AsHrb().Build();

        var ended = await svc.MemberAddAsync("f1",
            new MemberInput { PersonId = "p1", RemoveFromOtherFactions = true }, hrb);

        Assert.Equal(1, ended);
        using var check = ctx.NewContext();
        Assert.False(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p1"));
    }

    [Fact]
    public async Task MembersBulkApplyAsync_EndsOtherFactions_OnlyForRequestedRows()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika"));
            db.People.Add(Seed.Person(id: "p3", name: "Otto"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p2" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p3" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p3" }); // already a member
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.MembersBulkApplyAsync("f1",
            new List<MemberInput>
            {
                new() { PersonId = "p1", RemoveFromOtherFactions = true },
                new() { PersonId = "p2" },
                new() { PersonId = "p3", RemoveFromOtherFactions = true }, // already member: flag ignored
            },
            new List<string>(), Leader());

        Assert.Equal(2, result.AddedExisting);
        Assert.Equal(1, result.AlreadyMembers);
        Assert.Equal(1, result.RemovedFromOtherFactions);
        using var check = ctx.NewContext();
        Assert.False(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p1"));
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p2"));
        Assert.True(await check.FactionMembers.AnyAsync(m => m.FactionId == "f2" && m.PersonId == "p3"));
    }

    // ==================== MemberChangeAsync ====================

    [Fact]
    public async Task MemberChangeAsync_UpdatesRankAndLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1", Rank = "Soldat", IsLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MemberChangeAsync("m1", "  Boss  ", isLead: true, Leader());

        using var check = ctx.NewContext();
        var member = await check.FactionMembers.SingleAsync(m => m.Id == "m1");
        Assert.Equal("Boss", member.Rank);
        Assert.True(member.IsLead);
    }

    [Fact]
    public async Task MemberChangeAsync_Throws_OnUnknownMember()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MemberChangeAsync("nope", null, false, Leader()));
    }

    // ==================== MemberRemoveAsync ====================

    [Fact]
    public async Task MemberRemoveAsync_RemovesMembership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MemberRemoveAsync("m1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.FactionMembers.AnyAsync(m => m.Id == "m1"));
    }

    [Fact]
    public async Task MemberRemoveAsync_NoOp_OnUnknownMember()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // returns without throwing when the membership does not exist.
        await svc.MemberRemoveAsync("nope", Leader());
    }

    // ==================== GetAgentsAsync / GetInvestigationLeadAsync ====================

    [Fact]
    public async Task GetAgentsAsync_ReturnsAllocations_InvestigationLeadFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Users.Add(Seed.Agent(id: "a1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent(id: "a2", configure: a => a.Codename = "Alpha"));
            db.FactionAgents.Add(new FactionAgent { FactionId = "f1", AgentId = "a1", IsInvestigationLead = true });
            db.FactionAgents.Add(new FactionAgent { FactionId = "f1", AgentId = "a2", IsInvestigationLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAgentsAsync("f1");

        Assert.Equal(2, result.Count);
        Assert.Equal("a1", result[0].AgentId); // lead first despite codename ordering
    }

    [Fact]
    public async Task GetInvestigationLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Users.Add(Seed.Agent(id: "a1", configure: a => a.Codename = "Lead"));
            db.Users.Add(Seed.Agent(id: "a2", configure: a => a.Codename = "Other"));
            db.FactionAgents.Add(new FactionAgent { FactionId = "f1", AgentId = "a1", IsInvestigationLead = true });
            db.FactionAgents.Add(new FactionAgent { FactionId = "f1", AgentId = "a2", IsInvestigationLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetInvestigationLeadAsync("f1");

        Assert.Single(result);
        Assert.Equal("a1", result[0].AgentId);
    }

    // ==================== AgentAllocateAsync ====================

    [Fact]
    public async Task AgentAllocateAsync_AllocatesAgent_AsInvestigationLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Users.Add(Seed.Agent(id: "ag1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentAllocateAsync("f1", "ag1", asInvestigationLead: true, Leader());

        using var check = ctx.NewContext();
        var alloc = await check.FactionAgents.SingleAsync(a => a.FactionId == "f1" && a.AgentId == "ag1");
        Assert.True(alloc.IsInvestigationLead);
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_OnUnknownFaction()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("nope", "ag1", false, Leader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenNonLeaderAndNotInvestigationLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Users.Add(Seed.Agent(id: "ag1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // low-rank actor is neither leadership nor an EL of this faction.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("f1", "ag1", asInvestigationLead: false, LowRank()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAgentMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("f1", "ghost", asInvestigationLead: false, Leader()));
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Users.Add(NotSelectable(id));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("f1", id, asInvestigationLead: false, Leader()));
    }

    /// <summary>An agent shape no picker offers; the service must reject it too.</summary>
    private static NOOSE_Website.Data.Entities.Agent NotSelectable(string id) => id switch
    {
        "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
        "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
        "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
        _ => Seed.Agent(id, status: AgentStatus.Terminated),
    };

    // ==================== AgentRemoveAsync ====================

    [Fact]
    public async Task AgentRemoveAsync_RemovesAllocation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionAgents.Add(new FactionAgent { Id = "alloc1", FactionId = "f1", AgentId = "ag1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync("alloc1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.FactionAgents.AnyAsync(a => a.Id == "alloc1"));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_OnUnknown()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync("nope", Leader());
    }

    // ==================== InvestigationLeadSetAsync ====================

    [Fact]
    public async Task InvestigationLeadSetAsync_SetsFlag()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionAgents.Add(new FactionAgent { Id = "alloc1", FactionId = "f1", AgentId = "ag1", IsInvestigationLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.InvestigationLeadSetAsync("alloc1", @is: true, Leader());

        using var check = ctx.NewContext();
        var alloc = await check.FactionAgents.SingleAsync(a => a.Id == "alloc1");
        Assert.True(alloc.IsInvestigationLead);
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.InvestigationLeadSetAsync("alloc1", true, LowRank()));
    }

    // ==================== GetHistoryAsync ====================

    [Fact]
    public async Task GetHistoryAsync_ReturnsAuditEntries_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1" });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Faction), EntityId = "f1", Timestamp = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(FactionMember), EntityId = "m1", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Faction), EntityId = "other", Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetHistoryAsync("f1", isLeadership: true);

        // faction + member audits, but not the unrelated "other" faction entry.
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Faction), EntityId = "f1", Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetHistoryAsync("f1", isLeadership: false);

        Assert.Empty(result);
    }

    // ==================== GetPhotosAsync ====================

    [Fact]
    public async Task GetPhotosAsync_OrdersTitleImageFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "early", FactionId = "f1", FileNameSaved = "a.jpg", IsTitleImage = false, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.FactionPhotos.Add(new FactionPhoto { Id = "title", FactionId = "f1", FileNameSaved = "b.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotosAsync("f1");

        Assert.Equal("title", result[0].Id);
    }

    // ==================== GetPhotoWithFactionAsync ====================

    [Fact]
    public async Task GetPhotoWithFactionAsync_ReturnsPhoto_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "ph1", FactionId = "f1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotoWithFactionAsync("ph1", ViewerScope.From(LowRank()));

        Assert.NotNull(result);
        Assert.Equal("ph1", result!.Id);
    }

    [Fact]
    public async Task GetPhotoWithFactionAsync_ReturnsNull_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.FactionPhotos.Add(new FactionPhoto { Id = "ph1", FactionId = "f1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetPhotoWithFactionAsync("ph1", ViewerScope.From(LowRank())));
    }

    // ==================== PhotoAddAsync ====================

    [Fact]
    public async Task PhotoAddAsync_SavesPhoto_AsTitle_WhenFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        var photo = PhotoStorage(saved: "stored.jpg");
        var svc = NewService(ctx, photo: photo);

        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.PhotoAddAsync("f1", content, "orig.jpg", "image/jpeg", 3, Leader("me"));

        Assert.Equal("stored.jpg", result.FileNameSaved);
        Assert.True(result.IsTitleImage);
        using var check = ctx.NewContext();
        var stored = await check.FactionPhotos.SingleAsync(p => p.FactionId == "f1");
        Assert.Equal("me", stored.CreatedById);
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnDisallowedType()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx, photo: PhotoStorage(allowed: false));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("f1", content, "x.exe", "application/octet-stream", 1, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", configure: f => f.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx, photo: PhotoStorage());

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PhotoAddAsync("f1", content, "orig.jpg", "image/jpeg", 1, LowRank()));
    }

    // ==================== PhotoRemoveAsync ====================

    [Fact]
    public async Task PhotoRemoveAsync_RemovesRecord_AndDeletesFile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "ph1", FactionId = "f1", FileNameSaved = "gone.jpg" });
            db.SaveChanges();
        }
        var photo = PhotoStorage();
        var svc = NewService(ctx, photo: photo);

        await svc.PhotoRemoveAsync("ph1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.FactionPhotos.AnyAsync(p => p.Id == "ph1"));
        photo.Received().Delete("gone.jpg");
    }

    [Fact]
    public async Task PhotoRemoveAsync_NoOp_OnUnknown()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("nope", Leader());
    }

    // ==================== AsTitleImageSetAsync ====================

    [Fact]
    public async Task AsTitleImageSetAsync_MarksSingleTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "ph1", FactionId = "f1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.FactionPhotos.Add(new FactionPhoto { Id = "ph2", FactionId = "f1", FileNameSaved = "b.jpg", IsTitleImage = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AsTitleImageSetAsync("ph2", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.FactionPhotos.SingleAsync(p => p.Id == "ph1")).IsTitleImage);
        Assert.True((await check.FactionPhotos.SingleAsync(p => p.Id == "ph2")).IsTitleImage);
    }

    [Fact]
    public async Task AsTitleImageSetAsync_Throws_OnUnknown()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AsTitleImageSetAsync("nope", Leader()));
    }

    // Deleting the profile picture must hand the mark on, or the file would show the
    // placeholder icon while photos are still in the gallery.
    [Fact]
    public async Task PhotoRemoveAsync_PromotesOldestRemaining_WhenTitleImageGoes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "title", FactionId = "f1", FileNameSaved = "a.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.FactionPhotos.Add(new FactionPhoto { Id = "young", FactionId = "f1", FileNameSaved = "c.jpg", CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.FactionPhotos.Add(new FactionPhoto { Id = "old", FactionId = "f1", FileNameSaved = "b.jpg", CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.True((await check.FactionPhotos.SingleAsync(p => p.Id == "old")).IsTitleImage);
        Assert.False((await check.FactionPhotos.SingleAsync(p => p.Id == "young")).IsTitleImage);
    }

    [Fact]
    public async Task PhotoRemoveAsync_LeavesNoTitleImage_WhenTheLastPhotoGoes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "title", FactionId = "f1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.Empty(await check.FactionPhotos.ToListAsync());
    }

    // A sibling record must not inherit the mark across file boundaries.
    [Fact]
    public async Task PhotoRemoveAsync_PromotesOnlyWithinTheSameRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.FactionPhotos.Add(new FactionPhoto { Id = "title", FactionId = "f1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.FactionPhotos.Add(new FactionPhoto { Id = "foreign", FactionId = "f2", FileNameSaved = "b.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.FactionPhotos.SingleAsync(p => p.Id == "foreign")).IsTitleImage);
    }
}
