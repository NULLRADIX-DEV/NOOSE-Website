using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Calendar feed: secret handling, who may hold one, what leaves the site and the ICS shape.</summary>
public sealed class CalendarFeedTests : IDisposable
{
    private const string Site = "https://fibhq.com";

    private readonly SqliteTestContext _ctx = new();
    private readonly CalendarFeedService _service;

    public CalendarFeedTests()
    {
        var discord = Substitute.For<IDiscordWebhookService>();
        discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(false, Site,
            new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(), false));

        // stands in for the login factory: claims straight from the stored row
        var principals = Substitute.For<IUserClaimsPrincipalFactory<Agent>>();
        principals.CreateAsync(Arg.Any<Agent>()).Returns(call => PrincipalOf(call.Arg<Agent>()));

        _service = new CalendarFeedService(_ctx.Factory, new CalendarService(_ctx.Factory), principals, discord,
            NullLogger<CalendarFeedService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    private static ClaimsPrincipal PrincipalOf(Agent a)
    {
        var b = ClaimsPrincipalBuilder.Agent(a.Id).WithStatus(a.Status).WithCodename(a.Codename ?? a.Id);
        if (a.Rank is { } rank)
        {
            b.WithRank(rank);
        }
        if (a.IsTeamLead)
        {
            b.AsTeamLead();
        }
        if (a.PartnerAgency is { } agency)
        {
            b.AsPartner(agency, a.PartnerRank ?? PartnerRank.Member);
        }
        return b.Build();
    }

    private Agent SeedAgent(string id, Action<Agent>? configure = null)
    {
        var agent = Seed.Agent(id, Rank.SpecialAgent, configure: configure);
        using var db = _ctx.NewContext();
        db.Users.Add(agent);
        db.SaveChanges();
        return agent;
    }

    private void SeedAppointment(string id, string title, DateTime startUtc)
    {
        using var db = _ctx.NewContext();
        db.Appointments.Add(new Appointment
        {
            Id = id,
            CaseNumber = "FIB-TM-" + id,
            Title = title,
            Start = startUtc,
            Visibility = AppointmentVisibilityLevel.Public,
            Status = AppointmentStatus.Planned,
        });
        db.SaveChanges();
    }

    private static string TokenOf(CalendarFeedLink link)
    {
        var segment = link.Url[(link.Url.LastIndexOf('/') + 1)..];
        return CalendarFeedRules.TokenFromSegment(segment)!;
    }

    // ---------------------------------------------------------------- secret

    [Fact]
    public void Token_HasFixedShape_AndOnlyItsHashMatches()
    {
        var token = CalendarFeedToken.Generate();

        Assert.Equal(CalendarFeedToken.TextLength, token.Length);
        Assert.True(CalendarFeedToken.HasValidShape(token));
        var hash = CalendarFeedToken.Hash(token);
        Assert.Equal(64, hash.Length);
        Assert.True(CalendarFeedToken.Matches(hash, token));
        Assert.False(CalendarFeedToken.Matches(hash, CalendarFeedToken.Generate()));
        Assert.False(CalendarFeedToken.Matches(null, token));
        Assert.NotEqual(token, CalendarFeedToken.Generate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kurz")]
    [InlineData("../../etc/passwd")]
    public void Segment_WithoutSecretShape_YieldsNoToken(string? segment)
        => Assert.Null(CalendarFeedRules.TokenFromSegment(segment));

    [Fact]
    public void Segment_ToleratesIcsSuffix()
    {
        var token = CalendarFeedToken.Generate();
        Assert.Equal(token, CalendarFeedRules.TokenFromSegment(token + ".ics"));
        Assert.Equal(token, CalendarFeedRules.TokenFromSegment(token));
    }

    // ---------------------------------------------------------------- holder

    [Fact]
    public async Task Create_StoresOnlyTheHash_AndReturnsLinkOnConfiguredSite()
    {
        SeedAgent("me");
        var link = await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent)));

        Assert.StartsWith(Site + CalendarFeedRules.SiteRoute + "/", link.Url);
        Assert.EndsWith(".ics", link.Url);
        var token = TokenOf(link);
        await using var db = _ctx.NewContext();
        var row = await db.Users.SingleAsync(u => u.Id == "me");
        Assert.Equal(CalendarFeedToken.Hash(token), row.CalendarFeedTokenHash);
        Assert.DoesNotContain(token, row.CalendarFeedTokenHash!);
        Assert.NotNull(row.CalendarFeedCreatedAt);
        Assert.DoesNotContain(token, link.ToString());
    }

    [Fact]
    public async Task Create_AuditsTheState_NeverTheSecret()
    {
        SeedAgent("me");
        var token = TokenOf(await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent))));

        await using var db = _ctx.NewContext();
        var audit = await db.AuditLogs.ToListAsync();
        Assert.Single(audit);
        var all = string.Join("|", audit.Select(a => a.ChangesJson));
        Assert.Contains("angelegt", all);
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(CalendarFeedToken.Hash(token), all);
    }

    [Fact]
    public async Task Partner_TeamLead_AndReadOnly_GetNoFeed()
    {
        var partner = ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();
        var lead = ClaimsPrincipalBuilder.Agent("t").WithRank(Rank.SpecialAgent).AsTeamLead().Build();
        var applicant = ClaimsPrincipalBuilder.Agent("a").WithStatus(AgentStatus.Applicant).Build();

        foreach (var actor in new[] { partner, lead, applicant })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateAsync(actor));
        }
    }

    [Fact]
    public async Task Create_ChecksTheStoredRow_NotOnlyTheClaims()
    {
        // claims still say active, the row is already terminated
        SeedAgent("me", a => a.Status = AgentStatus.Terminated);
        var stale = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.SpecialAgent).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateAsync(stale));
    }

    // ---------------------------------------------------------------- fetch

    [Fact]
    public async Task Render_ServesTheAgentsCalendar()
    {
        SeedAgent("me");
        SeedAppointment("t1", "Lagebesprechung Hafen", DateTime.UtcNow.AddDays(2));
        var token = TokenOf(await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent))));

        var body = await _service.RenderAsync(token);

        Assert.NotNull(body);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", body);
        Assert.Contains("SUMMARY:Lagebesprechung Hafen", body);
        Assert.Contains("URL:" + Site + "/kalender/", body);
        Assert.EndsWith("END:VCALENDAR\r\n", body);
    }

    [Fact]
    public async Task Render_WithNeutralTitles_HidesTitleAndLink()
    {
        var me = PrincipalOf(Seed.Agent("me", Rank.SpecialAgent));
        SeedAgent("me");
        SeedAppointment("t1", "Zugriff bei Max Mustermann", DateTime.UtcNow.AddDays(2));
        var token = TokenOf(await _service.CreateAsync(me));
        await _service.SetNeutralTitlesAsync(true, me);

        var body = await _service.RenderAsync(token);

        Assert.NotNull(body);
        Assert.DoesNotContain("Mustermann", body);
        Assert.Contains("SUMMARY:Termin", body);
        Assert.DoesNotContain("URL:", body);
    }

    [Fact]
    public async Task Render_RotatedOrRevokedLink_IsDead()
    {
        var me = PrincipalOf(Seed.Agent("me", Rank.SpecialAgent));
        SeedAgent("me");
        var first = TokenOf(await _service.CreateAsync(me));
        var second = TokenOf(await _service.CreateAsync(me));

        Assert.Null(await _service.RenderAsync(first));
        Assert.NotNull(await _service.RenderAsync(second));

        await _service.RevokeAsync(me);
        Assert.Null(await _service.RenderAsync(second));
        Assert.False((await _service.GetStatusAsync(me)).Active);
    }

    [Fact]
    public async Task Render_DiesWithTheAccount()
    {
        SeedAgent("me");
        var token = TokenOf(await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent))));

        await using (var db = _ctx.NewContext())
        {
            await db.Users.Where(u => u.Id == "me")
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AgentStatus.Terminated));
        }

        Assert.Null(await _service.RenderAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nicht-das-geheimnis")]
    public async Task Render_UnknownSecret_IsNull(string? token)
        => Assert.Null(await _service.RenderAsync(token));

    [Fact]
    public async Task Render_UnknownWellFormedSecret_IsNull()
    {
        SeedAgent("me");
        await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent)));

        Assert.Null(await _service.RenderAsync(CalendarFeedToken.Generate()));
    }

    [Fact]
    public async Task Render_LogsAccess_AtMostOncePerHalfHour()
    {
        SeedAgent("me");
        var token = TokenOf(await _service.CreateAsync(PrincipalOf(Seed.Agent("me", Rank.SpecialAgent))));

        await _service.RenderAsync(token);
        await _service.RenderAsync(token);

        await using var db = _ctx.NewContext();
        var rows = await db.AccessLogs.Where(l => l.EntityType == CalendarFeedService.AccessType).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("me", row.AgentId);
        Assert.DoesNotContain(token, row.EntityId);
    }

    // ---------------------------------------------------------------- projection

    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<IcsEvent> Project(bool neutral, params CalendarEntry[] entries)
        => CalendarFeedRules.Project(entries, neutral, Site, "fibhq.com", TimeZoneInfo.Utc, Now);

    private static CalendarEntry Entry(string id, CalendarSource source, string title = "Titel",
        bool obsolete = false, string? href = "/irgendwo")
        => new(id, title, new DateTime(2026, 11, 10, 18, 0, 0), new DateTime(2026, 11, 10, 19, 0, 0), false,
            source, href, obsolete);

    [Theory]
    [InlineData(CalendarSource.Observation)]
    [InlineData(CalendarSource.PersonDoc)]
    [InlineData(CalendarSource.FactionActivity)]
    [InlineData(CalendarSource.TeamAbsence)]
    public void Project_LeavesOutWhatNamesTargetsOrOthers(CalendarSource source)
        => Assert.Empty(Project(false, Entry("x", source)));

    [Fact]
    public void Project_DropsObsolete_AndDuplicates()
    {
        var events = Project(false,
            Entry("a", CalendarSource.Appointment),
            Entry("a", CalendarSource.Appointment),
            Entry("b", CalendarSource.Meeting, obsolete: true));

        Assert.Equal("a@fibhq.com", Assert.Single(events).Uid);
    }

    [Theory]
    [InlineData(CalendarSource.Followup, "Wiedervorlage")]
    [InlineData(CalendarSource.Operation, "Operation")]
    public void Project_FollowupsAndOperations_AreAlwaysNeutral(CalendarSource source, string label)
    {
        var e = Assert.Single(Project(false, Entry("x", source, "Observation Max Mustermann")));

        Assert.Equal(label, e.Summary);
        Assert.Null(e.Url);
    }

    [Fact]
    public void Project_ReadableTitle_StripsMentions_AndLinksTheRecord()
    {
        var e = Assert.Single(Project(false, Entry("x", CalendarSource.Meeting, "Briefing", href: "/besprechungen/1")));

        Assert.Equal("Briefing", e.Summary);
        Assert.Equal(Site + "/besprechungen/1", e.Url);
    }

    [Fact]
    public void Project_OnlySiteRelativeLinks()
    {
        var e = Assert.Single(Project(false, Entry("x", CalendarSource.Appointment, href: "https://evil.example/x")));
        Assert.Null(e.Url);
    }

    [Fact]
    public void ToUtc_ConvertsBerlinWallClock()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

        Assert.Equal(new DateTime(2026, 11, 10, 17, 0, 0, DateTimeKind.Utc),
            CalendarFeedRules.ToUtc(new DateTime(2026, 11, 10, 18, 0, 0), berlin));
        // skipped hour of the spring change moves on instead of throwing
        var skipped = CalendarFeedRules.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0), berlin);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), skipped);
    }

    // ---------------------------------------------------------------- ics

    [Fact]
    public void Ics_EscapesText()
        => Assert.Equal("a\\, b\\; c\\\\ d\\ne", IcsWriter.EscapeText("a, b; c\\ d\r\ne"));

    [Fact]
    public void Ics_DropsControlCharacters()
        => Assert.Equal("ab", IcsWriter.EscapeText("a\u0007b"));

    [Fact]
    public void Ics_FoldsLongLines_AtSeventyFiveOctets_WithoutSplittingCharacters()
    {
        var sb = new StringBuilder();
        IcsWriter.Line(sb, "SUMMARY:" + new string('ä', 100));

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));
        var unfolded = string.Concat(lines.Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + new string('ä', 100), unfolded);
    }

    [Fact]
    public void Ics_AllDay_HasExclusiveEnd()
    {
        var body = IcsWriter.Serialize("-//T//T//DE", "T", [
            new IcsEvent("u@h", Now, "Abgemeldet", new DateTime(2026, 11, 10), null, true),
        ]);

        Assert.Contains("DTSTART;VALUE=DATE:20261110\r\n", body);
        Assert.Contains("DTEND;VALUE=DATE:20261111\r\n", body);
    }

    [Fact]
    public void Ics_Timed_UsesUtc()
    {
        var body = IcsWriter.Serialize("-//T//T//DE", "T", [
            new IcsEvent("u@h", Now, "Termin", new DateTime(2026, 11, 10, 17, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc), false, "javascript:alert(1)"),
        ]);

        Assert.Contains("DTSTART:20261110T170000Z\r\n", body);
        Assert.Contains("DTEND:20261110T180000Z\r\n", body);
        Assert.DoesNotContain("URL:", body);
    }
}
