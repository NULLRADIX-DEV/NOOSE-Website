using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Groups;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PersonGroupService"/> against in-memory SQLite.</summary>
public sealed class PersonGroupServiceTests
{
    // Rank >= SpecialAgentInCharge(4) or admin => IsLeadership() and MayClassifiedRead().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin, no classified read.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Senior Special Agent(3): MayHighestClassification() true, but NOT leadership.
    private static ClaimsPrincipal Senior(string id = "senior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SeniorSpecialAgent).Build();

    private static ViewerScope Scope(ClaimsPrincipal p) => ViewerScope.From(p);
    private static ViewerScope LeaderScope() => Scope(Leader());
    private static ViewerScope JuniorScope() => Scope(Junior());

    private static IPersonGroupPhotoStorageService PhotoStorage(
        bool allowed = true, long maxBytes = 10 * 1024 * 1024, string saved = "saved.jpg")
    {
        var s = Substitute.For<IPersonGroupPhotoStorageService>();
        s.IsAllowedType(Arg.Any<string>()).Returns(allowed);
        s.MaxBytes.Returns(maxBytes);
        s.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(saved);
        return s;
    }

    private static PersonGroupService NewService(SqliteTestContext ctx,
        IPersonService? person = null, IThreatScoreService? threat = null,
        IPersonGroupPhotoStorageService? photo = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-G-2026-0001");
        return new PersonGroupService(
            ctx.Factory,
            caseNo,
            person ?? Substitute.For<IPersonService>(),
            threat ?? Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(),
            photo ?? PhotoStorage());
    }

    private static PersonGroup Group(string id, string name = "Die Bande", Action<PersonGroup>? configure = null)
    {
        var g = new PersonGroup
        {
            Id = id,
            Name = name,
            CaseNumber = "FIB-G-2026-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(g);
        return g;
    }

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_ReturnsGroups_OrderedByModifiedThenCreated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", "Alpha", g => g.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.PersonGroups.Add(Group("g2", "Bravo", g => g.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(LeaderScope());

        Assert.Equal(new[] { "g2", "g1" }, result.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_HidesClassified_FromNonClassifiedScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("open", "Open"));
            db.PersonGroups.Add(Group("secret", "Secret", g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forJunior = await svc.GetListAsync(JuniorScope());
        var forLeader = await svc.GetListAsync(LeaderScope());

        Assert.Equal(new[] { "open" }, forJunior.Select(g => g.Id).ToArray());
        Assert.Equal(2, forLeader.Count);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_ReturnsGroup_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var group = await svc.GetDetailAsync("g1", JuniorScope());

        Assert.NotNull(group);
        Assert.Equal("g1", group!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenClassifiedAndScopeCannotSee()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("g1", JuniorScope()));
        Assert.NotNull(await svc.GetDetailAsync("g1", LeaderScope()));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("ghost", LeaderScope()));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("live"));
            db.PersonGroups.Add(Group("dead", configure: g =>
            {
                g.IsDeleted = true;
                g.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var trash = await svc.GetTrashAsync();

        Assert.Equal(new[] { "dead" }, trash.Select(g => g.Id).ToArray());
    }

    // ---------- SearchAsync ----------

    [Fact]
    public async Task SearchAsync_FiltersByNameOrCaseNumber_OrdersByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", "Ballas Crew"));
            db.PersonGroups.Add(Group("g2", "Vagos"));
            db.PersonGroups.Add(Group("g3", "Ballas East"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("Ballas", isLeadership: true);

        Assert.Equal(new[] { "Ballas Crew", "Ballas East" }, result.Select(g => g.Name).ToArray());
    }

    [Fact]
    public async Task SearchAsync_ExcludesClassified_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("open", "Open Group"));
            db.PersonGroups.Add(Group("secret", "Secret Group", g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forMember = await svc.SearchAsync(null, isLeadership: false);
        var forLeader = await svc.SearchAsync(null, isLeadership: true);

        Assert.Equal(new[] { "open" }, forMember.Select(g => g.Id).ToArray());
        Assert.Equal(2, forLeader.Count);
    }

    [Fact]
    public async Task SearchAsync_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.PersonGroups.Add(Group($"g{i}", $"Group {i}"));
            }
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync(null, isLeadership: true, max: 2);

        Assert.Equal(2, result.Count);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsGroup_AndAddsCreatorAsInvestigationLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonGroupInput
        {
            Name = "  Die Bande  ",
            Description = "  ein Text  ",
            Kind = GroupsKind.Personality,
            EstimatedMemberCount = 5,
        };

        var group = await svc.CreateAsync(input, Leader());

        Assert.Equal("FIB-G-2026-0001", group.CaseNumber);
        Assert.Equal("Die Bande", group.Name);
        Assert.Equal("ein Text", group.Description);
        Assert.Equal(GroupsKind.Personality, group.Kind);
        Assert.Equal(5, group.EstimatedMemberCount);

        using var check = ctx.NewContext();
        Assert.True(await check.PersonGroups.AnyAsync(g => g.Id == group.Id));
        var agent = await check.PersonGroupAgents.SingleAsync(a => a.PersonGroupId == group.Id);
        Assert.Equal("lead", agent.AgentId);
        Assert.True(agent.IsInvestigationLead);
        // Classification.Unknown => no history entry written
        Assert.False(await check.ClassificationHistory.AnyAsync(e => e.EntityId == group.Id));
    }

    [Fact]
    public async Task CreateAsync_WritesClassificationHistory_WhenClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonGroupInput
        {
            Name = "Verdaechtige",
            Classification = Classification.ReviewCase,
            ClassificationJustification = "Grund",
        };

        var group = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var entry = await check.ClassificationHistory.SingleAsync(e =>
            e.EntityType == nameof(PersonGroup) && e.EntityId == group.Id);
        Assert.Equal(Classification.ReviewCase, entry.Value);
        Assert.Equal("Grund", entry.Justification);
    }

    [Fact]
    public async Task CreateAsync_ImportsExistingMembers_AndBuildsColleagueLink()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.People.Add(Seed.Person("p1", "Person One"));
            db.People.Add(Seed.Person("p2", "Person Two"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonGroupInput
        {
            Name = "Zelle",
            Members =
            {
                new GroupMemberInput { PersonId = "p1", Role = "Boss", IsLead = true },
                new GroupMemberInput { PersonId = "p2" },
            },
        };

        var group = await svc.CreateAsync(input, Leader());

        using var check = ctx.NewContext();
        var members = await check.PersonGroupMembers.Where(m => m.PersonGroupId == group.Id).ToListAsync();
        Assert.Equal(2, members.Count);
        // one automatic colleague link between the two shared members
        var links = await check.Links
            .Where(l => l.Automatic && l.Label == ColleaguesSync.GroupColleague)
            .ToListAsync();
        Assert.Single(links);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenActorMayNotAssignSecrecyLevel()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new PersonGroupInput { Name = "VS", SecrecyLevel = DocumentClassification.Leadership };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenSecuredStateThreateningBelowRank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new PersonGroupInput
        {
            Name = "Gefahr",
            Classification = Classification.SecuredStateThreatening,
        };

        // CheckRankGate throws InvalidOperationException for special-agent rank.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, ClaimsPrincipalBuilder.Agent("sa").WithRank(Rank.SpecialAgent).Build()));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonGroupInput
        {
            Name = "  Neu  ",
            Description = "  Beschreibung  ",
            Targets = "  Ziele  ",
            Kind = GroupsKind.PersonOfInterest,
            EstimatedMemberCount = 12,
        };

        await svc.RefreshAsync("g1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonGroups.SingleAsync(g => g.Id == "g1");
        Assert.Equal("Neu", stored.Name);
        Assert.Equal("Beschreibung", stored.Description);
        Assert.Equal("Ziele", stored.Targets);
        Assert.Equal(GroupsKind.PersonOfInterest, stored.Kind);
        Assert.Equal(12, stored.EstimatedMemberCount);
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnClassifiedGroup_ForNonAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new PersonGroupInput { Name = "X" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("g1", input, Junior()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownGroup()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("ghost", new PersonGroupInput { Name = "X" }, Leader()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesGroup()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("g1", "Dublette", Leader());

        // interceptor absent => hard delete; row gone from the filtered set
        using var check = ctx.NewContext();
        Assert.False(await check.PersonGroups.AnyAsync(g => g.Id == "g1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("g1", "Dublette", Junior()));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_UndeletesGroup()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g =>
            {
                g.IsDeleted = true;
                g.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                g.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("g1", Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonGroups.SingleAsync(g => g.Id == "g1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("g1", Junior()));
    }

    // ---------- ClassificationSetAsync ----------

    [Fact]
    public async Task ClassificationSetAsync_SetsValue_AndWritesHistory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.ClassificationSetAsync("g1", Classification.SuspicionCase, "Begruendung", Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonGroups.SingleAsync(g => g.Id == "g1");
        Assert.Equal(Classification.SuspicionCase, stored.Classification);
        var entry = await check.ClassificationHistory.SingleAsync(e =>
            e.EntityType == nameof(PersonGroup) && e.EntityId == "g1");
        Assert.Equal(Classification.SuspicionCase, entry.Value);
        Assert.Equal("Begruendung", entry.Justification);
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenSecuredStateThreateningBelowRank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("g1", Classification.SecuredStateThreatening, null,
                ClaimsPrincipalBuilder.Agent("sa").WithRank(Rank.SpecialAgent).Build()));
    }

    // ---------- GetClassificationHistoryAsync ----------

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEntries_Descending_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(PersonGroup), EntityId = "g1", Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(PersonGroup), EntityId = "g1", Value = Classification.SuspicionCase,
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var history = await svc.GetClassificationHistoryAsync("g1", LeaderScope());

        Assert.Equal(new[] { Classification.SuspicionCase, Classification.ReviewCase },
            history.Select(e => e.Value).ToArray());
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(PersonGroup), EntityId = "g1", Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var history = await svc.GetClassificationHistoryAsync("g1", JuniorScope());

        Assert.Empty(history);
    }

    // ---------- GetMembersAsync ----------

    [Fact]
    public async Task GetMembersAsync_ReturnsVisibleMembers_LeadFirstThenByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Bravo"));
            db.People.Add(Seed.Person("p2", "Alpha"));
            db.People.Add(Seed.Person("p3", "Zulu", p => p.IsClassified = true));
            db.People.Add(Seed.Person("p4", "Deleted", p => p.IsDeleted = true));
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1", IsLead = true });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p2" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p3" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p4" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var members = await svc.GetMembersAsync("g1", LeaderScope());

        // p4 excluded (soft-deleted person => null); lead first, rest alphabetical
        Assert.Equal(new[] { "p1", "p2", "p3" }, members.Select(m => m.PersonId).ToArray());
    }

    [Fact]
    public async Task GetMembersAsync_HidesClassifiedPerson_FromNonClassifiedScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Alpha"));
            db.People.Add(Seed.Person("p2", "Bravo", p => p.IsClassified = true));
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var members = await svc.GetMembersAsync("g1", JuniorScope());

        Assert.Equal(new[] { "p1" }, members.Select(m => m.PersonId).ToArray());
    }

    // ---------- MemberAddAsync ----------

    [Fact]
    public async Task MemberAddAsync_AddsMember_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Person"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.MemberAddAsync("g1", new GroupMemberInput { PersonId = "p1", Role = "  Boss  ", IsLead = true }, Leader());

        using var check = ctx.NewContext();
        var member = await check.PersonGroupMembers.SingleAsync(m => m.PersonGroupId == "g1" && m.PersonId == "p1");
        Assert.Equal("Boss", member.Role);
        Assert.True(member.IsLead);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberAddAsync_Throws_OnDuplicateMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Person"));
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MemberAddAsync("g1", new GroupMemberInput { PersonId = "p1" }, Leader()));
    }

    [Fact]
    public async Task MemberAddAsync_Throws_OnClassifiedGroup_ForNonAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MemberAddAsync("g1", new GroupMemberInput { PersonId = "p1" }, Junior()));
    }

    // ---------- MemberChangeAsync ----------

    [Fact]
    public async Task MemberChangeAsync_UpdatesRoleAndLead_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        var member = new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1", Role = "Old", IsLead = false };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Person"));
            db.PersonGroupMembers.Add(member);
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.MemberChangeAsync(member.Id, "  New  ", true, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonGroupMembers.SingleAsync(m => m.Id == member.Id);
        Assert.Equal("New", stored.Role);
        Assert.True(stored.IsLead);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberChangeAsync_Throws_OnClassifiedGroup_ForNonAudience()
    {
        using var ctx = new SqliteTestContext();
        var member = new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.People.Add(Seed.Person("p1", "Person"));
            db.PersonGroupMembers.Add(member);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MemberChangeAsync(member.Id, "New", true, Junior()));
    }

    // ---------- MemberRemoveAsync ----------

    [Fact]
    public async Task MemberRemoveAsync_RemovesMember_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        var member = new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.People.Add(Seed.Person("p1", "Person"));
            db.PersonGroupMembers.Add(member);
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat: threat);

        await svc.MemberRemoveAsync(member.Id, Leader());

        using var check = ctx.NewContext();
        // interceptor absent => hard delete; gone from the filtered set
        Assert.False(await check.PersonGroupMembers.AnyAsync(m => m.Id == member.Id));
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberRemoveAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.MemberRemoveAsync("ghost", Leader());
    }

    // ---------- GetAgentsAsync ----------

    [Fact]
    public async Task GetAgentsAsync_ReturnsAgents_LeadsFirstThenByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Zeta"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Alpha"));
            db.Users.Add(Seed.Agent("a3", configure: a => a.Codename = "Mike"));
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a1", IsInvestigationLead = false });
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a2", IsInvestigationLead = false });
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a3", IsInvestigationLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var agents = await svc.GetAgentsAsync("g1");

        // lead (a3/Mike) first, then non-leads by codename: a2/Alpha, a1/Zeta
        Assert.Equal(new[] { "a3", "a2", "a1" }, agents.Select(a => a.AgentId).ToArray());
    }

    // ---------- GetInvestigationLeadAsync ----------

    [Fact]
    public async Task GetInvestigationLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Alpha"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Bravo"));
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a1", IsInvestigationLead = true });
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a2", IsInvestigationLead = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var leads = await svc.GetInvestigationLeadAsync("g1");

        Assert.Equal(new[] { "a1" }, leads.Select(a => a.AgentId).ToArray());
    }

    // ---------- AgentAllocateAsync ----------

    [Fact]
    public async Task AgentAllocateAsync_AddsAllocation_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentAllocateAsync("g1", "target", asInvestigationLead: false, Leader());

        using var check = ctx.NewContext();
        var alloc = await check.PersonGroupAgents.SingleAsync(a => a.PersonGroupId == "g1" && a.AgentId == "target");
        Assert.False(alloc.IsInvestigationLead);
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_ForNonLeadershipNonLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // junior is neither leadership nor an investigation lead of the group
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("g1", "target", asInvestigationLead: false, Junior()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenInvestigationLeadFlagRequestedByNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("el"));
            db.Users.Add(Seed.Agent("target"));
            // el is investigation lead of g1 (passes leadership-or-EL) but is not leadership
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "el", IsInvestigationLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("g1", "target", asInvestigationLead: true, Junior("el")));
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
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(NotSelectable(id));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("g1", id, asInvestigationLead: false, Leader()));
    }

    /// <summary>An agent shape no picker offers; the service must reject it too.</summary>
    private static NOOSE_Website.Data.Entities.Agent NotSelectable(string id) => id switch
    {
        "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
        "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
        "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
        _ => Seed.Agent(id, status: AgentStatus.Terminated),
    };

    [Fact]
    public async Task AgentAllocateAsync_Throws_OnUnknownAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("g1", "ghost", asInvestigationLead: false, Leader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_OnDuplicateAllocation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("target"));
            db.PersonGroupAgents.Add(new PersonGroupAgent { PersonGroupId = "g1", AgentId = "target" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("g1", "target", asInvestigationLead: false, Leader()));
    }

    // ---------- AgentRemoveAsync ----------

    [Fact]
    public async Task AgentRemoveAsync_RemovesAllocation()
    {
        using var ctx = new SqliteTestContext();
        var alloc = new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a1" };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("a1"));
            db.PersonGroupAgents.Add(alloc);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync(alloc.Id, Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.PersonGroupAgents.AnyAsync(a => a.Id == alloc.Id));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync("ghost", Leader());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_ForNonLeadershipNonLead()
    {
        using var ctx = new SqliteTestContext();
        var alloc = new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a1" };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("a1"));
            db.PersonGroupAgents.Add(alloc);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync(alloc.Id, Junior()));
    }

    // ---------- InvestigationLeadSetAsync ----------

    [Fact]
    public async Task InvestigationLeadSetAsync_SetsFlag_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        var alloc = new PersonGroupAgent { PersonGroupId = "g1", AgentId = "a1", IsInvestigationLead = false };
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.Users.Add(Seed.Agent("a1"));
            db.PersonGroupAgents.Add(alloc);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.InvestigationLeadSetAsync(alloc.Id, true, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonGroupAgents.SingleAsync(a => a.Id == alloc.Id);
        Assert.True(stored.IsInvestigationLead);
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.InvestigationLeadSetAsync("any", true, Junior()));
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_OnUnknownAllocation()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.InvestigationLeadSetAsync("ghost", true, Leader()));
    }

    // ---------- GetProgressAsync ----------

    [Fact]
    public async Task GetProgressAsync_CountsLiveMembers_AndReturnsEstimate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.EstimatedMemberCount = 10));
            db.People.Add(Seed.Person("p1", "Live"));
            db.People.Add(Seed.Person("p2", "Deleted", p => p.IsDeleted = true));
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var progress = await svc.GetProgressAsync("g1");

        // p2's person is soft-deleted => the join to People excludes it
        Assert.Equal(1, progress.Captured);
        Assert.Equal(10, progress.Estimated);
    }

    // ---------- GetHistoryAsync ----------

    [Fact]
    public async Task GetHistoryAsync_ReturnsAuditLogs_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(PersonGroup), EntityId = "g1",
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(PersonGroup), EntityId = "other",
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var history = await svc.GetHistoryAsync("g1", isLeadership: true);

        var entry = Assert.Single(history);
        Assert.Equal("g1", entry.EntityId);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(PersonGroup), EntityId = "g1",
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var history = await svc.GetHistoryAsync("g1", isLeadership: false);

        Assert.Empty(history);
    }

    // ==================== GetPhotosAsync ====================

    [Fact]
    public async Task GetPhotosAsync_OrdersTitleImageFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "early", PersonGroupId = "g1", FileNameSaved = "a.jpg", IsTitleImage = false, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "title", PersonGroupId = "g1", FileNameSaved = "b.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotosAsync("g1");

        Assert.Equal("title", result[0].Id);
    }

    // ==================== GetPhotoWithGroupAsync ====================

    [Fact]
    public async Task GetPhotoWithGroupAsync_ReturnsPhoto_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph1", PersonGroupId = "g1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetPhotoWithGroupAsync("ph1", JuniorScope());

        Assert.NotNull(result);
        Assert.Equal("ph1", result!.Id);
    }

    [Fact]
    public async Task GetPhotoWithGroupAsync_ReturnsNull_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph1", PersonGroupId = "g1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        Assert.Null(await svc.GetPhotoWithGroupAsync("ph1", JuniorScope()));
    }

    // A TRU record reads to TRU, not to leadership alone: the coarse
    // "classified, so leadership" check would lock its own audience out.
    [Fact]
    public async Task GetPhotoWithGroupAsync_ReturnsPhoto_ForTruRecord_AndTruAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Tru));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph1", PersonGroupId = "g1", FileNameSaved = "a.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var tru = ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.Trainee).AsTru().Build();

        Assert.NotNull(await svc.GetPhotoWithGroupAsync("ph1", ViewerScope.From(tru)));
    }

    // ==================== PhotoAddAsync ====================

    [Fact]
    public async Task PhotoAddAsync_SavesPhoto_AsTitle_WhenFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.SaveChanges();
        }
        var photo = PhotoStorage(saved: "stored.jpg");
        var svc = NewService(ctx, photo: photo);

        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await svc.PhotoAddAsync("g1", content, "orig.jpg", "image/jpeg", 3, Leader("me"));

        Assert.Equal("stored.jpg", result.FileNameSaved);
        Assert.True(result.IsTitleImage);
        using var check = ctx.NewContext();
        var stored = await check.PersonGroupPhotos.SingleAsync(p => p.PersonGroupId == "g1");
        Assert.Equal("me", stored.CreatedById);
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnDisallowedType()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx, photo: PhotoStorage(allowed: false));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("g1", content, "x.exe", "application/octet-stream", 1, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnOversizedFile()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx, photo: PhotoStorage(maxBytes: 10));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("g1", content, "orig.jpg", "image/jpeg", 11, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_WhenClassified_AndNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1", configure: g => g.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PhotoAddAsync("g1", content, "orig.jpg", "image/jpeg", 1, Junior()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnUnknownGroup()
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
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph1", PersonGroupId = "g1", FileNameSaved = "gone.jpg" });
            db.SaveChanges();
        }
        var photo = PhotoStorage();
        var svc = NewService(ctx, photo: photo);

        await svc.PhotoRemoveAsync("ph1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.PersonGroupPhotos.AnyAsync(p => p.Id == "ph1"));
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
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph1", PersonGroupId = "g1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "ph2", PersonGroupId = "g1", FileNameSaved = "b.jpg", IsTitleImage = false });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AsTitleImageSetAsync("ph2", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.PersonGroupPhotos.SingleAsync(p => p.Id == "ph1")).IsTitleImage);
        Assert.True((await check.PersonGroupPhotos.SingleAsync(p => p.Id == "ph2")).IsTitleImage);
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
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "title", PersonGroupId = "g1", FileNameSaved = "a.jpg", IsTitleImage = true, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "young", PersonGroupId = "g1", FileNameSaved = "c.jpg", CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "old", PersonGroupId = "g1", FileNameSaved = "b.jpg", CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.True((await check.PersonGroupPhotos.SingleAsync(p => p.Id == "old")).IsTitleImage);
        Assert.False((await check.PersonGroupPhotos.SingleAsync(p => p.Id == "young")).IsTitleImage);
    }

    [Fact]
    public async Task PhotoRemoveAsync_LeavesNoTitleImage_WhenTheLastPhotoGoes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "title", PersonGroupId = "g1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.Empty(await check.PersonGroupPhotos.ToListAsync());
    }

    // A sibling record must not inherit the mark across file boundaries.
    [Fact]
    public async Task PhotoRemoveAsync_PromotesOnlyWithinTheSameRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(Group("g1"));
            db.PersonGroups.Add(Group("g2", "Andere Bande"));
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "title", PersonGroupId = "g1", FileNameSaved = "a.jpg", IsTitleImage = true });
            db.PersonGroupPhotos.Add(new PersonGroupPhoto { Id = "foreign", PersonGroupId = "g2", FileNameSaved = "b.jpg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PhotoRemoveAsync("title", Leader());

        using var check = ctx.NewContext();
        Assert.False((await check.PersonGroupPhotos.SingleAsync(p => p.Id == "foreign")).IsTitleImage);
    }
}
