using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Shares;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DocumentAccessService"/> against in-memory SQLite.</summary>
public sealed class DocumentAccessServiceTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (DocumentAccessService Svc, DocumentAccessBroadcaster Broadcaster) Build(SqliteTestContext ctx)
    {
        var broadcaster = new DocumentAccessBroadcaster();
        return (new DocumentAccessService(ctx.Factory, broadcaster), broadcaster);
    }

    // Rank >= SpecialAgentInCharge(4) => IsLeadership(): may always manage.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Non-leadership writer: MayWrite() true, IsLeadership() false.
    private static ClaimsPrincipal Writer(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    // Junior agent, non-leadership.
    private static ClaimsPrincipal Junior(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Read-only supervision: TeamLead without admin => RequireWriteAccess rejects.
    private static ClaimsPrincipal OnlyReader(string id = "reader")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).AsTeamLead().Build();

    private static void AddDocument(SqliteTestContext ctx, string id, Action<Document>? configure = null)
    {
        using var db = ctx.NewContext();
        var d = new Document { Id = id, Title = "Doc " + id, CreatedAt = Ts };
        configure?.Invoke(d);
        db.Documents.Add(d);
        db.SaveChanges();
    }

    private static void AddAgent(SqliteTestContext ctx, string id, Rank rank = Rank.Trainee,
        AgentStatus status = AgentStatus.Active, Action<Agent>? configure = null)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id, rank, status, configure));
        db.SaveChanges();
    }

    private static void AddExclusion(SqliteTestContext ctx, string documentId, string agentId)
    {
        using var db = ctx.NewContext();
        db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { DocumentId = documentId, AgentId = agentId, CreatedAt = Ts });
        db.SaveChanges();
    }

    private static void AddTaskforceMember(SqliteTestContext ctx, string taskforceId, string agentId, TaskforceRole role)
    {
        using var db = ctx.NewContext();
        if (!db.Taskforces.Any(t => t.Id == taskforceId))
        {
            db.Taskforces.Add(new Taskforce { Id = taskforceId, Name = "TF " + taskforceId, CaseNumber = "FIB-TF-2026-0001", CreatedAt = Ts });
        }
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = taskforceId, AgentId = agentId, Role = role, CreatedAt = Ts });
        db.SaveChanges();
    }

    // ---------- CanManageAccessAsync ----------

    [Fact]
    public async Task CanManageAccessAsync_ReturnsFalse_WhenDocumentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        Assert.False(await svc.CanManageAccessAsync("nope", Leader()));
    }

    [Fact]
    public async Task CanManageAccessAsync_ReturnsTrue_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "someone-else");
        var (svc, _) = Build(ctx);

        Assert.True(await svc.CanManageAccessAsync("d1", Leader()));
    }

    [Fact]
    public async Task CanManageAccessAsync_ReturnsTrue_WhenCreator()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "creator1");
        var (svc, _) = Build(ctx);

        // non-leadership but owns the document
        Assert.True(await svc.CanManageAccessAsync("d1", Junior("creator1")));
    }

    [Fact]
    public async Task CanManageAccessAsync_ReturnsTrue_WhenTaskforceLead()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => { d.CreatedById = "someone-else"; d.OwnerTaskforceId = "tf1"; });
        AddTaskforceMember(ctx, "tf1", "leadX", TaskforceRole.LeadInvestigator);
        var (svc, _) = Build(ctx);

        // non-leadership, not creator, but a lead (non-member role) of the owning taskforce
        Assert.True(await svc.CanManageAccessAsync("d1", Junior("leadX")));
    }

    [Fact]
    public async Task CanManageAccessAsync_ReturnsFalse_WhenTaskforcePlainMember()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => { d.CreatedById = "someone-else"; d.OwnerTaskforceId = "tf1"; });
        AddTaskforceMember(ctx, "tf1", "mem1", TaskforceRole.Member);
        var (svc, _) = Build(ctx);

        // plain member (Member role) cannot manage
        Assert.False(await svc.CanManageAccessAsync("d1", Junior("mem1")));
    }

    [Fact]
    public async Task CanManageAccessAsync_ReturnsFalse_WhenUnrelatedNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "owner");
        var (svc, _) = Build(ctx);

        Assert.False(await svc.CanManageAccessAsync("d1", Junior("stranger")));
    }

    // ---------- GetAccessListAsync ----------

    [Fact]
    public async Task GetAccessListAsync_Throws_InvalidOperation_WhenDocumentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessListAsync("ghost", Leader()));
    }

    [Fact]
    public async Task GetAccessListAsync_Throws_Unauthorized_WhenCannotManage()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "owner");
        var (svc, _) = Build(ctx);

        // exists but requester is not leadership, creator or a taskforce lead
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetAccessListAsync("d1", Junior("stranger")));
    }

    [Fact]
    public async Task GetAccessListAsync_ReturnsInternalActiveAgents_OrderedByCodename_FlaggingExcluded()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddAgent(ctx, "a1", configure: a => a.Codename = "Alpha");
        AddAgent(ctx, "a2", configure: a => a.Codename = "Bravo");
        AddAgent(ctx, "p1", configure: a => { a.Codename = "Aaa"; a.PartnerAgency = PartnerAgency.LSPD; }); // partner => excluded
        AddAgent(ctx, "pend", status: AgentStatus.Pending, configure: a => a.Codename = "Aab"); // not active => excluded
        AddExclusion(ctx, "d1", "a2");
        var (svc, _) = Build(ctx);

        var list = await svc.GetAccessListAsync("d1", Leader());

        // only internal active agents, ordered by codename
        Assert.Equal(new[] { "a1", "a2" }, list.Select(e => e.AgentId).ToArray());
        Assert.False(list.Single(e => e.AgentId == "a1").Excluded);
        Assert.True(list.Single(e => e.AgentId == "a2").Excluded);
    }

    [Fact]
    public async Task GetAccessListAsync_Classified_IncludesOnlyLeadership_NeverReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.IsClassified = true);
        AddAgent(ctx, "plain");
        AddAgent(ctx, "boss", rank: Rank.SpecialAgentInCharge);
        AddAgent(ctx, "adm", configure: a => a.IsAdmin = true);
        // read-only supervision can read the file, it is just never listed anywhere
        AddAgent(ctx, "tl", configure: a => a.IsTeamLead = true);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetAccessListAsync("d1", Leader())).Select(e => e.AgentId).ToHashSet();

        Assert.Equal(new HashSet<string> { "boss", "adm" }, ids);
    }

    [Fact]
    public async Task GetAccessListAsync_TeamLeadAdmin_IsNotListed()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.IsClassified = true);
        AddAgent(ctx, "adm", configure: a => a.IsAdmin = true);
        // the admin flag must not resurrect a team lead
        AddAgent(ctx, "tl-adm", configure: a => { a.IsTeamLead = true; a.IsAdmin = true; });
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetAccessListAsync("d1", Leader())).Select(e => e.AgentId).ToHashSet();

        Assert.Equal(new HashSet<string> { "adm" }, ids);
    }

    [Fact]
    public async Task GetAccessListAsync_TruClassified_IncludesTruAgents()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.IsTRUClassified = true);
        AddAgent(ctx, "plain");
        AddAgent(ctx, "tru", configure: a => a.IsTRU = true);
        AddAgent(ctx, "boss", rank: Rank.SpecialAgentInCharge);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetAccessListAsync("d1", Leader())).Select(e => e.AgentId).ToHashSet();

        Assert.Equal(new HashSet<string> { "tru", "boss" }, ids);
    }

    [Fact]
    public async Task GetAccessListAsync_HrbClassified_IncludesHrbAgents()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.IsHRBClassified = true);
        AddAgent(ctx, "plain");
        AddAgent(ctx, "hrb", configure: a => a.IsHRB = true);
        AddAgent(ctx, "boss", rank: Rank.SpecialAgentInCharge);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetAccessListAsync("d1", Leader())).Select(e => e.AgentId).ToHashSet();

        Assert.Equal(new HashSet<string> { "hrb", "boss" }, ids);
    }

    [Fact]
    public async Task GetAccessListAsync_TaskforceOwned_IncludesMembersAndLeadershipOnly()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.OwnerTaskforceId = "tf1");
        AddAgent(ctx, "mem");
        AddAgent(ctx, "out");
        AddAgent(ctx, "boss", rank: Rank.SpecialAgentInCharge);
        AddTaskforceMember(ctx, "tf1", "mem", TaskforceRole.Member);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetAccessListAsync("d1", Leader())).Select(e => e.AgentId).ToHashSet();

        // assigned member + leadership; the unassigned junior is excluded
        Assert.Equal(new HashSet<string> { "mem", "boss" }, ids);
    }

    // ---------- RevokeAsync ----------

    [Fact]
    public async Task RevokeAsync_Throws_Unauthorized_WhenOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // write guard runs first, before any lookup
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RevokeAsync("d1", "t1", OnlyReader()));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenRevokingSelf()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // cannot revoke your own access
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("d1", "lead", Leader("lead")));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenDocumentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("ghost", "t1", Leader()));
    }

    [Fact]
    public async Task RevokeAsync_Throws_Unauthorized_WhenCannotManage()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "someone-else");
        var (svc, _) = Build(ctx);

        // non-leadership writer who neither created the doc nor leads its taskforce
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RevokeAsync("d1", "t1", Writer("me2")));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenTargetMissing()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        var (svc, _) = Build(ctx);

        // target agent not in db.Users
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("d1", "ghost", Leader()));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenTargetIsAdmin()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddAgent(ctx, "adm", configure: a => a.IsAdmin = true);
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("d1", "adm", Leader()));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenTargetIsPartner()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddAgent(ctx, "partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD);
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("d1", "partner", Leader()));
    }

    [Fact]
    public async Task RevokeAsync_Throws_InvalidOperation_WhenNonLeadershipRevokesLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "creator1");
        AddAgent(ctx, "boss", rank: Rank.SpecialAgentInCharge);
        var (svc, _) = Build(ctx);

        // creator (non-leadership) may manage the doc but cannot strip a leadership agent
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevokeAsync("d1", "boss", Writer("creator1")));
    }

    [Fact]
    public async Task RevokeAsync_AddsExclusion_AndBroadcasts_OnHappyPath()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddAgent(ctx, "t1");
        var (svc, broadcaster) = Build(ctx);
        string? reported = null;
        broadcaster.Modified += id => reported = id;

        await svc.RevokeAsync("d1", "t1", Leader());

        using var check = ctx.NewContext();
        Assert.True(await check.DocumentAccessExclusions.AnyAsync(x => x.DocumentId == "d1" && x.AgentId == "t1"));
        Assert.Equal("d1", reported);
    }

    [Fact]
    public async Task RevokeAsync_Idempotent_WhenExclusionAlreadyExists()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddAgent(ctx, "t1");
        AddExclusion(ctx, "d1", "t1");
        var (svc, _) = Build(ctx);

        await svc.RevokeAsync("d1", "t1", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.DocumentAccessExclusions.CountAsync(x => x.DocumentId == "d1" && x.AgentId == "t1"));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_Throws_Unauthorized_WhenOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // write guard runs first
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("d1", "t1", OnlyReader()));
    }

    [Fact]
    public async Task RestoreAsync_Throws_InvalidOperation_WhenDocumentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreAsync("ghost", "t1", Leader()));
    }

    [Fact]
    public async Task RestoreAsync_Throws_Unauthorized_WhenCannotManage()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1", d => d.CreatedById = "someone-else");
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("d1", "t1", Writer("me2")));
    }

    [Fact]
    public async Task RestoreAsync_RemovesExclusion_AndBroadcasts_OnHappyPath()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        AddExclusion(ctx, "d1", "t1");
        var (svc, broadcaster) = Build(ctx);
        string? reported = null;
        broadcaster.Modified += id => reported = id;

        await svc.RestoreAsync("d1", "t1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row gone from the filtered set
        Assert.False(await check.DocumentAccessExclusions.AnyAsync(x => x.DocumentId == "d1" && x.AgentId == "t1"));
        Assert.Equal("d1", reported);
    }

    [Fact]
    public async Task RestoreAsync_NoOp_WhenNoExclusion()
    {
        using var ctx = new SqliteTestContext();
        AddDocument(ctx, "d1");
        var (svc, _) = Build(ctx);

        // nothing to restore => returns silently
        await svc.RestoreAsync("d1", "t1", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.DocumentAccessExclusions.CountAsync());
    }
}
