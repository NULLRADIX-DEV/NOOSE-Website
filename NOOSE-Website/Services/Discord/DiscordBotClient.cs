using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordBotClient" />
public sealed class DiscordBotClient(IHttpClientFactory httpFactory, IConfiguration configuration) : IDiscordBotClient
{
    /// <summary>Named client; its logging is off so the Authorization header never reaches a log.</summary>
    public const string ClientName = "discord-bot";

    private const string ApiBase = "https://discord.com/api/v10/";
    private const string ReasonHeader = "X-Audit-Log-Reason";
    private const int MaxAttempts = 3;
    private const int MaxDetailChars = 200;
    private const int MaxSnowflakeChars = 20;
    private const int MaxMessageChars = 2000;
    private const int UnknownChannelCode = 10003;
    private const int UnknownMemberCode = 10007;
    private const int UnknownUserCode = 10013;
    private static readonly TimeSpan MaxRetryWaitTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryWait = TimeSpan.FromSeconds(1);

    // static: the client is scoped, the channels outlive it
    private static readonly ConcurrentDictionary<string, string> DmChannels = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);

    private string? Token => configuration["Discord:BotToken"]?.Trim();

    /// <summary>How long to honour a rate limit; never negative, never beyond ten seconds.</summary>
    public static TimeSpan RetryWait(double seconds)
        => seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryWaitTime.TotalSeconds)) : TimeSpan.Zero;

    public async Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, MemberPath(guildId, userId), null, ct);
        // only Discord's own "no such member"; any other 404 (an unknown server above all) is an error, not an absent member
        if (status == HttpStatusCode.NotFound && ErrorDetails(body).Code is UnknownMemberCode or UnknownUserCode)
        {
            return null;
        }

        EnsureSuccess(status, body);
        using var json = JsonDocument.Parse(body);
        return ReadRoleIds(json.RootElement);
    }

    public async Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"{GuildPath(guildId)}/roles", null, ct);
        EnsureSuccess(status, body);
        using var json = JsonDocument.Parse(body);
        var roles = json.RootElement.EnumerateArray()
            .Select(r => new DiscordGuildRole(
                r.GetProperty("id").GetString()!,
                r.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                r.TryGetProperty("position", out var position) ? position.GetInt32() : 0,
                r.TryGetProperty("managed", out var managed) && managed.ValueKind == JsonValueKind.True))
            .ToList();

        var botId = await GetBotIdAsync(ct);
        var botRoles = await GetMemberRolesAsync(guildId, botId, ct) ?? [];
        var top = roles.Where(r => botRoles.Contains(r.Id)).Select(r => r.Position).DefaultIfEmpty(0).Max();
        return new DiscordGuildRoles(roles, top);
    }

    public async Task AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Put, RolePath(guildId, userId, roleId), reason, ct);
        EnsureSuccess(status, body);
    }

    public async Task RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Delete, RolePath(guildId, userId, roleId), reason, ct);
        EnsureSuccess(status, body);
    }

    public async Task SendDirectMessageAsync(string userId, string content, CancellationToken ct)
    {
        var user = Seg(userId, nameof(userId));
        var message = JsonSerializer.Serialize(new
        {
            content = Cut(content),
            allowed_mentions = new { parse = Array.Empty<string>() },
        });

        // a cached channel may have gone stale; evict it and reopen once
        var cached = DmChannels.TryGetValue(user, out var channelId);
        if (!cached)
        {
            channelId = await OpenDmChannelAsync(user, ct);
            DmChannels[user] = channelId;
        }

        var (status, body) = await SendAsync(HttpMethod.Post, $"channels/{channelId}/messages", null, ct, message);
        if (IsStaleChannel(status, body))
        {
            DmChannels.TryRemove(user, out _);
            if (cached)
            {
                channelId = await OpenDmChannelAsync(user, ct);
                DmChannels[user] = channelId;
                (status, body) = await SendAsync(HttpMethod.Post, $"channels/{channelId}/messages", null, ct, message);
                if (IsStaleChannel(status, body))
                {
                    DmChannels.TryRemove(user, out _);
                }
            }
        }

        EnsureSuccess(status, body);
    }

    /// <summary>Unknown channel, or the bot lost access; 50007 is the user's own setting and leaves the channel fine.</summary>
    private static bool IsStaleChannel(HttpStatusCode status, string body)
    {
        if (IsSuccess(status))
        {
            return false;
        }

        var (_, code) = ErrorDetails(body);
        return status == HttpStatusCode.NotFound || code == UnknownChannelCode
            || (status == HttpStatusCode.Forbidden && code != DiscordBotException.CannotSendToUser);
    }

    private async Task<string> OpenDmChannelAsync(string userId, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "users/@me/channels", null, ct,
            JsonSerializer.Serialize(new { recipient_id = userId }));
        EnsureSuccess(status, body);
        return ReadChannelId(body)
            ?? throw new DiscordBotException((int)status, $"Discord {(int)status}: no usable direct channel");
    }

    private async Task<string> GetBotIdAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "users/@me", null, ct);
        EnsureSuccess(status, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? reason,
        CancellationToken ct, string? jsonBody = null)
    {
        var token = Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Discord bot token is not configured.");
        }

        var http = httpFactory.CreateClient(ClientName);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                // header values must be ASCII
                request.Headers.TryAddWithoutValidation(ReasonHeader, Uri.EscapeDataString(reason));
            }

            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxAttempts)
            {
                return (response.StatusCode, body);
            }

            await Task.Delay(RetryWait(RetryAfterSeconds(response, body)), ct);
        }
    }

    private static double RetryAfterSeconds(HttpResponseMessage response, string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("retry_after", out var after) && after.TryGetDouble(out var seconds))
            {
                return seconds;
            }
        }
        catch (JsonException)
        {
            // fall back to the header
        }

        return (response.Headers.RetryAfter?.Delta ?? DefaultRetryWait).TotalSeconds;
    }

    private static void EnsureSuccess(HttpStatusCode status, string body)
    {
        if (IsSuccess(status))
        {
            return;
        }

        var (text, code) = ErrorDetails(body);
        throw new DiscordBotException((int)status, $"Discord {(int)status}: {text}", code);
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>Discord's own "message" and "code" fields; raw bodies (HTML error pages) are not echoed.</summary>
    private static (string Text, int? Code) ErrorDetails(string body)
    {
        var text = "no details";
        int? code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    text = message.GetString()!;
                    text = text.Length > MaxDetailChars ? text[..MaxDetailChars] : text;
                }

                if (json.RootElement.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number
                    && number.TryGetInt32(out var value))
                {
                    code = value;
                }
            }
        }
        catch (JsonException)
        {
            // no usable details
        }

        return (text, code);
    }

    private static string? ReadChannelId(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } value && IsSnowflake(value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cut(string content)
    {
        if (content.Length <= MaxMessageChars)
        {
            return content;
        }

        // keep surrogate pairs whole
        return content[..(char.IsHighSurrogate(content[MaxMessageChars - 1]) ? MaxMessageChars - 1 : MaxMessageChars)];
    }

    private static IReadOnlyList<string> ReadRoleIds(JsonElement member)
        => member.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

    private static string GuildPath(string guildId) => $"guilds/{Seg(guildId, nameof(guildId))}";

    private static string MemberPath(string guildId, string userId)
        => $"{GuildPath(guildId)}/members/{Seg(userId, nameof(userId))}";

    private static string RolePath(string guildId, string userId, string roleId)
        => $"{MemberPath(guildId, userId)}/roles/{Seg(roleId, nameof(roleId))}";

    /// <summary>Only snowflakes become a path segment: "." and ".." would otherwise be collapsed by <see cref="Uri"/>.</summary>
    private static string Seg(string id, string name)
    {
        if (!IsSnowflake(id))
        {
            throw new ArgumentException("Not a Discord id.", name);
        }

        return Uri.EscapeDataString(id);
    }

    private static bool IsSnowflake(string id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxSnowflakeChars && id.All(char.IsAsciiDigit);
}
