using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Parties;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PartyService"/> against in-memory SQLite.</summary>
public sealed class PartyServiceTests
{
    // Rank >= SpecialAgentInCharge(4) or admin => leadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin, cannot read classified.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ViewerScope LeaderScope() => ViewerScope.From(Leader());
    private static ViewerScope PlainScope() => ViewerScope.From(Junior());

    private static IPartyPhotoStorageService PhotoStorage(
        bool allowed = true, long maxBytes = 10 * 1024 * 1024, string saved = "saved.jpg")
    {
        var s = Substitute.For<IPartyPhotoStorageService>();
        s.IsAllowedType(Arg.Any<string>()).Returns(allowed);
        s.MaxBytes.Returns(maxBytes);
        s.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(saved);
        return s;
    }

    private static PartyService NewService(SqliteTestContext ctx,
        IPersonService? person = null, IThreatScoreService? threat = null, IProfileSuggestionService? suggestion = null,
        IPartyPhotoStorageService? photo = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-PT-2026-0001");
        return new PartyService(
            ctx.Factory,
            caseNo,
            suggestion ?? Substitute.For<IProfileSuggestionService>(),
            person ?? Substitute.For<IPersonService>(),
            threat ?? Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(),
            photo ?? PhotoStorage());
    }

    private static Party NewParty(string id, string name = "Grove Street", Action<Party>? configure = null)
    {
        var p = new Party
        {
            Id = id,
            Name = name,
            CaseNumber = $"FIB-PT-2026-{id}",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(p);
        return p;
    }

    // ---- GetListAsync ----

    [Fact]
    public async Task GetListAsync_ReturnsNonDeleted_OrderedByModifiedDescending()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("a", "Alpha", p => p.ModifiedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Parties.Add(NewParty("b", "Bravo", p => p.ModifiedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(LeaderScope());

        Assert.Equal(new[] { "b", "a" }, result.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_ExcludesClassified_ForPlainScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("open", "Open"));
            db.Parties.Add(NewParty("secret", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forPlain = await svc.GetListAsync(PlainScope());
        var forLeader = await svc.GetListAsync(LeaderScope());

        Assert.Equal(new[] { "open" }, forPlain.Select(p => p.Id).ToArray());
        Assert.Equal(2, forLeader.Count);
    }

    // ---- GetDetailAsync ----

    [Fact]
    public async Task GetDetailAsync_ReturnsParty_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("p1", LeaderScope());

        Assert.NotNull(result);
        Assert.Equal("Ballas", result!.Name);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenClassified_AndPlainScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Ballas", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("p1", PlainScope()));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("missing", LeaderScope()));
    }

    // ---- GetTrashAsync ----

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("live", "Live"));
            db.Parties.Add(NewParty("dead", "Dead", p =>
            {
                p.IsDeleted = true;
                p.DeletedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetTrashAsync();

        Assert.Equal(new[] { "dead" }, result.Select(p => p.Id).ToArray());
    }

    // ---- SearchAsync ----

    [Fact]
    public async Task SearchAsync_FiltersByNameOrCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("a", "Grove Street Families"));
            db.Parties.Add(NewParty("b", "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var byName = await svc.SearchAsync("Grove", isLeadership: true);
        var byCase = await svc.SearchAsync("FIB-PT-2026-b", isLeadership: true);

        Assert.Equal(new[] { "a" }, byName.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "b" }, byCase.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task SearchAsync_ExcludesClassified_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("open", "Open"));
            db.Parties.Add(NewParty("secret", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var nonLead = await svc.SearchAsync(null, isLeadership: false);
        var lead = await svc.SearchAsync(null, isLeadership: true);

        Assert.Equal(new[] { "open" }, nonLead.Select(p => p.Id).ToArray());
        Assert.Equal(2, lead.Count);
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_PersistsParty_WithMemberAndCreatorAgent()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person("person-1", "Big Smoke");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PartyInput
        {
            Name = "  Grove Street  ",
            Members = { new PartyMemberInput { PersonId = "person-1", Role = "Boss", IsLead = true } },
        };

        var created = await svc.CreateAsync(input, Leader());

        Assert.Equal("FIB-PT-2026-0001", created.CaseNumber);
        Assert.Equal("Grove Street", created.Name);

        using var check = ctx.NewContext();
        var members = await check.PartyMembers.Where(m => m.PartyId == created.Id).ToListAsync();
        Assert.Single(members);
        Assert.Equal("person-1", members[0].PersonId);
        Assert.True(members[0].IsLead);

        var agents = await check.PartyAgents.Where(a => a.PartyId == created.Id).ToListAsync();
        Assert.Single(agents);
        Assert.Equal("lead", agents[0].AgentId);
        Assert.True(agents[0].IsInvestigationLead);
    }

    [Fact]
    public async Task CreateAsync_AddsClassificationHistory_WhenClassified()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new PartyInput
        {
            Name = "Verdachtsfall",
            Classification = Classification.ReviewCase,
            ClassificationJustification = "Verdacht auf Waffenhandel",
        };

        var created = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Parties.SingleAsync(p => p.Id == created.Id);
        Assert.Equal(Classification.ReviewCase, stored.Classification);
        var history = await check.ClassificationHistory
            .Where(e => e.EntityType == nameof(Party) && e.EntityId == created.Id).ToListAsync();
        Assert.Single(history);
        Assert.Equal(Classification.ReviewCase, history[0].Value);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenMayNotAssignSecrecyLevel()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new PartyInput { Name = "Secret", SecrecyLevel = DocumentClassification.Leadership };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Junior()));
    }

    // ---- RefreshAsync ----

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Old Name"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PartyInput { Name = "  New Name  ", Description = "Desc", Targets = "Targets" };

        await svc.RefreshAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Parties.SingleAsync(p => p.Id == "p1");
        Assert.Equal("New Name", stored.Name);
        Assert.Equal("Desc", stored.Description);
        Assert.Equal("Targets", stored.Targets);
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", new PartyInput { Name = "X" }, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassified_AndNotPrivileged()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("p1", new PartyInput { Name = "X" }, Junior()));
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task DeleteAsync_RemovesParty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("p1", Leader());

        // interceptor absent in tests => hard delete; row gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.Parties.AnyAsync(p => p.Id == "p1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("p1", Junior()));
    }

    // ---- RestoreAsync ----

    [Fact]
    public async Task RestoreAsync_UndeletesParty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Dead", p =>
            {
                p.IsDeleted = true;
                p.DeletedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
                p.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("p1", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Parties.SingleAsync(p => p.Id == "p1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("p1", Junior()));
    }

    // ---- ClassificationSetAsync ----

    [Fact]
    public async Task ClassificationSetAsync_SetsValue_AndRecordsHistory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.ClassificationSetAsync("p1", Classification.SuspicionCase, "Grund", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Parties.SingleAsync(p => p.Id == "p1");
        Assert.Equal(Classification.SuspicionCase, stored.Classification);
        var history = await check.ClassificationHistory
            .Where(e => e.EntityType == nameof(Party) && e.EntityId == "p1").ToListAsync();
        Assert.Single(history);
        Assert.Equal("Grund", history[0].Justification);
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_OnHighestWithoutRank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // rank gate: "SecuredStateThreatening" requires Senior Special Agent+ or admin.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("p1", Classification.SecuredStateThreatening, null, Junior()));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenClassified_AndNotPrivileged()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ClassificationSetAsync("p1", Classification.ReviewCase, null, Junior()));
    }

    // ---- GetClassificationHistoryAsync ----

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEntries_OrderedByTimestampDescending()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Party), EntityId = "p1", Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Party), EntityId = "p1", Value = Classification.SuspicionCase,
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetClassificationHistoryAsync("p1", LeaderScope());

        Assert.Equal(2, result.Count);
        Assert.Equal(Classification.SuspicionCase, result[0].Value);
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Party), EntityId = "p1", Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetClassificationHistoryAsync("p1", PlainScope());

        Assert.Empty(result);
    }

    // ---- GetMembersAsync ----

    [Fact]
    public async Task GetMembersAsync_ReturnsVisibleMembers_LeadsFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.People.Add(Seed.Person("m1", "Zulu Member"));
            db.People.Add(Seed.Person("m2", "Alpha Member"));
            db.PartyMembers.Add(new PartyMember { PartyId = "p1", PersonId = "m1", IsLead = false });
            db.PartyMembers.Add(new PartyMember { PartyId = "p1", PersonId = "m2", IsLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetMembersAsync("p1", LeaderScope());

        Assert.Equal(new[] { "m2", "m1" }, result.Select(m => m.PersonId).ToArray());
    }

    // ---- MemberAddAsync ----

    [Fact]
    public async Task MemberAddAsync_AddsMember_AndRecalculatesScore()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.People.Add(Seed.Person("person-1", "Ryder"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.MemberAddAsync("p1", new PartyMemberInput { PersonId = "person-1", Role = "Soldier" }, Leader());

        using var check = ctx.NewContext();
        var member = await check.PartyMembers.SingleAsync(m => m.PartyId == "p1" && m.PersonId == "person-1");
        Assert.Equal("Soldier", member.Role);
        await threat.Received(1).NewCalculatePersonScoreAsync("person-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberAddAsync_Throws_OnDuplicateMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.People.Add(Seed.Person("person-1"));
            db.PartyMembers.Add(new PartyMember { PartyId = "p1", PersonId = "person-1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MemberAddAsync("p1", new PartyMemberInput { PersonId = "person-1" }, Leader()));
    }

    [Fact]
    public async Task MemberAddAsync_Throws_WhenClassified_AndNotPrivileged()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.People.Add(Seed.Person("person-1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MemberAddAsync("p1", new PartyMemberInput { PersonId = "person-1" }, Junior()));
    }

    // ---- MemberChangeAsync ----

    [Fact]
    public async Task MemberChangeAsync_UpdatesRoleAndLead()
    {
        using var ctx = new SqliteTestContext();
        var member = new PartyMember { PartyId = "p1", PersonId = "person-1", Role = "Old", IsLead = false };
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.People.Add(Seed.Person("person-1"));
            db.PartyMembers.Add(member);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MemberChangeAsync(member.Id, "  New Role  ", isLead: true, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PartyMembers.SingleAsync(m => m.Id == member.Id);
        Assert.Equal("New Role", stored.Role);
        Assert.True(stored.IsLead);
    }

    [Fact]
    public async Task MemberChangeAsync_Throws_OnUnknownMember()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MemberChangeAsync("missing", "Role", false, Leader()));
    }

    // ---- MemberRemoveAsync ----

    [Fact]
    public async Task MemberRemoveAsync_RemovesMember()
    {
        using var ctx = new SqliteTestContext();
        var member = new PartyMember { PartyId = "p1", PersonId = "person-1" };
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.People.Add(Seed.Person("person-1"));
            db.PartyMembers.Add(member);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MemberRemoveAsync(member.Id, Leader());

        // interceptor absent => hard delete; row gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.PartyMembers.AnyAsync(m => m.Id == member.Id));
    }

    [Fact]
    public async Task MemberRemoveAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // returns without throwing when the membership does not exist.
        await svc.MemberRemoveAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.PartyMembers.CountAsync());
    }

    // ---- GetAgentsAsync ----

    [Fact]
    public async Task GetAgentsAsync_ReturnsAllocations_LeadsFirstThenByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.Users.Add(Seed.Agent("ag-lead", configure: a => a.Codename = "Zeta"));
            db.Users.Add(Seed.Agent("ag-plain", configure: a => a.Codename = "Alpha"));
            db.PartyAgents.Add(new PartyAgent { PartyId = "p1", AgentId = "ag-plain", IsInvestigationLead = false });
            db.PartyAgents.Add(new PartyAgent { PartyId = "p1", AgentId = "ag-lead", IsInvestigationLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAgentsAsync("p1");

        Assert.Equal(new[] { "ag-lead", "ag-plain" }, result.Select(a => a.AgentId).ToArray());
    }

    // ---- GetInvestigationLeadAsync ----

    [Fact]
    public async Task GetInvestigationLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.Users.Add(Seed.Agent("ag-lead", configure: a => a.Codename = "Lead"));
            db.Users.Add(Seed.Agent("ag-plain", configure: a => a.Codename = "Plain"));
            db.PartyAgents.Add(new PartyAgent { PartyId = "p1", AgentId = "ag-lead", IsInvestigationLead = true });
            db.PartyAgents.Add(new PartyAgent { PartyId = "p1", AgentId = "ag-plain", IsInvestigationLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetInvestigationLeadAsync("p1");

        Assert.Single(result);
        Assert.Equal("ag-lead", result[0].AgentId);
    }

    // ---- AgentAllocateAsync ----

    [Fact]
    public async Task AgentAllocateAsync_AddsAllocation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentAllocateAsync("p1", "target", asInvestigationLead: false, Leader());

        using var check = ctx.NewContext();
        var alloc = await check.PartyAgents.SingleAsync(a => a.PartyId == "p1" && a.AgentId == "target");
        Assert.False(alloc.IsInvestigationLead);
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenNotLeadershipOrEL()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // junior is neither leadership nor an investigation lead of this file.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("p1", "target", asInvestigationLead: false, Junior()));
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
            db.Parties.Add(NewParty("p1"));
            db.Users.Add(NotSelectable(id));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("p1", id, asInvestigationLead: false, Leader()));
    }

    /// <summary>An agent shape no picker offers; the service must reject it too.</summary>
    private static NOOSE_Website.Data.Entities.Agent NotSelectable(string id) => id switch
    {
        "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
        "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
        "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
        _ => Seed.Agent(id, status: AgentStatus.Terminated),
    };

    // ---- AgentRemoveAsync ----

    [Fact]
    public async Task AgentRemoveAsync_RemovesAllocation()
    {
        using var ctx = new SqliteTestContext();
        var alloc = new PartyAgent { PartyId = "p1", AgentId = "target" };
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyAgents.Add(alloc);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync(alloc.Id, Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.PartyAgents.AnyAsync(a => a.Id == alloc.Id));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.PartyAgents.CountAsync());
    }

    // ---- InvestigationLeadSetAsync ----

    [Fact]
    public async Task InvestigationLeadSetAsync_SetsFlag()
    {
        using var ctx = new SqliteTestContext();
        var alloc = new PartyAgent { PartyId = "p1", AgentId = "target", IsInvestigationLead = false };
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyAgents.Add(alloc);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.InvestigationLeadSetAsync(alloc.Id, @is: true, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PartyAgents.SingleAsync(a => a.Id == alloc.Id);
        Assert.True(stored.IsInvestigationLead);
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.InvestigationLeadSetAsync("any-id", true, Junior()));
    }

    // ---- GetHistoryAsync ----

    [Fact]
    public async Task GetHistoryAsync_ReturnsAuditForPartyAndMembers()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Party), EntityId = "p1", Action = AuditAction.Created,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "SomethingElse", EntityId = "p1", Action = AuditAction.Modified,
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetHistoryAsync("p1", isLeadership: true);

        Assert.Single(result);
        Assert.Equal(nameof(Party), result[0].EntityType);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Party), EntityId = "p1", Action = AuditAction.Created,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetHistoryAsync("p1", isLeadership: false);

        Assert.Empty(result);
    }

    // ==================== GetPhotosAsync ====================

    [Fact]
    public async Task GetPhotosAsync_OrdersTitleImageFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "early", PartyId = "p1", FileNameSaved = "a.jpg", IsTitleImage = false, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PartyPhotos.Add(new PartyPhoto { Id = "title", PartyId = "p1", FileNameSaved = "b.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotosAsync("p1");

        Assert.Equal("title", result[0].Id);
    }

    // ==================== GetPhotoWithPartyAsync ====================

    [Fact]
    public async Task GetPhotoWithPartyAsync_ReturnsPhoto_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph1", PartyId = "p1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotoWithPartyAsync("ph1", PlainScope());

        Assert.NotNull(result);
        Assert.Equal("ph1", result!.Id);
    }

    [Fact]
    public async Task GetPhotoWithPartyAsync_ReturnsNull_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph1", PartyId = "p1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetPhotoWithPartyAsync("ph1", PlainScope()));
    }

    // A TRU record reads to TRU, not to leadership alone: the coarse
    // "classified, so leadership" check would lock its own audience out.
    [Fact]
    public async Task GetPhotoWithPartyAsync_ReturnsPhoto_ForTruRecord_AndTruAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", configure: p => p.SecrecyLevel = DocumentClassification.Tru));
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph1", PartyId = "p1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var tru = ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.Trainee).AsTru().Build();

        Assert.NotNull(await svc.GetPhotoWithPartyAsync("ph1", ViewerScope.From(tru)));
    }

    // ==================== PhotoAddAsync ====================

    [Fact]
    public async Task PhotoAddAsync_SavesPhoto_AsTitle_WhenFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.SaveChanges();
        }
        var photo = PhotoStorage(saved: "stored.jpg");
        var svc = NewService(ctx, photo: photo);

        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.PhotoAddAsync("p1", content, "orig.jpg", "image/jpeg", 3, Leader());

        Assert.Equal("stored.jpg", result.FileNameSaved);
        Assert.True(result.IsTitleImage);
        using var check = ctx.NewContext();
        var stored = await check.PartyPhotos.SingleAsync(p => p.PartyId == "p1");
        Assert.Equal("lead", stored.CreatedById);
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnDisallowedType()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx, photo: PhotoStorage(allowed: false));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("p1", content, "x.exe", "application/octet-stream", 1, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnOversizedFile()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx, photo: PhotoStorage(maxBytes: 10));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("p1", content, "orig.jpg", "image/jpeg", 11, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PhotoAddAsync("p1", content, "orig.jpg", "image/jpeg", 1, Junior()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnUnknownParty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("nope", content, "orig.jpg", "image/jpeg", 1, Leader()));
    }

    // ==================== PhotoRemoveAsync ====================

    [Fact]
    public async Task PhotoRemoveAsync_RemovesRecord_AndDeletesFile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph1", PartyId = "p1", FileNameSaved = "gone.jpg" });
            db.SaveChanges();
        }
        var photo = PhotoStorage();
        var svc = NewService(ctx, photo: photo);

        await svc.PhotoRemoveAsync("ph1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.PartyPhotos.AnyAsync(p => p.Id == "ph1"));
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
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph1", PartyId = "p1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.PartyPhotos.Add(new PartyPhoto { Id = "ph2", PartyId = "p1", FileNameSaved = "b.jpg", IsTitleImage = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AsTitleImageSetAsync("ph2", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.PartyPhotos.SingleAsync(p => p.Id == "ph1")).IsTitleImage);
        Assert.True((await check.PartyPhotos.SingleAsync(p => p.Id == "ph2")).IsTitleImage);
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
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "title", PartyId = "p1", FileNameSaved = "a.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PartyPhotos.Add(new PartyPhoto { Id = "young", PartyId = "p1", FileNameSaved = "c.jpg", CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PartyPhotos.Add(new PartyPhoto { Id = "old", PartyId = "p1", FileNameSaved = "b.jpg", CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.True((await check.PartyPhotos.SingleAsync(p => p.Id == "old")).IsTitleImage);
        Assert.False((await check.PartyPhotos.SingleAsync(p => p.Id == "young")).IsTitleImage);
    }

    [Fact]
    public async Task PhotoRemoveAsync_LeavesNoTitleImage_WhenTheLastPhotoGoes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "title", PartyId = "p1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.Empty(await check.PartyPhotos.ToListAsync());
    }

    // A sibling record must not inherit the mark across file boundaries.
    [Fact]
    public async Task PhotoRemoveAsync_PromotesOnlyWithinTheSameRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(NewParty("p1"));
            db.Parties.Add(NewParty("p2", "Andere Partei"));
            db.PartyPhotos.Add(new PartyPhoto { Id = "title", PartyId = "p1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.PartyPhotos.Add(new PartyPhoto { Id = "foreign", PartyId = "p2", FileNameSaved = "b.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.PartyPhotos.SingleAsync(p => p.Id == "foreign")).IsTitleImage);
    }
}
