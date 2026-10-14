using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Services.Integration;

public class AgentManagementServiceTests
{
    private sealed class Fixture : IDisposable
    {
        public required SqliteTestContext Ctx;
        public required AppDbContext Db;
        public required AgentManagementService Svc;
        public required INotificationService Notifications;
        public required IDiscordWebhookService Discord;
        public required string AvatarPath;
        public required string Root;
        public void Dispose()
        {
            Db.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* ignore */ }
            catch (UnauthorizedAccessException) { /* ignore */ }
        }
    }

    private static UserManager<Agent> BuildUserManager(AppDbContext db)
        => new(new UserStore<Agent>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<Agent>(), Array.Empty<IUserValidator<Agent>>(),
            Array.Empty<IPasswordValidator<Agent>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);

    private static Fixture Make(string? bootstrapDiscordId = null)
    {
        var ctx = new SqliteTestContext();
        var db = ctx.NewContext();
        var notifications = Substitute.For<INotificationService>();
        var discord = Substitute.For<IDiscordWebhookService>();
        var builder = new ConfigurationBuilder();
        if (bootstrapDiscordId is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:AdminDiscordId"] = bootstrapDiscordId,
            });
        }
        // real files on disk: the service deletes superseded pictures, and that is worth asserting
        var root = Path.Combine(Path.GetTempPath(), "noose-avatar-tests", Guid.NewGuid().ToString("N"));
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(root);
        var uploadOptions = new FileUploadOptions();
        var avatars = new AgentAvatarStorageService(env, Options.Create(uploadOptions));

        var svc = new AgentManagementService(BuildUserManager(db), db, ctx.Factory, notifications,
            discord, builder.Build(), avatars);
        return new Fixture
        {
            Ctx = ctx, Db = db, Svc = svc, Notifications = notifications, Discord = discord,
            Root = root, AvatarPath = Path.Combine(root, uploadOptions.AvatarsPath),
        };
    }

    private static MemoryStream Image(int bytes = 32) => new(new byte[bytes]);

    private static void Persist(SqliteTestContext ctx, params Agent[] agents)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        db.SaveChanges();
    }

    private static Agent NewAgent(string id, AgentStatus status = AgentStatus.Pending,
        Rank? rank = null, Action<Agent>? cfg = null)
        => Seed.Agent(id, rank ?? Rank.JuniorAgent, status, a =>
        {
            a.Rank = rank;
            a.SecurityStamp = System.Guid.NewGuid().ToString();
            a.ConcurrencyStamp = System.Guid.NewGuid().ToString();
            cfg?.Invoke(a);
        });

    private static ClaimsPrincipal Admin(string id = "admin")
        => ClaimsPrincipalBuilder.Agent(id).AsAdmin().WithRank(Rank.Director).WithCodename("Chief").Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SupervisorySpecialAgent).WithCodename("Lead").Build();

    private static ClaimsPrincipal Junior(string id = "jr")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static ClaimsPrincipal OnlyReader(string id = "ro")
        => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().WithRank(Rank.Director).Build();

    private Agent Reload(Fixture f, string id)
    {
        using var db = f.Ctx.NewContext();
        return db.Users.Single(a => a.Id == id);
    }

    // ---- read methods ----

    [Fact]
    public async Task GetPendingAsync_ReturnsOnlyPending_OrderedByRegistered()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("p1", AgentStatus.Pending, cfg: a => a.RegisteredAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewAgent("p2", AgentStatus.Pending, cfg: a => a.RegisteredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewAgent("a1", AgentStatus.Active));

        var pending = await f.Svc.GetPendingAsync();

        Assert.Equal(new[] { "p2", "p1" }, pending.Select(a => a.Id));
    }

    [Fact]
    public async Task GetAllAsync_ExcludesApplicants_PendingFirst()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("act", AgentStatus.Active, cfg: a => a.Codename = "Bravo"),
            NewAgent("pen", AgentStatus.Pending, cfg: a => a.Codename = "Alpha"),
            NewAgent("app", AgentStatus.Applicant));

        var all = await f.Svc.GetAllAsync();

        Assert.DoesNotContain(all, a => a.Id == "app");
        Assert.Equal("pen", all.First().Id);
    }

    [Fact]
    public async Task GetSelectableAsync_OnlyActiveInternalNonTeamLead()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("ok", AgentStatus.Active),
            NewAgent("tl", AgentStatus.Active, cfg: a => a.IsTeamLead = true),
            // not even with the admin flag on top
            NewAgent("tl-adm", AgentStatus.Active, cfg: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
            NewAgent("partner", AgentStatus.Active, cfg: a => a.PartnerAgency = PartnerAgency.LSPD),
            NewAgent("pen", AgentStatus.Pending));

        var sel = await f.Svc.GetSelectableAsync();

        Assert.Single(sel);
        Assert.Equal("ok", sel[0].Id);
    }

    [Fact]
    public async Task FindAsync_ReturnsAgent_OrNull()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("x"));

        Assert.Equal("x", (await f.Svc.FindAsync("x"))!.Id);
        Assert.Null(await f.Svc.FindAsync("nope"));
    }

    [Fact]
    public async Task GetAvailableBadgeNumbersAsync_ExcludesAssignedAndPending_AndKeepsOwnLegacyValue()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("target", AgentStatus.Active, cfg: a => { a.BadgeNumber = "legacy"; a.PendingBadgeNumber = "iv"; }),
            NewAgent("assigned", AgentStatus.Active, cfg: a => a.BadgeNumber = " II "),
            NewAgent("pending", AgentStatus.Active, cfg: a => a.PendingBadgeNumber = "V"));

        var options = await f.Svc.GetAvailableBadgeNumbersAsync("target");

        Assert.DoesNotContain("II", options);
        Assert.DoesNotContain("V", options);
        Assert.Contains("IV", options);
        Assert.DoesNotContain("iv", options);
        Assert.Contains("legacy", options);
        Assert.Contains("I", options);
        Assert.Equal(23, options.Count(x => BadgeNumbers.All.Contains(x, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- release / reject ----

    [Fact]
    public async Task ReleaseAsync_ActivatesAndSetsRank_WritesHistoryAndNotifies()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, isTRU: true, isHRB: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Equal(Rank.SpecialAgent, a.Rank);
        Assert.True(a.IsTRU);
        using var db = f.Ctx.NewContext();
        Assert.True(db.AgentRankHistories.Any(h => h.AgentId == "t"));
        await f.Notifications.Received().NotifyAsync("t", NotificationType.Account, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReleaseAsPartnerAsync_RequiresLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.ReleaseAsPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Junior()));
    }

    [Fact]
    public async Task ReleaseAsPartnerAsync_SetsPartnerClearsInternal()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending, rank: Rank.Director, cfg: a => a.IsAdmin = true));

        await f.Svc.ReleaseAsPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Equal(PartnerAgency.LSPD, a.PartnerAgency);
        Assert.Null(a.Rank);
        Assert.False(a.IsAdmin);
    }

    [Fact]
    public async Task RejectAsync_BlocksWithReason()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t"));

        await f.Svc.RejectAsync("t", "  ", Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Blocked, a.Status);
        Assert.False(string.IsNullOrWhiteSpace(a.BlockedReason));
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_RejectsNonApplicant()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, false, false, Admin()));
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_ActivatesApplicant()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, false, true, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.True(a.IsHRB);
    }

    private static void PersistPartner(SqliteTestContext ctx, string id = "t")
        => Persist(ctx, NewAgent(id, AgentStatus.Active, cfg: a =>
        {
            a.PartnerAgency = PartnerAgency.DoJ;
            a.PartnerRank = PartnerRank.Member;
        }));

    private static void PersistApplication(SqliteTestContext ctx, string applicantUserId)
    {
        using var db = ctx.NewContext();
        db.Bewerbungen.Add(new NOOSE_Website.Data.Entities.Recruiting.Bewerbung
        {
            CaseNumber = "NOOSE-B-2026-0001", ApplicantUserId = applicantUserId, Name = "Trevor Ward",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_HiresAPartnerAndEndsThePartnerRole()
    {
        // a partner applied without ever leaving status Active, so it reaches the hire panel from there
        using var f = Make();
        PersistPartner(f.Ctx);
        PersistApplication(f.Ctx, "t");

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, false, false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Equal(Rank.SpecialAgent, a.Rank);
        Assert.Null(a.PartnerAgency);
        Assert.Null(a.PartnerRank);
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_RejectsAPartnerThatNeverApplied()
    {
        // status Applicant is the applicant's proof of having gone through recruiting; a partner needs the row
        using var f = Make();
        PersistPartner(f.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, false, false, Admin()));

        var a = Reload(f, "t");
        Assert.Equal(PartnerAgency.DoJ, a.PartnerAgency);
    }

    // ---- citizen starts an application ----

    private static Agent NewCitizen(string id = "c")
        => NewAgent(id, AgentStatus.Civilian, cfg: a =>
        {
            // citizens carry no codename; the audit row has to name them by their Discord handle
            a.Codename = string.Empty;
            a.DiscordUsername = "buerger-" + id;
        });

    [Fact]
    public async Task StartApplicationAsync_PromotesCitizenAndRotatesStamp()
    {
        using var f = Make();
        Persist(f.Ctx, NewCitizen());
        var before = Reload(f, "c").SecurityStamp;

        var returned = await f.Svc.StartApplicationAsync("c");

        Assert.Equal(AgentStatus.Applicant, returned.Status);
        var a = Reload(f, "c");
        Assert.Equal(AgentStatus.Applicant, a.Status);
        Assert.NotEqual(before, a.SecurityStamp);
    }

    [Fact]
    public async Task StartApplicationAsync_WritesAnAuditRowTheViewerCanRender()
    {
        using var f = Make();
        Persist(f.Ctx, NewCitizen());

        await f.Svc.StartApplicationAsync("c");

        using var db = f.Ctx.NewContext();
        var row = Assert.Single(db.AuditLogs.Where(l => l.EntityType == nameof(Agent) && l.EntityId == "c"));
        Assert.Equal("c", row.AgentId);
        Assert.Equal("buerger-c", row.AgentName);
        // the free-text shape the other agent rows use renders as an em dash
        var change = Assert.Single(AuditDisplay.Parse(row.ChangesJson));
        Assert.Equal("Status", change.Field);
        Assert.Equal("Bürger", change.Alt);
        Assert.Equal("Bewerber", change.New);
    }

    [Theory]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Applicant)]
    public async Task StartApplicationAsync_RejectsEveryStatusButCitizen(AgentStatus status)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", status));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.StartApplicationAsync("t"));

        Assert.Equal(status, Reload(f, "t").Status);
    }

    [Fact]
    public async Task StartApplicationAsync_KeepsTheAccountOutOfPickers()
    {
        using var f = Make();
        Persist(f.Ctx, NewCitizen());

        await f.Svc.StartApplicationAsync("c");

        Assert.DoesNotContain(await f.Svc.GetSelectableAsync(), a => a.Id == "c");
        Assert.DoesNotContain(await f.Svc.GetAllAsync(), a => a.Id == "c");
    }

    // ---- master data / name change ----

    [Fact]
    public async Task MasterDataChangeAsync_EmptyCodenameThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.MasterDataChangeAsync("t", "Real", "  ", "42", Admin()));
    }

    [Fact]
    public async Task MasterDataChangeAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.MasterDataChangeAsync("t", "Real", "Cool", "42", OnlyReader()));
    }

    [Fact]
    public async Task MasterDataChangeAsync_UpdatesFields()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.MasterDataChangeAsync("t", "  John  ", "  Ghost  ", "  IX  ", Admin());

        var a = Reload(f, "t");
        Assert.Equal("Ghost", a.Codename);
        Assert.Equal("John", a.RealName);
        Assert.Equal("IX", a.BadgeNumber);
    }

    [Fact]
    public async Task MasterDataChangeAsync_RejectsInvalidOrAssignedBadgeNumber()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("target", AgentStatus.Active),
            NewAgent("assigned", AgentStatus.Active, cfg: a => a.BadgeNumber = " IV "),
            NewAgent("pending", AgentStatus.Active, cfg: a => a.PendingBadgeNumber = "V"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Svc.MasterDataChangeAsync("target", null, "Target", "99", Admin()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Svc.MasterDataChangeAsync("target", null, "Target", "IV", Admin()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Svc.MasterDataChangeAsync("target", null, "Target", "V", Admin()));
    }

    [Fact]
    public async Task MasterDataChangeAsync_AllowsUnchangedLegacyBadgeNumber()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("target", AgentStatus.Active, cfg: a => a.BadgeNumber = "legacy"));

        await f.Svc.MasterDataChangeAsync("target", "Real", "Target", "legacy", Admin());

        Assert.Equal("legacy", Reload(f, "target").BadgeNumber);
    }

    [Fact]
    public async Task MasterDataChangeAsync_KeepsADuplicateLegacyBadgeNumberOfItsOwnAgent()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("a", AgentStatus.Active, cfg: x => x.BadgeNumber = "V"),
            NewAgent("b", AgentStatus.Active, cfg: x => x.BadgeNumber = "V"));

        await f.Svc.MasterDataChangeAsync("a", "Real", "Changed", "V", Admin());

        var a = Reload(f, "a");
        Assert.Equal("Changed", a.Codename);
        Assert.Equal("V", a.BadgeNumber);
    }

    [Fact]
    public async Task MasterDataChangeAsync_StillRejectsAForeignBadgeNumber()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("a", AgentStatus.Active, cfg: x => x.BadgeNumber = "V"),
            NewAgent("b", AgentStatus.Active, cfg: x => x.BadgeNumber = "IX"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.MasterDataChangeAsync("a", null, "A", "IX", Admin()));
    }

    [Fact]
    public async Task NameChangeRequest_Then_Approve_Flow()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, cfg: a => a.Codename = "Old"));

        await f.Svc.NameChangeRequestAsync("t", "New Real", "NewCode", "VII", Admin());
        var pendings = await f.Svc.GetPendingNameChangesAsync();
        Assert.Contains(pendings, a => a.Id == "t");

        await f.Svc.NameChangeApproveAsync("t", Admin());
        var a = Reload(f, "t");
        Assert.Equal("NewCode", a.Codename);
        Assert.Null(a.NameChangeRequestedAt);
    }

    [Fact]
    public async Task NameChangeApproveAsync_NoRequestThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.NameChangeApproveAsync("t", Admin()));
    }

    [Fact]
    public async Task NameChangeRejectAsync_ClearsPending()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active,
            cfg: a => { a.PendingCodename = "Want"; a.NameChangeRequestedAt = DateTime.UtcNow; }));

        await f.Svc.NameChangeRejectAsync("t", "no", Admin());

        var a = Reload(f, "t");
        Assert.Null(a.NameChangeRequestedAt);
        Assert.Null(a.PendingCodename);
    }

    // ---- rank / flags ----

    [Fact]
    public async Task RankChangeAsync_RequiresLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, Junior()));
    }

    [Fact]
    public async Task RankChangeAsync_SetsRank_WritesHistory()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.JuniorAgent));

        await f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, Admin());

        Assert.Equal(Rank.SeniorSpecialAgent, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task SetPartnerRankAsync_NonPartnerThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.SetPartnerRankAsync("t", PartnerRank.Special, Admin()));
    }

    [Fact]
    public async Task SetPartnerRankAsync_UpdatesPartner()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active,
            cfg: a => { a.PartnerAgency = PartnerAgency.LSPD; a.PartnerRank = PartnerRank.Member; }));

        await f.Svc.SetPartnerRankAsync("t", PartnerRank.Special, Admin());

        Assert.Equal(PartnerRank.Special, Reload(f, "t").PartnerRank);
    }

    [Fact]
    public async Task ConvertToPartnerAsync_InactiveThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Admin()));
    }

    [Fact]
    public async Task ConvertToPartnerAsync_LastAdminBlocked()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("only", AgentStatus.Active, rank: Rank.Director, cfg: a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ConvertToPartnerAsync("only", PartnerAgency.LSPD, PartnerRank.Member, Admin("other")));
    }

    [Fact]
    public async Task ConvertToPartnerAsync_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSMD, PartnerRank.Member, Admin("keepadmin"));

        var a = Reload(f, "t");
        Assert.Equal(PartnerAgency.LSMD, a.PartnerAgency);
        Assert.Null(a.Rank);
    }

    [Fact]
    public async Task ConvertToInternalAsync_AlreadyInternalThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, Admin()));
    }

    [Fact]
    public async Task ConvertToInternalAsync_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active,
            cfg: a => { a.PartnerAgency = PartnerAgency.LSPD; a.PartnerRank = PartnerRank.Member; }));

        await f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, Admin());

        var a = Reload(f, "t");
        Assert.Null(a.PartnerAgency);
        Assert.Equal(Rank.SpecialAgent, a.Rank);
    }

    // ---- promotion decision ----

    [Fact]
    public async Task PromotionDecideAsync_ApprovesAndSetsRank()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.JuniorAgent));
        using (var db = f.Ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new NOOSE_Website.Data.Entities.Personnel.AgentPromotionRequest
            {
                Id = "req1", AgentId = "t", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested,
            });
            db.SaveChanges();
        }

        await f.Svc.PromotionDecideAsync("req1", approved: true, note: "ok", Admin());

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task PromotionDecideAsync_NotFoundThrows()
    {
        using var f = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.PromotionDecideAsync("nope", true, null, Admin()));
    }

    [Fact]
    public async Task TruSet_HrbSet_ToggleFlags()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await f.Svc.TruSetAsync("t", true, Admin());
        await f.Svc.HrbSetAsync("t", true, Admin());

        var a = Reload(f, "t");
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    // ---- departments follow the rank (service regulation 2.4.1) ----

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.JuniorAgent)]
    public async Task TruSetAsync_BelowSpecialAgent_Throws(Rank? rank)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: rank));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TruSetAsync("t", true, Admin()));
        Assert.False(Reload(f, "t").IsTRU);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.JuniorAgent)]
    public async Task HrbSetAsync_BelowSpecialAgent_Throws(Rank? rank)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: rank));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.HrbSetAsync("t", true, Admin()));
        Assert.False(Reload(f, "t").IsHRB);
    }

    /// <summary>A legacy flag below the threshold has to stay removable, or the account is stuck with it.</summary>
    [Fact]
    public async Task HrbSetAsync_ClearingIsAllowedBelowTheThreshold()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.JuniorAgent, cfg: a => a.IsHRB = true));

        await f.Svc.HrbSetAsync("t", false, Admin());

        Assert.False(Reload(f, "t").IsHRB);
    }

    [Fact]
    public async Task TruSetAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.TruSetAsync("t", true, OnlyReader()));
    }

    [Fact]
    public async Task HrbSetAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.HrbSetAsync("t", true, OnlyReader()));
    }

    [Fact]
    public async Task ReleaseAsync_JuniorAgentWithDepartment_Throws()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ReleaseAsync("t", Rank.JuniorAgent, isTRU: false, isHRB: true, Admin()));
        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);
    }

    [Fact]
    public async Task ReleaseAsync_SpecialAgentWithDepartment_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, isTRU: true, isHRB: true, Admin());

        var a = Reload(f, "t");
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    /// <summary>A demotion has to take the departments with it; otherwise the flag keeps opening every HRB gate.</summary>
    [Fact]
    public async Task RankChangeAsync_DemotionBelowThreshold_DropsDepartments()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.IsTRU = true; a.IsHRB = true; }));

        await f.Svc.RankChangeAsync("t", Rank.JuniorAgent, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Rank.JuniorAgent, a.Rank);
        Assert.False(a.IsTRU);
        Assert.False(a.IsHRB);
    }

    [Fact]
    public async Task RankChangeAsync_StayingAboveThreshold_KeepsDepartments()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.IsTRU = true; a.IsHRB = true; }));

        await f.Svc.RankChangeAsync("t", Rank.SpecialAgent, Admin());

        var a = Reload(f, "t");
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    [Fact]
    public async Task PromotionDecideAsync_DemotionBelowThreshold_DropsDepartments()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent,
            cfg: a => a.IsHRB = true));
        using (var db = f.Ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new NOOSE_Website.Data.Entities.Personnel.AgentPromotionRequest
            {
                Id = "req-down", AgentId = "t", TargetRank = Rank.JuniorAgent, Status = PromotionStatus.Requested,
            });
            db.SaveChanges();
        }

        await f.Svc.PromotionDecideAsync("req-down", approved: true, note: null, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Rank.JuniorAgent, a.Rank);
        Assert.False(a.IsHRB);
    }

    [Fact]
    public async Task TeamLeadSetAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.TeamLeadSetAsync("t", true, OnlyReader()));
    }

    [Fact]
    public async Task AdminSetAsync_RequiresAdmin()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.AdminSetAsync("t", true, Leader()));
    }

    [Fact]
    public async Task AdminSetAsync_SelfStripBlocked()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("me", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AdminSetAsync("me", false, Admin("me")));
    }

    [Fact]
    public async Task AdminSetAsync_LastAdminBlocked()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("only", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AdminSetAsync("only", false, Admin("other")));
    }

    [Fact]
    public async Task AdminSetAsync_GrantsAdmin()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.AdminSetAsync("t", true, Admin());

        Assert.True(Reload(f, "t").IsAdmin);
    }

    // ---- block / unblock / delete ----

    [Fact]
    public async Task BlockAsync_SelfThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("me", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.BlockAsync("me", "x", Admin("me")));
    }

    [Fact]
    public async Task Block_Then_Unblock()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.BlockAsync("t", "spy", Admin());
        Assert.Equal(AgentStatus.Blocked, Reload(f, "t").Status);

        await f.Svc.UnblockAsync("t", Admin());
        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Null(a.BlockedReason);
    }

    [Fact]
    public async Task Becoming_selectable_again_restarts_the_inactivity_count()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("blocked", AgentStatus.Blocked, Rank.SpecialAgent),
            NewAgent("gone", AgentStatus.Terminated, Rank.SpecialAgent),
            NewAgent("partner", AgentStatus.Active,
                cfg: a => { a.PartnerAgency = PartnerAgency.LSPD; a.PartnerRank = PartnerRank.Member; }),
            NewAgent("tl", AgentStatus.Active, Rank.SpecialAgent, a => a.IsTeamLead = true),
            NewAgent("plain", AgentStatus.Active, Rank.SpecialAgent));
        var before = DateTime.UtcNow.AddSeconds(-1);

        await f.Svc.UnblockAsync("blocked", Admin());
        await f.Svc.TerminationRevokeAsync("gone", Admin());
        await f.Svc.ConvertToInternalAsync("partner", Rank.SpecialAgent, Admin());
        await f.Svc.TeamLeadSetAsync("tl", false, Admin());
        // becoming oversight is no return to the roster, and clearing a flag that was never set is none either
        await f.Svc.TeamLeadSetAsync("plain", false, Admin());
        Assert.Null(Reload(f, "plain").ActiveSinceAt);
        await f.Svc.TeamLeadSetAsync("plain", true, Admin());

        foreach (var id in new[] { "blocked", "gone", "partner", "tl" })
        {
            Assert.True(Reload(f, id).ActiveSinceAt > before, id);
        }
        Assert.Null(Reload(f, "plain").ActiveSinceAt);
    }

    [Fact]
    public async Task DeleteAccountAsync_SelfThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("me", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.DeleteAccountAsync("me", Admin("me")));
    }

    [Fact]
    public async Task DeleteAccountAsync_RemovesUser_WritesAudit()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active, cfg: a => a.Codename = "Gone"),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.DeleteAccountAsync("t", Admin("keepadmin"));

        using var db = f.Ctx.NewContext();
        Assert.False(db.Users.Any(u => u.Id == "t"));
        Assert.True(db.AuditLogs.Any(l => l.EntityId == "t" && l.Action == AuditAction.Deleted));
    }

    [Fact]
    public async Task DeleteAccountAsync_LastAdminThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("only", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.DeleteAccountAsync("only", Admin("other")));
    }

    // ---- terminate ----

    [Fact]
    public async Task TerminateAsync_SetsStatusAndFields()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.SpecialAgent, a => a.Codename = "Falcon"));
        var before = Reload(f, "t").SecurityStamp;

        await f.Svc.TerminateAsync("t", "  Dienstvergehen  ", createNote: false, postDiscord: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Terminated, a.Status);
        Assert.NotNull(a.TerminatedAt);
        Assert.Equal("admin", a.TerminatedById);
        Assert.Equal("Chief", a.TerminatedByName);
        Assert.Equal("Dienstvergehen", a.TerminationReason);
        Assert.Equal(Rank.SpecialAgent, a.Rank); // last rank is kept for the roster column
        Assert.NotEqual(before, a.SecurityStamp);
    }

    [Fact]
    public async Task TerminateAsync_DropsAdminRights()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active, cfg: a => a.IsAdmin = true),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.TerminateAsync("t", "weg", createNote: false, postDiscord: false, Admin("keepadmin"));

        Assert.False(Reload(f, "t").IsAdmin);
    }

    [Fact]
    public async Task TerminateAsync_CreatesBlacklistEntry()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, cfg: a =>
        {
            a.Codename = "Falcon";
            a.RealName = "John Doe";
            a.DiscordId = "999";
        }));

        await f.Svc.TerminateAsync("t", "Verrat", createNote: false, postDiscord: false, Admin());

        using var db = f.Ctx.NewContext();
        var sperre = Assert.Single(db.Bewerbungssperren.Where(s => s.AgentId == "t"));
        Assert.True(sperre.IsBlacklist);
        Assert.Null(sperre.BannedUntil);
        Assert.Equal("999", sperre.DiscordId);
        Assert.Equal("John Doe - Falcon", sperre.ApplicantName);
        Assert.Contains("Verrat", sperre.Reason);
    }

    [Fact]
    public async Task TerminateAsync_SupersedesTemporaryBan()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));
        using (var seed = f.Ctx.NewContext())
        {
            seed.Bewerbungssperren.Add(new Bewerbungssperre
            {
                AgentId = "t",
                IsBlacklist = false,
                BannedUntil = DateTime.UtcNow.AddDays(7),
            });
            seed.SaveChanges();
        }

        await f.Svc.TerminateAsync("t", "weg", createNote: false, postDiscord: false, Admin());

        using var db = f.Ctx.NewContext();
        var sperre = Assert.Single(db.Bewerbungssperren.Where(s => s.AgentId == "t"));
        Assert.True(sperre.IsBlacklist);
    }

    [Fact]
    public async Task TerminateAsync_CreateNoteFlagControlsPersonnelEntry()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("with", AgentStatus.Active), NewAgent("without", AgentStatus.Active));

        await f.Svc.TerminateAsync("with", "Grund A", createNote: true, postDiscord: false, Admin());
        await f.Svc.TerminateAsync("without", "Grund B", createNote: false, postDiscord: false, Admin());

        using var db = f.Ctx.NewContext();
        var note = Assert.Single(db.AgentNotes.Where(n => n.AgentId == "with"));
        Assert.Equal(AgentNoteKind.Termination, note.Kind);
        Assert.Contains("Grund A", note.Text);
        Assert.Empty(db.AgentNotes.Where(n => n.AgentId == "without"));
    }

    [Fact]
    public async Task TerminateAsync_NoteEscapesReasonHtml()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.TerminateAsync("t", "<script>alert(1)</script>", createNote: true, postDiscord: false, Admin());

        using var db = f.Ctx.NewContext();
        var note = Assert.Single(db.AgentNotes.Where(n => n.AgentId == "t"));
        Assert.DoesNotContain("<script>", note.Text);
    }

    [Fact]
    public async Task TerminateAsync_PostDiscordFlagControlsWebhook()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.TerminateAsync("t", "weg", createNote: false, postDiscord: false, Admin());

        await f.Discord.DidNotReceiveWithAnyArgs().PushTerminationAsync(
            default!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task TerminateAsync_PostsToTheTerminationChannelNotThePersonnelOne()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, cfg: a => a.RealName = "David Kowalski"));

        await f.Svc.TerminateAsync("t", "Eigenwunsch", createNote: false, postDiscord: true, Admin());

        await f.Discord.Received(1).PushTerminationAsync(
            "t",
            Arg.Is<string>(s => s.Contains("David Kowalski")),
            "admin",
            "Chief",
            Arg.Any<DateTime>(),
            "Eigenwunsch",
            "/personal/t",
            Arg.Any<CancellationToken>());
        // the generic personnel embed stays for the other note kinds
        await f.Discord.DidNotReceiveWithAnyArgs().PushPersonnelEntryAsync(
            default!, default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task TerminateAsync_SelfThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("me", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("me", "weg", false, false, Admin("me")));
    }

    [Fact]
    public async Task TerminateAsync_EmptyReasonThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("t", "   ", false, false, Admin()));
    }

    [Fact]
    public async Task TerminateAsync_AlreadyTerminatedThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Terminated));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("t", "weg", false, false, Admin()));
    }

    [Fact]
    public async Task TerminateAsync_ApplicantThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("t", "weg", false, false, Admin()));
    }

    [Fact]
    public async Task TerminateAsync_LastAdminThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("only", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("only", "weg", false, false, Admin("other")));
    }

    [Fact]
    public async Task TerminateAsync_BootstrapAdminThrows()
    {
        using var f = Make(bootstrapDiscordId: "4711");
        Persist(f.Ctx, NewAgent("boot", AgentStatus.Active, cfg: a => a.DiscordId = "4711"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.TerminateAsync("boot", "weg", false, false, Admin()));
    }

    [Fact]
    public async Task TerminateAsync_JuniorDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.TerminateAsync("t", "weg", false, false, Junior()));
    }

    // The read-only barrier interceptor is absent under SQLite, so this proves the explicit guard carries it.
    [Fact]
    public async Task TerminateAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.TerminateAsync("t", "weg", false, false, OnlyReader()));
    }

    [Fact]
    public async Task TerminateAsync_WritesAuditLog()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await f.Svc.TerminateAsync("t", "Verrat", false, false, Admin());

        using var db = f.Ctx.NewContext();
        Assert.Contains(db.AuditLogs.Where(l => l.EntityId == "t" && l.Action == AuditAction.Modified),
            l => l.ChangesJson != null && l.ChangesJson.Contains("Gek"));
    }

    [Fact]
    public async Task UnblockAsync_TerminatedThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Terminated));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.UnblockAsync("t", Admin()));
    }

    [Fact]
    public async Task TerminationRevokeAsync_RestoresAgentAndLiftsBlacklist()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.SpecialAgent));
        await f.Svc.TerminateAsync("t", "Irrtum", createNote: true, postDiscord: false, Admin());

        await f.Svc.TerminationRevokeAsync("t", Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Null(a.TerminatedAt);
        Assert.Null(a.TerminatedById);
        Assert.Null(a.TerminatedByName);
        Assert.Null(a.TerminationReason);
        Assert.Equal(Rank.SpecialAgent, a.Rank);

        using var db = f.Ctx.NewContext();
        Assert.Empty(db.Bewerbungssperren.Where(s => s.AgentId == "t"));
        // the personnel-file entry survives as the historical record
        Assert.Single(db.AgentNotes.Where(n => n.AgentId == "t"));
    }

    [Fact]
    public async Task TerminationRevokeAsync_NotTerminatedThrows()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.TerminationRevokeAsync("t", Admin()));
    }

    [Fact]
    public async Task TerminationRevokeAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Terminated));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.TerminationRevokeAsync("t", OnlyReader()));
    }

    [Fact]
    public async Task GetSelectableAsync_ExcludesTerminated()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("live", AgentStatus.Active), NewAgent("gone", AgentStatus.Terminated));

        var result = await f.Svc.GetSelectableAsync();

        Assert.Contains(result, a => a.Id == "live");
        Assert.DoesNotContain(result, a => a.Id == "gone");
    }

    [Fact]
    public async Task GetAllAsync_IncludesTerminated()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("gone", AgentStatus.Terminated));

        Assert.Contains(await f.Svc.GetAllAsync(), a => a.Id == "gone");
    }

    // ---- profile picture ----

    [Fact]
    public async Task AvatarSetAsync_BelowLeadership_StagesForRelease()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));

        var agent = Reload(f, "jr");
        Assert.Null(agent.AvatarFileName);
        Assert.NotNull(agent.PendingAvatarFileName);
        Assert.Equal("image/png", agent.PendingAvatarContentType);
        Assert.NotNull(agent.AvatarRequestedAt);
        Assert.True(File.Exists(Path.Combine(f.AvatarPath, agent.PendingAvatarFileName!)));
    }

    [Fact]
    public async Task AvatarSetAsync_Leadership_AppliesInstantly()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("lead", AgentStatus.Active, Rank.SupervisorySpecialAgent));

        await f.Svc.AvatarSetAsync("lead", Image(), "image/jpeg", 32, Leader("lead"));

        var agent = Reload(f, "lead");
        Assert.NotNull(agent.AvatarFileName);
        Assert.Equal("image/jpeg", agent.AvatarContentType);
        Assert.Null(agent.PendingAvatarFileName);
        Assert.Null(agent.AvatarRequestedAt);
    }

    [Fact]
    public async Task AvatarSetAsync_SecondUpload_ReplacesStagedFile()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var first = Reload(f, "jr").PendingAvatarFileName!;
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var second = Reload(f, "jr").PendingAvatarFileName!;

        Assert.NotEqual(first, second);
        Assert.False(File.Exists(Path.Combine(f.AvatarPath, first)));
        Assert.True(File.Exists(Path.Combine(f.AvatarPath, second)));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    [InlineData("")]
    public async Task AvatarSetAsync_Throws_OnDisallowedType(string contentType)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AvatarSetAsync("jr", Image(), contentType, 32, Junior("jr")));

        Assert.Null(Reload(f, "jr").PendingAvatarFileName);
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_WhenTooLarge()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        var tooBig = new FileUploadOptions().AvatarMaxBytes + 1;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AvatarSetAsync("jr", Image(), "image/png", tooBig, Junior("jr")));

        Assert.Null(Reload(f, "jr").PendingAvatarFileName);
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_ForForeignAccount_EvenAsLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        // leadership moderates by removing, it never uploads on someone else's behalf
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Leader()));
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_ForOnlyReader()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("ro", AgentStatus.Active, Rank.Director, a => a.IsTeamLead = true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.AvatarSetAsync("ro", Image(), "image/png", 32, OnlyReader("ro")));
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_ForPartner()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("pa", AgentStatus.Active, Rank.JuniorAgent, a => a.PartnerAgency = PartnerAgency.LSPD));
        var partner = ClaimsPrincipalBuilder.Agent("pa").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.AvatarSetAsync("pa", Image(), "image/png", 32, partner));
    }

    [Fact]
    public async Task AvatarApproveAsync_MovesStagedToActive_AndNotifies()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var staged = Reload(f, "jr").PendingAvatarFileName!;

        await f.Svc.AvatarApproveAsync("jr", Leader());

        var agent = Reload(f, "jr");
        Assert.Equal(staged, agent.AvatarFileName);
        Assert.Equal("image/png", agent.AvatarContentType);
        Assert.Null(agent.PendingAvatarFileName);
        Assert.Null(agent.AvatarRequestedAt);
        Assert.True(File.Exists(Path.Combine(f.AvatarPath, staged)));
        await f.Notifications.Received(1).NotifyAsync("jr", NotificationType.Account,
            Arg.Any<string>(), "/profil", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AvatarApproveAsync_DeletesThePreviousPicture()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        await f.Svc.AvatarApproveAsync("jr", Leader());
        var first = Reload(f, "jr").AvatarFileName!;

        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        await f.Svc.AvatarApproveAsync("jr", Leader());
        var second = Reload(f, "jr").AvatarFileName!;

        Assert.NotEqual(first, second);
        Assert.False(File.Exists(Path.Combine(f.AvatarPath, first)));
        Assert.True(File.Exists(Path.Combine(f.AvatarPath, second)));
    }

    [Fact]
    public async Task AvatarApproveAsync_Throws_WithoutStagedPicture()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.AvatarApproveAsync("jr", Leader()));
    }

    [Fact]
    public async Task AvatarApproveAsync_Throws_ForNonLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.AvatarApproveAsync("jr", Junior("other")));
    }

    [Fact]
    public async Task AvatarRejectAsync_DropsStaged_KeepsActive()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent,
            a => { a.AvatarFileName = "alt.png"; a.AvatarContentType = "image/png"; }));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var staged = Reload(f, "jr").PendingAvatarFileName!;

        await f.Svc.AvatarRejectAsync("jr", "unpassend", Leader());

        var agent = Reload(f, "jr");
        Assert.Equal("alt.png", agent.AvatarFileName);
        Assert.Null(agent.PendingAvatarFileName);
        Assert.Null(agent.AvatarRequestedAt);
        Assert.False(File.Exists(Path.Combine(f.AvatarPath, staged)));
    }

    [Fact]
    public async Task AvatarRejectAsync_Throws_ForNonLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.AvatarRejectAsync("jr", "nein", Junior("other")));
    }

    [Fact]
    public async Task AvatarRemoveAsync_OwnAccount_ClearsBothAndDeletesFiles()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        await f.Svc.AvatarApproveAsync("jr", Leader());
        var active = Reload(f, "jr").AvatarFileName!;
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var staged = Reload(f, "jr").PendingAvatarFileName!;

        await f.Svc.AvatarRemoveAsync("jr", Junior("jr"));

        var agent = Reload(f, "jr");
        Assert.Null(agent.AvatarFileName);
        Assert.Null(agent.AvatarContentType);
        Assert.Null(agent.PendingAvatarFileName);
        Assert.Null(agent.AvatarRequestedAt);
        Assert.False(File.Exists(Path.Combine(f.AvatarPath, active)));
        Assert.False(File.Exists(Path.Combine(f.AvatarPath, staged)));
    }

    [Fact]
    public async Task AvatarRemoveAsync_ForeignAccount_LeadershipOnly()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent,
            a => { a.AvatarFileName = "alt.png"; a.AvatarContentType = "image/png"; }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.AvatarRemoveAsync("jr", Junior("other")));
        Assert.Equal("alt.png", Reload(f, "jr").AvatarFileName);

        await f.Svc.AvatarRemoveAsync("jr", Leader());
        Assert.Null(Reload(f, "jr").AvatarFileName);
    }

    [Fact]
    public async Task GetPendingAvatarsAsync_ListsOnlyStaged_OrderedByRequest()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("a", AgentStatus.Active, Rank.JuniorAgent),
            NewAgent("b", AgentStatus.Active, Rank.JuniorAgent),
            NewAgent("c", AgentStatus.Active, Rank.JuniorAgent,
                a => { a.AvatarFileName = "aktiv.png"; a.AvatarContentType = "image/png"; }));
        await f.Svc.AvatarSetAsync("b", Image(), "image/png", 32, Junior("b"));
        await f.Svc.AvatarSetAsync("a", Image(), "image/png", 32, Junior("a"));

        var pending = await f.Svc.GetPendingAvatarsAsync();

        Assert.Equal(new[] { "b", "a" }, pending.Select(x => x.Id));
    }

    [Fact]
    public async Task FindByAvatarFileAsync_MatchesActiveAndStaged()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("act", AgentStatus.Active, Rank.JuniorAgent,
                a => { a.AvatarFileName = "aktiv.png"; a.AvatarContentType = "image/png"; }),
            NewAgent("jr", AgentStatus.Active, Rank.JuniorAgent));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var staged = Reload(f, "jr").PendingAvatarFileName!;

        Assert.Equal("act", (await f.Svc.FindByAvatarFileAsync("aktiv.png"))?.Id);
        Assert.Equal("jr", (await f.Svc.FindByAvatarFileAsync(staged))?.Id);
        Assert.Null(await f.Svc.FindByAvatarFileAsync("unbekannt.png"));
        Assert.Null(await f.Svc.FindByAvatarFileAsync(""));
    }
}
