using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>End-to-end: a rejection, a closure and a failed security check must each leave a 14-day ban in the table.</summary>
/// <remarks>
/// The sibling BewerbungServiceTests mock IBewerbungssperreService and so only prove that BanAsync is called —
/// never how long the applicant ends up banned. Here the real service runs on the same context, which makes the
/// stored GesperrtBis the assertion.
/// </remarks>
public sealed class BewerbungRejectionBanTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BewerbungService Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-B-2026-0002");
        return new BewerbungService(
            ctx.Factory,
            caseNo,
            Substitute.For<ISourcesStorageService>(),
            new BewerbungBroadcaster(),
            new BewerbungssperreService(ctx.Factory), // the real one: that is the whole point of this file
            Substitute.For<INotificationService>(),
            Substitute.For<IApplicantDmNotifier>(),
            Substitute.For<IApplicationCaseService>(),
            Substitute.For<ILogger<BewerbungService>>());
    }

    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").AsHrb().WithRank(Rank.Trainee).WithCodename("Falcon").Build();

    // Leadership by rank but read-only supervision: passes the rank check, may not write.
    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("reader").WithRank(Rank.Director).AsTeamLead().Build();

    // Public demo visitor; the synthetic principal carries HRB and Director.
    private static ClaimsPrincipal Demo()
        => ClaimsPrincipalBuilder.Agent("demo").AsHrb().WithRank(Rank.Director).AsDemo().Build();

    private static void SeedApplication(SqliteTestContext ctx, BewerbungStatus status)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("u1", status: AgentStatus.Applicant));
        db.Bewerbungen.Add(new Bewerbung
        {
            Id = "b1",
            CaseNumber = "FIB-B-2026-0001",
            ApplicantUserId = "u1",
            Name = "Max Mustermann",
            Status = status,
            SubmittedAt = T0,
            CreatedAt = T0,
        });
        db.SaveChanges();
    }

    /// <summary>Assert the one row that must exist is a running temporary ban of exactly 14 days.</summary>
    private static Bewerbungssperre AssertFourteenDayBan(SqliteTestContext ctx)
    {
        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList());
        Assert.Equal("u1", row.AgentId);
        Assert.Equal("b1", row.BewerbungId);
        Assert.Equal("discord-u1", row.DiscordId); // the ban hangs off the Discord account, not the name
        Assert.False(row.IsBlacklist);
        Assert.NotNull(row.BannedUntil);
        // 14 days written out: reading BanDuration here would let a wrong constant pass
        Assert.Equal(DateTime.UtcNow.AddDays(14), row.BannedUntil!.Value, TimeSpan.FromMinutes(1));
        return row;
    }

    [Fact]
    public async Task Reject_ImposesFourteenDayBan()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);

        await Build(ctx).SetStatusAsync("b1", BewerbungStatus.Abgelehnt, "Kein Fit", Hrb());

        var row = AssertFourteenDayBan(ctx);
        Assert.Equal("Kein Fit", row.Reason);
        Assert.Equal("Falcon", row.CreatedByName);
    }

    [Fact]
    public async Task Close_ImposesFourteenDayBan()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);

        await Build(ctx).SetStatusAsync("b1", BewerbungStatus.Geschlossen, null, Hrb());

        AssertFourteenDayBan(ctx);
    }

    [Fact]
    public async Task FailedSecurityCheck_RejectsAndImposesFourteenDayBan()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.InSicherheitspruefung);

        await Build(ctx).SetSecurityResultAsync("b1", passed: false, Hrb());

        using (var check = ctx.NewContext())
        {
            var stored = check.Bewerbungen.Single(b => b.Id == "b1");
            Assert.Equal(BewerbungStatus.Abgelehnt, stored.Status);
            Assert.False(stored.SecurityCheckPassed);
        }
        var row = AssertFourteenDayBan(ctx);
        Assert.Equal("Sicherheitsüberprüfung nicht bestanden.", row.Reason);
    }

    [Fact]
    public async Task PassedSecurityCheck_ImposesNoBan()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.InSicherheitspruefung);

        await Build(ctx).SetSecurityResultAsync("b1", passed: true, Hrb());

        using var check = ctx.NewContext();
        Assert.Equal(BewerbungStatus.ImTest, check.Bewerbungen.Single(b => b.Id == "b1").Status);
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Fact]
    public async Task RejectingAgainAfterAnAcceptance_BansForFourteenDaysOnceMore()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        var svc = Build(ctx);

        await svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, "erste", Hrb());
        // Abgelehnt -> Angenommen -> Abgelehnt is the only route back into a decision, and it must re-ban
        await svc.SetStatusAsync("b1", BewerbungStatus.Angenommen, null, Hrb());
        await svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, "zweite", Hrb());

        var row = AssertFourteenDayBan(ctx);
        Assert.Equal("zweite", row.Reason);
    }

    [Fact]
    public async Task RejectingWhileABanRuns_RefreshesTheSameRowBackToFourteenDays()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        // a ban with one day left, as an earlier application would have left behind
        using (var db = ctx.NewContext())
        {
            db.Bewerbungssperren.Add(new Bewerbungssperre
            {
                Id = "existing",
                AgentId = "u1",
                BewerbungId = "b1",
                DiscordId = "discord-u1",
                IsBlacklist = false,
                BannedUntil = DateTime.UtcNow.AddDays(1),
                Reason = "alt",
                CreatedAt = T0,
            });
            db.SaveChanges();
        }

        await Build(ctx).SetStatusAsync("b1", BewerbungStatus.Abgelehnt, "neu", Hrb());

        var row = AssertFourteenDayBan(ctx);
        Assert.Equal("existing", row.Id); // refreshed, not stacked
        Assert.Equal("neu", row.Reason);
    }

    [Fact]
    public async Task Accepting_LiftsTheTemporaryBan()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        var svc = Build(ctx);

        await svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, null, Hrb());
        await svc.SetStatusAsync("b1", BewerbungStatus.Angenommen, null, Hrb());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> LiftAsync hard-deletes
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Fact]
    public async Task Accepting_KeepsAnActiveBlacklist()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        using (var db = ctx.NewContext())
        {
            db.Bewerbungssperren.Add(new Bewerbungssperre
            {
                AgentId = "u1",
                IsBlacklist = true,
                Reason = "Kündigung",
                CreatedAt = T0,
            });
            db.SaveChanges();
        }

        await Build(ctx).SetStatusAsync("b1", BewerbungStatus.Angenommen, null, Hrb());

        using var check = ctx.NewContext();
        Assert.True(Assert.Single(check.Bewerbungssperren.ToList()).IsBlacklist);
    }

    [Theory]
    [InlineData(false)] // read-only supervision
    [InlineData(true)]  // demo visitor
    public async Task ReadOnlyActor_CannotReject_AndLeavesNoUnbannedRejection(bool demo)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        var actor = demo ? Demo() : OnlyReader();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).SetStatusAsync("b1", BewerbungStatus.Abgelehnt, null, actor));

        using var check = ctx.NewContext();
        Assert.Equal(BewerbungStatus.ImVorstellungsgespraech, check.Bewerbungen.Single(b => b.Id == "b1").Status);
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyActor_CannotFailTheSecurityCheck(bool demo)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.InSicherheitspruefung);
        var actor = demo ? Demo() : OnlyReader();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).SetSecurityResultAsync("b1", passed: false, actor));

        using var check = ctx.NewContext();
        var stored = check.Bewerbungen.Single(b => b.Id == "b1");
        Assert.Equal(BewerbungStatus.InSicherheitspruefung, stored.Status);
        Assert.Null(stored.SecurityCheckPassed);
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Fact]
    public async Task BannedApplicant_CannotReapplyBeforeTheBanRunsOut()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImVorstellungsgespraech);
        var svc = Build(ctx);
        await svc.SetStatusAsync("b1", BewerbungStatus.Abgelehnt, null, Hrb());

        var applicant = ClaimsPrincipalBuilder.Agent("u1").WithStatus(AgentStatus.Applicant).Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitAsync(
            new BewerbungSubmitModel { StreamConsent = true, Name = "Max Mustermann" }, null, null, null, applicant));

        Assert.Contains(DateTime.UtcNow.AddDays(14).ToLocalTime().ToString("dd.MM.yyyy"), ex.Message);
    }
}
