using System.Net;
using System.Text;
using System.Text.Json;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NOOSE_Website.Infrastructure.Discord.Bot;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>What the bot sender puts on the wire.</summary>
public class DiscordBotSenderTests
{
    private const ulong Channel = 700000000000000001;
    private const ulong Message = 800000000000000002;

    private const string MessageJson = """
        {"id":"800000000000000002","channel_id":"700000000000000001","author":{"id":"1","username":"fib","discriminator":"0"},
         "content":"","timestamp":"2026-11-15T12:00:00.000000+00:00","edited_timestamp":null,"tts":false,
         "mention_everyone":false,"mentions":[],"mention_roles":[],"attachments":[],"embeds":[],"pinned":false,"type":0,"flags":32768}
        """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, JsonElement? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone()));
            return request.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageJson, Encoding.UTF8, "application/json") };
        }
    }

    private static (DiscordBotSender Sender, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        // bot token shape
        var client = new GatewayClient(new BotToken("MTIzNDU2Nzg5MDEyMzQ1Njc4.AAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAA"), new GatewayClientConfiguration
        {
            RestClientConfiguration = new RestClientConfiguration { RequestHandler = new RestRequestHandler(handler) },
        });
        return (new DiscordBotSender(client), handler);
    }

    private static IReadOnlyList<IMessageComponentProperties> Card()
        => [DiscordCards.Card(DiscordCards.Bronze, DiscordCards.Text("Karte"))];

    private static AllowedMentionsProperties Mentions(IEnumerable<ulong> users, IEnumerable<ulong> roles)
        => new() { Everyone = false, AllowedUsers = users.ToList(), AllowedRoles = roles.ToList() };

    // ids as numbers
    private static ulong Id(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? ulong.Parse(element.GetString()!) : element.GetUInt64();

    private static void AssertNobodyIsParsed(JsonElement allowed)
    {
        // parse means everyone
        if (allowed.TryGetProperty("parse", out var parse))
        {
            Assert.Equal(0, parse.GetArrayLength());
        }
    }

    [Fact]
    public async Task A_card_goes_out_as_components_v2_with_an_explicit_allow_list()
    {
        var (sender, handler) = Create();

        var id = await sender.SendAsync(Channel, Card(), Mentions([11, 12], []));

        Assert.Equal(Message, id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith($"/channels/{Channel}/messages", request.Path);
        var body = request.Body!.Value;
        Assert.Equal((int)MessageFlags.IsComponentsV2, body.GetProperty("flags").GetInt32());
        Assert.Equal(1, body.GetProperty("components").GetArrayLength());
        var allowed = body.GetProperty("allowed_mentions");
        Assert.Equal<ulong>([11, 12], allowed.GetProperty("users").EnumerateArray().Select(Id));
        Assert.Equal(0, allowed.GetProperty("roles").GetArrayLength());
        AssertNobodyIsParsed(allowed);
        Assert.False(body.TryGetProperty("message_reference", out _));
        Assert.False(body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString()!.Length > 0);
    }

    [Fact]
    public async Task A_role_ping_allows_that_role_and_no_user()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(Channel, Card(), Mentions([], [900]));

        var allowed = handler.Requests.Single().Body!.Value.GetProperty("allowed_mentions");
        Assert.Equal<ulong>([900], allowed.GetProperty("roles").EnumerateArray().Select(Id));
        Assert.Equal(0, allowed.GetProperty("users").GetArrayLength());
        AssertNobodyIsParsed(allowed);
    }

    [Fact]
    public async Task No_ping_target_allows_nobody()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(Channel, Card(), Mentions([], []));

        var allowed = handler.Requests.Single().Body!.Value.GetProperty("allowed_mentions");
        Assert.Equal(0, allowed.GetProperty("users").GetArrayLength());
        Assert.Equal(0, allowed.GetProperty("roles").GetArrayLength());
        AssertNobodyIsParsed(allowed);
    }

    [Fact]
    public async Task A_reply_points_at_the_card_and_survives_a_card_deleted_by_hand()
    {
        var (sender, handler) = Create();

        await sender.SendTextAsync(Channel, "<@11>", Mentions([11], []), replyTo: Message);

        var body = handler.Requests.Single().Body!.Value;
        Assert.Equal("<@11>", body.GetProperty("content").GetString());
        Assert.False(body.TryGetProperty("flags", out var flags) && flags.GetInt32() != 0);
        var reference = body.GetProperty("message_reference");
        Assert.Equal(Message, Id(reference.GetProperty("message_id")));
        Assert.False(reference.GetProperty("fail_if_not_exists").GetBoolean());
    }

    [Fact]
    public async Task An_edit_replaces_the_card_and_pings_nobody()
    {
        var (sender, handler) = Create();

        await sender.EditAsync(Channel, Message, Card());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.EndsWith($"/channels/{Channel}/messages/{Message}", request.Path);
        var body = request.Body!.Value;
        Assert.Equal((int)MessageFlags.IsComponentsV2, body.GetProperty("flags").GetInt32());
        var allowed = body.GetProperty("allowed_mentions");
        Assert.Equal(0, allowed.GetProperty("users").GetArrayLength());
        Assert.Equal(0, allowed.GetProperty("roles").GetArrayLength());
        AssertNobodyIsParsed(allowed);
    }

    [Fact]
    public async Task A_delete_removes_the_message()
    {
        var (sender, handler) = Create();

        await sender.DeleteAsync(Channel, Message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith($"/channels/{Channel}/messages/{Message}", request.Path);
    }
}
