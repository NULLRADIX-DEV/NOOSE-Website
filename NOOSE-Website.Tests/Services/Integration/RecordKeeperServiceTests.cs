using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Keeper and deputy of person records: who may set them, what the record keeps, and the own list.</summary>
public sealed class RecordKeeperServiceTests
{
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Agent(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();
    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RecordKeeperService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var principals = Substitute.For<IAgentPrincipalService>();
        principals.BuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<string>() == "lead" ? Leader() : Agent(call.Arg<string>()));
        var recency = Substitute.For<IRecencyService>();
        var settings = new[] { nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Case) }
            .ToDictionary(t => t, _ => new RecencySettings(7, 14, false));
        recency.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var notifications = Substitute.For<INotificationService>();
        return (new RecordKeeperService(ctx.Factory, principals, recency, notifications), notifications);
    }

    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("a", Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("b", Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Terminated));
        db.People.Add(Seed.Person("p", "Tony Spitzel"));
        db.People.Add(Seed.Person("vs", "Geheim", p => p.SecrecyLevel = DocumentClassification.Leadership));
        await db.SaveChangesAsync();
        // aged on purpose, so a write that stamped the record would show
        await db.People.ExecuteUpdateAsync(s => s.SetProperty(p => p.ModifiedAt, Old).SetProperty(p => p.CreatedAt, Old));
        return ctx;
    }

    private static async Task<Person> PersonAsync(SqliteTestContext ctx, string id)
    {
        await using var db = ctx.NewContext();
        return await db.People.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Anyone_writing_claims_a_record_nobody_keeps_but_not_one_that_has_a_keeper()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);

        await svc.ClaimAsync("p", Agent("a"));
        Assert.Equal("a", (await PersonAsync(ctx, "p")).KeeperId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ClaimAsync("p", Agent("b")));
        Assert.Equal("a", (await PersonAsync(ctx, "p")).KeeperId);
    }

    [Fact]
    public async Task Handing_a_record_on_keeps_its_age_and_is_audited()
    {
        using var ctx = await SeededAsync();
        var (svc, notifications) = Build(ctx);

        await svc.AssignAsync("p", "a", "b", Leader());

        var person = await PersonAsync(ctx, "p");
        Assert.Equal(("a", "b"), (person.KeeperId, person.DeputyId));
        Assert.Equal(Old, person.ModifiedAt);
        await using var db = ctx.NewContext();
        var audit = await db.AuditLogs.SingleAsync(l => l.EntityId == "p" && l.ChangesJson != null && l.ChangesJson.Contains("Codename-a"));
        Assert.Contains("Codename-a", audit.ChangesJson);
        await notifications.Received(1).NotifyAsync("a", NotificationType.RecordKeeperAssigned,
            Arg.Any<string>(), "/personen/p", Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyAsync("b", NotificationType.RecordKeeperAssigned,
            Arg.Any<string>(), "/personen/p", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_leadership_or_the_keeper_assigns()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);
        await svc.AssignAsync("p", "a", null, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AssignAsync("p", "b", null, Agent("b")));
        // the keeper may hand the record on
        await svc.AssignAsync("p", "b", "a", Agent("a"));
        Assert.Equal(("b", "a"), ((await PersonAsync(ctx, "p")).KeeperId, (await PersonAsync(ctx, "p")).DeputyId));
        // the read-only supervision writes nothing
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AssignAsync("p", null, null, Supervision()));
    }

    [Fact]
    public async Task A_pair_must_be_valid_and_pickable()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("p", "a", "a", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("p", null, "a", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("p", "gone", null, Leader()));
    }

    [Fact]
    public async Task A_classified_record_goes_only_to_someone_cleared_for_it()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("vs", "a", null, Leader()));
        await svc.AssignAsync("vs", "lead", null, Leader());
        Assert.Equal("lead", (await PersonAsync(ctx, "vs")).KeeperId);
    }

    [Fact]
    public async Task A_leaving_keeper_is_followed_by_the_deputy()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);
        await svc.AssignAsync("p", "a", "b", Leader());

        await svc.StepDownAsync("p", Agent("a"));

        var person = await PersonAsync(ctx, "p");
        Assert.Equal(("b", (string?)null), (person.KeeperId, person.DeputyId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StepDownAsync("p", Agent("a")));
    }

    [Fact]
    public async Task The_own_list_holds_kept_and_led_records_stalest_first()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);
        await svc.AssignAsync("p", "a", null, Leader());
        await using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("fa", "Ballas"));
            db.FactionAgents.Add(new FactionAgent { FactionId = "fa", AgentId = "a", IsInvestigationLead = true });
            db.Factions.Add(Seed.Faction("fb", "Vagos"));
            db.FactionAgents.Add(new FactionAgent { FactionId = "fb", AgentId = "a" });
            await db.SaveChangesAsync();
        }

        var mine = await svc.GetMineAsync(Agent("a"));

        Assert.Equal(["fa", "p"], mine.Select(m => m.Id).Order());
        var person = mine.Single(m => m.Id == "p");
        Assert.Equal(RecencyLevel.Stale, person.Level);
        Assert.Equal(RecordKeeperRules.Keeper, person.Role);
        Assert.Equal(RecordKeeperRules.InvestigationLead, mine.Single(m => m.Id == "fa").Role);
        Assert.Empty(await svc.GetMineAsync(Agent("b")));
    }

    [Fact]
    public async Task Viewers_outside_the_house_see_no_keepers()
    {
        using var ctx = await SeededAsync();
        var (svc, _) = Build(ctx);
        var partner = ClaimsPrincipalBuilder.Agent("lspd").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

        Assert.Null(await svc.GetAsync("p", partner));
        Assert.NotNull(await svc.GetAsync("p", Agent("a")));
        Assert.Null(await svc.GetAsync("vs", Agent("a")));
    }

    [Fact]
    public async Task A_departure_hands_the_record_on_without_making_it_look_current()
    {
        using var ctx = await SeededAsync();
        // the keeper left after taking the record; a terminated agent can no longer be picked
        await using (var db = ctx.NewContext())
        {
            await db.People.Where(p => p.Id == "p")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.KeeperId, "gone").SetProperty(p => p.DeputyId, "b"));
        }
        var handover = new AgentHandoverService(ctx.Factory, Substitute.For<INotificationService>());

        var plan = await handover.GetAsync("gone", Leader());
        var row = Assert.Single(plan.Rows, r => r.Kind == NOOSE_Website.Models.Personnel.HandoverKind.PersonRecord);
        Assert.Equal(RecordKeeperRules.Keeper, row.Role);
        await handover.ApplyAsync("gone", [new(row.Kind, row.Id, "a")], Leader());

        var person = await PersonAsync(ctx, "p");
        Assert.Equal(("a", "b"), (person.KeeperId, person.DeputyId));
        Assert.Equal(Old, person.ModifiedAt);
    }

    [Theory]
    [InlineData("k", "d", "k", "s", "s", "d")]
    [InlineData("k", "d", "d", "s", "k", "s")]
    [InlineData("k", "d", "k", "d", "d", null)]
    [InlineData("k", "d", "d", "k", "k", null)]
    [InlineData("k", null, "k", "s", "s", null)]
    public void Handover_puts_the_successor_in_the_leaving_role(string keeper, string? deputy, string leaving,
        string successor, string? newKeeper, string? newDeputy)
        => Assert.Equal((newKeeper, newDeputy), AgentHandoverService.PassKeeper(keeper, deputy, leaving, successor));
}
