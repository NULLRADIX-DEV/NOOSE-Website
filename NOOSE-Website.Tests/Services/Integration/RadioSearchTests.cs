using System.Security.Claims;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The reverse lookup: an overheard frequency has to reach the record through the ordinary search.</summary>
/// <remarks>
/// Deliberately without <c>MaxMode</c> everywhere. The whole point of the change is that the frequency is found
/// without the Deep-Scan checkbox, and a test that ticked it would pass over the regression it exists to catch.
/// </remarks>
public sealed class RadioSearchTests
{
    private static SearchService Svc(SqliteTestContext ctx) => SearchTestHost.NewService(ctx);

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithStatus(AgentStatus.Active).Build();

    private static ClaimsPrincipal Plain(string id = "agent")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithStatus(AgentStatus.Active).Build();

    private static SearchCriteria Query(string text) => new() { Text = text };

    private static IReadOnlyList<SearchHit> Of(SearchResults results, string category)
        => results.Groups.FirstOrDefault(g => g.Category == category)?.Hit ?? [];

    // ==================== faction frequencies ====================

    [Fact]
    public async Task A_dot_stored_faction_frequency_is_found_by_the_comma_spelling()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.Radio = "411.7"));
            db.SaveChanges();
        }

        var hits = Of(await Svc(ctx).SearchAsync(Query("411,7"), Leader()), nameof(Faction));

        Assert.Contains(hits, h => h.TargetId == "f1");
    }

    [Fact]
    public async Task A_comma_stored_faction_frequency_is_found_by_either_spelling()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // nothing normalises Faction.Radio on write, so this is what a German keyboard actually leaves behind
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.Radio = "411,7"));
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.Contains(Of(await svc.SearchAsync(Query("411,7"), Leader()), nameof(Faction)), h => h.TargetId == "f1");
        Assert.Contains(Of(await svc.SearchAsync(Query("411.7"), Leader()), nameof(Faction)), h => h.TargetId == "f1");
    }

    // ==================== channels ====================

    [Fact]
    public async Task A_channel_is_found_by_its_frequency_in_either_spelling()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Funkkanaele.Add(new RadioChannel { Id = "c1", Frequency = "411.7", Label = "TRU Einsatzkanal" });
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.Contains(Of(await svc.SearchAsync(Query("411.7"), Plain()), nameof(RadioChannel)), h => h.TargetId == "c1");
        Assert.Contains(Of(await svc.SearchAsync(Query("411,7"), Plain()), nameof(RadioChannel)), h => h.TargetId == "c1");
    }

    [Fact]
    public async Task The_search_hides_a_classified_channel_from_a_plain_agent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Funkkanaele.Add(new RadioChannel
            {
                Id = "c1", Frequency = "411.7", Label = "Geheim", IsClassified = true,
            });
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.Empty(Of(await svc.SearchAsync(Query("411.7"), Plain()), nameof(RadioChannel)));
        Assert.Single(Of(await svc.SearchAsync(Query("411.7"), Leader()), nameof(RadioChannel)));
    }

    [Fact]
    public async Task The_search_hides_a_taskforce_channel_from_someone_who_is_not_assigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "tf1", Name = "Nachtfalke", CaseNumber = "FIB-TF-2026-0001" });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf1", AgentId = "member" });
            db.Funkkanaele.Add(new RadioChannel
            {
                Id = "c1", Frequency = "412.1", Label = "Nachtfalke", TaskforceId = "tf1",
            });
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.Empty(Of(await svc.SearchAsync(Query("412.1"), Plain("outsider")), nameof(RadioChannel)));
        Assert.Single(Of(await svc.SearchAsync(Query("412.1"), Plain("member")), nameof(RadioChannel)));
    }
}
