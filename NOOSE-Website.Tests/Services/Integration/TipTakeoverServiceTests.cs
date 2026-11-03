using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="TipTakeoverService"/>: who may take a tip over, and what it leaves behind.</summary>
public sealed class TipTakeoverServiceTests
{
    private const string TipId = "h1";
    private const string ProfileId = "profil1";
    private const string ExistingPersonId = "p1";

    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    /// <summary>Read-only supervision: reads everything, writes nothing.</summary>
    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger1").WithStatus(AgentStatus.Civilian).Build();

    private sealed record Host(
        TipTakeoverService Takeover,
        TipService Tips,
        LinkService Links,
        TestDbContextFactory Factory);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var factory = ctx.Factory;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        var seq = 0;
        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-{++seq:0000}");

        var threat = Substitute.For<IThreatScoreService>();
        var notifications = Substitute.For<INotificationService>();
        var suggestion = Substitute.For<IProfileSuggestionService>();
        var tipPriority = new TipPriorityService(factory);

        var wanted = new PublicWantedService(factory, modules, caseNumbers,
            Substitute.For<IFileStorageService>(), Substitute.For<IPublicWantedPhotoStorageService>(),
            notifications, tipPriority, Substitute.For<IDiscordWebhookService>(),
            Substitute.For<IPressReleaseService>(), cache);
        var tips = new TipService(factory, modules, new BuergerService(factory), wanted, caseNumbers,
            Substitute.For<ITipAttachmentStorageService>(), notifications, tipPriority,
            new PublicTemplateService(factory), Substitute.For<IDiscordWebhookService>(), new TipsBroadcaster());

        var people = new PersonService(factory, Substitute.For<IFileStorageService>(), suggestion, caseNumbers,
            threat, notifications, wanted);
        var cases = new CaseService(factory, caseNumbers, suggestion, notifications);
        var observations = new ObservationService(factory, threat, notifications);
        var links = new LinkService(factory, threat);

        var takeover = new TipTakeoverService(factory, tips, people, cases, observations, links);
        return new Host(takeover, tips, links, factory);
    }

    /// <summary>One citizen profile, one existing person file and one tip in the inbox.</summary>
    private static async Task<SqliteTestContext> SeededAsync(Action<Hinweis>? tweak = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        // out of the stubbed counter's range, which starts at 0001 for every fresh takeover
        db.People.Add(Seed.Person(ExistingPersonId, "Max Mustermann", p => p.CaseNumber = "FIB-P-2026-9001"));
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId,
            UserId = "buerger1",
            FirstName = "Erika",
            LastName = "Musterfrau",
        });

        var tip = new Hinweis
        {
            Id = TipId,
            CaseNumber = "FIB-H-2026-0001",
            CitizenProfileId = ProfileId,
            Text = "Der Gesuchte war heute Abend am Hafen und ist in ein Boot gestiegen.",
            Status = TipStatus.Neu,
        };
        tweak?.Invoke(tip);
        db.Hinweise.Add(tip);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task<TipStatus> StatusAsync(Host host)
    {
        await using var db = host.Factory.CreateDbContext();
        return await db.Hinweise.Where(h => h.Id == TipId).Select(h => h.Status).SingleAsync();
    }

    // ---- new person file ----

    [Fact]
    public async Task Taking_a_tip_over_opens_a_person_file_and_links_it_back()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var personId = await host.Takeover.ToNewPersonAsync(TipId, "Unbekannter am Hafen", Agent());

        await using var db = host.Factory.CreateDbContext();
        var person = await db.People.SingleAsync(p => p.Id == personId);
        Assert.Equal("Unbekannter am Hafen", person.Name);

        var link = await db.Links.SingleAsync();
        Assert.Equal(nameof(Hinweis), link.SourceType);
        Assert.Equal(TipId, link.SourceId);
        Assert.Equal(nameof(Person), link.TargetType);
        Assert.Equal(personId, link.TargetId);
        Assert.Contains("FIB-H-2026-0001", link.Label);
        // an automatic link would be filtered out of the timeline, which is the whole point of the link
        Assert.False(link.Automatic);
    }

    [Fact]
    public async Task The_link_reads_as_a_case_number_on_the_person_file()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var personId = await host.Takeover.ToNewPersonAsync(TipId, "Unbekannter am Hafen", Agent());

        var shown = Assert.Single(await host.Links.GetForRecordAsync(nameof(Person), personId,
            ViewerScope.From(Agent())));

        Assert.Equal(nameof(Hinweis), shown.OtherType);
        Assert.Equal("Bürgerhinweis FIB-H-2026-0001", shown.OtherDesignation);
        Assert.Equal($"/hinweise/{TipId}", shown.Href);
    }

    [Fact]
    public async Task A_second_person_takeover_is_refused_with_plain_text()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Takeover.ToNewPersonAsync(TipId, "Unbekannter am Hafen", Agent());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Takeover.ToNewPersonAsync(TipId, "Noch jemand", Agent()));

        Assert.Contains("bereits", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(2, await db.People.CountAsync()); // the seeded one plus exactly one takeover
    }

    [Fact]
    public async Task A_takeover_without_a_name_is_refused_before_anything_is_written()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Takeover.ToNewPersonAsync(TipId, "   ", Agent()));

        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(1, await db.People.CountAsync());
        Assert.Empty(await db.Links.ToListAsync());
    }

    // ---- existing records ----

    [Fact]
    public async Task An_existing_person_file_can_be_attached()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Takeover.AttachPersonAsync(TipId, ExistingPersonId, Agent());

        await using var db = host.Factory.CreateDbContext();
        var link = await db.Links.SingleAsync();
        Assert.Equal(ExistingPersonId, link.TargetId);
    }

    [Fact]
    public async Task A_classified_person_file_is_refused_as_a_target()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == ExistingPersonId);
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Takeover.AttachPersonAsync(TipId, ExistingPersonId, Agent()));

        await using var check = host.Factory.CreateDbContext();
        Assert.Empty(await check.Links.ToListAsync());
    }

    [Fact]
    public async Task Leadership_may_attach_a_classified_person_file()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == ExistingPersonId);
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        await host.Takeover.AttachPersonAsync(TipId, ExistingPersonId, Leader());

        await using var check = host.Factory.CreateDbContext();
        Assert.Single(await check.Links.ToListAsync());
    }

    [Fact]
    public async Task A_case_takeover_titles_itself_after_the_tip()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var caseId = await host.Takeover.ToCaseAsync(TipId, null, Agent());

        await using var db = host.Factory.CreateDbContext();
        var @case = await db.Cases.SingleAsync(v => v.Id == caseId);
        Assert.Equal("Bürgerhinweis FIB-H-2026-0001", @case.Title);
        Assert.Equal(CaseStatus.Open, @case.Status);
        var link = await db.Links.SingleAsync();
        Assert.Equal(nameof(Case), link.TargetType);
    }

    [Fact]
    public async Task An_observation_takes_the_text_and_the_time_of_the_tip()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        var observationId = await host.Takeover.ToObservationAsync(TipId, ExistingPersonId, Agent());

        await using var db = host.Factory.CreateDbContext();
        var observation = await db.Observations.SingleAsync(o => o.Id == observationId);
        Assert.Equal(ExistingPersonId, observation.PersonId);
        Assert.Contains("Hafen", observation.Sighting);
        // the observer was a citizen, not an agent
        Assert.Null(observation.ObservingAgentId);
        Assert.Equal(nameof(Observation), (await db.Links.SingleAsync()).TargetType);
    }

    [Fact]
    public async Task An_observation_on_a_classified_file_is_refused()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == ExistingPersonId);
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        // ObservationService only gates the secrecy level, so the takeover has to check the read gate itself
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Takeover.ToObservationAsync(TipId, ExistingPersonId, Agent()));

        await using var check = host.Factory.CreateDbContext();
        Assert.Empty(await check.Observations.ToListAsync());
        Assert.Empty(await check.Links.ToListAsync());
    }

    [Fact]
    public async Task Leadership_may_observe_a_classified_file()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == ExistingPersonId);
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        await host.Takeover.ToObservationAsync(TipId, ExistingPersonId, Leader());

        await using var check = host.Factory.CreateDbContext();
        Assert.Single(await check.Observations.ToListAsync());
    }

    // ---- status ----

    [Fact]
    public async Task A_fresh_tip_moves_into_review_after_a_takeover()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await host.Takeover.ToCaseAsync(TipId, null, Agent());

        Assert.Equal(TipStatus.InPruefung, await StatusAsync(host));
    }

    [Fact]
    public async Task A_decided_tip_keeps_its_status()
    {
        using var ctx = await SeededAsync(t => t.Status = TipStatus.Bestaetigt);
        var host = NewHost(ctx);

        await host.Takeover.ToCaseAsync(TipId, null, Agent());

        Assert.Equal(TipStatus.Bestaetigt, await StatusAsync(host));
    }

    // ---- guards ----

    [Fact]
    public async Task Read_only_supervision_may_look_but_not_take_over()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        Assert.Empty(await host.Takeover.GetStateAsync(TipId, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Takeover.ToCaseAsync(TipId, null, Supervision()));
    }

    [Fact]
    public async Task A_citizen_account_may_not_take_anything_over()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Takeover.GetStateAsync(TipId, Citizen()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Takeover.ToNewPersonAsync(TipId, "Irgendwer", Citizen()));
    }

    [Fact]
    public async Task An_unknown_tip_is_refused()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Takeover.ToCaseAsync("gibt-es-nicht", null, Agent()));
    }

    [Fact]
    public async Task The_state_lists_what_the_tip_already_became()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Takeover.ToNewPersonAsync(TipId, "Unbekannter am Hafen", Agent());
        await host.Takeover.ToCaseAsync(TipId, null, Agent());

        var state = await host.Takeover.GetStateAsync(TipId, Agent());

        Assert.Equal(2, state.Count);
        Assert.Contains(state, l => l.OtherType == nameof(Person));
        Assert.Contains(state, l => l.OtherType == nameof(Case));
    }
}
