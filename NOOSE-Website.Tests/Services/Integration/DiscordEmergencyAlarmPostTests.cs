using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>What leaves the house for an emergency alarm: the JSON Discord receives, whatever the admin toggle says.</summary>
public sealed class DiscordEmergencyAlarmPostTests
{
    private const string Webhook = "https://discord.test/api/webhooks/1/token";
    private const string FirstSnowflake = "111111111111111111";
    private const string SecondSnowflake = "222222222222222222";
    private const string OutsiderSnowflake = "333333333333333333";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Posts.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static async Task<(DiscordWebhookService Service, CapturingHandler Handler)> HostAsync(
        SqliteTestContext ctx, NotificationType type, bool includeHeadline)
    {
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordEnabled, Value = "true" });
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.DiscordIncludeHeadline, Value = includeHeadline ? "true" : "false",
            });
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.DiscordWebhookPrefix + type, Value = Webhook,
            });
            db.Users.Add(Seed.Agent("a1", configure: a => a.DiscordId = FirstSnowflake));
            db.Users.Add(Seed.Agent("a2", configure: a => a.DiscordId = SecondSnowflake));
            db.Users.Add(Seed.Agent("outsider", configure: a => a.DiscordId = OutsiderSnowflake));
            await db.SaveChangesAsync();
        }

        var handler = new CapturingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("discord").Returns(_ => new HttpClient(handler, disposeHandler: false));
        var service = new DiscordWebhookService(factory, ctx.Factory, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordWebhookService>.Instance);
        return (service, handler);
    }

    private static JsonElement Sent(CapturingHandler handler)
        => JsonDocument.Parse(Assert.Single(handler.Posts)).RootElement;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmergencyAlarm_PostCarriesSituation(bool includeHeadline)
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, NotificationType.EmergencyAlarm, includeHeadline);

        await service.PushAsync(NotificationType.EmergencyAlarm, "/einsatzalarme/7", ["a1", "a2"],
            "Einsatzalarm: Geiselnahme in der Maze Bank");

        var body = Sent(handler);
        var content = body.GetProperty("content").GetString()!;
        Assert.Contains("Einsatzalarm: Geiselnahme in der Maze Bank", content);
        Assert.EndsWith("\nhttps://fibhq.com/einsatzalarme/7", content);
        // the mentions are the recipients, and the allow-list admits nobody else
        Assert.Contains($"<@{FirstSnowflake}>", content);
        Assert.Contains($"<@{SecondSnowflake}>", content);
        Assert.DoesNotContain(OutsiderSnowflake, content);
        var mentions = body.GetProperty("allowed_mentions");
        Assert.Empty(mentions.GetProperty("parse").EnumerateArray());
        Assert.False(mentions.TryGetProperty("roles", out _));
        Assert.Equivalent(new[] { FirstSnowflake, SecondSnowflake },
            mentions.GetProperty("users").EnumerateArray().Select(u => u.GetString()).ToArray());
    }

    [Fact]
    public async Task EmergencyAlarm_PostKeepsTheLongestSituationAndLocationUncut()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, NotificationType.EmergencyAlarm, includeHeadline: false);
        var title = "Einsatzalarm: " + new string('L', EmergencyAlarmRules.MaxSituation)
            + " (" + new string('O', EmergencyAlarmRules.MaxLocation) + ")";

        await service.PushAsync(NotificationType.EmergencyAlarm, "/einsatzalarme/7", ["a1"], title);

        var content = Sent(handler).GetProperty("content").GetString()!;
        Assert.Contains(title, content);
        Assert.DoesNotContain('…', content);
    }

    [Fact]
    public async Task EmergencyAlarm_WithoutAHeadlineFallsBackToTheNoticeAndTheAlarmPage()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, NotificationType.EmergencyAlarm, includeHeadline: false);

        await service.PushAsync(NotificationType.EmergencyAlarm, null, ["a1"], headline: null);

        var content = Sent(handler).GetProperty("content").GetString()!;
        Assert.Contains("🚨 Einsatzalarm", content);
        Assert.EndsWith("\nhttps://fibhq.com/einsatzalarme", content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheHeadlineSwitchStillGovernsAnnouncements(bool includeHeadline)
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, NotificationType.Announcement, includeHeadline);

        await service.PushAsync(NotificationType.Announcement, "/brett/1", null, "Titel der Ankündigung");

        var content = Sent(handler).GetProperty("content").GetString()!;
        Assert.Equal(includeHeadline, content.Contains("Titel der Ankündigung"));
    }
}
