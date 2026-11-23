using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>/alarm passes the optional dauer on and names the runtime on the confirmation card.</summary>
public sealed class CaptureCommandsAlarmTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 18, 0, 0, DateTimeKind.Utc);

    private static (CaptureCommands Commands, IEmergencyAlarmService Alarms, SqliteTestContext Ctx) Build(int minutes)
    {
        var ctx = new SqliteTestContext();
        var alarms = Substitute.For<IEmergencyAlarmService>();
        alarms.TriggerAsync(Arg.Any<EmergencyAlarmInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns("alarm-1");
        var summary = new EmergencyAlarmSummary("alarm-1", "Lage", null, "Lead", Start, true, 1, 0, 0, null, false);
        alarms.GetDetailAsync("alarm-1", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new EmergencyAlarmDetail(summary, null, null, Start.AddMinutes(minutes), false, null, false, false, false, []));
        var webhook = Substitute.For<IDiscordWebhookService>();
        webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, "https://fib.test",
            new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(), false));
        var records = new BotRecords(Substitute.For<ISearchService>(), webhook, Substitute.For<IAccessLogService>(), ctx.Factory);
        var commands = new CaptureCommands(records, new BotScratch(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), null!, null!, null!, null!, alarms, null!, null!,
            null!, TimeProvider.System);
        return (commands, alarms, ctx);
    }

    private static BotCall Call(params (string Key, string Value)[] options)
        => new()
        {
            Context = new BotContext(300000000000000001, "tester", 100000000000000001, 5),
            Actor = new DiscordActor(Seed.Agent("lead"), ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).Build()),
            Options = new BotOptions(options.ToDictionary(o => o.Key, o => o.Value)),
        };

    [Fact]
    public async Task Dauer_IsPassedOnAndNamedOnTheCard()
    {
        var (commands, alarms, ctx) = Build(45);
        using var _ = ctx;

        var reply = await commands.AlarmAsync(Call(("lage", "Geiselnahme"), ("dauer", "45")));

        await alarms.Received(1).TriggerAsync(
            Arg.Is<EmergencyAlarmInput>(i => i.DurationMinutes == 45 && i.Situation == "Geiselnahme"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        Assert.Contains("45 Minuten", CardText(reply));
    }

    [Fact]
    public async Task WithoutDauer_NoDurationIsSentSoTheDefaultApplies()
    {
        var (commands, alarms, ctx) = Build(30);
        using var _ = ctx;

        var reply = await commands.AlarmAsync(Call(("lage", "Geiselnahme")));

        await alarms.Received(1).TriggerAsync(Arg.Is<EmergencyAlarmInput>(i => i.DurationMinutes == null),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        Assert.Contains("30 Minuten", CardText(reply));
    }

    [Fact]
    public async Task OneMinute_IsWrittenInTheSingular()
    {
        var (commands, _, ctx) = Build(1);
        using var _ = ctx;

        var reply = await commands.AlarmAsync(Call(("lage", "Geiselnahme"), ("dauer", "1")));

        Assert.Contains("1 Minute ", CardText(reply));
    }

    private static string CardText(BotReply reply)
        => string.Concat(reply.Components.OfType<NetCord.Rest.ComponentContainerProperties>()
            .SelectMany(c => c.Components).OfType<NetCord.Rest.TextDisplayProperties>().Select(t => t.Content));
}
