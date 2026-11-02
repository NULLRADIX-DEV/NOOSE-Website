using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>What leaves the house when a wanted card is posted: the JSON Discord receives, not the card the app words.</summary>
public sealed class DiscordWantedCardPostTests
{
    private const string Webhook = "https://discord.test/api/webhooks/1/token";
    private const string Case = "NOOSE-FA-2026-0001";
    private static readonly DateTime At = new(2026, 11, 2, 14, 3, 0, DateTimeKind.Utc);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Posts { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Posts.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Throw ? throw new HttpRequestException("tot") : new HttpResponseMessage(Status);
        }
    }

    private static async Task<(DiscordWebhookService Service, CapturingHandler Handler)> HostAsync(
        SqliteTestContext ctx, bool enabled = true, string? webhook = Webhook, string? baseUrl = null,
        NotificationType type = NotificationType.PublicWantedPublished)
    {
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.DiscordEnabled, Value = enabled ? "true" : "false",
            });
            if (webhook is not null)
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = SystemSettingKeys.DiscordWebhookPrefix + type, Value = webhook,
                });
            }
            if (baseUrl is not null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.SiteBaseUrl, Value = baseUrl });
            }
            await db.SaveChangesAsync();
        }

        var handler = new CapturingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("discord").Returns(_ => new HttpClient(handler, disposeHandler: false));
        var service = new DiscordWebhookService(factory, ctx.Factory, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordWebhookService>.Instance);
        return (service, handler);
    }

    private static PublicWantedCard Notice(bool photo = true)
        => new(Case, PublicWantedKind.Fahndung, "Max Mustermann", "Der Schatten", photo, HazardLevel.Critical, At,
            [new PublicWantedHint("bewaffnet", "Error")]);

    private static WantedDiscordEmbed Published(bool photo = true)
        => WantedDiscordCard.Published(Notice(photo), new PublicBounty(250_000m, true));

    private static JsonElement Sent(CapturingHandler handler)
        => JsonDocument.Parse(Assert.Single(handler.Posts).Body).RootElement;

    [Fact]
    public async Task ACard_GoesOutWithTheRolePingAsItsOnlyContent()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);

        var sent = await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published());

        Assert.True(sent);
        var role = DiscordRouting.DefaultRole(NotificationType.PublicWantedPublished);
        var body = Sent(handler);
        // a link in the content would make Discord add a second preview card
        Assert.Equal($"<@&{role}>", body.GetProperty("content").GetString());
        Assert.Equal("NOOSE", body.GetProperty("username").GetString());
        var mentions = body.GetProperty("allowed_mentions");
        Assert.Empty(mentions.GetProperty("parse").EnumerateArray());
        Assert.Equal(role, Assert.Single(mentions.GetProperty("roles").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task TheEmbedLinksAndImageAreAbsoluteOnTheConfiguredSite()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);

        await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published());

        var embed = Assert.Single(Sent(handler).GetProperty("embeds").EnumerateArray());
        Assert.Equal($"https://noose.info/gesucht/{Case}", embed.GetProperty("url").GetString());
        Assert.Equal($"https://noose.info/gesucht/{Case}/foto", embed.GetProperty("image").GetProperty("url").GetString());
        Assert.False(embed.TryGetProperty("thumbnail", out _));
    }

    [Fact]
    public async Task ABaseUrlWithATrailingSlash_StillJoinsCleanly()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, baseUrl: "https://example.test/");

        await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published());

        var embed = Assert.Single(Sent(handler).GetProperty("embeds").EnumerateArray());
        Assert.Equal($"https://example.test/gesucht/{Case}", embed.GetProperty("url").GetString());
    }

    [Fact]
    public async Task TheEmbed_CarriesTitleColourFooterFieldsAndALocalTimestamp()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);

        await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published());

        var embed = Assert.Single(Sent(handler).GetProperty("embeds").EnumerateArray());
        Assert.Equal("🔎 Fahndung: Max Mustermann", embed.GetProperty("title").GetString());
        Assert.Equal(0xE53935, embed.GetProperty("color").GetInt32());
        Assert.Equal("National Office of Security Enforcement", embed.GetProperty("footer").GetProperty("text").GetString());
        var fields = embed.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(["Aktenzeichen", "Gefahrenstufe", "Kopfgeld", "Auch bekannt als", "Warnhinweise"],
            fields.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal("bis 250.000 $", fields[2].GetProperty("value").GetString());
        Assert.True(fields[0].GetProperty("inline").GetBoolean());
        Assert.False(fields[3].GetProperty("inline").GetBoolean());
        // the same shape the termination embed sends, and the same moment as the card
        var stamp = embed.GetProperty("timestamp").GetString()!;
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", stamp);
        Assert.Equal(At, DateTimeOffset.Parse(stamp).UtcDateTime);
    }

    [Fact]
    public async Task ARaiseCard_SendsThePhotoAsAThumbnail()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, type: NotificationType.PublicWantedBountyRaised);
        var card = WantedDiscordCard.BountyRaised(Notice(), new PublicBounty(250_000m, false), At);

        var sent = await service.PushWantedCardAsync(NotificationType.PublicWantedBountyRaised, card);

        Assert.True(sent);
        var embed = Assert.Single(Sent(handler).GetProperty("embeds").EnumerateArray());
        Assert.Equal($"https://noose.info/gesucht/{Case}/foto", embed.GetProperty("thumbnail").GetProperty("url").GetString());
        Assert.False(embed.TryGetProperty("image", out _));
        Assert.Equal(0xC9A227, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task ACardWithoutAPhoto_SendsNeitherImageNorThumbnail()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);

        await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published(photo: false));

        var embed = Assert.Single(Sent(handler).GetProperty("embeds").EnumerateArray());
        Assert.False(embed.TryGetProperty("image", out _));
        Assert.False(embed.TryGetProperty("thumbnail", out _));
    }

    [Fact]
    public async Task WithDiscordSwitchedOff_NothingIsPosted()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, enabled: false);

        Assert.False(await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published()));
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task WithoutAWebhookUrl_NothingIsPosted()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, webhook: null);

        Assert.False(await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published()));
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task ACategoryWithoutAChannel_IsNotPosted()
    {
        // the expiry bell is deliberately not routable: it would post every expiry into the public channel
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx, type: NotificationType.PublicWantedExpired);

        Assert.False(await service.PushWantedCardAsync(NotificationType.PublicWantedExpired, Published()));
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task ARefusedPost_ReportsFalse()
    {
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);
        handler.Status = HttpStatusCode.InternalServerError;

        Assert.False(await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published()));
    }

    [Fact]
    public async Task ADeadWebhook_IsSwallowedAndReportsFalse()
    {
        // a dead webhook must never fail a publication
        using var ctx = new SqliteTestContext();
        var (service, handler) = await HostAsync(ctx);
        handler.Throw = true;

        Assert.False(await service.PushWantedCardAsync(NotificationType.PublicWantedPublished, Published()));
    }
}
