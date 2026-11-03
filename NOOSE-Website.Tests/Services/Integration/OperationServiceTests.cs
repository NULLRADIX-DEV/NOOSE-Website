using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="OperationService"/> over in-memory SQLite.</summary>
public sealed class OperationServiceTests
{
    // Rank >= SpecialAgentInCharge(4) or admin => IsLeadership() and MayHighestClassification (Director).
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: neither leadership nor high-classification.
    private static ClaimsPrincipal NonLeader(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static ViewerScope LeaderScope() => ViewerScope.From(Leader());
    private static ViewerScope NonLeaderScope() => ViewerScope.From(NonLeader());

    private static (OperationService Svc, IProfileSuggestionService Suggestion) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns("FIB-OP-2026-0001");
        var suggestion = Substitute.For<IProfileSuggestionService>();
        var svc = new OperationService(ctx.Factory, caseNo, suggestion, Substitute.For<INotificationService>());
        return (svc, suggestion);
    }

    private static Operation Op(string id, string title = "Operation", Classification cls = Classification.Unknown,
        DocumentClassification secrecy = DocumentClassification.None, OperationStatus status = OperationStatus.Planned,
        DateTime? createdAt = null, Action<Operation>? configure = null)
    {
        var o = new Operation
        {
            Id = id,
            CaseNumber = $"FIB-OP-2026-{id}",
            Title = title,
            Classification = cls,
            Status = status,
            CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        o.SecrecyLevel = secrecy;
        configure?.Invoke(o);
        return o;
    }

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_NonLeadership_ExcludesClassified_OrderedNewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("a", "Older", createdAt: t0));
            db.Operations.Add(Op("b", "Newer", createdAt: t0.AddDays(2)));
            db.Operations.Add(Op("c", "Secret", secrecy: DocumentClassification.Leadership, createdAt: t0.AddDays(3)));
            db.SaveChanges();
        }

        var result = await svc.GetListAsync(NonLeaderScope());

        // classified excluded; ordered by ModifiedAt ?? CreatedAt descending.
        Assert.Equal(new[] { "Newer", "Older" }, result.Select(o => o.Title).ToArray());
    }

    [Fact]
    public async Task GetListAsync_Leadership_IncludesClassified()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("a", "Plain"));
            db.Operations.Add(Op("b", "Secret", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }

        var result = await svc.GetListAsync(LeaderScope());

        Assert.Equal(2, result.Count);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_ReturnsOperation_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", "Visible"));
            db.SaveChanges();
        }

        var result = await svc.GetDetailAsync("op1", NonLeaderScope());

        Assert.NotNull(result);
        Assert.Equal("Visible", result!.Title);
    }

    [Fact]
    public async Task GetDetailAsync_NonLeadership_ClassifiedReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }

        Assert.Null(await svc.GetDetailAsync("op1", NonLeaderScope()));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        Assert.Null(await svc.GetDetailAsync("missing", LeaderScope()));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsSoftDeleted_NewestDeletedFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var t0 = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("live", "Alive"));
            db.Operations.Add(Op("d1", "OlderDelete", configure: o => { o.IsDeleted = true; o.DeletedAt = t0; }));
            db.Operations.Add(Op("d2", "NewerDelete", configure: o => { o.IsDeleted = true; o.DeletedAt = t0.AddDays(1); }));
            db.SaveChanges();
        }

        var result = await svc.GetTrashAsync();

        Assert.Equal(new[] { "NewerDelete", "OlderDelete" }, result.Select(o => o.Title).ToArray());
    }

    // ---------- SearchAsync ----------

    [Fact]
    public async Task SearchAsync_NonLeadership_ExcludesClassified_AndFiltersText()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("a", "Razzia Downtown"));
            db.Operations.Add(Op("b", "Observation"));
            db.Operations.Add(Op("c", "Razzia Secret", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }

        var result = await svc.SearchAsync("Razzia", isLeadership: false);

        // only the unclassified "Razzia" matches for a non-leadership viewer.
        Assert.Equal(new[] { "Razzia Downtown" }, result.Select(o => o.Title).ToArray());
    }

    [Fact]
    public async Task SearchAsync_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.Operations.Add(Op($"o{i}", $"Op {i}"));
            }
            db.SaveChanges();
        }

        var result = await svc.SearchAsync(null, isLeadership: true, max: 2);

        Assert.Equal(2, result.Count);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsOperation_AssignsCreatorAsLead_StagesSuggestion()
    {
        using var ctx = new SqliteTestContext();
        var (svc, suggestion) = Build(ctx);
        var input = new OperationInput { Title = "  Nighthawk  ", Type = "Razzia", Status = OperationStatus.Running };

        var created = await svc.CreateAsync(input, Leader("lead"));

        Assert.Equal("Nighthawk", created.Title);
        Assert.Equal("FIB-OP-2026-0001", created.CaseNumber);
        Assert.Equal(OperationStatus.Running, created.Status);

        using var db = ctx.NewContext();
        var stored = await db.Operations.SingleAsync(o => o.Id == created.Id);
        Assert.Equal("Nighthawk", stored.Title);
        // creator auto-assigned as investigation lead.
        var alloc = await db.OperationAgents.SingleAsync(a => a.OperationId == created.Id);
        Assert.Equal("lead", alloc.AgentId);
        Assert.True(alloc.IsInvestigationLead);
        // type staged into the shared suggestion catalog.
        await suggestion.Received(1).StageAsync(
            Arg.Any<AppDbContext>(), SuggestionType.OperationType,
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithClassification_WritesHistory()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = new OperationInput
        {
            Title = "Case",
            Classification = Classification.ReviewCase,
            ClassificationJustification = "  Anfangsverdacht  ",
        };

        var created = await svc.CreateAsync(input, Leader());

        using var db = ctx.NewContext();
        var entry = await db.ClassificationHistory
            .SingleAsync(e => e.EntityType == nameof(Operation) && e.EntityId == created.Id);
        Assert.Equal(Classification.ReviewCase, entry.Value);
        Assert.Equal("Anfangsverdacht", entry.Justification);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenActorMayNotAssignSecrecy()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        // non-leadership actor cannot assign a leadership secrecy level.
        var input = new OperationInput { Title = "X", SecrecyLevel = DocumentClassification.Leadership };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, NonLeader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenSecuredStateThreatening_WithoutRank()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        // rank gate: junior may not set "Gesichert staatsgefährdend" directly.
        var input = new OperationInput { Title = "X", Classification = Classification.SecuredStateThreatening };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, NonLeader()));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", "Old"));
            db.SaveChanges();
        }
        var input = new OperationInput
        {
            Title = "  New Title  ",
            Location = "  Vinewood  ",
            Status = OperationStatus.Completed,
            Result = "  success  ",
        };

        await svc.RefreshAsync("op1", input, Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.Operations.SingleAsync(o => o.Id == "op1");
        Assert.Equal("New Title", stored.Title);
        Assert.Equal("Vinewood", stored.Location);
        Assert.Equal(OperationStatus.Completed, stored.Status);
        Assert.Equal("success", stored.Result);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", new OperationInput { Title = "X" }, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassifiedAndActorCannotSee()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("op1", new OperationInput { Title = "X" }, NonLeader()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_HardDeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.SaveChanges();
        }

        await svc.DeleteAsync("op1", Leader());

        // no soft-delete interceptor in the test context => the row is hard-deleted.
        using var db2 = ctx.NewContext();
        Assert.False(await db2.Operations.IgnoreQueryFilters().AnyAsync(o => o.Id == "op1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("any", NonLeader()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("missing", Leader()));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_ClearsSoftDelete()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", configure: o =>
            {
                o.IsDeleted = true;
                o.DeletedAt = DateTime.UtcNow;
                o.DeletedById = "someone";
            }));
            db.SaveChanges();
        }

        await svc.RestoreAsync("op1", Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.Operations.IgnoreQueryFilters().SingleAsync(o => o.Id == "op1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("any", NonLeader()));
    }

    // ---------- ClassificationSetAsync ----------

    [Fact]
    public async Task ClassificationSetAsync_UpdatesAndWritesHistory()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.SaveChanges();
        }

        await svc.ClassificationSetAsync("op1", Classification.SuspicionCase, "escalation", Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.Operations.SingleAsync(o => o.Id == "op1");
        Assert.Equal(Classification.SuspicionCase, stored.Classification);
        var entry = await db2.ClassificationHistory
            .SingleAsync(e => e.EntityType == nameof(Operation) && e.EntityId == "op1");
        Assert.Equal(Classification.SuspicionCase, entry.Value);
        Assert.Equal("escalation", entry.Justification);
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("missing", Classification.ReviewCase, null, Leader()));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenSecuredStateThreatening_WithoutRank()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.SaveChanges();
        }

        // rank gate runs first: junior cannot set the highest classification directly.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("op1", Classification.SecuredStateThreatening, null, NonLeader()));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenClassifiedAndActorCannotSee()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ClassificationSetAsync("op1", Classification.ReviewCase, null, NonLeader()));
    }

    // ---------- GetClassificationHistoryAsync ----------

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsHistory_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Operation), EntityId = "op1", Value = Classification.ReviewCase, Timestamp = t0,
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Operation), EntityId = "op1", Value = Classification.SuspicionCase, Timestamp = t0.AddDays(1),
            });
            // unrelated record must not leak.
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Operation), EntityId = "other", Value = Classification.ReviewCase, Timestamp = t0,
            });
            db.SaveChanges();
        }

        var result = await svc.GetClassificationHistoryAsync("op1", LeaderScope());

        // newest first, scoped to op1.
        Assert.Equal(new[] { Classification.SuspicionCase, Classification.ReviewCase },
            result.Select(e => e.Value).ToArray());
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", secrecy: DocumentClassification.Leadership));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Operation), EntityId = "op1", Value = Classification.ReviewCase, Timestamp = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        Assert.Empty(await svc.GetClassificationHistoryAsync("op1", NonLeaderScope()));
    }

    // ---------- GetAgentsAsync / GetInvestigationLeadAsync ----------

    [Fact]
    public async Task GetAgentsAsync_ReturnsAllocations_LeadFirstThenCodename()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(Seed.Agent("a-lead", configure: a => a.Codename = "Mike"));
            db.Users.Add(Seed.Agent("a-zulu", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("a-alpha", configure: a => a.Codename = "Alpha"));
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "a-zulu", IsInvestigationLead = false });
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "a-lead", IsInvestigationLead = true });
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "a-alpha", IsInvestigationLead = false });
            db.SaveChanges();
        }

        var result = await svc.GetAgentsAsync("op1");

        // lead first, then non-leads alphabetically by codename.
        Assert.Equal(new[] { "Mike", "Alpha", "Zulu" }, result.Select(a => a.Agent!.Codename).ToArray());
    }

    [Fact]
    public async Task GetInvestigationLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Bravo"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Charlie"));
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "a1", IsInvestigationLead = true });
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "a2", IsInvestigationLead = false });
            db.SaveChanges();
        }

        var result = await svc.GetInvestigationLeadAsync("op1");

        Assert.Single(result);
        Assert.Equal("a1", result[0].AgentId);
    }

    // ---------- AgentAllocateAsync ----------

    [Fact]
    public async Task AgentAllocateAsync_AddsAllocation()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }

        await svc.AgentAllocateAsync("op1", "target", asInvestigationLead: false, Leader());

        using var db2 = ctx.NewContext();
        var alloc = await db2.OperationAgents.SingleAsync(a => a.OperationId == "op1" && a.AgentId == "target");
        Assert.False(alloc.IsInvestigationLead);
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenNotLeadershipOrEL()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.SaveChanges();
        }

        // junior actor who is neither leadership nor an investigation lead of the file.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("op1", "target", asInvestigationLead: false, NonLeader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_AsLead_Throws_WhenELButNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(Seed.Agent("target"));
            // the actor is an investigation lead of this file but not leadership.
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "junior", IsInvestigationLead = true });
            db.SaveChanges();
        }

        // an EL may allocate, but only leadership may grant the lead flag.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("op1", "target", asInvestigationLead: true, NonLeader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("op1", "ghost", asInvestigationLead: false, Leader()));
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(NotSelectable(id));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("op1", id, asInvestigationLead: false, Leader()));
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
    public async Task AgentAllocateAsync_Throws_WhenAlreadyAllocated()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.Users.Add(Seed.Agent("target"));
            db.OperationAgents.Add(new OperationAgent { OperationId = "op1", AgentId = "target", IsInvestigationLead = false });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("op1", "target", asInvestigationLead: false, Leader()));
    }

    // ---------- AgentRemoveAsync ----------

    [Fact]
    public async Task AgentRemoveAsync_RemovesAllocation()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var alloc = new OperationAgent { OperationId = "op1", AgentId = "target", IsInvestigationLead = false };
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.OperationAgents.Add(alloc);
            db.SaveChanges();
        }

        await svc.AgentRemoveAsync(alloc.Id, Leader());

        using var db2 = ctx.NewContext();
        Assert.False(await db2.OperationAgents.AnyAsync(a => a.Id == alloc.Id));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // missing allocation returns without throwing.
        await svc.AgentRemoveAsync("missing", Leader());

        using var db = ctx.NewContext();
        Assert.Equal(0, await db.OperationAgents.CountAsync());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenNotLeadershipOrEL()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var alloc = new OperationAgent { OperationId = "op1", AgentId = "target", IsInvestigationLead = false };
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.OperationAgents.Add(alloc);
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync(alloc.Id, NonLeader()));
    }

    // ---------- InvestigationLeadSetAsync ----------

    [Fact]
    public async Task InvestigationLeadSetAsync_SetsFlag()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var alloc = new OperationAgent { OperationId = "op1", AgentId = "target", IsInvestigationLead = false };
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.OperationAgents.Add(alloc);
            db.SaveChanges();
        }

        await svc.InvestigationLeadSetAsync(alloc.Id, true, Leader());

        using var db2 = ctx.NewContext();
        Assert.True((await db2.OperationAgents.SingleAsync(a => a.Id == alloc.Id)).IsInvestigationLead);
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.InvestigationLeadSetAsync("any", true, NonLeader()));
    }

    [Fact]
    public async Task InvestigationLeadSetAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.InvestigationLeadSetAsync("missing", true, Leader()));
    }

    // ---------- GetHistoryAsync ----------

    [Fact]
    public async Task GetHistoryAsync_ReturnsAuditEntries_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var t0 = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1"));
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Operation), EntityId = "op1", Action = AuditAction.Created, Timestamp = t0 });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Operation), EntityId = "op1", Action = AuditAction.Modified, Timestamp = t0.AddHours(1) });
            // unrelated audit row must not appear.
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Operation), EntityId = "other", Action = AuditAction.Modified, Timestamp = t0 });
            db.SaveChanges();
        }

        var result = await svc.GetHistoryAsync("op1", isLeadership: true);

        // newest first, scoped to this operation.
        Assert.Equal(2, result.Count);
        Assert.Equal(AuditAction.Modified, result[0].Action);
        Assert.Equal(AuditAction.Created, result[1].Action);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(Op("op1", secrecy: DocumentClassification.Leadership));
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Operation), EntityId = "op1", Action = AuditAction.Created, Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }

        Assert.Empty(await svc.GetHistoryAsync("op1", isLeadership: false));
    }
}
