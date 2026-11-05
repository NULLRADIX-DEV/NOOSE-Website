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
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services;
using NOOSE_Website.Models.Enums;
using System.Threading.Channels;

namespace NOOSE_Website.Tests.Services.Integration;

public class AgentManagementServiceTests
{
    /// <summary>Remembers what the write paths queue for the role sync; can be told to fail.</summary>
    private sealed class RecordingQueue : IDiscordRoleSyncQueue
    {
        public List<DiscordRoleSyncJob> Jobs { get; } = [];

        public Exception? Fault { get; set; }

        public ChannelReader<DiscordRoleSyncJob> Reader => throw new NotSupportedException();

        public void Enqueue(string agentId) => Add(new AgentJob(agentId, DiscordRoleSyncReason.AgentChanged));

        public void EnqueueDeleted(string discordId, bool isTeamLead)
            => Add(new DeletedJob(discordId, isTeamLead, DiscordRoleSyncReason.AccountDeleted));

        public void EnqueueAll(string reason) => Add(new AllJob(reason));

        private void Add(DiscordRoleSyncJob job)
        {
            if (Fault is not null)
            {
                throw Fault;
            }
            Jobs.Add(job);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public required SqliteTestContext Ctx;
        public required AppDbContext Db;
        public required AgentManagementService Svc;
        public required INotificationService Notifications;
        public required IDiscordWebhookService Discord;
        public required RecordingQueue RoleSync;
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

        var roleSync = new RecordingQueue();
        var svc = new AgentManagementService(BuildUserManager(db), db, ctx.Factory, notifications,
            discord, builder.Build(), avatars, Substitute.For<IPublicLeadershipPhotoStorageService>(), roleSync);
        return new Fixture
        {
            Ctx = ctx, Db = db, Svc = svc, Notifications = notifications, Discord = discord, RoleSync = roleSync,
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
        => Seed.Agent(id, rank ?? Rank.Trainee, status, a =>
        {
            a.Rank = rank;
            a.SecurityStamp = System.Guid.NewGuid().ToString();
            a.ConcurrencyStamp = System.Guid.NewGuid().ToString();
            cfg?.Invoke(a);
        });

    private static ClaimsPrincipal Admin(string id = "admin")
        => ClaimsPrincipalBuilder.Agent(id).AsAdmin().WithRank(Rank.Director).WithCodename("Chief").Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SectionChief).WithCodename("Lead").Build();

    private static ClaimsPrincipal Junior(string id = "jr")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

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
        Assert.Equal(38, options.Count(x => BadgeNumbers.All.Contains(x, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Terminated_agents_badge_number_is_free_again()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active),
            NewAgent("leaver", AgentStatus.Active, cfg: a => { a.BadgeNumber = "II"; a.PendingBadgeNumber = "III"; }));

        Assert.DoesNotContain("II", await f.Svc.GetAvailableBadgeNumbersAsync("t"));
        await f.Svc.TerminateAsync("leaver", "weg", createNote: false, postDiscord: false, Admin());

        var options = await f.Svc.GetAvailableBadgeNumbersAsync("t");
        Assert.Contains("II", options);
        Assert.Contains("III", options);
        await f.Svc.MasterDataChangeAsync("t", null, "Neu", "II", Admin());
        Assert.Equal("II", Reload(f, "t").BadgeNumber);
        // the file keeps the old number for the record
        Assert.Equal("II", Reload(f, "leaver").BadgeNumber);
    }

    [Fact]
    public async Task TerminationRevokeAsync_DropsBadgeNumberTakenMeanwhile()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("gone", AgentStatus.Terminated, Rank.SpecialAgent, a => a.BadgeNumber = "II"),
            NewAgent("new", AgentStatus.Active, Rank.SpecialAgent, a => a.BadgeNumber = "II"));

        await f.Svc.TerminationRevokeAsync("gone", Admin());

        Assert.Null(Reload(f, "gone").BadgeNumber);
        Assert.Equal("II", Reload(f, "new").BadgeNumber);
        using var db = f.Ctx.NewContext();
        Assert.True(db.AuditLogs.Any(l => l.EntityId == "gone" && l.ChangesJson!.Contains("Dienstnummer II")));
    }

    [Fact]
    public async Task TerminationRevokeAsync_KeepsBadgeNumberStillFree()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("gone", AgentStatus.Terminated, Rank.SpecialAgent, a => a.BadgeNumber = "II"));

        await f.Svc.TerminationRevokeAsync("gone", Admin());

        Assert.Equal("II", Reload(f, "gone").BadgeNumber);
    }

    // ---- release / reject ----

    [Fact]
    public async Task ReleaseAsync_ActivatesAndSetsRank_WritesHistoryAndNotifies()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, Division.Hrt, isHRB: false, confirmOverLimit: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Equal(Rank.SpecialAgent, a.Rank);
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
        using var db = f.Ctx.NewContext();
        Assert.True(db.AgentRankHistories.Any(h => h.AgentId == "t"));
        await f.Notifications.Received().NotifyAsync("t", NotificationType.Account, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReleaseAsync_OpensTheOnboardingPlanAndNotifiesTheMentor()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending), NewAgent("m", AgentStatus.Active, Rank.SeniorSpecialAgent));

        await f.Svc.ReleaseAsync("t", Rank.Trainee, Division.Cid, isHRB: false, confirmOverLimit: false, Admin(), mentorId: "m");

        using var db = f.Ctx.NewContext();
        var plan = db.AgentProbations.Single(p => p.AgentId == "t");
        Assert.Equal("m", plan.MentorAgentId);
        Assert.Null(plan.ClosedAt);
        Assert.Equal(ProbationRules.DefaultDays, (plan.EndsAt - plan.StartedAt).TotalDays, precision: 3);
        await f.Notifications.Received().NotifyAsync("m", NotificationType.Account, Arg.Any<string>(), ProbationRules.Route("t"));
    }

    [Fact]
    public async Task ReleaseAsync_WithAnInactiveMentor_IsRefusedBeforeAnythingChanges()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending), NewAgent("gone", AgentStatus.Terminated, Rank.SpecialAgent));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ReleaseAsync("t", Rank.Trainee, Division.Cid, false, false, Admin(), mentorId: "gone"));

        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);
        using var db = f.Ctx.NewContext();
        Assert.False(db.AgentProbations.Any());
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_OpensTheOnboardingPlan()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.Trainee, Division.Cid, false, false, Admin());

        using var db = f.Ctx.NewContext();
        Assert.True(db.AgentProbations.Any(p => p.AgentId == "t" && p.MentorAgentId == null));
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
            () => f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Cid, false, false, Admin()));
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_ActivatesApplicant()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Cid, true, false, Admin());

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
            CaseNumber = "FIB-B-2026-0001", ApplicantUserId = applicantUserId, Name = "Trevor Ward",
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

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Cid, false, false, Admin());

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
            () => f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Cid, false, false, Admin()));

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
            () => f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, Junior()));
    }

    [Fact]
    public async Task RankChangeAsync_SetsRank_WritesHistory()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.Trainee));

        await f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, Admin());

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
            () => f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, false, Admin()));
    }

    [Fact]
    public async Task ConvertToInternalAsync_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active,
            cfg: a => { a.PartnerAgency = PartnerAgency.LSPD; a.PartnerRank = PartnerRank.Member; }));

        await f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, false, Admin());

        var a = Reload(f, "t");
        Assert.Null(a.PartnerAgency);
        Assert.Equal(Rank.SpecialAgent, a.Rank);
    }

    // ---- promotion decision ----

    [Fact]
    public async Task PromotionDecideAsync_ApprovesAndSetsRank()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.Trainee));
        using (var db = f.Ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new NOOSE_Website.Data.Entities.Personnel.AgentPromotionRequest
            {
                Id = "req1", AgentId = "t", TargetRank = Rank.SpecialAgent, Status = PromotionStatus.Requested,
            });
            db.SaveChanges();
        }

        await f.Svc.PromotionDecideAsync("req1", approved: true, note: "ok", false, Admin());

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task PromotionDecideAsync_NotFoundThrows()
    {
        using var f = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.PromotionDecideAsync("nope", true, null, false, Admin()));
    }

    [Fact]
    public async Task DivisionSet_HrbSet_WriteDivisionAndFlag()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, Admin());
        await f.Svc.HrbSetAsync("t", true, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    // ---- departments follow the rank (service regulation 2.4.1) ----

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.Trainee)]
    public async Task DivisionSetAsync_HrtBelowSpecialAgent_Throws(Rank? rank)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: rank));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, Admin()));
        var a = Reload(f, "t");
        Assert.Null(a.Division);
        Assert.False(a.IsTRU);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Rank.Trainee)]
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
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.Trainee, cfg: a => a.IsHRB = true));

        await f.Svc.HrbSetAsync("t", false, Admin());

        Assert.False(Reload(f, "t").IsHRB);
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
    public async Task ReleaseAsync_TraineeWithDepartment_Throws()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ReleaseAsync("t", Rank.Trainee, Division.Cid, isHRB: true, confirmOverLimit: false, Admin()));
        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);
    }

    [Fact]
    public async Task ReleaseAsync_TraineeIntoTheHrt_Throws()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.ReleaseAsync("t", Rank.Trainee, Division.Hrt, isHRB: false, confirmOverLimit: false, Admin()));
        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Pending, a.Status);
        Assert.Null(a.Division);
    }

    [Fact]
    public async Task ReleaseAsync_SpecialAgentWithDepartment_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, Division.Hrt, isHRB: true, confirmOverLimit: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    /// <summary>A demotion has to take the departments with it; otherwise the flag keeps opening every gate of the Administration Division.</summary>
    [Fact]
    public async Task RankChangeAsync_DemotionBelowThreshold_DropsDepartments()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; a.IsHRB = true; }));

        await f.Svc.RankChangeAsync("t", Rank.Trainee, false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Rank.Trainee, a.Rank);
        Assert.Equal(Division.Cid, a.Division);
        Assert.False(a.IsTRU);
        Assert.False(a.IsHRB);
    }

    [Fact]
    public async Task RankChangeAsync_StayingAboveThreshold_KeepsDepartments()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; a.IsHRB = true; }));

        await f.Svc.RankChangeAsync("t", Rank.SpecialAgent, false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
        Assert.True(a.IsHRB);
    }

    /// <summary>A row written before the division column has no division; the TRU flag still says where the agent belongs.</summary>
    [Fact]
    public async Task RankChangeAsync_LegacyTruFlagWithoutDivision_BecomesTheHrt()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent, cfg: a => a.IsTRU = true));

        await f.Svc.RankChangeAsync("t", Rank.SpecialAgent, false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
    }

    [Fact]
    public async Task RankChangeAsync_DemotionBelowThreshold_KeepsTheCtd()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent, cfg: a => a.Division = Division.Ctd));

        await f.Svc.RankChangeAsync("t", Rank.Trainee, false, Admin());

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    /// <summary>The Board keeps the home division but never counts as TRU; the way back restores the flag.</summary>
    [Fact]
    public async Task RankChangeAsync_IntoAndOutOfTheBoard_KeepsTheHomeAndSwitchesTru()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }));

        await f.Svc.RankChangeAsync("t", Rank.Director, false, Admin());
        var inBoard = Reload(f, "t");
        Assert.Equal(Division.Hrt, inBoard.Division);
        Assert.False(inBoard.IsTRU);

        await f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, Admin());
        var back = Reload(f, "t");
        Assert.Equal(Division.Hrt, back.Division);
        Assert.True(back.IsTRU);
    }

    // ---- division set ----

    private static ClaimsPrincipal Board(string id = "board")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.DeputyDirector).WithCodename("Board").Build();

    private static ClaimsPrincipal AdminWithoutBoardRank(string id = "adm")
        => ClaimsPrincipalBuilder.Agent(id).AsAdmin().WithRank(Rank.SpecialAgent).WithCodename("Adm").Build();

    [Fact]
    public async Task DivisionSetAsync_BoardMemberMovesAnAgent_WritesTruAndRotatesTheStamp()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));
        var before = Reload(f, "t").SecurityStamp;

        await f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, Board());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
        Assert.NotEqual(before, a.SecurityStamp);
        using var db = f.Ctx.NewContext();
        Assert.Contains(db.AuditLogs.Where(l => l.EntityId == "t").AsEnumerable(), l => l.ChangesJson!.Contains("Abteilung"));
    }

    [Fact]
    public async Task DivisionSetAsync_AdminWithoutABoardRankMayMove()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.Trainee));

        await f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: false, AdminWithoutBoardRank());

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    [Fact]
    public async Task DivisionSetAsync_LeavingTheHrtClearsTru()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }));

        await f.Svc.DivisionSetAsync("t", Division.Cid, confirmOverLimit: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Cid, a.Division);
        Assert.False(a.IsTRU);
    }

    [Fact]
    public async Task DivisionSetAsync_BoardMemberIntoTheHrt_KeepsTheTruFlagOff()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.Director));

        await f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.False(a.IsTRU);
    }

    [Fact]
    public async Task DivisionSetAsync_SpecialAgentInChargeIsRefused()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: false, Leader()));
        Assert.Null(Reload(f, "t").Division);
    }

    [Fact]
    public async Task DivisionSetAsync_OnlyReaderDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, OnlyReader()));
        Assert.Null(Reload(f, "t").Division);
    }

    /// <summary>The write guard comes first: a demo visitor carries Director and would otherwise pass the Board check.</summary>
    [Fact]
    public async Task DivisionSetAsync_DemoVisitorDenied()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.DivisionSetAsync("t", Division.Hrt, confirmOverLimit: false, demo));
    }

    [Fact]
    public async Task DivisionSetAsync_PartnerAccountThrows()
    {
        using var f = Make();
        PersistPartner(f.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.DivisionSetAsync("t", Division.Cid, confirmOverLimit: false, Admin()));
    }

    private static Agent[] Seats(Division division, int count, string prefix, Action<Agent>? cfg = null)
        => Enumerable.Range(0, count)
            .Select(i => NewAgent($"{prefix}{i}", AgentStatus.Active, Rank.SpecialAgent, a =>
            {
                a.Division = division;
                a.IsTRU = division == Division.Hrt;
                cfg?.Invoke(a);
            }))
            .ToArray();

    [Fact]
    public async Task DivisionSetAsync_FullDivisionWithoutConfirmation_IsRefused()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd), "ctd"));
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        var ex = await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: false, Admin()));

        Assert.Contains("6 von 6", ex.Message, StringComparison.Ordinal);
        Assert.Null(Reload(f, "t").Division);
    }

    [Fact]
    public async Task DivisionSetAsync_FullDivisionWithConfirmation_Succeeds()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd), "ctd"));
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent));

        await f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: true, Admin());

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    /// <summary>Counted over the selectable roster: a blocked account, a team lead and a partner hold no seat.</summary>
    [Fact]
    public async Task DivisionSetAsync_LimitCountsOnlySelectableAgents()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd) - 1, "ctd"));
        Persist(f.Ctx,
            NewAgent("blocked", AgentStatus.Blocked, Rank.SpecialAgent, a => a.Division = Division.Ctd),
            NewAgent("tl", AgentStatus.Active, Rank.SpecialAgent, a => { a.Division = Division.Ctd; a.IsTeamLead = true; }),
            NewAgent("ex", AgentStatus.Terminated, Rank.SpecialAgent, a => a.Division = Division.Ctd),
            NewAgent("t", AgentStatus.Active, Rank.SpecialAgent));

        await f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: false, Admin());

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    /// <summary>An agent already seated in a full division takes no new seat by being set to it again.</summary>
    [Fact]
    public async Task DivisionSetAsync_SameDivisionNeedsNoSeat()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd) + 1, "ctd"));

        await f.Svc.DivisionSetAsync("ctd0", Division.Ctd, confirmOverLimit: false, Admin());

        Assert.Equal(Division.Ctd, Reload(f, "ctd0").Division);
    }

    /// <summary>The Board is no division seat: a Board member counts against the Board, not against the home division.</summary>
    [Fact]
    public async Task DivisionSetAsync_BoardMemberDoesNotCountAgainstTheDivision()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd) - 1, "ctd"));
        Persist(f.Ctx,
            NewAgent("director", AgentStatus.Active, Rank.Director, a => a.Division = Division.Ctd),
            NewAgent("t", AgentStatus.Active, Rank.SpecialAgent));

        await f.Svc.DivisionSetAsync("t", Division.Ctd, confirmOverLimit: false, Admin());

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    [Fact]
    public async Task ReleaseAsync_FullDivisionNeedsConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd), "ctd"));
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.ReleaseAsync("t", Rank.SpecialAgent, Division.Ctd, isHRB: false, confirmOverLimit: false, Admin()));
        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, Division.Ctd, isHRB: false, confirmOverLimit: true, Admin());
        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    [Fact]
    public async Task PromoteApplicantToAgentAsync_FullDivisionNeedsConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Hrt, DivisionDisplay.Limit(Division.Hrt), "hrt"));
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Hrt, false, confirmOverLimit: false, Admin()));
        Assert.Equal(AgentStatus.Applicant, Reload(f, "t").Status);

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SpecialAgent, Division.Hrt, false, confirmOverLimit: true, Admin());
        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
    }

    // ---- who may hand out which rank: the fine gate (PromotionRules) on every path that writes a rank ----

    private static ClaimsPrincipal Holder(string id, Rank rank)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).WithCodename(id).Build();

    /// <summary>The row of an actor: the guard reads rank and home from here, never from the claims.</summary>
    private static void PersistHolder(Fixture f, string id, Rank rank, Division? home = null)
        => Persist(f.Ctx, NewAgent(id, AgentStatus.Active, rank, a =>
        {
            a.Division = home;
            a.IsTRU = home == Division.Hrt && !DivisionRules.IsBoard(rank);
        }));

    private static void PersistRequest(SqliteTestContext ctx, string agentId, Rank target, string id = "req")
    {
        using var db = ctx.NewContext();
        db.AgentPromotionRequests.Add(new NOOSE_Website.Data.Entities.Personnel.AgentPromotionRequest
        {
            Id = id, AgentId = agentId, TargetRank = target, Status = PromotionStatus.Requested,
        });
        db.SaveChanges();
    }

    private static PromotionStatus RequestStatus(Fixture f, string id = "req")
    {
        using var db = f.Ctx.NewContext();
        return db.AgentPromotionRequests.Single(r => r.Id == id).Status;
    }

    // -- ReleaseAsync --

    [Fact]
    public async Task ReleaseAsync_BoardReleasesAnyRankIntoAnyDivision()
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.AssistantDirector);
        Persist(f.Ctx, NewAgent("t1", AgentStatus.Pending), NewAgent("t2", AgentStatus.Pending),
            NewAgent("t3", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t1", Rank.SpecialAgent, Division.Hrt, false, false, Holder("board", Rank.AssistantDirector));
        await f.Svc.ReleaseAsync("t2", Rank.SectionChief, Division.Ctd, false, false, Holder("board", Rank.AssistantDirector));
        await f.Svc.ReleaseAsync("t3", Rank.Director, Division.Cid, false, false, Holder("board", Rank.AssistantDirector));

        Assert.Equal((Rank.SpecialAgent, Division.Hrt), (Reload(f, "t1").Rank!.Value, Reload(f, "t1").Division!.Value));
        Assert.Equal((Rank.SectionChief, Division.Ctd), (Reload(f, "t2").Rank!.Value, Reload(f, "t2").Division!.Value));
        Assert.Equal((Rank.Director, Division.Cid), (Reload(f, "t3").Rank!.Value, Reload(f, "t3").Division!.Value));
    }

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.SpecialAgentInCharge)]
    public async Task ReleaseAsync_SectionChiefReleasesBelowSectionChiefIntoTheOwnDivision(Rank rank)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", rank, Division.Cid, false, false, Holder("sc", Rank.SectionChief));

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Active, a.Status);
        Assert.Equal(rank, a.Rank);
        Assert.Equal(Division.Cid, a.Division);
    }

    [Fact]
    public async Task ReleaseAsync_SectionChiefOfTheHrtReleasesIntoTheHrt()
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Hrt);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", Rank.SpecialAgent, Division.Hrt, false, false, Holder("sc", Rank.SectionChief));

        var a = Reload(f, "t");
        Assert.Equal(Division.Hrt, a.Division);
        Assert.True(a.IsTRU);
    }

    [Theory]
    [InlineData(Rank.SpecialAgent, Division.Ctd)]          // a foreign division
    [InlineData(Rank.SpecialAgent, Division.Hrt)]
    [InlineData(Rank.SectionChief, Division.Cid)]          // not up to Section Chief or the Board
    [InlineData(Rank.AssistantDirector, Division.Cid)]
    [InlineData(Rank.Director, Division.Cid)]
    public async Task ReleaseAsync_SectionChiefIsRefusedOutsideTheOwnDivisionAndFromSectionChiefUp(Rank rank, Division division)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.ReleaseAsync("t", rank, division, false, false, Holder("sc", Rank.SectionChief)));

        var a = Reload(f, "t");
        Assert.Equal(AgentStatus.Pending, a.Status);
        Assert.Null(a.Rank);
    }

    [Theory]
    [InlineData(Rank.Trainee, Division.Cid)]
    [InlineData(Rank.SpecialAgent, Division.Cid)]
    [InlineData(Rank.SpecialAgentInCharge, Division.Cid)]
    [InlineData(Rank.Director, Division.Cid)]
    [InlineData(Rank.Trainee, Division.Ctd)]
    public async Task ReleaseAsync_SpecialAgentInChargeReleasesNobody(Rank rank, Division division)
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.ReleaseAsync("t", rank, division, false, false, Holder("sac", Rank.SpecialAgentInCharge)));

        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);
    }

    [Fact]
    public async Task ReleaseAsync_ReadsTheActorFromTheRowAndFailsClosed()
    {
        using var f = Make();
        // the claim says Board; the row says an agent who was demoted, one who left, and one nobody knows
        Persist(f.Ctx,
            NewAgent("demoted", AgentStatus.Active, Rank.SeniorSpecialAgent),
            NewAgent("left", AgentStatus.Terminated, Rank.Director),
            NewAgent("t", AgentStatus.Pending));

        foreach (var id in new[] { "demoted", "left", "ghost" })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => f.Svc.ReleaseAsync("t", Rank.Trainee, Division.Cid, false, false, Holder(id, Rank.Director)));
        }
        Assert.Equal(AgentStatus.Pending, Reload(f, "t").Status);
    }

    // -- PromoteApplicantToAgentAsync --

    [Fact]
    public async Task PromoteApplicantToAgentAsync_BoardAssignsAnyRankIntoAnyDivision()
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.DeputyDirector);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await f.Svc.PromoteApplicantToAgentAsync("t", Rank.SectionChief, Division.Hrt, false, false, Holder("board", Rank.DeputyDirector));

        var a = Reload(f, "t");
        Assert.Equal(Rank.SectionChief, a.Rank);
        Assert.Equal(Division.Hrt, a.Division);
    }

    [Theory]
    [InlineData(Rank.SpecialAgent, Division.Cid, true)]
    [InlineData(Rank.SpecialAgent, Division.Ctd, false)]
    [InlineData(Rank.SectionChief, Division.Cid, false)]
    public async Task PromoteApplicantToAgentAsync_SectionChiefOnlyIntoTheOwnDivisionBelowSectionChief(
        Rank rank, Division division, bool allowed)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        var call = () => f.Svc.PromoteApplicantToAgentAsync("t", rank, division, false, false, Holder("sc", Rank.SectionChief));

        if (allowed)
        {
            await call();
            Assert.Equal(AgentStatus.Active, Reload(f, "t").Status);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(call);
            Assert.Equal(AgentStatus.Applicant, Reload(f, "t").Status);
        }
    }

    [Theory]
    [InlineData(Rank.Trainee, false)]
    [InlineData(Rank.SpecialAgent, false)]
    public async Task PromoteApplicantToAgentAsync_SpecialAgentInChargeTakesNoApplicantOver(Rank rank, bool allowed)
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        var call = () => f.Svc.PromoteApplicantToAgentAsync("t", rank, Division.Cid, false, false, Holder("sac", Rank.SpecialAgentInCharge));

        if (allowed)
        {
            await call();
            Assert.Equal(rank, Reload(f, "t").Rank);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(call);
            Assert.Equal(AgentStatus.Applicant, Reload(f, "t").Status);
        }
    }

    // -- ConvertToInternalAsync --

    [Fact]
    public async Task ConvertToInternalAsync_BoardAssignsAnyRank()
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.Director);
        PersistPartner(f.Ctx);

        await f.Svc.ConvertToInternalAsync("t", Rank.SectionChief, false, Holder("board", Rank.Director));

        Assert.Equal(Rank.SectionChief, Reload(f, "t").Rank);
    }

    [Theory]
    [InlineData(Division.Cid, Rank.SpecialAgent, true)]
    [InlineData(Division.Cid, Rank.SectionChief, false)]
    // the former partner joins the Section Chief's own division
    [InlineData(Division.Ctd, Rank.SpecialAgent, true)]
    public async Task ConvertToInternalAsync_SectionChiefOnlyIntoTheirOwnDivisionBelowSectionChief(
        Division home, Rank rank, bool allowed)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, home);
        PersistPartner(f.Ctx);

        var call = () => f.Svc.ConvertToInternalAsync("t", rank, false, Holder("sc", Rank.SectionChief));

        if (allowed)
        {
            await call();
            Assert.Equal(rank, Reload(f, "t").Rank);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(call);
            Assert.NotNull(Reload(f, "t").PartnerAgency);
            Assert.Null(Reload(f, "t").Rank);
        }
    }

    [Theory]
    [InlineData(Rank.Trainee, false)]
    [InlineData(Rank.SpecialAgent, false)]
    public async Task ConvertToInternalAsync_SpecialAgentInChargeConvertsNobody(Rank rank, bool allowed)
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        PersistPartner(f.Ctx);

        var call = () => f.Svc.ConvertToInternalAsync("t", rank, false, Holder("sac", Rank.SpecialAgentInCharge));

        if (allowed)
        {
            await call();
            Assert.Equal(rank, Reload(f, "t").Rank);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(call);
            Assert.NotNull(Reload(f, "t").PartnerAgency);
        }
    }

    [Fact]
    public async Task ConvertToInternalAsync_StopsAtTheWriteGuardForTheDemoVisitor()
    {
        using var f = Make();
        PersistPartner(f.Ctx);
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.ConvertToInternalAsync("t", Rank.Trainee, false, demo));
        Assert.NotNull(Reload(f, "t").PartnerAgency);
    }

    // -- RankChangeAsync --

    private static Agent Member(string id, Rank rank, Division? home) => NewAgent(id, AgentStatus.Active, rank, a => a.Division = home);

    [Theory]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SectionChief)]
    [InlineData(Rank.Director)]
    public async Task RankChangeAsync_BoardSetsAnyRank(Rank target)
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.AssistantDirector);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Hrt));

        await f.Svc.RankChangeAsync("t", target, false, Holder("board", Rank.AssistantDirector));

        Assert.Equal(target, Reload(f, "t").Rank);
    }

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SpecialAgentInCharge)]
    public async Task RankChangeAsync_SectionChiefSetsRanksBelowSectionChiefInTheOwnDivision(Rank target)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Cid));

        await f.Svc.RankChangeAsync("t", target, false, Holder("sc", Rank.SectionChief));

        Assert.Equal(target, Reload(f, "t").Rank);
    }

    [Theory]
    // an agent of a foreign division, a rank at Section Chief or above, a peer, a Board member
    [InlineData(Rank.SpecialAgent, Division.Ctd, Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SpecialAgent, Division.Cid, Rank.SectionChief)]
    [InlineData(Rank.SpecialAgent, Division.Cid, Rank.Director)]
    [InlineData(Rank.SectionChief, Division.Cid, Rank.SpecialAgentInCharge)]
    [InlineData(Rank.AssistantDirector, Division.Cid, Rank.Trainee)]
    public async Task RankChangeAsync_SectionChiefIsRefusedForeignDivisionsPeersAndTheBoard(Rank current, Division home, Rank target)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", current, home));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.RankChangeAsync("t", target, false, Holder("sc", Rank.SectionChief)));

        Assert.Equal(current, Reload(f, "t").Rank);
    }

    /// <summary>A Special Agent in Charge is leadership but has lost the direct rank change.</summary>
    [Theory]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.Trainee)]
    public async Task RankChangeAsync_SpecialAgentInChargeIsRefused(Rank target)
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Cid));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.RankChangeAsync("t", target, false, Holder("sac", Rank.SpecialAgentInCharge)));

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task RankChangeAsync_ReadsTheSectionChiefsDivisionFromTheRow()
    {
        using var f = Make();
        // the cookie says Section Chief of the HRT, the row says the CID since a Board member moved them
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Hrt));
        var stale = ClaimsPrincipalBuilder.Agent("sc").WithRank(Rank.SectionChief).AsDivision(Division.Hrt).AsTru().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, stale));

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
    }

    // -- PromotionDecideAsync --

    [Fact]
    public async Task PromotionDecideAsync_SectionChiefDecidesARequestOfTheOwnDivision()
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.SeniorSpecialAgent);

        await f.Svc.PromotionDecideAsync("req", approved: true, note: null, false, Holder("sc", Rank.SectionChief));

        Assert.Equal(Rank.SeniorSpecialAgent, Reload(f, "t").Rank);
        Assert.Equal(PromotionStatus.Approved, RequestStatus(f));
    }

    [Fact]
    public async Task PromotionDecideAsync_SectionChiefMayAlsoRejectARequestOfTheOwnDivision()
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.SeniorSpecialAgent);

        await f.Svc.PromotionDecideAsync("req", approved: false, note: "noch nicht", false, Holder("sc", Rank.SectionChief));

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
        Assert.Equal(PromotionStatus.Rejected, RequestStatus(f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotionDecideAsync_SectionChiefIsRefusedARequestOfAForeignDivision(bool approved)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Ctd));
        PersistRequest(f.Ctx, "t", Rank.SeniorSpecialAgent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.PromotionDecideAsync("req", approved, null, false, Holder("sc", Rank.SectionChief)));

        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
        Assert.Equal(PromotionStatus.Requested, RequestStatus(f));
    }

    [Theory]
    [InlineData(Rank.SectionChief)]
    [InlineData(Rank.AssistantDirector)]
    public async Task PromotionDecideAsync_SectionChiefIsRefusedARequestForSectionChiefOrAbove(Rank target)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", Rank.SpecialAgentInCharge, Division.Cid));
        PersistRequest(f.Ctx, "t", target);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.PromotionDecideAsync("req", true, null, false, Holder("sc", Rank.SectionChief)));

        Assert.Equal(Rank.SpecialAgentInCharge, Reload(f, "t").Rank);
        Assert.Equal(PromotionStatus.Requested, RequestStatus(f));
    }

    [Fact]
    public async Task PromotionDecideAsync_BoardDecidesEveryRequest()
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.AssistantDirector);
        Persist(f.Ctx, Member("t", Rank.SpecialAgentInCharge, Division.Ctd));
        PersistRequest(f.Ctx, "t", Rank.SectionChief);

        await f.Svc.PromotionDecideAsync("req", true, null, false, Holder("board", Rank.AssistantDirector));

        Assert.Equal(Rank.SectionChief, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task PromotionDecideAsync_SpecialAgentInChargeStopsAtTheCoarseGate()
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, Member("t", Rank.Trainee, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.Trainee);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.PromotionDecideAsync("req", true, null, false, Holder("sac", Rank.SpecialAgentInCharge)));

        Assert.Equal(PromotionStatus.Requested, RequestStatus(f));
    }

    [Fact]
    public async Task PromotionDecideAsync_ReadsTheActorFromTheRow()
    {
        using var f = Make();
        // the cookie says Board, the row says a Special Agent in Charge
        PersistHolder(f, "demoted", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, Member("t", Rank.SpecialAgent, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.SeniorSpecialAgent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.PromotionDecideAsync("req", true, null, false, Holder("demoted", Rank.Director)));

        Assert.Equal(PromotionStatus.Requested, RequestStatus(f));
    }

    // ---- every path that writes a rank leaves division and TRU flag consistent ----

    private static void AssertConsistent(Agent a)
    {
        Assert.NotNull(a.Rank);
        Assert.NotNull(a.Division);
        var division = a.Division.GetValueOrDefault();
        Assert.True(DivisionRules.MayHold(division, a.Rank));
        Assert.Equal(division == Division.Hrt && !DivisionRules.IsBoard(a.Rank), a.IsTRU);
    }

    [Theory]
    [InlineData(Rank.Trainee, Division.Cid)]
    [InlineData(Rank.SpecialAgent, Division.Hrt)]
    [InlineData(Rank.SpecialAgent, Division.Ctd)]
    [InlineData(Rank.Director, Division.Hrt)]
    public async Task ReleaseAsync_LeavesDivisionAndTruConsistent(Rank rank, Division division)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending));

        await f.Svc.ReleaseAsync("t", rank, division, isHRB: false, confirmOverLimit: false, Admin());

        var a = Reload(f, "t");
        AssertConsistent(a);
        Assert.Equal(division, a.Division);
    }

    [Theory]
    [InlineData(Rank.Trainee, Division.Cid)]
    [InlineData(Rank.SpecialAgent, Division.Hrt)]
    [InlineData(Rank.Director, Division.Hrt)]
    public async Task PromoteApplicantToAgentAsync_LeavesDivisionAndTruConsistent(Rank rank, Division division)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Applicant));

        await f.Svc.PromoteApplicantToAgentAsync("t", rank, division, false, confirmOverLimit: false, Admin());

        AssertConsistent(Reload(f, "t"));
    }

    [Fact]
    public async Task ConvertToInternalAsync_StartsInTheCid()
    {
        using var f = Make();
        PersistPartner(f.Ctx);

        await f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, false, Admin());

        var a = Reload(f, "t");
        AssertConsistent(a);
        Assert.Equal(Division.Cid, a.Division);
    }

    [Fact]
    public async Task ConvertToInternalAsync_OnlyReaderDenied()
    {
        using var f = Make();
        PersistPartner(f.Ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, false, OnlyReader()));
        Assert.NotNull(Reload(f, "t").PartnerAgency);
    }

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.Director)]
    public async Task RankChangeAsync_LeavesDivisionAndTruConsistent(Rank target)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }));

        await f.Svc.RankChangeAsync("t", target, false, Admin());

        AssertConsistent(Reload(f, "t"));
    }

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.Director)]
    public async Task PromotionDecideAsync_LeavesDivisionAndTruConsistent(Rank target)
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, rank: Rank.SeniorSpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }));
        using (var db = f.Ctx.NewContext())
        {
            db.AgentPromotionRequests.Add(new NOOSE_Website.Data.Entities.Personnel.AgentPromotionRequest
            {
                Id = "req-c", AgentId = "t", TargetRank = target, Status = PromotionStatus.Requested,
            });
            db.SaveChanges();
        }

        await f.Svc.PromotionDecideAsync("req-c", approved: true, note: null, false, Admin());

        var a = Reload(f, "t");
        AssertConsistent(a);
        Assert.Equal(target == Rank.Trainee ? Division.Cid : Division.Hrt, a.Division);
    }

    [Fact]
    public async Task ReleaseAsPartnerAsync_ClearsDivisionAndTru()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Pending, rank: Rank.SpecialAgent,
            cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }));

        await f.Svc.ReleaseAsPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Admin());

        var a = Reload(f, "t");
        Assert.Null(a.Division);
        Assert.False(a.IsTRU);
    }

    [Fact]
    public async Task ConvertToPartnerAsync_ClearsDivisionAndTru()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active, rank: Rank.SpecialAgent, cfg: a => { a.Division = Division.Hrt; a.IsTRU = true; }),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSMD, PartnerRank.Member, Admin("keepadmin"));

        var a = Reload(f, "t");
        Assert.Null(a.Division);
        Assert.False(a.IsTRU);
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
                Id = "req-down", AgentId = "t", TargetRank = Rank.Trainee, Status = PromotionStatus.Requested,
            });
            db.SaveChanges();
        }

        await f.Svc.PromotionDecideAsync("req-down", approved: true, note: null, false, Admin());

        var a = Reload(f, "t");
        Assert.Equal(Rank.Trainee, a.Rank);
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
        await f.Svc.ConvertToInternalAsync("partner", Rank.SpecialAgent, false, Admin());
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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

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
        Persist(f.Ctx, NewAgent("lead", AgentStatus.Active, Rank.SpecialAgentInCharge));

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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AvatarSetAsync("jr", Image(), contentType, 32, Junior("jr")));

        Assert.Null(Reload(f, "jr").PendingAvatarFileName);
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_WhenTooLarge()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));
        var tooBig = new FileUploadOptions().AvatarMaxBytes + 1;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Svc.AvatarSetAsync("jr", Image(), "image/png", tooBig, Junior("jr")));

        Assert.Null(Reload(f, "jr").PendingAvatarFileName);
    }

    [Fact]
    public async Task AvatarSetAsync_Throws_ForForeignAccount_EvenAsLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

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
        Persist(f.Ctx, NewAgent("pa", AgentStatus.Active, Rank.Trainee, a => a.PartnerAgency = PartnerAgency.LSPD));
        var partner = ClaimsPrincipalBuilder.Agent("pa").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.AvatarSetAsync("pa", Image(), "image/png", 32, partner));
    }

    [Fact]
    public async Task AvatarApproveAsync_MovesStagedToActive_AndNotifies()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));
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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.AvatarApproveAsync("jr", Leader()));
    }

    [Fact]
    public async Task AvatarApproveAsync_Throws_ForNonLeadership()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.AvatarApproveAsync("jr", Junior("other")));
    }

    [Fact]
    public async Task AvatarRejectAsync_DropsStaged_KeepsActive()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee,
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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.AvatarRejectAsync("jr", "nein", Junior("other")));
    }

    [Fact]
    public async Task AvatarRemoveAsync_OwnAccount_ClearsBothAndDeletesFiles()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee));
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
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee,
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
            NewAgent("a", AgentStatus.Active, Rank.Trainee),
            NewAgent("b", AgentStatus.Active, Rank.Trainee),
            NewAgent("c", AgentStatus.Active, Rank.Trainee,
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
            NewAgent("act", AgentStatus.Active, Rank.Trainee,
                a => { a.AvatarFileName = "aktiv.png"; a.AvatarContentType = "image/png"; }),
            NewAgent("jr", AgentStatus.Active, Rank.Trainee));
        await f.Svc.AvatarSetAsync("jr", Image(), "image/png", 32, Junior("jr"));
        var staged = Reload(f, "jr").PendingAvatarFileName!;

        Assert.Equal("act", (await f.Svc.FindByAvatarFileAsync("aktiv.png"))?.Id);
        Assert.Equal("jr", (await f.Svc.FindByAvatarFileAsync(staged))?.Id);
        Assert.Null(await f.Svc.FindByAvatarFileAsync("unbekannt.png"));
        Assert.Null(await f.Svc.FindByAvatarFileAsync(""));
    }

    // ---- turning an agent into a partner strips the rank: the fine gate ----

    [Theory]
    [InlineData(Rank.Director)]
    [InlineData(Rank.SpecialAgent)]
    public async Task ConvertToPartnerAsync_BoardMayConvertAnybody(Rank target)
    {
        using var f = Make();
        PersistHolder(f, "board", Rank.AssistantDirector);
        Persist(f.Ctx, Member("t", target, Division.Ctd));

        await f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Holder("board", Rank.AssistantDirector));

        Assert.Null(Reload(f, "t").Rank);
    }

    [Fact]
    public async Task ConvertToPartnerAsync_AdminMayConvertABoardMember()
    {
        using var f = Make();
        Persist(f.Ctx, Member("t", Rank.Director, Division.Cid));

        await f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Admin());

        Assert.Equal(PartnerAgency.LSPD, Reload(f, "t").PartnerAgency);
    }

    [Theory]
    [InlineData(Division.Cid, Rank.SpecialAgent, true)]
    [InlineData(Division.Ctd, Rank.SpecialAgent, false)]
    [InlineData(Division.Cid, Rank.SectionChief, false)]
    [InlineData(Division.Cid, Rank.Director, false)]
    public async Task ConvertToPartnerAsync_SectionChiefOnlyInTheOwnDivisionBelowSectionChief(Division home, Rank current, bool allowed)
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Cid);
        Persist(f.Ctx, Member("t", current, home));

        var call = () => f.Svc.ConvertToPartnerAsync("t", PartnerAgency.LSPD, PartnerRank.Member, Holder("sc", Rank.SectionChief));

        if (allowed)
        {
            await call();
            Assert.Null(Reload(f, "t").Rank);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(call);
            Assert.Equal(current, Reload(f, "t").Rank);
        }
    }

    [Fact]
    public async Task ConvertToPartnerAsync_SpecialAgentInChargeIsRefused()
    {
        using var f = Make();
        PersistHolder(f, "sac", Rank.SpecialAgentInCharge);
        Persist(f.Ctx, Member("t", Rank.Director, Division.Cid), Member("u", Rank.Trainee, Division.Cid));

        foreach (var id in new[] { "t", "u" })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Svc.ConvertToPartnerAsync(
                id, PartnerAgency.LSPD, PartnerRank.Member, Holder("sac", Rank.SpecialAgentInCharge)));
            Assert.NotNull(Reload(f, id).Rank);
        }
    }

    // ---- soft seat limits on the rank write paths ----

    private static Agent[] BoardSeats(int count)
        => Enumerable.Range(0, count)
            .Select(i => NewAgent($"b{i}", AgentStatus.Active, Rank.AssistantDirector, a => a.Division = Division.Cid))
            .ToArray();

    [Fact]
    public async Task RankChangeAsync_PromotionIntoAFullBoardNeedsConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, BoardSeats(DivisionDisplay.BoardLimit));
        Persist(f.Ctx, Member("t", Rank.SectionChief, Division.Cid));

        var ex = await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.RankChangeAsync("t", Rank.AssistantDirector, false, Admin()));
        Assert.Equal(Rank.SectionChief, Reload(f, "t").Rank);

        await f.Svc.RankChangeAsync("t", Rank.AssistantDirector, true, Admin());
        Assert.Equal(Rank.AssistantDirector, Reload(f, "t").Rank);
        Assert.Contains("4 von 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankChangeAsync_DemotionOutOfTheBoardIntoAFullHomeDivisionNeedsConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd), "ctd"));
        Persist(f.Ctx, Member("t", Rank.AssistantDirector, Division.Ctd));

        await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.RankChangeAsync("t", Rank.SpecialAgent, false, Admin()));
        Assert.Equal(Rank.AssistantDirector, Reload(f, "t").Rank);

        await f.Svc.RankChangeAsync("t", Rank.SpecialAgent, true, Admin());
        Assert.Equal(Rank.SpecialAgent, Reload(f, "t").Rank);
    }

    [Fact]
    public async Task RankChangeAsync_StayingInTheSameUnitNeedsNoSeat()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Ctd, DivisionDisplay.Limit(Division.Ctd) + 1, "ctd"));

        await f.Svc.RankChangeAsync("ctd0", Rank.SeniorSpecialAgent, false, Admin());

        Assert.Equal(Rank.SeniorSpecialAgent, Reload(f, "ctd0").Rank);
    }

    [Fact]
    public async Task PromotionDecideAsync_ApprovalIntoAFullBoardNeedsConfirmationButARejectionDoesNot()
    {
        using var f = Make();
        Persist(f.Ctx, BoardSeats(DivisionDisplay.BoardLimit));
        Persist(f.Ctx, Member("t", Rank.SectionChief, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.AssistantDirector);

        await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.PromotionDecideAsync("req", true, null, false, Admin()));
        Assert.Equal(PromotionStatus.Requested, RequestStatus(f));
        Assert.Equal(Rank.SectionChief, Reload(f, "t").Rank);

        await f.Svc.PromotionDecideAsync("req", false, null, false, Admin());
        Assert.Equal(PromotionStatus.Rejected, RequestStatus(f));
    }

    [Fact]
    public async Task PromotionDecideAsync_ApprovalIntoAFullBoardSucceedsWithConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, BoardSeats(DivisionDisplay.BoardLimit));
        Persist(f.Ctx, Member("t", Rank.SectionChief, Division.Cid));
        PersistRequest(f.Ctx, "t", Rank.AssistantDirector);

        await f.Svc.PromotionDecideAsync("req", true, null, true, Admin());

        Assert.Equal(Rank.AssistantDirector, Reload(f, "t").Rank);
        Assert.Equal(PromotionStatus.Approved, RequestStatus(f));
    }

    [Fact]
    public async Task ConvertToInternalAsync_FullDivisionNeedsConfirmation()
    {
        using var f = Make();
        Persist(f.Ctx, Seats(Division.Cid, DivisionDisplay.Limit(Division.Cid), "cid"));
        PersistPartner(f.Ctx);

        await Assert.ThrowsAsync<DivisionLimitExceededException>(
            () => f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, false, Admin()));
        Assert.NotNull(Reload(f, "t").PartnerAgency);

        await f.Svc.ConvertToInternalAsync("t", Rank.SpecialAgent, true, Admin());
        Assert.Equal(Division.Cid, Reload(f, "t").Division);
    }

    [Fact]
    public async Task ConvertToInternalAsync_SectionChiefStartsTheFormerPartnerInTheirDivision()
    {
        using var f = Make();
        PersistHolder(f, "sc", Rank.SectionChief, Division.Ctd);
        PersistPartner(f.Ctx);

        await f.Svc.ConvertToInternalAsync("t", Rank.Trainee, false, Holder("sc", Rank.SectionChief));

        Assert.Equal(Division.Ctd, Reload(f, "t").Division);
    }

    // ---- Discord role sync ----

    private static AgentJob RoleJob(string agentId) => new(agentId, DiscordRoleSyncReason.AgentChanged);

    [Fact]
    public async Task Rank_change_enqueues_the_agent()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.Trainee));

        await f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, Admin());

        Assert.Equal(RoleJob("t"), Assert.Single(f.RoleSync.Jobs));
    }

    [Fact]
    public async Task Terminate_enqueues_the_agent()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.SpecialAgent));

        await f.Svc.TerminateAsync("t", "weg", createNote: false, postDiscord: false, Admin());

        Assert.Equal(RoleJob("t"), Assert.Single(f.RoleSync.Jobs));
    }

    [Fact]
    public async Task Release_and_unblock_enqueue_the_agent()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("new", AgentStatus.Pending), NewAgent("bl", AgentStatus.Blocked, Rank.Trainee));

        await f.Svc.ReleaseAsync("new", Rank.SpecialAgent, Division.Hrt, isHRB: false, confirmOverLimit: false, Admin());
        await f.Svc.UnblockAsync("bl", Admin());

        Assert.Equal([RoleJob("new"), RoleJob("bl")], f.RoleSync.Jobs);
    }

    [Fact]
    public async Task Master_data_change_enqueues_the_agent()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.Trainee));

        await f.Svc.MasterDataChangeAsync("t", null, "Neu", "II", Admin());

        Assert.Equal(RoleJob("t"), Assert.Single(f.RoleSync.Jobs));
    }

    [Fact]
    public async Task Master_data_without_new_stamp_does_not_enqueue()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("jr", AgentStatus.Active, Rank.Trainee,
            a => { a.AvatarFileName = "alt.png"; a.AvatarContentType = "image/png"; }));

        await f.Svc.AvatarRemoveAsync("jr", Leader());

        Assert.Null(Reload(f, "jr").AvatarFileName);
        Assert.Empty(f.RoleSync.Jobs);
    }

    [Fact]
    public async Task A_refused_write_enqueues_nothing()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.Trainee));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Svc.RankChangeAsync("t", Rank.SeniorSpecialAgent, false, Junior()));

        Assert.Empty(f.RoleSync.Jobs);
    }

    [Fact]
    public async Task Discord_failure_never_fails_the_write()
    {
        using var f = Make();
        f.RoleSync.Fault = new InvalidOperationException("queue is broken");
        Persist(f.Ctx, NewAgent("t", AgentStatus.Active, Rank.SpecialAgent));

        await f.Svc.TerminateAsync("t", "weg", createNote: false, postDiscord: false, Admin());

        Assert.Equal(AgentStatus.Terminated, Reload(f, "t").Status);
    }

    [Fact]
    public async Task Delete_enqueues_discord_id_of_former_agent_only()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("former", AgentStatus.Active, Rank.SpecialAgent, a => a.DiscordId = "200000000000000001"),
            NewAgent("left", AgentStatus.Terminated, Rank.SpecialAgent, a => a.DiscordId = "200000000000000002"),
            NewAgent("pending", AgentStatus.Pending, cfg: a => a.DiscordId = "200000000000000003"),
            NewAgent("partner", AgentStatus.Active, Rank.SpecialAgent,
                a => { a.PartnerAgency = PartnerAgency.LSPD; a.DiscordId = "200000000000000004"; }),
            NewAgent("noid", AgentStatus.Active, Rank.SpecialAgent, a => a.DiscordId = ""),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        foreach (var id in new[] { "pending", "partner", "noid" })
        {
            await f.Svc.DeleteAccountAsync(id, Admin("keepadmin"));
        }
        Assert.Empty(f.RoleSync.Jobs);

        await f.Svc.DeleteAccountAsync("former", Admin("keepadmin"));
        await f.Svc.DeleteAccountAsync("left", Admin("keepadmin"));

        Assert.Equal(
        [
            new DeletedJob("200000000000000001", false, DiscordRoleSyncReason.AccountDeleted),
            new DeletedJob("200000000000000002", false, DiscordRoleSyncReason.AccountDeleted),
        ], f.RoleSync.Jobs);
    }

    [Fact]
    public async Task Deleting_a_team_lead_enqueues_the_team_lead_flag()
    {
        using var f = Make();
        Persist(f.Ctx,
            NewAgent("lead", AgentStatus.Active, Rank.SpecialAgent,
                a => { a.IsTeamLead = true; a.DiscordId = "200000000000000001"; }),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.DeleteAccountAsync("lead", Admin("keepadmin"));

        Assert.Equal(new DeletedJob("200000000000000001", TeamLead: true, DiscordRoleSyncReason.AccountDeleted),
            Assert.Single(f.RoleSync.Jobs));
    }

    [Fact]
    public async Task A_refused_delete_enqueues_nothing()
    {
        using var f = Make();
        Persist(f.Ctx, NewAgent("only", AgentStatus.Active, Rank.Director, a => a.IsAdmin = true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Svc.DeleteAccountAsync("only", Admin("other")));

        Assert.Empty(f.RoleSync.Jobs);
    }

    [Fact]
    public async Task Discord_failure_never_fails_the_delete()
    {
        using var f = Make();
        f.RoleSync.Fault = new InvalidOperationException("queue is broken");
        Persist(f.Ctx,
            NewAgent("t", AgentStatus.Active, Rank.SpecialAgent),
            NewAgent("keepadmin", AgentStatus.Active, cfg: a => a.IsAdmin = true));

        await f.Svc.DeleteAccountAsync("t", Admin("keepadmin"));

        using var db = f.Ctx.NewContext();
        Assert.False(db.Users.Any(u => u.Id == "t"));
    }
}
