using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The record kinds NOOSEI gained access to, each checked against the helper that owns it.</summary>
/// <remarks>Two of these — the restricted job and the non-public appointment — were existence checks in the
/// central gate while their real rules sat in JobVisibility and AppointmentVisibility. That stayed harmless only
/// as long as neither was readable, so these are the tests that prove the repair.</remarks>
public sealed class NooseiOpenedTypesTests
{
    private const string Stranger = "stranger";
    private const string Owner = "owner";

    private static ClaimsPrincipal Junior(string id = Stranger)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.Trainee).AsHrb().Build();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ReadRecordTool Tool(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IAccessLogService>());

    private static async Task<(string Forbidden, string Allowed)> ReadBothAsync(
        SqliteTestContext ctx, string type, string id, ClaimsPrincipal denied, ClaimsPrincipal permitted)
    {
        var tool = Tool(ctx);
        var forbidden = await tool.InvokeAsync(
            Args($$"""{"typ":"{{type}}","id":"{{id}}"}"""), NooseiToolContext.From(denied));
        var allowed = await tool.InvokeAsync(
            Args($$"""{"typ":"{{type}}","id":"{{id}}"}"""), NooseiToolContext.From(permitted));
        return (forbidden.Text, allowed.Text);
    }

    [Fact]
    public async Task ARestrictedJob_StaysHiddenFromAnAgentWhoIsNeitherCreatorNorAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job
            {
                Id = "j1", CaseNumber = "FIB-A-2026-0001", Title = "Waffenlager prüfen",
                IsRestricted = true, CreatedById = Owner,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        var (forbidden, allowed) = await ReadBothAsync(ctx, "Aufgabe", "j1", Junior(), Leader());

        Assert.DoesNotContain("Waffenlager", forbidden);
        Assert.Contains("Waffenlager", allowed);
    }

    [Fact]
    public async Task ARestrictedJob_IsVisibleToTheAgentItIsAssignedTo()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job
            {
                Id = "j1", CaseNumber = "FIB-A-2026-0001", Title = "Waffenlager prüfen",
                IsRestricted = true, CreatedById = Owner,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.JobAssignments.Add(new JobAssignment { Id = "z1", JobId = "j1", AgentId = Stranger });
            db.SaveChanges();
        }

        var result = await Tool(ctx).InvokeAsync(
            Args("""{"typ":"Aufgabe","id":"j1"}"""), NooseiToolContext.From(Junior()));

        Assert.Contains("Waffenlager", result.Text);
    }

    [Fact]
    public async Task ANonPublicAppointment_StaysHiddenFromAnUninvolvedAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(new Appointment
            {
                Id = "t1", CaseNumber = "FIB-T-2026-0001", Title = "Treffen mit Quelle",
                Visibility = AppointmentVisibilityLevel.Private, CreatedById = Owner,
                Start = new DateTime(2026, 2, 1, 20, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        var (forbidden, allowed) = await ReadBothAsync(ctx, "Termin", "t1", Junior(), Leader());

        Assert.DoesNotContain("Treffen mit Quelle", forbidden);
        Assert.Contains("Treffen mit Quelle", allowed);
    }

    [Fact]
    public async Task AnInformant_IsReadableByEveryInternalAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Informants.Add(new Informant
            {
                Id = "i1", CaseNumber = "FIB-VP-2026-0001", RealName = "Klara Klarname",
                HandlerId = Owner, Reliability = InformantReliability.B, Status = InformantStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        var tool = Tool(ctx);
        var stranger = await tool.InvokeAsync(
            Args("""{"typ":"Informant","id":"i1"}"""), NooseiToolContext.From(Junior()));
        var handler = await tool.InvokeAsync(
            Args("""{"typ":"Informant","id":"i1"}"""), NooseiToolContext.From(Junior(Owner)));

        // record access implies full detail: no second tier hides the real name from a non-handler
        Assert.Contains("Klara Klarname", stranger.Text);
        Assert.Contains("Klara Klarname", handler.Text);
    }

    [Fact]
    public async Task AnApplication_StaysHiddenFromAnAgentWithoutRecruitingAccess()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Bewerbungen.Add(new Bewerbung
            {
                Id = "b1", CaseNumber = "FIB-B-2026-0001", Name = "Bea Bewerberin",
                ApplicantUserId = "applicant", Status = BewerbungStatus.ImTest,
                SubmittedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        var (forbidden, allowed) = await ReadBothAsync(ctx, "Bewerbung", "b1", Junior(), Hrb());

        Assert.DoesNotContain("Bea Bewerberin", forbidden);
        Assert.Contains("Bea Bewerberin", allowed);
        // the status is the question the file is most often opened for
        Assert.Contains("Status: Test", allowed);
    }

    [Fact]
    public async Task AnAnnouncement_StaysHiddenFromAnAgentOutsideItsAudience()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(new Announcement
            {
                Id = "a1", CaseNumber = "FIB-AK-2026-0001", Title = "Lagebesprechung der Leitung",
                Content = "<p>Nur für die Führung.</p>",
                Audience = AnnouncementAudience.FromRank, MinRank = Rank.DeputyDirector,
                CreatedById = Owner,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        var (forbidden, allowed) = await ReadBothAsync(ctx, "Ankündigung", "a1", Junior(), Leader());

        Assert.DoesNotContain("Lagebesprechung", forbidden);
        Assert.Contains("Lagebesprechung", allowed);
    }

    [Fact]
    public async Task AnAbsence_ShowsWhoIsAwayToPeersButTheReasonOnlyToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: Owner, configure: a => a.Codename = "Falke"));
            db.Absences.Add(new Absence
            {
                Id = "ab1", AgentId = Owner,
                FromDate = new DateOnly(2026, 3, 1), ToDate = new DateOnly(2026, 3, 5), Days = 5,
                Category = AbsenceCategory.Vacation, Reason = "Familiäre Angelegenheit",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var tool = Tool(ctx);

        var peer = await tool.InvokeAsync(
            Args("""{"typ":"Abmeldung","id":"ab1"}"""), NooseiToolContext.From(Junior()));
        var leader = await tool.InvokeAsync(
            Args("""{"typ":"Abmeldung","id":"ab1"}"""), NooseiToolContext.From(Leader()));

        // the roster tier is "who is away", never "why"
        Assert.Contains("Falke", peer.Text);
        Assert.DoesNotContain("Familiäre Angelegenheit", peer.Text);
        Assert.Contains("Familiäre Angelegenheit", leader.Text);
    }

    [Fact]
    public async Task AnAgentFile_NeverShowsTheRealNameToAViewerWhoMayNotReadIt()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: Owner, configure: a =>
            {
                a.Codename = "Falke";
                a.RealName = "Fabian Falkenstein";
            }));
            db.SaveChanges();
        }
        var tool = Tool(ctx);

        // read-only supervision reads everything classified but never a real name
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();
        var watched = await tool.InvokeAsync(
            Args($$"""{"typ":"Personalakte","id":"{{Owner}}"}"""), NooseiToolContext.From(supervision));
        var leader = await tool.InvokeAsync(
            Args($$"""{"typ":"Personalakte","id":"{{Owner}}"}"""), NooseiToolContext.From(Leader()));

        Assert.Contains("Falke", watched.Text);
        Assert.DoesNotContain("Fabian Falkenstein", watched.Text);
        Assert.Contains("Fabian Falkenstein", leader.Text);
    }

    [Fact]
    public async Task AnAgentFile_StaysHiddenFromAnOrdinaryAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: Owner, configure: a => a.Codename = "Falke"));
            db.SaveChanges();
        }

        var (forbidden, allowed) = await ReadBothAsync(ctx, "Personalakte", Owner, Junior(), Leader());

        Assert.DoesNotContain("Falke", forbidden);
        Assert.Contains("Falke", allowed);
    }
}
