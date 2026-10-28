using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The public roster: every selectable agent by name, the leadership as an org chart with photo and phone.</summary>
public sealed class PublicLeadershipServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal PlainAgent()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).Build();

    private sealed record Host(PublicLeadershipService Service, IPublicLeadershipPhotoStorageService Storage);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var storage = Substitute.For<IPublicLeadershipPhotoStorageService>();
        storage.IsAllowedType(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0).StartsWith("image/"));
        var saved = 0;
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (++saved).ToString("x32") + ".jpg");
        return new Host(new PublicLeadershipService(ctx.Factory, new PublicModuleService(ctx.Factory, cache), storage, cache),
            storage);
    }

    private static PublicLeadershipService NewService(SqliteTestContext ctx) => NewHost(ctx).Service;

    private static Agent Named(string id, Rank? rank, string? realName, Action<Agent>? configure = null)
        => Seed.Agent(id, rank ?? Rank.JuniorAgent, configure: a =>
        {
            a.Rank = rank;
            a.RealName = realName;
            configure?.Invoke(a);
        });

    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true, params Agent[] agents)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Leadership)).IsEnabled = moduleOn;
        db.Users.AddRange(agents.Length > 0
            ? agents
            :
            [
                Named("chef", Rank.Director, "Zora Zett"),
                Named("vize", Rank.DeputyDirector, "Bert Berg"),
                Named("ssa", Rank.SupervisorySpecialAgent, "Anna Alt"),
                Named("ssa2", Rank.SupervisorySpecialAgent, "Albert Ast"),
                Named("junior", Rank.JuniorAgent, "Clara Cello"),
                Named("special", Rank.SpecialAgent, "Ärne Ahorn"),
            ]);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task DemoteAsync(SqliteTestContext ctx, string agentId)
    {
        await using var db = ctx.NewContext();
        (await db.Users.SingleAsync(u => u.Id == agentId)).Rank = Rank.SeniorSpecialAgent;
        await db.SaveChangesAsync();
    }

    private static Stream Picture() => new MemoryStream([1, 2, 3]);

    // ---- who appears, and how ----

    [Fact]
    public async Task Leadership_is_a_chart_by_rank_and_everyone_else_a_list_by_name()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        var roster = await service.GetPublicAsync();

        Assert.Equal(["Director", "Deputy Director", "Supervisory Special Agent"],
            roster.Leadership.Select(t => t.Title).ToList());
        Assert.Equal("Zora Zett | Bert Berg | Albert Ast, Anna Alt",
            string.Join(" | ", roster.Leadership.Select(t => string.Join(", ", t.Cards.Select(c => c.Name)))));
        Assert.All(roster.Leadership.SelectMany(t => t.Cards), c => Assert.Null(c.Phone));
        // German collation: Ä sorts with A, not after Z
        Assert.Equal(["Ärne Ahorn", "Clara Cello"], roster.Members.Select(m => m.Name).ToList());
    }

    [Fact]
    public async Task Team_leads_partners_inactive_and_nameless_accounts_never_appear()
    {
        using var ctx = await SeededAsync(true,
            Named("ok", Rank.SpecialAgent, "Erika Echt"),
            Named("tl", Rank.Director, "Theo Teamleitung", a => a.IsTeamLead = true),
            Named("tladmin", Rank.Director, "Tina Admin", a => { a.IsTeamLead = true; a.IsAdmin = true; }),
            Named("partner", Rank.Director, "Paul Partner", a => a.PartnerAgency = PartnerAgency.LSPD),
            Named("weg", Rank.Director, "Gert Gekuendigt", a => a.Status = AgentStatus.Terminated),
            Named("gesperrt", Rank.SpecialAgent, "Gabi Gesperrt", a => a.Status = AgentStatus.Blocked),
            Named("bewerber", null, "Bea Bewerber", a => a.Status = AgentStatus.Applicant),
            Named("buerger", null, "Bodo Buerger", a => a.Status = AgentStatus.Civilian),
            Named("ohne", Rank.DeputyDirector, null),
            Named("leer", Rank.SpecialAgent, "   "),
            Named("ausstehend", Rank.SpecialAgent, null, a => a.PendingRealName = "Noch Nicht"));
        var service = NewService(ctx);

        var roster = await service.GetPublicAsync();

        Assert.Empty(roster.Leadership);
        Assert.Equal(["Erika Echt"], roster.Members.Select(m => m.Name).ToList());
    }

    [Fact]
    public void The_outward_shapes_carry_nothing_but_name_phone_photo_and_rank_wording()
    {
        // whatever else the account holds — codename, badge number, unit, id — has no field to travel in
        Assert.Equal(["Name", "Phone", "PhotoUrl"],
            typeof(PublicLeadershipCard).GetProperties().Select(p => p.Name).ToArray());
        Assert.Equal(["Title", "Cards"], typeof(PublicLeadershipTier).GetProperties().Select(p => p.Name).ToArray());
        Assert.Equal(["Name"], typeof(PublicRosterMember).GetProperties().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Nothing_goes_out_while_the_module_is_off()
    {
        using var ctx = await SeededAsync(moduleOn: false);
        var service = NewService(ctx);

        var roster = await service.GetPublicAsync();

        Assert.Empty(roster.Leadership);
        Assert.Empty(roster.Members);
    }

    [Fact]
    public async Task Nothing_goes_out_while_the_kill_switch_is_on()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true" });
            await db.SaveChangesAsync();
        }
        var service = NewService(ctx);

        var roster = await service.GetPublicAsync();

        Assert.Empty(roster.Leadership);
        Assert.Empty(roster.Members);
    }

    // ---- phone numbers ----

    [Fact]
    public async Task A_saved_phone_shows_on_the_card_at_once()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        Assert.Null((await service.GetPublicAsync()).Leadership[0].Cards[0].Phone);

        await service.SetPhoneAsync("chef", "  555-0100 ", Leader());

        Assert.Equal("555-0100", (await service.GetPublicAsync()).Leadership[0].Cards[0].Phone);
        Assert.Equal("555-0100", (await service.GetLeadershipAsync(Leader()))[0].Phone);
    }

    [Fact]
    public async Task A_blank_phone_removes_it()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SetPhoneAsync("chef", "555-0100", Leader());

        await service.SetPhoneAsync("chef", "  ", Leader());

        Assert.Null((await service.GetPublicAsync()).Leadership[0].Cards[0].Phone);
        await using var db = ctx.NewContext();
        Assert.False(await db.OeffentlicheTelefonnummern.AnyAsync());
    }

    [Fact]
    public async Task A_plain_agent_may_not_set_a_phone()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SetPhoneAsync("chef", "555-0100", PlainAgent()));
    }

    [Fact]
    public async Task The_read_only_supervision_may_not_set_a_phone()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SetPhoneAsync("chef", "555-0100", Supervision()));
    }

    [Fact]
    public async Task The_demo_visitor_may_not_set_a_phone()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetPhoneAsync("chef", "555-0100", demo));
    }

    [Fact]
    public async Task A_number_left_behind_by_a_demotion_can_still_be_removed()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SetPhoneAsync("ssa", "555-0100", Leader());
        await DemoteAsync(ctx, "ssa");

        await service.SetPhoneAsync("ssa", null, Leader());

        await using var check = ctx.NewContext();
        Assert.False(await check.OeffentlicheTelefonnummern.AnyAsync());
    }

    [Theory]
    [InlineData("junior")]
    [InlineData("erfunden")]
    public async Task Only_the_leadership_band_gets_a_phone(string agentId)
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetPhoneAsync(agentId, "555-0100", Leader()));
    }

    [Theory]
    [InlineData("555-0100", "555-0100")]
    [InlineData("+49 (0)30 / 123.45", "+49 (0)30 / 123.45")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void A_phone_is_trimmed_and_blank_is_none(string? input, string? expected)
    {
        Assert.Equal(expected, PublicLeadershipService.CleanPhone(input));
    }

    [Theory]
    [InlineData("ruf mich an")]
    [InlineData("555-0100 <b>")]
    [InlineData("---")]
    [InlineData("123456789012345678901234567890123")]
    public void A_phone_that_is_no_number_is_refused(string input)
    {
        Assert.Throws<InvalidOperationException>(() => PublicLeadershipService.CleanPhone(input));
    }

    // ---- photos ----

    [Fact]
    public async Task A_saved_photo_shows_on_the_card_and_is_served()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await service.SetPhotoAsync("chef", Picture(), "image/png", Leader());

        var card = (await service.GetPublicAsync()).Leadership[0].Cards[0];
        var file = 1.ToString("x32") + ".jpg";
        Assert.Equal("/fuehrung/foto/" + file, card.PhotoUrl);
        Assert.Equal(new PublicLeadershipPhoto(file, "image/png"), await service.GetPublishedPhotoAsync(file));
    }

    [Fact]
    public async Task A_photo_of_someone_no_longer_on_a_card_is_not_served()
    {
        using var ctx = await SeededAsync();
        await NewService(ctx).SetPhotoAsync("ssa", Picture(), "image/png", Leader());
        await DemoteAsync(ctx, "ssa");

        Assert.Null(await NewService(ctx).GetPublishedPhotoAsync(1.ToString("x32") + ".jpg"));
    }

    [Fact]
    public async Task No_photo_is_served_while_the_module_is_off()
    {
        using var ctx = await SeededAsync();
        await NewService(ctx).SetPhotoAsync("chef", Picture(), "image/png", Leader());
        await using (var db = ctx.NewContext())
        {
            (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Leadership)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.Null(await NewService(ctx).GetPublishedPhotoAsync(1.ToString("x32") + ".jpg"));
    }

    [Theory]
    [InlineData("../geheim.jpg")]
    [InlineData("00000000000000000000000000000001.JPG")]
    [InlineData("00000000000000000000000000000001.svg")]
    [InlineData("profilbild.png")]
    public async Task A_name_the_storage_never_hands_out_is_not_even_looked_up(string fileName)
    {
        // the card carries exactly that name, so only the name check can refuse it
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.OeffentlicheTelefonnummern.Add(new NOOSE_Website.Data.Entities.Public.OeffentlicheTelefonnummer
            {
                AgentId = "chef", PhotoFileName = fileName, PhotoContentType = "image/png",
            });
            await db.SaveChangesAsync();
        }
        var service = NewService(ctx);
        Assert.Equal("/fuehrung/foto/" + fileName, (await service.GetPublicAsync()).Leadership[0].Cards[0].PhotoUrl);

        Assert.Null(await service.GetPublishedPhotoAsync(fileName));
    }

    [Fact]
    public async Task Removing_a_photo_needs_the_same_rights_as_adding_one()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SetPhotoAsync("chef", Picture(), "image/png", Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemovePhotoAsync("chef", Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemovePhotoAsync("chef", PlainAgent()));
        Assert.NotNull((await service.GetPublicAsync()).Leadership[0].Cards[0].PhotoUrl);
    }

    [Fact]
    public async Task Replacing_a_photo_deletes_the_old_copy()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetPhotoAsync("chef", Picture(), "image/png", Leader());

        await host.Service.SetPhotoAsync("chef", Picture(), "image/png", Leader());

        host.Storage.Received(1).Delete(1.ToString("x32") + ".jpg");
        Assert.EndsWith(2.ToString("x32") + ".jpg", (await host.Service.GetPublicAsync()).Leadership[0].Cards[0].PhotoUrl);
    }

    [Fact]
    public async Task Phone_and_photo_come_and_go_independently()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        await host.Service.SetPhoneAsync("chef", "555-0100", Leader());
        await host.Service.SetPhotoAsync("chef", Picture(), "image/png", Leader());

        await host.Service.SetPhoneAsync("chef", null, Leader());
        var card = (await host.Service.GetPublicAsync()).Leadership[0].Cards[0];
        Assert.Null(card.Phone);
        Assert.NotNull(card.PhotoUrl);

        await host.Service.RemovePhotoAsync("chef", Leader());
        host.Storage.Received(1).Delete(1.ToString("x32") + ".jpg");
        await using var db = ctx.NewContext();
        Assert.False(await db.OeffentlicheTelefonnummern.AnyAsync());
    }

    [Fact]
    public async Task Removing_the_photo_keeps_the_phone()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SetPhotoAsync("chef", Picture(), "image/png", Leader());
        await service.SetPhoneAsync("chef", "555-0100", Leader());

        await service.RemovePhotoAsync("chef", Leader());

        var card = (await service.GetPublicAsync()).Leadership[0].Cards[0];
        Assert.Equal("555-0100", card.Phone);
        Assert.Null(card.PhotoUrl);
    }

    [Fact]
    public async Task Only_a_picture_for_a_current_leader_is_taken()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetPhotoAsync("junior", Picture(), "image/png", Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.SetPhotoAsync("chef", Picture(), "application/pdf", Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => host.Service.SetPhotoAsync("chef", Picture(), "image/png", Supervision()));
        await host.Storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    // ---- the settings panel ----

    [Fact]
    public async Task The_panel_lists_the_leadership_band_only()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        var rows = await service.GetLeadershipAsync(Leader());

        Assert.Equal(["chef", "vize", "ssa2", "ssa"], rows.Select(r => r.AgentId).ToList());
        Assert.Equal("Zora Zett", rows[0].Name);
    }

    [Fact]
    public async Task The_supervision_may_not_open_the_panel()
    {
        // row order, phone and photo would pair every codename with its public card
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetLeadershipAsync(Supervision()));
    }

    [Fact]
    public async Task A_plain_agent_may_not_open_the_panel()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetLeadershipAsync(PlainAgent()));
    }
}
