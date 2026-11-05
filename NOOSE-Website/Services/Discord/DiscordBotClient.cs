using System.Net;
using System.Net.Http.Headers;
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
    private static readonly TimeSpan MaxRetryWaitTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryWait = TimeSpan.FromSeconds(1);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);

    private string? Token => configuration["Discord:BotToken"]?.Trim();

    /// <summary>How long to honour a rate limit; never negative, never beyond ten seconds.</summary>
    public static TimeSpan RetryWait(double seconds)
        => seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryWaitTime.TotalSeconds)) : TimeSpan.Zero;

    public async Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, MemberPath(guildId, userId), null, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(status, body);
        using var json = JsonDocument.Parse(body);
        return ReadRoleIds(json.RootElement);
    }

    public async Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"guilds/{Seg(guildId)}/roles", null, ct);
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

    private async Task<string> GetBotIdAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "users/@me", null, ct);
        EnsureSuccess(status, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? reason,
        CancellationToken ct)
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
        if ((int)status is >= 200 and < 300)
        {
            return;
        }

        throw new DiscordBotException((int)status, $"Discord {(int)status}: {ErrorText(body)}");
    }

    /// <summary>Discord's own "message" field; raw bodies (HTML error pages) are not echoed.</summary>
    private static string ErrorText(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString()!;
                return text.Length > MaxDetailChars ? text[..MaxDetailChars] : text;
            }
        }
        catch (JsonException)
        {
            // no usable text
        }

        return "no details";
    }

    private static IReadOnlyList<string> ReadRoleIds(JsonElement member)
        => member.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

    private static string MemberPath(string guildId, string userId) => $"guilds/{Seg(guildId)}/members/{Seg(userId)}";

    private static string RolePath(string guildId, string userId, string roleId)
        => $"{MemberPath(guildId, userId)}/roles/{Seg(roleId)}";

    private static string Seg(string id) => Uri.EscapeDataString(id);
}
