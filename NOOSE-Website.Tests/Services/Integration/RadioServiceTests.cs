using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Radio;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The two read gates of the radio plan and its one write rule.</summary>
public sealed class RadioServiceTests
{
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithStatus(AgentStatus.Active).Build();

    private static ClaimsPrincipal Plain(string id = "agent")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithStatus(AgentStatus.Active).Build();

    private static ClaimsPrincipal Partner(string id = "partner")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).WithStatus(AgentStatus.Active)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    /// <summary>Read-only supervision: reads classified material, writes nothing, and is nobody in the roleplay.</summary>
    private static ClaimsPrincipal Supervisor(string id = "aufsicht")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).WithStatus(AgentStatus.Active)
            .AsTeamLead().Build();

    private static void Add(SqliteTestContext ctx, Action<RadioChannel> configure)
    {
        using var db = ctx.NewContext();
        var channel = new RadioChannel { Frequency = "411.7", Label = "Kanal" };
        configure(channel);
        db.Funkkanaele.Add(channel);
        db.SaveChanges();
    }

    private static void AddTaskforce(SqliteTestContext ctx, string id, params string[] memberIds)
    {
        using var db = ctx.NewContext();
        db.Taskforces.Add(new Taskforce { Id = id, Name = "Nachtfalke", CaseNumber = "NOOSE-TF-2026-0001" });
        foreach (var member in memberIds)
        {
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = id, AgentId = member });
        }
        db.SaveChanges();
    }

    // ==================== classification gate ====================

    [Fact]
    public async Task GetPlanAsync_HidesAClassifiedChannelFromAPlainAgent()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, c => { c.Label = "Offen"; });
        Add(ctx, c => { c.Frequency = "500.1"; c.Label = "Geheim"; c.IsClassified = true; });
        var svc = new RadioService(ctx.Factory);

        var plain = await svc.GetPlanAsync(Plain());
        var leader = await svc.GetPlanAsync(Leader());

        Assert.Equal(new[] { "Offen" }, plain.Channels.Select(c => c.Label));
        Assert.Equal(new[] { "Offen", "Geheim" }, leader.Channels.Select(c => c.Label));
    }

    // ==================== taskforce gate ====================

    [Fact]
    public async Task GetPlanAsync_ShowsATaskforceChannelOnlyToItsMembers()
    {
        using var ctx = new SqliteTestContext();
        AddTaskforce(ctx, "tf1", "member");
        Add(ctx, c => { c.Label = "Allgemein"; });
        Add(ctx, c => { c.Frequency = "412.1"; c.Label = "Nachtfalke"; c.TaskforceId = "tf1"; });
        var svc = new RadioService(ctx.Factory);

        var outsider = await svc.GetPlanAsync(Plain("outsider"));
        var member = await svc.GetPlanAsync(Plain("member"));
        var leader = await svc.GetPlanAsync(Leader());

        Assert.Equal(new[] { "Allgemein" }, outsider.Channels.Select(c => c.Label));
        Assert.Equal(new[] { "Allgemein", "Nachtfalke" }, member.Channels.Select(c => c.Label));
        // leadership sees every taskforce, so it sees every bound channel
        Assert.Equal(new[] { "Allgemein", "Nachtfalke" }, leader.Channels.Select(c => c.Label));
    }

    [Fact]
    public async Task GetPlanAsync_NamesTheBoundTaskforce()
    {
        using var ctx = new SqliteTestContext();
        AddTaskforce(ctx, "tf1", "member");
        Add(ctx, c => { c.TaskforceId = "tf1"; });
        var svc = new RadioService(ctx.Factory);

        var plan = await svc.GetPlanAsync(Plain("member"));

        Assert.Equal("Nachtfalke", Assert.Single(plan.Channels).TaskforceName);
    }

    [Fact]
    public async Task GetAsync_AppliesBothArmsOfTheGate()
    {
        using var ctx = new SqliteTestContext();
        AddTaskforce(ctx, "tf1", "member");
        string bound, classified;
        using (var db = ctx.NewContext())
        {
            var a = new RadioChannel { Frequency = "412.1", Label = "Nachtfalke", TaskforceId = "tf1" };
            var b = new RadioChannel { Frequency = "500.1", Label = "Geheim", IsClassified = true };
            db.Funkkanaele.AddRange(a, b);
            db.SaveChanges();
            (bound, classified) = (a.Id, b.Id);
        }
        var svc = new RadioService(ctx.Factory);

        // taskforce arm
        Assert.NotNull(await svc.GetAsync(bound, Plain("member")));
        Assert.Null(await svc.GetAsync(bound, Plain("outsider")));
        // classification arm
        Assert.Null(await svc.GetAsync(classified, Plain()));
        Assert.NotNull(await svc.GetAsync(classified, Leader()));
    }

    // ==================== read-only supervision ====================

    [Fact]
    public async Task ReadOnlySupervision_SeesEverythingAndWritesNothing()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, c => { c.Label = "Geheim"; c.IsClassified = true; });
        var svc = new RadioService(ctx.Factory);
        var supervisor = Supervisor();

        // MayClassifiedRead is true for supervision, so the classified row is visible
        Assert.Single((await svc.GetPlanAsync(supervisor)).Channels);
        // MayWrite is false, so every write path refuses before the interceptor would
        var input = new RadioChannelInput { Frequency = "300.0", Label = "Neu" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, supervisor));
    }

    // ==================== partners ====================

    [Fact]
    public async Task GetPlanAsync_RefusesAPartnerOutright()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, _ => { });
        var svc = new RadioService(ctx.Factory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetPlanAsync(Partner()));
    }

    // ==================== faction frequencies ====================

    [Fact]
    public async Task GetPlanAsync_ReadsFactionFrequenciesLive_AndSkipsTheEmptyOnes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.Radio = "98.4"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            // both ways a faction can carry no frequency: never filled, and emptied again
            db.Factions.Add(Seed.Faction(id: "f3", name: "Lost", configure: f => f.Radio = string.Empty));
            db.SaveChanges();
        }
        var svc = new RadioService(ctx.Factory);

        var plan = await svc.GetPlanAsync(Plain());

        var row = Assert.Single(plan.Factions);
        Assert.Equal("Ballas", row.Name);
        Assert.Equal("98.4", row.Frequency);
    }

    [Fact]
    public async Task GetPlanAsync_HidesAClassifiedFactionFrequencyFromAPlainAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "fx", name: "Geheim", configure: f =>
            {
                f.Radio = "777.7";
                f.IsClassified = true;
            }));
            db.SaveChanges();
        }
        var svc = new RadioService(ctx.Factory);

        Assert.Empty((await svc.GetPlanAsync(Plain())).Factions);
        Assert.Single((await svc.GetPlanAsync(Leader())).Factions);
    }

    // ==================== write rule ====================

    [Fact]
    public async Task CreateAsync_RefusesAPlainAgentTheClassifiedFlag()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var input = new RadioChannelInput { Frequency = "411.7", Label = "Geheim", IsClassified = true };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Plain()));
        await svc.CreateAsync(input, Leader());
    }

    [Fact]
    public async Task RefreshAsync_RefusesAPlainAgentAnAlreadyClassifiedRow()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var created = await svc.CreateAsync(
            new RadioChannelInput { Frequency = "411.7", Label = "Geheim", IsClassified = true }, Leader());

        // lowering the flag is itself a leadership decision, so the plain agent is refused either way
        var open = new RadioChannelInput { Frequency = "411.7", Label = "Offen", IsClassified = false };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync(created.Id, open, Plain()));
    }

    [Fact]
    public async Task CreateAsync_NormalizesTheFrequency_AndDropsAnAgencyOutsideThePartnerBlock()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);

        var created = await svc.CreateAsync(new RadioChannelInput
        {
            Frequency = " 411,7 ",
            Label = "  TRU  ",
            Scope = RadioScope.Noose,
            Agency = PartnerAgency.LSPD,
        }, Plain());

        Assert.Equal("411.7", created.Frequency);
        Assert.Equal("TRU", created.Label);
        Assert.Null(created.Agency);
    }

    [Fact]
    public async Task CreateAsync_DropsATaskforceBindingOutsideTheNooseBlock()
    {
        using var ctx = new SqliteTestContext();
        AddTaskforce(ctx, "tf1", "member");
        var svc = new RadioService(ctx.Factory);

        var created = await svc.CreateAsync(new RadioChannelInput
        {
            Frequency = "300.0",
            Label = "LSPD Dispatch",
            Scope = RadioScope.Partner,
            TaskforceId = "tf1",
        }, Plain());

        Assert.Null(created.TaskforceId);
    }

    [Fact]
    public async Task RefreshAsync_PersistsAnOrdinaryEditByAnOrdinaryWriter()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var created = await svc.CreateAsync(
            new RadioChannelInput { Frequency = "411.7", Label = "Alt" }, Plain());

        await svc.RefreshAsync(created.Id,
            new RadioChannelInput { Frequency = "412,1", Label = "Neu", Note = "Ausweichkanal" }, Plain());

        var read = await svc.GetAsync(created.Id, Plain());
        Assert.NotNull(read);
        Assert.Equal("412.1", read.Frequency);
        Assert.Equal("Neu", read.Label);
        Assert.Equal("Ausweichkanal", read.Note);
    }

    [Fact]
    public async Task CreateAsync_StoresTheClassifiedFlag_AndTheRowReadsBackAsClassified()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);

        var created = await svc.CreateAsync(
            new RadioChannelInput { Frequency = "411.7", Label = "Geheim", IsClassified = true }, Leader());

        var read = await svc.GetAsync(created.Id, Leader());
        Assert.NotNull(read);
        Assert.True(read.IsClassified);
    }

    [Fact]
    public async Task DeleteAsync_RefusesAPlainAgentAClassifiedRow()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var created = await svc.CreateAsync(
            new RadioChannelInput { Frequency = "411.7", Label = "Geheim", IsClassified = true }, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(created.Id, Plain()));
        Assert.NotNull(await svc.GetAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task RestoreAsync_IsLeadershipOnly()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var created = await svc.CreateAsync(new RadioChannelInput { Frequency = "411.7", Label = "Offen" }, Plain());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(created.Id, Plain()));
    }

    [Fact]
    public async Task EveryWritePathRefusesAPartner()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);
        var created = await svc.CreateAsync(new RadioChannelInput { Frequency = "411.7", Label = "Offen" }, Plain());
        var input = new RadioChannelInput { Frequency = "300.0", Label = "LSPD" };
        var partner = Partner();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, partner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync(created.Id, input, partner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(created.Id, partner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(created.Id, partner));
    }

    [Fact]
    public async Task CreateAsync_KeepsTheAgencyOnAPartnerChannel()
    {
        using var ctx = new SqliteTestContext();
        var svc = new RadioService(ctx.Factory);

        var created = await svc.CreateAsync(new RadioChannelInput
        {
            Frequency = "300.0",
            Label = "LSPD Dispatch",
            Scope = RadioScope.Partner,
            Agency = PartnerAgency.LSPD,
        }, Plain());

        Assert.Equal(PartnerAgency.LSPD, created.Agency);
    }

    // ==================== partner with the radio function ====================

    private static void GrantRadio(SqliteTestContext ctx, PartnerAgency agency)
    {
        using var db = ctx.NewContext();
        db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = agency, Features = PartnerFeature.Radio });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetPlanAsync_PartnerWithTheRadioFunction_SeesUnclassifiedChannels_AndLinksOnlyReleasedFactions()
    {
        using var ctx = new SqliteTestContext();
        Add(ctx, c => { c.Label = "Offen"; c.Frequency = "100.1"; });
        Add(ctx, c => { c.Label = "Geheim"; c.Frequency = "100.2"; c.IsClassified = true; });
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("released", "Ballas", f => f.Radio = "200.1"));
            db.Factions.Add(Seed.Faction("hidden", "Vagos", f => f.Radio = "200.2"));
            db.Factions.Add(Seed.Faction("vs", "Geheim", f => { f.Radio = "200.3"; f.IsClassified = true; }));
            db.PartnerShares.Add(new PartnerShare { EntityType = "Faction", EntityId = "released", Agency = PartnerAgency.LSPD });
            db.SaveChanges();
        }
        GrantRadio(ctx, PartnerAgency.LSPD);

        var plan = await new RadioService(ctx.Factory).GetPlanAsync(Partner());

        Assert.Equal(new[] { "Offen" }, plan.Channels.Select(c => c.Label));
        Assert.Equal(new[] { "Ballas", "Vagos" }, plan.Factions.Select(f => f.Name).OrderBy(n => n));
        Assert.True(plan.Factions.Single(f => f.FactionId == "released").Linkable);
        Assert.False(plan.Factions.Single(f => f.FactionId == "hidden").Linkable);
    }

    [Fact]
    public async Task GetPlanAsync_ThePartnerFunctionOfAnotherAgency_DoesNotOpenThePlan()
    {
        using var ctx = new SqliteTestContext();
        GrantRadio(ctx, PartnerAgency.Parlament);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RadioService(ctx.Factory).GetPlanAsync(Partner()));
    }

    [Fact]
    public async Task GetPlanAsync_PartnerWithTheRadioFunction_NeverSeesATaskforceChannel()
    {
        using var ctx = new SqliteTestContext();
        AddTaskforce(ctx, "tf1", "someone");
        Add(ctx, c => { c.Label = "TF"; c.TaskforceId = "tf1"; });
        GrantRadio(ctx, PartnerAgency.LSPD);

        var plan = await new RadioService(ctx.Factory).GetPlanAsync(Partner());

        Assert.Empty(plan.Channels);
    }
}
