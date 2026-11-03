using System.Security.Claims;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="CalendarService" /> against in-memory SQLite.</summary>
public sealed class CalendarServiceTests
{
    // wide UTC window; individual records sit near the middle
    private static readonly DateTime WindowStart = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mid = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Outside = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CalendarService Build(SqliteTestContext ctx) => new(ctx.Factory);

    // rank >= SpecialAgentInCharge => leadership => MayClassifiedRead true
    private static ClaimsPrincipal PrivilegedViewer(string id = "me") =>
        ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Trainee, no admin/teamlead => MayClassifiedRead false
    private static ClaimsPrincipal PlainViewer(string id = "me") =>
        ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static Appointment NewAppointment(string id, DateTime start,
        AppointmentVisibilityLevel visibility = AppointmentVisibilityLevel.Public, string? createdBy = null,
        AppointmentStatus status = AppointmentStatus.Planned) => new()
    {
        Id = id,
        CaseNumber = "FIB-TM-" + id,
        Title = "Termin-" + id,
        Start = start,
        Visibility = visibility,
        CreatedById = createdBy,
        Status = status,
    };

    private static Operation NewOperation(string id, DateTime start, bool classified = false) => new()
    {
        Id = id,
        CaseNumber = "FIB-OP-" + id,
        Title = "Op-" + id,
        Start = start,
        IsClassified = classified,
        Status = OperationStatus.Planned,
    };

    private static Meeting NewMeeting(string id, DateTime start, MeetingStatus status = MeetingStatus.Planned) => new()
    {
        Id = id,
        CaseNumber = "FIB-BS-" + id,
        Title = "Besprechung-" + id,
        Start = start,
        Status = status,
    };

    private static Job NewJob(string id, DateTime? due, string? createdBy = null) => new()
    {
        Id = id,
        CaseNumber = "FIB-A-" + id,
        Title = "Aufgabe-" + id,
        DueDate = due,
        CreatedById = createdBy,
        Status = JobStatus.Open,
    };

    // ---------------------------------------------------------------- my mode

    [Fact]
    public async Task GetEntriesAsync_MyMode_ReturnsEmpty_WithoutAgentContext()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(NewAppointment("t1", Mid));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, ClaimsPrincipalBuilder.Anonymous(), CalendarMode.My);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_IncludesOwnAppointment_AndExcludesOutsideWindow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("other"));
            db.Appointments.Add(NewAppointment("inside", Mid, createdBy: "me"));
            db.Appointments.Add(NewAppointment("later", Outside, createdBy: "me"));
            db.Appointments.Add(NewAppointment("foreign", Mid, createdBy: "other"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.My);

        var appt = Assert.Single(entries, e => e.Source == CalendarSource.Appointment);
        Assert.Equal("tm:inside", appt.Id);
        Assert.Equal("/kalender/inside", appt.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_IncludesCreatedAndAssignedJobsWithDueDate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("other"));
            db.Jobs.Add(NewJob("mine", Mid, createdBy: "me"));
            db.Jobs.Add(NewJob("assigned", Mid, createdBy: "other"));
            db.Jobs.Add(NewJob("nodue", null, createdBy: "me"));
            db.Jobs.Add(NewJob("elsewhere", Outside, createdBy: "me"));
            db.JobAssignments.Add(new JobAssignment { JobId = "assigned", AgentId = "me" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.My);

        var jobs = entries.Where(e => e.Source == CalendarSource.Job).Select(e => e.Id).ToHashSet();
        Assert.Equal(2, jobs.Count);
        Assert.Contains("auf:mine", jobs);
        Assert.Contains("auf:assigned", jobs);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_IncludesOpenFollowup_WithResolvedParentName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(new Followup
            {
                Id = "w1",
                EntityType = nameof(Person),
                EntityId = "p1",
                ResponsibleAgentId = "me",
                DueAt = Mid,
                Note = "Rückruf",
                Done = false,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.My);

        var wv = Assert.Single(entries, e => e.Source == CalendarSource.Followup);
        Assert.Equal("wv:w1", wv.Id);
        Assert.StartsWith("Wiedervorlage: Max Mustermann", wv.Title);
        Assert.Contains("Rückruf", wv.Title);
        Assert.NotNull(wv.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_Followup_HidesNameAndHref_WhenParentUnresolvable()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Followups.Add(new Followup
            {
                Id = "w1",
                EntityType = nameof(Person),
                EntityId = "ghost",
                CreatedById = "me",
                DueAt = Mid,
                Done = false,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.My);

        var wv = Assert.Single(entries, e => e.Source == CalendarSource.Followup);
        Assert.Equal("Wiedervorlage fällig", wv.Title);
        Assert.Null(wv.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_DoneFollowup_IsExcluded()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Followups.Add(new Followup
            {
                Id = "done",
                EntityType = nameof(Person),
                EntityId = "p1",
                ResponsibleAgentId = "me",
                DueAt = Mid,
                Done = true,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.My);

        Assert.DoesNotContain(entries, e => e.Source == CalendarSource.Followup);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_Meeting_ExcusedBySignOff_IsMarkedObsolete()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Meetings.Add(NewMeeting("excused", Mid));
            db.Meetings.Add(NewMeeting("normal", Mid));
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "excused", AgentId = "me", Reason = "Urlaub" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.My);

        var byId = entries.Where(e => e.Source == CalendarSource.Meeting).ToDictionary(e => e.Id);
        Assert.True(byId["bes:excused"].Obsolete);
        Assert.False(byId["bes:normal"].Obsolete);
    }

    [Fact]
    public async Task GetEntriesAsync_MyMode_IncludesOwnAbsence_AsWholeDay()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("other"));
            db.Absences.Add(new Absence
            {
                Id = "ab1",
                AgentId = "me",
                FromDate = new DateOnly(2026, 6, 10),
                ToDate = new DateOnly(2026, 6, 12),
                Category = AbsenceCategory.Vacation,
            });
            db.Absences.Add(new Absence
            {
                Id = "foreign",
                AgentId = "other",
                FromDate = new DateOnly(2026, 6, 10),
                ToDate = new DateOnly(2026, 6, 12),
                Category = AbsenceCategory.Sick,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.My);

        var abs = Assert.Single(entries, e => e.Source == CalendarSource.Absence);
        Assert.Equal("abm:ab1", abs.Id);
        Assert.True(abs.WholeDay);
        Assert.Equal("Abgemeldet: Urlaub", abs.Title);

        // foreign absences belong to the authority calendar, not to mine
        Assert.DoesNotContain(entries, e => e.Source == CalendarSource.TeamAbsence);
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_TeamAbsence_CarriesCodenameAndNeverTheReason()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("other"));
            db.Absences.Add(new Absence
            {
                Id = "foreign",
                AgentId = "other",
                FromDate = new DateOnly(2026, 6, 10),
                ToDate = new DateOnly(2026, 6, 12),
                Category = AbsenceCategory.Sick,
                Reason = "streng geheim",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);

        var team = Assert.Single(entries, e => e.Source == CalendarSource.TeamAbsence);
        Assert.Equal("Codename-other: Krank", team.Title);
        Assert.DoesNotContain("geheim", team.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(team.WholeDay);
        Assert.Equal("/abmeldungen", team.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_TeamAbsence_ExcludesTeamLeadBlockedAndPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("blocked", status: AgentStatus.Blocked));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            foreach (var agentId in new[] { "tl", "blocked", "partner" })
            {
                db.Absences.Add(new Absence
                {
                    Id = "ab-" + agentId,
                    AgentId = agentId,
                    FromDate = new DateOnly(2026, 6, 10),
                    ToDate = new DateOnly(2026, 6, 12),
                    Category = AbsenceCategory.Vacation,
                });
            }
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);

        Assert.DoesNotContain(entries, e => e.Source == CalendarSource.TeamAbsence);
    }

    // --------------------------------------------------------- authority mode

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_ShowsPublicAppointmentButNotPrivate_ForPlainViewer()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(NewAppointment("pub", Mid, AppointmentVisibilityLevel.Public));
            db.Appointments.Add(NewAppointment("priv", Mid, AppointmentVisibilityLevel.Private));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var plain = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);
        var privileged = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.Authority);

        var plainAppt = Assert.Single(plain, e => e.Source == CalendarSource.Appointment);
        Assert.Equal("tm:pub", plainAppt.Id);
        Assert.Equal(2, privileged.Count(e => e.Source == CalendarSource.Appointment));
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_Operation_RespectsClassification()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(NewOperation("open", Mid, classified: false));
            db.Operations.Add(NewOperation("secret", Mid, classified: true));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var plain = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);
        var privileged = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.Authority);

        var openOp = Assert.Single(plain, e => e.Source == CalendarSource.Operation);
        Assert.Equal("op:open", openOp.Id);
        Assert.Equal(2, privileged.Count(e => e.Source == CalendarSource.Operation));
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_IncludesObservation_WithLocationTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(new Observation { Id = "o1", PersonId = "p1", Start = Mid, Location = "Bank" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);

        var ob = Assert.Single(entries, e => e.Source == CalendarSource.Observation);
        Assert.Equal("ob:o1", ob.Id);
        Assert.Equal("Observation – Bank", ob.Title);
        Assert.Equal("/personen/p1", ob.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_IncludesPersonDoc_WithReasonInTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", "John Doe"));
            db.PersonDocs.Add(new PersonDoc { Id = "d1", PersonId = "p2", Timestamp = Mid, Reason = "Verhör" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);

        var doc = Assert.Single(entries, e => e.Source == CalendarSource.PersonDoc);
        Assert.Equal("dok:d1", doc.Id);
        Assert.Equal("Dok: John Doe – Verhör", doc.Title);
        Assert.Equal("/personen/p2?tab=doks", doc.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_IncludesFactionActivity()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1"));
            db.AgentActivities.Add(new AgentActivity { Id = "act1", Title = "Razzia", Kind = "Einsatz", ActivityDate = Mid });
            db.AgentActivityLinks.Add(new AgentActivityLink { Id = "l1", AgentActivityId = "act1", TargetType = nameof(Faction), TargetId = "f1" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PlainViewer(), CalendarMode.Authority);

        var fa = Assert.Single(entries, e => e.Source == CalendarSource.FactionActivity);
        Assert.Equal("fa:act1:f1", fa.Id);
        Assert.Equal("Razzia (Einsatz)", fa.Title);
        Assert.Equal("/fraktionen/f1", fa.Href);
    }

    [Fact]
    public async Task GetEntriesAsync_AuthorityMode_IncludesMeetingAndForeignAbsences_ButNeverMyOwn()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Users.Add(Seed.Agent("other"));
            db.Meetings.Add(NewMeeting("m1", Mid));
            db.Absences.Add(new Absence
            {
                Id = "mine",
                AgentId = "me",
                FromDate = new DateOnly(2026, 6, 10),
                ToDate = new DateOnly(2026, 6, 12),
                Category = AbsenceCategory.Vacation,
            });
            db.Absences.Add(new Absence
            {
                Id = "foreign",
                AgentId = "other",
                FromDate = new DateOnly(2026, 6, 10),
                ToDate = new DateOnly(2026, 6, 12),
                Category = AbsenceCategory.Sick,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var entries = await svc.GetEntriesAsync(WindowStart, WindowEnd, PrivilegedViewer(), CalendarMode.Authority);

        Assert.Contains(entries, e => e.Source == CalendarSource.Meeting && e.Id == "bes:m1");

        // only the other agents' absences; my own stay in my calendar
        var team = Assert.Single(entries, e => e.Source == CalendarSource.TeamAbsence);
        Assert.Equal("tabm:foreign", team.Id);
        Assert.DoesNotContain(entries, e => e.Source == CalendarSource.Absence);
    }
}
