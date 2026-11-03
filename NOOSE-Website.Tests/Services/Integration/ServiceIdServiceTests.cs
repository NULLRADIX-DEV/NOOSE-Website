using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The printable card: who gets one, what is on it, and that the real name stays off it.</summary>
public sealed class ServiceIdServiceTests
{
    private sealed record Host(ServiceIdService Service, IAccessLogService AccessLog);

    private static Agent Holder(string id, Action<Agent>? configure = null)
        => Seed.Agent(id, Rank.SpecialAgent, configure: a =>
        {
            a.BadgeNumber = "IV";
            a.Codename = "Falke";
            a.RealName = "Klara Klarname";
            configure?.Invoke(a);
        });

    private static ClaimsPrincipal Self(string id = "holder")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn, params Agent[] agents)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.ServiceId)).IsEnabled = moduleOn;
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static Host NewHost(SqliteTestContext ctx)
    {
        var discord = Substitute.For<IDiscordWebhookService>();
        discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(
            false, "https://fibhq.com", new Dictionary<NotificationType, string?>(),
            new Dictionary<NotificationType, string?>(), false));
        var accessLog = Substitute.For<IAccessLogService>();
        var modules = new PublicModuleService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));
        return new Host(new ServiceIdService(ctx.Factory, discord, modules, accessLog), accessLog);
    }

    [Fact]
    public async Task An_agent_gets_their_own_card_with_the_check_address_and_a_qr_code()
    {
        using var ctx = await SeededAsync(true, Holder("holder", a => a.AvatarFileName = "bild.jpg"));
        var host = NewHost(ctx);

        var result = await host.Service.GetAsync("holder", Self());

        Assert.Equal(ServiceIdAccess.Allowed, result.Access);
        var card = result.Card!;
        Assert.Equal("Falke", card.DisplayName);
        Assert.Equal("IV", card.Number);
        Assert.Equal(RankDisplay.Name(Rank.SpecialAgent), card.RankName);
        Assert.Equal("fibhq.com/ausweis/IV", card.VerifyAddress);
        Assert.Contains("<svg", card.QrSvg, StringComparison.Ordinal);
        Assert.Equal("/dateien/agenten/profilbild/bild.jpg", card.PhotoUrl);
        Assert.True(card.CheckOnline);
        // an own card is not a look into somebody else's file
        await host.AccessLog.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    [Fact]
    public async Task The_card_flags_a_switched_off_check()
    {
        using var ctx = await SeededAsync(false, Holder("holder"));

        var result = await NewHost(ctx).Service.GetAsync("holder", Self());

        Assert.False(result.Card!.CheckOnline);
        Assert.NotNull(result.Card.QrSvg);
    }

    [Fact]
    public async Task Leadership_prints_somebody_elses_card_and_leaves_a_trace()
    {
        using var ctx = await SeededAsync(true, Holder("holder"));
        var host = NewHost(ctx);

        var result = await host.Service.GetAsync("holder", Leader());

        Assert.Equal(ServiceIdAccess.Allowed, result.Access);
        await host.AccessLog.Received(1).LogViewAsync(nameof(Agent), "holder", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_agent_gets_no_card_and_no_trace()
    {
        using var ctx = await SeededAsync(true, Holder("holder"), Holder("other", a => a.BadgeNumber = "V"));
        var host = NewHost(ctx);

        var result = await host.Service.GetAsync("holder", Self("other"));

        Assert.Equal(ServiceIdAccess.Forbidden, result.Access);
        Assert.Null(result.Card);
        await host.AccessLog.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);
    }

    [Fact]
    public async Task The_real_name_never_reaches_the_card_not_even_for_leadership()
    {
        using var ctx = await SeededAsync(true, Holder("holder", a => a.PendingRealName = "Noch Nicht"));
        var service = NewHost(ctx).Service;

        foreach (var viewer in new[] { Self(), Leader() })
        {
            var card = (await service.GetAsync("holder", viewer)).Card!;
            var everything = string.Join('|', card.GetType().GetProperties().Select(p => p.GetValue(card)));
            Assert.Equal("Falke", card.DisplayName);
            Assert.DoesNotContain("Klara", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("Klarname", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("Noch Nicht", everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_terminated_or_numberless_agent_has_no_card_for_leadership_either()
    {
        using var ctx = await SeededAsync(true,
            Holder("gone", a => a.Status = AgentStatus.Terminated),
            Holder("free", a => a.BadgeNumber = "A-12"));
        var service = NewHost(ctx).Service;

        Assert.Equal(ServiceIdAccess.NotIssuable, (await service.GetAsync("gone", Leader())).Access);
        Assert.Equal(ServiceIdAccess.NotIssuable, (await service.GetAsync("free", Leader())).Access);
    }

    [Fact]
    public async Task An_unknown_agent_is_not_found()
    {
        using var ctx = await SeededAsync(true);

        var result = await NewHost(ctx).Service.GetAsync("nobody", Leader());

        Assert.Equal(ServiceIdAccess.NotFound, result.Access);
        Assert.Null(result.Card);
    }
}
