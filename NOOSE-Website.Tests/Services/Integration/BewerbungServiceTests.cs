using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="BewerbungService"/> over in-memory SQLite.</summary>
public sealed class BewerbungServiceTests
{
    private const string CaseNo = "FIB-B-2026-0001";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (BewerbungService Svc, ISourcesStorageService Storage, IBewerbungssperreService Sperren,
        INotificationService Notifications, BewerbungBroadcaster Broadcaster, List<string> Reported) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(CaseNo);
        var storage = Substitute.For<ISourcesStorageService>();
        var sperren = Substitute.For<IBewerbungssperreService>();
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new BewerbungBroadcaster();
        var reported = new List<string>();
        broadcaster.Modified += id => reported.Add(id);
        var applicationCases = Substitute.For<IApplicationCaseService>();
        var logger = Substitute.For<ILogger<BewerbungService>>();
        var svc = new BewerbungService(ctx.Factory, caseNo, storage, broadcaster, sperren, notifications, Substitute.For<IApplicantDmNotifier>(),
            FakeDiscordMembership.Always(DiscordMembership.Unknown), Substitute.For<IDiscordDmService>(), applicationCases, logger);
        return (svc, storage, sperren, notifications, broadcaster, reported);
    }

    // HRB member (flag), junior rank => IsHrbOrLeadership() true, MayClassifiedRead() false.
    private static ClaimsPrincipal Hrb(string id = "hrb", string codename = "Falcon")
        => ClaimsPrincipalBuilder.Agent(id).AsHrb().WithRank(Rank.Trainee).WithCodename(codename).Build();

    // Rank >= SpecialAgentInCharge => leadership; MayClassifiedRead() true.
    private static ClaimsPrincipal Leader(string id = "lead", string codename = "Director")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename(codename).Build();

    // Junior active agent: not HRB, not leadership.
    private static ClaimsPrincipal Junior(string id = "me")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Leadership-by-rank team lead (not admin) = read-only supervision: passes the rank check, may not write.
    private static ClaimsPrincipal OnlyReader(string id = "reader")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    // Public applicant (status Applicant).
    private static ClaimsPrincipal Applicant(string id = "u1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Applicant).Build();

    // Partner account: status Active with an agency, so it may apply while keeping partner access.
    private static ClaimsPrincipal Partner(string id = "partner-1")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithStatus(AgentStatus.Active)
            .AsPartner(PartnerAgency.DoJ, PartnerRank.Member).Build();

    private static Bewerbung Bew(string id = "b1", string applicantUserId = "u1", string name = "Max Mustermann",
        BewerbungStatus status = BewerbungStatus.Eingereicht, Action<Bewerbung>? configure = null)
    {
        var b = new Bewerbung
        {
            Id = id,
            CaseNumber = "FIB-B-2026-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
            ApplicantUserId = applicantUserId,
            Name = name,
            Status = status,
            SubmittedAt = T0,
            CreatedAt = T0,
        };
        configure?.Invoke(b);
        return b;
    }

    private static BewerbungMessage Msg(string bewerbungId, BewerbungMessageAudience audience, string text,
        DateTime createdAt, bool fromApplicant = false)
        => new()
        {
            BewerbungId = bewerbungId,
            Audience = audience,
            Text = text,
            AuthorIsApplicant = fromApplicant,
            CreatedAt = createdAt,
        };

    private static BewerbungssperreInfo Ban(string agentId, bool blacklist, DateTime? until = null)
        => new("s1", agentId, null, "Max", null, blacklist, until, null, T0, null);

    // ---------- GetOwnAsync ----------

    [Fact]
    public async Task GetOwnAsync_ReturnsMostRecentForApplicant()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("old", "u1", configure: b => b.SubmittedAt = T0));
            db.Bewerbungen.Add(Bew("new", "u1", configure: b => b.SubmittedAt = T0.AddDays(5)));
            db.Bewerbungen.Add(Bew("other", "u2"));
            db.SaveChanges();
        }

        var own = await svc.GetOwnAsync(Applicant("u1"));

        Assert.NotNull(own);
        Assert.Equal("new", own!.Id);
    }

    [Fact]
    public async Task GetOwnAsync_NoUserId_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetOwnAsync(ClaimsPrincipalBuilder.Anonymous()));
    }

    // ---------- SubmitAsync ----------

    [Fact]
    public async Task SubmitAsync_Applicant_CreatesApplication()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "  Max Mustermann  ", CoverLetter = "Mein Anschreiben" };

        var created = await svc.SubmitAsync(model, null, null, null, Applicant("u1"));

        Assert.Equal(CaseNo, created.CaseNumber);
        Assert.Equal("Max Mustermann", created.Name);
        Assert.Equal("u1", created.ApplicantUserId);
        Assert.Equal(BewerbungStatus.Eingereicht, created.Status);

        using var db = ctx.NewContext();
        var stored = Assert.Single(db.Bewerbungen.ToList());
        Assert.Equal("Max Mustermann", stored.Name);
        Assert.Equal("Mein Anschreiben", stored.CoverLetter);
        Assert.True(stored.StreamConsent);
    }

    [Fact]
    public async Task SubmitAsync_StoresARefusedStream()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await svc.SubmitAsync(new BewerbungSubmitModel { StreamConsent = false, Name = "Max" }, null, null, null, Applicant("u1"));

        using var db = ctx.NewContext();
        Assert.False(Assert.Single(db.Bewerbungen.ToList()).StreamConsent);
    }

    [Fact]
    public async Task SubmitAsync_WithoutStreamAnswer_Throws()
    {
        // the page enforces it too, but the hub path skips the page
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAsync(new BewerbungSubmitModel { Name = "Max" }, null, null, null, Applicant("u1")));

        Assert.Contains("gestreamt", ex.Message);
        using var db = ctx.NewContext();
        Assert.Empty(db.Bewerbungen.ToList());
    }

    [Fact]
    public async Task SubmitAsync_Partner_CreatesApplication_AndLeavesTheAccountAlone()
    {
        // a partner applies without becoming an applicant: the row is the application, the account keeps its agency
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "Trevor Ward", CoverLetter = "Wechsel zum FIB" };

        var created = await svc.SubmitAsync(model, null, null, null, Partner("partner-1"));

        Assert.Equal("partner-1", created.ApplicantUserId);
        Assert.Equal(BewerbungStatus.Eingereicht, created.Status);
        using var db = ctx.NewContext();
        Assert.Single(db.Bewerbungen.ToList());
    }

    [Fact]
    public async Task SubmitAsync_NonApplicant_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "Max" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SubmitAsync(model, null, null, null, Junior("me")));

        using var db = ctx.NewContext();
        Assert.Empty(db.Bewerbungen.ToList());
    }

    [Fact]
    public async Task SubmitAsync_EmptyName_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "   " };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAsync(model, null, null, null, Applicant("u1")));
    }

    [Fact]
    public async Task SubmitAsync_ActiveApplicationExists_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Eingereicht));
            db.SaveChanges();
        }

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "Max" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAsync(model, null, null, null, Applicant("u1")));
    }

    [Fact]
    public async Task SubmitAsync_Blacklisted_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);
        sperren.GetActiveAsync("u1", Arg.Any<CancellationToken>()).Returns(Ban("u1", blacklist: true));

        var model = new BewerbungSubmitModel { StreamConsent = true, Name = "Max" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SubmitAsync(model, null, null, null, Applicant("u1")));

        using var db = ctx.NewContext();
        Assert.Empty(db.Bewerbungen.ToList());
    }

    // ---------- ListAsync ----------

    [Fact]
    public async Task ListAsync_Hrb_ReturnsNewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("old", "u1", configure: b => b.SubmittedAt = T0));
            db.Bewerbungen.Add(Bew("new", "u2", configure: b => b.SubmittedAt = T0.AddDays(3)));
            db.SaveChanges();
        }

        var list = await svc.ListAsync(Hrb());

        Assert.Equal(new[] { "new", "old" }, list.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task ListAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ListAsync(Junior("me")));
    }

    // ---------- GetForHrbAsync ----------

    [Fact]
    public async Task GetForHrbAsync_Hrb_ReturnsApplication()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        var found = await svc.GetForHrbAsync("b1", Leader());
        Assert.NotNull(found);
        Assert.Equal("b1", found!.Id);

        Assert.Null(await svc.GetForHrbAsync("missing", Leader()));
    }

    [Fact]
    public async Task GetForHrbAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetForHrbAsync("b1", Junior("me")));
    }

    // ---------- GetForFileAccessAsync ----------

    [Fact]
    public async Task GetForFileAccessAsync_Owner_Returns()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        var byOwner = await svc.GetForFileAccessAsync("b1", Applicant("u1"));
        Assert.NotNull(byOwner);

        var byHrb = await svc.GetForFileAccessAsync("b1", Hrb());
        Assert.NotNull(byHrb);
    }

    [Fact]
    public async Task GetForFileAccessAsync_Stranger_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        // A different active agent, not HRB/leadership, not the owner.
        Assert.Null(await svc.GetForFileAccessAsync("b1", Junior("someone-else")));
    }

    [Fact]
    public async Task GetForFileAccessAsync_Missing_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetForFileAccessAsync("missing", Hrb()));
    }

    // ---------- AssignSelfAsync ----------

    [Fact]
    public async Task AssignSelfAsync_Hrb_SetsHandler_AndReports()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        await svc.AssignSelfAsync("b1", Hrb("hrb", "Falcon"));

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.Equal("hrb", stored.AssignedAgentId);
        Assert.Equal("Falcon", stored.AssignedAgentName);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task AssignSelfAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AssignSelfAsync("b1", Junior("me")));
    }

    private static (BewerbungService Svc, IApplicationCaseService ApplicationCases) BuildWithProvisioning(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(CaseNo);
        var applicationCases = Substitute.For<IApplicationCaseService>();
        var svc = new BewerbungService(ctx.Factory, caseNo, Substitute.For<ISourcesStorageService>(),
            new BewerbungBroadcaster(), Substitute.For<IBewerbungssperreService>(),
            Substitute.For<INotificationService>(), Substitute.For<IApplicantDmNotifier>(),
            FakeDiscordMembership.Always(DiscordMembership.Unknown), Substitute.For<IDiscordDmService>(), applicationCases,
            Substitute.For<ILogger<BewerbungService>>());
        return (svc, applicationCases);
    }

    [Fact]
    public async Task AssignSelfAsync_Hrb_InvokesAutoProvisioning()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }
        var (svc, applicationCases) = BuildWithProvisioning(ctx);
        var hrb = Hrb("hrb", "Falcon");

        await svc.AssignSelfAsync("b1", hrb);

        await applicationCases.Received(1).EnsureSecurityCheckCaseAsync("b1", hrb, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignSelfAsync_ProvisioningThrows_AssignmentStillSucceeds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }
        var (svc, applicationCases) = BuildWithProvisioning(ctx);
        applicationCases
            .When(x => x.EnsureSecurityCheckCaseAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        await svc.AssignSelfAsync("b1", Hrb("hrb", "Falcon"));

        using var check = ctx.NewContext();
        Assert.Equal("hrb", check.Bewerbungen.Single(b => b.Id == "b1").AssignedAgentId);
    }

    // ---------- SetStatusAsync ----------

    [Fact]
    public async Task SetStatusAsync_ValidTransition_Updates_AndReports()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Eingereicht));
            db.SaveChanges();
        }

        await svc.SetStatusAsync("b1", BewerbungStatus.InSicherheitspruefung, null, Hrb());

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.Equal(BewerbungStatus.InSicherheitspruefung, stored.Status);
        // Non-terminal target does not stamp a decision.
        Assert.Null(stored.DecidedAt);
        Assert.Equal(new[] { "b1" }, reported);
        await sperren.DidNotReceive().BanAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_Reject_SetsDecision_AndBans()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Eingereicht));
            db.SaveChanges();
        }

        await svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, "Kein Fit", Hrb("hrb", "Falcon"));

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.Equal(BewerbungStatus.Abgelehnt, stored.Status);
        Assert.Equal("Falcon", stored.DecidedByName);
        Assert.NotNull(stored.DecidedAt);
        Assert.Equal("Kein Fit", stored.DecisionNote);
        await sperren.Received(1).BanAsync("u1", "b1", Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_Close_Bans_LikeARejection()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Eingereicht));
            db.SaveChanges();
        }

        await svc.SetStatusAsync("b1", BewerbungStatus.Geschlossen, "Doppelbewerbung", Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(BewerbungStatus.Geschlossen, check.Bewerbungen.Single(b => b.Id == "b1").Status);
        await sperren.Received(1).BanAsync("u1", "b1", Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_OnlyReader_Throws_BeforeDecidingOrBanning()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Eingereicht));
            db.SaveChanges();
        }

        // leadership by rank but read-only: used to reject fine and lose the ban inside a swallowed catch
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, null, OnlyReader()));

        using var check = ctx.NewContext();
        Assert.Equal(BewerbungStatus.Eingereicht, check.Bewerbungen.Single(b => b.Id == "b1").Status);
        await sperren.DidNotReceive().BanAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_TerminalSource_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Angenommen));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetStatusAsync("b1", BewerbungStatus.InSicherheitspruefung, null, Hrb()));
    }

    [Fact]
    public async Task SetStatusAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, null, Junior("me")));
    }

    // ---------- SetSecurityResultAsync ----------

    [Fact]
    public async Task SetSecurityResultAsync_Passed_AdvancesToTest()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.InSicherheitspruefung));
            db.SaveChanges();
        }

        await svc.SetSecurityResultAsync("b1", passed: true, Hrb());

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.True(stored.SecurityCheckPassed);
        Assert.Equal(BewerbungStatus.ImTest, stored.Status);
        Assert.Equal(new[] { "b1" }, reported);
        await sperren.DidNotReceive().BanAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSecurityResultAsync_Failed_Rejects_AndBans()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.InSicherheitspruefung));
            db.SaveChanges();
        }

        await svc.SetSecurityResultAsync("b1", passed: false, Hrb("hrb", "Falcon"));

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.False(stored.SecurityCheckPassed);
        Assert.Equal(BewerbungStatus.Abgelehnt, stored.Status);
        Assert.Equal("Falcon", stored.DecidedByName);
        Assert.NotNull(stored.DecidedAt);
        Assert.False(string.IsNullOrEmpty(stored.DecisionNote));
        await sperren.Received(1).BanAsync("u1", "b1", Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSecurityResultAsync_OnlyReader_Throws_BeforeRejectingOrBanning()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, sperren, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.InSicherheitspruefung));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetSecurityResultAsync("b1", passed: false, OnlyReader()));

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.Equal(BewerbungStatus.InSicherheitspruefung, stored.Status);
        Assert.Null(stored.SecurityCheckPassed);
        await sperren.DidNotReceive().BanAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSecurityResultAsync_Terminal_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", status: BewerbungStatus.Geschlossen));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetSecurityResultAsync("b1", passed: true, Hrb()));
    }

    [Fact]
    public async Task SetSecurityResultAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetSecurityResultAsync("b1", passed: true, Junior("me")));
    }

    // ---------- LinkPersonAsync ----------

    [Fact]
    public async Task LinkPersonAsync_ValidPerson_Links()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }

        await svc.LinkPersonAsync("b1", "p1", Hrb());

        using var check = ctx.NewContext();
        Assert.Equal("p1", check.Bewerbungen.Single(b => b.Id == "b1").LinkedPersonId);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task LinkPersonAsync_Null_Unlinks()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", configure: b => b.LinkedPersonId = "p1"));
            db.SaveChanges();
        }

        await svc.LinkPersonAsync("b1", null, Hrb());

        using var check = ctx.NewContext();
        Assert.Null(check.Bewerbungen.Single(b => b.Id == "b1").LinkedPersonId);
    }

    [Fact]
    public async Task LinkPersonAsync_UnknownPerson_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkPersonAsync("b1", "ghost", Hrb()));
    }

    [Fact]
    public async Task LinkPersonAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.LinkPersonAsync("b1", "p1", Junior("me")));
    }

    // ---------- GetLinkedPersonAsync ----------

    [Fact]
    public async Task GetLinkedPersonAsync_Leader_ReturnsInfo()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", configure: b => b.LinkedPersonId = "p1"));
            db.People.Add(Seed.Person(id: "p1", name: "Ziel", configure: p =>
            {
                p.CaseNumber = "FIB-P-2026-0042";
                p.ThreatScore = 77;
            }));
            db.SaveChanges();
        }

        var info = await svc.GetLinkedPersonAsync("b1", Leader());

        Assert.NotNull(info);
        Assert.Equal("p1", info!.PersonId);
        Assert.Equal("Ziel", info.Name);
        Assert.Equal("FIB-P-2026-0042", info.CaseNumber);
        Assert.Equal(77, info.ThreatScore);
    }

    [Fact]
    public async Task GetLinkedPersonAsync_ClassifiedHiddenFromHrb_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", configure: b => b.LinkedPersonId = "p1"));
            db.People.Add(Seed.Person(id: "p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }

        // HRB member without classified-read must not see a classified person.
        Assert.Null(await svc.GetLinkedPersonAsync("b1", Hrb()));
    }

    [Fact]
    public async Task GetLinkedPersonAsync_NoLink_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        Assert.Null(await svc.GetLinkedPersonAsync("b1", Leader()));
    }

    [Fact]
    public async Task GetLinkedPersonAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetLinkedPersonAsync("b1", Junior("me")));
    }

    // ---------- GetMessagesAsync ----------

    [Fact]
    public async Task GetMessagesAsync_Intern_Hrb_ReturnsInternOnly()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.BewerbungMessages.Add(Msg("b1", BewerbungMessageAudience.Intern, "erste", T0));
            db.BewerbungMessages.Add(Msg("b1", BewerbungMessageAudience.Intern, "zweite", T0.AddMinutes(5)));
            db.BewerbungMessages.Add(Msg("b1", BewerbungMessageAudience.Bewerber, "an bewerber", T0));
            db.SaveChanges();
        }

        var msgs = await svc.GetMessagesAsync("b1", BewerbungMessageAudience.Intern, Hrb());

        Assert.Equal(new[] { "erste", "zweite" }, msgs.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetMessagesAsync_Intern_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetMessagesAsync("b1", BewerbungMessageAudience.Intern, Junior("me")));
    }

    [Fact]
    public async Task GetMessagesAsync_Bewerber_Owner_Returns()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.BewerbungMessages.Add(Msg("b1", BewerbungMessageAudience.Bewerber, "hallo", T0));
            db.SaveChanges();
        }

        var msgs = await svc.GetMessagesAsync("b1", BewerbungMessageAudience.Bewerber, Applicant("u1"));

        Assert.Equal(new[] { "hallo" }, msgs.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetMessagesAsync_Bewerber_Owner_HidesAgentCodename()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            var fromAgent = Msg("b1", BewerbungMessageAudience.Bewerber, "hallo", T0);
            fromAgent.AuthorName = "Falcon";
            db.BewerbungMessages.Add(fromAgent);
            db.SaveChanges();
        }

        var forApplicant = await svc.GetMessagesAsync("b1", BewerbungMessageAudience.Bewerber, Applicant("u1"));
        Assert.Null(Assert.Single(forApplicant).AuthorName);

        // HRB keeps the sender for internal accountability
        var forHrb = await svc.GetMessagesAsync("b1", BewerbungMessageAudience.Bewerber, Hrb());
        Assert.Equal("Falcon", Assert.Single(forHrb).AuthorName);
    }

    [Fact]
    public async Task GetMessagesAsync_Bewerber_Stranger_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        // A different applicant (not the owner, not HRB) may not read the conversation.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetMessagesAsync("b1", BewerbungMessageAudience.Bewerber, Applicant("u2")));
    }

    // ---------- PostInternalAsync ----------

    [Fact]
    public async Task PostInternalAsync_Hrb_CreatesInternMessage_AndReports()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        var msg = await svc.PostInternalAsync("b1", "  interne Notiz  ", Hrb("hrb", "Falcon"));

        Assert.Equal("interne Notiz", msg.Text);
        Assert.Equal(BewerbungMessageAudience.Intern, msg.Audience);
        Assert.Equal("Falcon", msg.AuthorName);

        using var check = ctx.NewContext();
        var stored = Assert.Single(check.BewerbungMessages.Where(m => m.BewerbungId == "b1").ToList());
        Assert.Equal(BewerbungMessageAudience.Intern, stored.Audience);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task PostInternalAsync_EmptyText_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostInternalAsync("b1", "   ", Hrb()));
    }

    [Fact]
    public async Task PostInternalAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PostInternalAsync("b1", "text", Junior("me")));
    }

    // ---------- EditInternalMessageAsync ----------

    [Fact]
    public async Task EditInternalMessageAsync_OwnMessage_Updates_AndReports()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        // CreatedById mirrors what the (absent) audit interceptor would stamp.
        var msg = Msg("b1", BewerbungMessageAudience.Intern, "alter Text", T0);
        msg.Id = "m1";
        msg.CreatedById = "hrb";
        using (var db = ctx.NewContext())
        {
            db.BewerbungMessages.Add(msg);
            db.SaveChanges();
        }

        var edited = await svc.EditInternalMessageAsync("m1", "  neuer Text  ", Hrb("hrb"));

        Assert.Equal("neuer Text", edited.Text);
        using var check = ctx.NewContext();
        Assert.Equal("neuer Text", check.BewerbungMessages.Single(m => m.Id == "m1").Text);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task EditInternalMessageAsync_OtherAuthor_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var msg = Msg("b1", BewerbungMessageAudience.Intern, "alter Text", T0);
        msg.Id = "m1";
        msg.CreatedById = "other";
        using (var db = ctx.NewContext())
        {
            db.BewerbungMessages.Add(msg);
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EditInternalMessageAsync("m1", "text", Hrb("hrb")));
    }

    [Fact]
    public async Task EditInternalMessageAsync_BewerberAudience_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        var msg = Msg("b1", BewerbungMessageAudience.Bewerber, "alter Text", T0);
        msg.Id = "m1";
        msg.CreatedById = "hrb";
        using (var db = ctx.NewContext())
        {
            db.BewerbungMessages.Add(msg);
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EditInternalMessageAsync("m1", "text", Hrb("hrb")));
    }

    [Fact]
    public async Task EditInternalMessageAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.EditInternalMessageAsync("m1", "text", Junior("me")));
    }

    [Fact]
    public async Task EditInternalMessageAsync_Empty_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EditInternalMessageAsync("m1", "   ", Hrb()));
    }

    // ---------- PostToApplicantAsync ----------

    [Fact]
    public async Task PostToApplicantAsync_Hrb_CreatesBewerberMessage()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", name: "Max Mustermann"));
            db.SaveChanges();
        }

        var msg = await svc.PostToApplicantAsync("b1", "<p>Willkommen beim FIB</p>", Hrb("hrb", "Falcon"));

        Assert.Equal(BewerbungMessageAudience.Bewerber, msg.Audience);
        Assert.False(msg.AuthorIsApplicant);
        Assert.Equal("Falcon", msg.AuthorName);
        Assert.Contains("Willkommen", msg.Text);

        using var check = ctx.NewContext();
        var stored = Assert.Single(check.BewerbungMessages.Where(m => m.BewerbungId == "b1").ToList());
        Assert.Equal(BewerbungMessageAudience.Bewerber, stored.Audience);
        Assert.False(stored.AuthorIsApplicant);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task PostToApplicantAsync_EmptyText_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostToApplicantAsync("b1", "  ", Hrb()));
    }

    [Fact]
    public async Task PostToApplicantAsync_NonHrb_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PostToApplicantAsync("b1", "text", Junior("me")));
    }

    // ---------- PostAsApplicantAsync ----------

    [Fact]
    public async Task PostAsApplicantAsync_Owner_CreatesMessage()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, reported) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1", configure: b => b.AssignedAgentId = "hrb"));
            db.SaveChanges();
        }

        var msg = await svc.PostAsApplicantAsync("b1", "<p>Danke für die Rückmeldung</p>", Applicant("u1"));

        Assert.Equal(BewerbungMessageAudience.Bewerber, msg.Audience);
        Assert.True(msg.AuthorIsApplicant);
        Assert.Contains("Danke", msg.Text);

        using var check = ctx.NewContext();
        var stored = Assert.Single(check.BewerbungMessages.Where(m => m.BewerbungId == "b1").ToList());
        Assert.True(stored.AuthorIsApplicant);
        Assert.Equal(new[] { "b1" }, reported);
    }

    [Fact]
    public async Task PostAsApplicantAsync_NotOwner_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(Bew("b1", "u1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PostAsApplicantAsync("b1", "hallo", Applicant("u2")));
    }

    [Fact]
    public async Task PostAsApplicantAsync_EmptyText_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostAsApplicantAsync("b1", "   ", Applicant("u1")));
    }

    [Fact]
    public async Task PostAsApplicantAsync_NonApplicant_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PostAsApplicantAsync("b1", "hallo", Junior("me")));
    }
}
