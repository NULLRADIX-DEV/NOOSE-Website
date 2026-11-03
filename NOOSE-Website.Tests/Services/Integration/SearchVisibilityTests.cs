using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>What the search must NOT return. One case per category whose gate is easy to get wrong.</summary>
/// <remarks>Asserts against the whole result, not one group: a leak that shows up as a hit in another category, or
/// only inside a snippet, is still a leak.</remarks>
public class SearchVisibilityTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static ClaimsPrincipal Plain(string me = "agent-1")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leadership(string me = "lead")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Supervision(string me = "ro")
        => ClaimsPrincipalBuilder.Agent(me).WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal PartnerViewer(string me = "partner")
        => ClaimsPrincipalBuilder.Agent(me).AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();

    private static SearchCriteria Text(string text) => new() { Text = text };

    /// <summary>Every string the result carries, so a leak cannot hide in a snippet or a case number.</summary>
    private static IEnumerable<string> AllText(SearchResults results)
        => results.Groups.SelectMany(g => g.Hit).SelectMany(h => new[] { h.Title, h.Snippet, h.CaseNumber, h.Actor ?? "" });

    // ---- Document: three layers, not just the secrecy level ----

    private static Document Doc(string id, string title, Action<Document>? configure = null)
    {
        var d = new Document { Id = id, Title = title, ContentHtml = "<p>Nadel im Heuhaufen</p>" };
        configure?.Invoke(d);
        return d;
    }

    [Fact]
    public async Task A_taskforce_internal_document_is_not_findable_by_a_non_member()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("open", "Nadel offen"));
            db.Documents.Add(Doc("tf", "Nadel intern", d => d.OwnerTaskforceId = "t1"));
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t1", AgentId = "member" });
            await db.SaveChangesAsync();
        }

        var outsider = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Plain("outsider"));
        var member = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Plain("member"));

        Assert.DoesNotContain("Nadel intern", AllText(outsider));
        Assert.Contains("Nadel offen", AllText(outsider));
        Assert.Contains("Nadel intern", AllText(member));
    }

    [Fact]
    public async Task A_document_an_agent_was_excluded_from_is_not_findable_by_them()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", "Nadel entzogen"));
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { DocumentId = "d1", AgentId = "agent-1" });
            await db.SaveChangesAsync();
        }

        var excluded = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Plain("agent-1"));
        var other = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Plain("agent-2"));

        Assert.DoesNotContain("Nadel entzogen", AllText(excluded));
        Assert.Contains("Nadel entzogen", AllText(other));
    }

    [Fact]
    public async Task A_classified_document_is_not_findable_by_a_plain_agent()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("vs", "Nadel geheim", d => d.IsClassified = true));
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Plain());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text("Nadel"), Leadership());

        Assert.DoesNotContain("Nadel geheim", AllText(plain));
        Assert.Contains("Nadel geheim", AllText(lead));
    }

    // ---- Meeting minutes: agenda-grade, behind the time gate ----

    private static Meeting MeetingAt(string id, string title, DateTime start, string minutes)
        => new()
        {
            Id = id, Title = title, CaseNumber = "FIB-B-2026-" + id, Start = start,
            MinutesHtml = $"<p>{minutes}</p>",
        };

    [Fact]
    public async Task A_match_only_in_the_minutes_stays_hidden_until_the_agenda_opens()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            // ended 30 minutes ago: inside the 2h grace window
            db.Meetings.Add(MeetingAt("fresh", "Lagebesprechung A", Now.AddMinutes(-30), "Codewort Zitrone"));
            // 5 hours ago: open to every internal agent
            db.Meetings.Add(MeetingAt("old", "Lagebesprechung B", Now.AddHours(-5), "Codewort Zitrone"));
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain());

        var titles = plain.Groups.SelectMany(g => g.Hit).Select(h => h.Title).ToList();
        Assert.Contains("Lagebesprechung B", titles);
        Assert.DoesNotContain("Lagebesprechung A", titles);
        Assert.DoesNotContain("Codewort Zitrone", AllText(plain).Where(t => t.Contains("Zitrone")).Skip(1));
    }

    [Fact]
    public async Task Rank_reads_the_minutes_of_a_meeting_that_has_not_opened_yet()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Meetings.Add(MeetingAt("future", "Lagebesprechung C", Now.AddHours(3), "Codewort Zitrone"));
            await db.SaveChangesAsync();
        }

        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Leadership());

        Assert.Contains("Lagebesprechung C", lead.Groups.SelectMany(g => g.Hit).Select(h => h.Title));
    }

    // ---- personnel: leadership only, and the real name is a separate right ----

    [Fact]
    public async Task Personnel_files_and_notes_are_absent_for_a_plain_agent()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => { a.Codename = "Zitrone"; a.RealName = "Johnny Cash"; }));
            db.AgentNotes.Add(new AgentNote { Id = "n1", AgentId = "a1", Text = "<p>Zitrone vermerkt</p>", EntryDate = Now });
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain());

        // the category is not merely empty for them — it is not one of their categories at all
        Assert.DoesNotContain(nameof(Agent), plain.Groups.Select(g => g.Category));
        Assert.DoesNotContain(nameof(AgentNote), plain.Groups.Select(g => g.Category));
        Assert.DoesNotContain("Zitrone vermerkt", AllText(plain));
    }

    [Theory]
    [InlineData(true, AgentStatus.Active, false)]     // team lead: read-only supervision, invisible RP-wide
    [InlineData(false, AgentStatus.Blocked, false)]   // blocked account: hidden on /personal too
    [InlineData(false, AgentStatus.Applicant, false)] // an applicant is not an agent; recruiting owns them
    [InlineData(false, AgentStatus.Active, true)]
    [InlineData(false, AgentStatus.Terminated, true)] // a terminated agent keeps their file
    public async Task The_personnel_search_offers_exactly_the_accounts_the_roster_page_shows(
        bool teamLead, AgentStatus status, bool expected)
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", status: status, configure: a =>
            {
                a.Codename = "Zitrone";
                a.IsTeamLead = teamLead;
            }));
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Leadership());

        // the search must not be wider than /personal: raw db.Users would hand out all three hidden cases
        Assert.Equal(expected, AllText(results).Any(t => t.Contains("Zitrone", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_note_on_a_team_leads_file_is_not_findable_either()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl", configure: a => { a.Codename = "Aufsicht"; a.IsTeamLead = true; }));
            db.AgentNotes.Add(new AgentNote { Id = "n1", AgentId = "tl", Text = "<p>Zitrone vermerkt</p>", EntryDate = Now });
            await db.SaveChangesAsync();
        }

        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Leadership());

        // the note would name the team lead in its title, which is the thing that must stay invisible
        Assert.DoesNotContain(AllText(results), t => t.Contains("Aufsicht", StringComparison.Ordinal));
        Assert.DoesNotContain(AllText(results), t => t.Contains("Zitrone vermerkt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_read_only_supervision_never_sees_a_real_name()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => { a.Codename = "Zitrone"; a.RealName = "Johnny Cash"; }));
            await db.SaveChangesAsync();
        }

        // supervision reads everything, including classified — but never a Klarname
        var results = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Supervision());

        Assert.Contains("Zitrone", AllText(results));
        Assert.DoesNotContain(AllText(results), t => t.Contains("Johnny Cash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_real_name_is_not_a_match_field_for_a_viewer_who_may_not_read_it()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => { a.Codename = "Zitrone"; a.RealName = "Johnny Cash"; }));
            await db.SaveChangesAsync();
        }

        // a hit on the real name would confirm it to someone not allowed to see it
        var supervision = await SearchTestHost.NewService(ctx).SearchAsync(Text("Johnny"), Supervision());
        var leadership = await SearchTestHost.NewService(ctx).SearchAsync(Text("Johnny"), Leadership());

        Assert.DoesNotContain(nameof(Agent), supervision.Groups.Select(g => g.Category));
        Assert.Contains(nameof(Agent), leadership.Groups.Select(g => g.Category));
    }

    // ---- informants: every internal agent, never a partner ----

    [Fact]
    public async Task An_informant_is_findable_by_any_internal_agent_but_never_by_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Informants.Add(new Informant
            {
                Id = "i1", CaseNumber = "FIB-VP-2026-0001", RealName = "Zitrone Mueller", HandlerId = "handler",
            });
            db.InformantMeetings.Add(new InformantMeeting
            {
                Id = "m1", InformantId = "i1", MeetingDate = Now, Content = "<p>Zitrone berichtete</p>",
            });
            await db.SaveChangesAsync();
        }

        var other = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain("someone-else"));
        var partner = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), PartnerViewer());

        Assert.Contains("Zitrone Mueller", AllText(other));
        Assert.Contains(AllText(other), t => t.Contains("Zitrone berichtete", StringComparison.Ordinal));
        Assert.DoesNotContain("Zitrone Mueller", AllText(partner));
        Assert.DoesNotContain(AllText(partner), t => t.Contains("Zitrone berichtete", StringComparison.Ordinal));
    }

    // ---- taskforce chat: members only ----

    [Fact]
    public async Task Taskforce_chat_is_not_findable_by_a_non_member()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "t1", Name = "Einheit Nord", CaseNumber = "FIB-TF-2026-0001" });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t1", AgentId = "member" });
            db.TaskforceMessages.Add(new TaskforceMessage
            {
                Id = "msg1", TaskforceId = "t1", Text = "Zitrone bestätigt", AuthorName = "Falke",
            });
            await db.SaveChangesAsync();
        }

        var outsider = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain("outsider"));
        var member = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain("member"));

        Assert.DoesNotContain("Zitrone bestätigt", AllText(outsider));
        Assert.Contains("Zitrone bestätigt", AllText(member));
    }

    // ---- observations inherit their person's secrecy ----

    [Fact]
    public async Task An_observation_on_a_classified_person_is_not_findable_by_a_plain_agent()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("secret", "Max Geheim", p => p.IsClassified = true));
            db.Observations.Add(new Observation
            {
                Id = "o1", PersonId = "secret", Start = Now, Sighting = "Zitrone gesichtet",
            });
            await db.SaveChangesAsync();
        }

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Leadership());

        Assert.DoesNotContain(AllText(plain), t => t.Contains("Zitrone gesichtet", StringComparison.Ordinal));
        Assert.DoesNotContain("Max Geheim", AllText(plain));
        Assert.Contains("Max Geheim", AllText(lead));
    }

    // ---- absences: who is away, not why ----

    [Fact]
    public async Task A_peers_absence_reason_is_neither_matched_nor_shown()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("peer", configure: a => a.Codename = "Falke"));
            db.Users.Add(Seed.Agent("agent-1", configure: a => a.Codename = "Adler"));
            db.Absences.Add(new Absence
            {
                Id = "ab1", AgentId = "peer", Reason = "Zitrone privat",
                FromDate = DateOnly.FromDateTime(Now), ToDate = DateOnly.FromDateTime(Now.AddDays(2)),
            });
            await db.SaveChangesAsync();
        }

        var peerView = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Plain("agent-1"));
        var leadView = await SearchTestHost.NewService(ctx).SearchAsync(Text("Zitrone"), Leadership());

        Assert.DoesNotContain(AllText(peerView), t => t.Contains("Zitrone privat", StringComparison.Ordinal));
        Assert.Contains(AllText(leadView), t => t.Contains("Zitrone privat", StringComparison.Ordinal));
    }

    // ---- the category set a viewer is told about ----

    [Fact]
    public async Task A_category_a_viewer_may_not_search_is_absent_rather_than_empty()
    {
        using var ctx = new SqliteTestContext();

        var plain = await SearchTestHost.NewService(ctx).SearchAsync(Text("egal"), Plain());
        var lead = await SearchTestHost.NewService(ctx).SearchAsync(Text("egal"), Leadership());

        // "you may search here and nothing matched" and "this category is not yours" must not look the same
        Assert.True(plain.VisibleCategories < lead.VisibleCategories);
        Assert.DoesNotContain(nameof(Agent), plain.Searched);
        Assert.Contains(nameof(Agent), lead.Searched);
    }
}
