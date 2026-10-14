using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordWebhookService" />
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public class DiscordWebhookService(
    IHttpClientFactory httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    ILogger<DiscordWebhookService> logger) : IDiscordWebhookService
{
    private const string CacheKey = "DiscordWebhookConfig";

    /// <summary>Named client of the threat board; a longer timeout, so a slow post is not mistaken for a lost one.</summary>
    public const string BoardClient = "discord-board";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    // Discord takes 2000 characters and 100 mentions per message
    private const int MaxMessageChars = 2000;
    private const int MaxMessageMentions = 100;

    public async Task PushAsync(NotificationType type, string? href,
        IReadOnlyCollection<string>? recipientAgentIds, string? headline = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!DiscordRouting.IsRoutable(type))
            {
                return;
            }
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled || !config.Webhooks.TryGetValue(type, out var url) || string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            var content = ShouldIncludeHeadline(type, config, headline)
                ? ComposeHeadline(headline!, config.SiteBaseUrl, href)
                : Compose(type, config.SiteBaseUrl, href);
            // a long ping list goes out as several posts, or Discord refuses the whole message
            foreach (var mention in await ResolveMentionsAsync(type, recipientAgentIds, config, content.Length, cancellationToken))
            {
                await SendAsync(url, content, mention, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord webhook push for {Type} failed.", type);
        }
    }

    // rich "EINTRAG" embed to the personnel channel; pings only the subject agent
    public async Task PushPersonnelEntryAsync(string subjectAgentId, string subjectDisplay, string artLabel,
        DateTime entryDate, string reasonPlain, IReadOnlyList<string> executorDisplays,
        string? href, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled
                || !config.Webhooks.TryGetValue(NotificationType.PersonnelEntry, out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var subjectIds = await ResolveDiscordIdsAsync(new[] { subjectAgentId }, cancellationToken);
            var mention = subjectIds.Count > 0 ? MentionSpec.Users(subjectIds) : MentionSpec.None;

            var link = Link(config.SiteBaseUrl, href);
            var executors = executorDisplays.Count > 0 ? string.Join(", ", executorDisplays) : "—";
            var embed = new
            {
                title = "📝 EINTRAG",
                description = link,
                color = 0x00B8D4, // NOOSE cyan
                footer = new { text = "National Office of Security Enforcement" },
                fields = new object[]
                {
                    new { name = "👤 Name", value = Field(subjectDisplay), inline = false },
                    new { name = "🏷️ Art", value = Field(artLabel), inline = true },
                    new { name = "📅 Datum", value = entryDate.ToString("dddd, d. MMMM yyyy 'um' HH:mm"), inline = true },
                    new { name = "📋 Begründung", value = Field(Truncate(reasonPlain, 1024)), inline = false },
                    new { name = "👮 Auszuführender", value = Field(executors), inline = false },
                },
            };
            await SendEmbedAsync(url, mention, embed, cancellationToken);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord personnel-entry embed failed.");
        }
    }

    // golden "EHRUNG" embed to the decoration channel; pings only the honoured agent, names the codename only
    public async Task PushDecorationAsync(string recipientAgentId, string recipientCodename, string decorationName,
        DateTime awardedOn, string citationPlain, string? awardedByName, string? href,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled
                || !config.Webhooks.TryGetValue(NotificationType.DecorationAwarded, out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var recipientIds = await ResolveDiscordIdsAsync(new[] { recipientAgentId }, cancellationToken);
            var mention = recipientIds.Count > 0 ? MentionSpec.Users(recipientIds) : MentionSpec.None;

            var link = Link(config.SiteBaseUrl, href);
            var fields = new List<object>
            {
                new { name = "👤 Agent", value = Field(recipientCodename), inline = true },
                new { name = "🎖️ Ehrenzeichen", value = Field(decorationName), inline = true },
                new { name = "📅 Verliehen am", value = awardedOn.ToString("dddd, d. MMMM yyyy"), inline = false },
            };
            if (!string.IsNullOrWhiteSpace(citationPlain))
            {
                fields.Add(new { name = "📜 Verleihungstext", value = Field(Truncate(citationPlain, 1024)), inline = false });
            }
            fields.Add(new { name = "✍️ Verliehen durch", value = Field(awardedByName ?? "—"), inline = false });
            var embed = new
            {
                title = "🎖️ EHRUNG",
                description = link,
                color = 0xC9A227, // decoration gold
                footer = new { text = "National Office of Security Enforcement" },
                fields = fields.ToArray(),
            };
            await SendEmbedAsync(url, mention, embed, cancellationToken);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord decoration embed failed.");
        }
    }

    // official "KÜNDIGUNG" notice to its own channel; mention chips inside the embed, so nobody is pinged
    public async Task PushTerminationAsync(string subjectAgentId, string subjectDisplay, string? executorAgentId,
        string? executorDisplay, DateTime terminatedAt, string reasonPlain, string? href,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled
                || !config.Webhooks.TryGetValue(NotificationType.AgentTerminated, out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var subjectMention = await ResolveMentionChipAsync(subjectAgentId, cancellationToken);
            var executorMention = await ResolveMentionChipAsync(executorAgentId, cancellationToken);
            var stamp = Local(terminatedAt);
            var link = Link(config.SiteBaseUrl, href);
            var description = new StringBuilder(
                "> Hiermit wird die **Kündigung** des folgenden Mitglieds offiziell bekannt gegeben.");
            if (link is not null)
            {
                description.Append('\n').Append(link);
            }
            var embed = new
            {
                author = new { name = "National Office of Security Enforcement" },
                title = "⚠️ KÜNDIGUNG",
                description = description.ToString(),
                color = 0xE53935, // termination red
                timestamp = stamp.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                footer = new { text = "National Office of Security Enforcement" },
                fields = new object[]
                {
                    new
                    {
                        name = "👤 Name",
                        value = subjectMention is null
                            ? Field($"**{subjectDisplay}**")
                            : $"**{subjectDisplay}** ( {subjectMention} )",
                        inline = false,
                    },
                    new
                    {
                        name = "📅 Datum",
                        value = $"`{stamp.ToString("dddd, d. MMMM yyyy HH:mm")}`",
                        inline = true,
                    },
                    new
                    {
                        name = "👮 Ausgeführt von",
                        value = Field(executorMention ?? executorDisplay),
                        inline = true,
                    },
                    new { name = "📋 Begründung", value = Field(Truncate(reasonPlain, 1024)), inline = false },
                },
            };
            // no ping: MentionSpec.None still carries the empty allow-list, so the chips stay inert
            await SendEmbedAsync(url, MentionSpec.None, embed, cancellationToken);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord termination embed failed.");
        }
    }

    // app-authored broadcast (e.g. the top-agent post): custom content + the category ping + optional link
    public async Task<bool> PushCustomAsync(NotificationType type, string content, string? href = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!DiscordRouting.IsRoutable(type) || string.IsNullOrWhiteSpace(content))
            {
                return false;
            }
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled || !config.Webhooks.TryGetValue(type, out var url) || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            var link = Link(config.SiteBaseUrl, href);
            var body = link is null ? content : $"{content}\n{link}";
            var sent = true;
            foreach (var mention in await ResolveMentionsAsync(type, null, config, body.Length, cancellationToken))
            {
                sent &= await SendAsync(url, body, mention, cancellationToken);
            }
            return sent;
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord custom push for {Type} failed.", type);
            return false;
        }
    }

    // the lines already carry their mentions, so no prefix; the allow-list keeps the ping to exactly these users
    public async Task<bool> PushDigestAsync(string content, IReadOnlyList<string> discordUserIds, string? href = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }
            var config = await GetCachedConfigAsync(cancellationToken);
            if (!config.Enabled
                || !config.Webhooks.TryGetValue(NotificationType.PingDigest, out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            var ids = discordUserIds.Where(IsSnowflake).Distinct().Take(100).ToList();
            var link = Link(config.SiteBaseUrl, href);
            var body = link is null ? content : $"{content}\n{link}";
            return await SendAsync(url, body, MentionSpec.Inline(ids), cancellationToken);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord ping digest failed.");
            return false;
        }
    }

    // wait=true makes Discord answer with the message, whose id the board edits from then on
    public async Task<string?> PostThreatBoardAsync(string webhookUrl, ThreatBoardEmbed embed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                username = "NOOSE",
                embeds = new[] { BoardEmbed(embed) },
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
            var client = httpFactory.CreateClient(BoardClient);
            using var response = await client.PostAsJsonAsync(FactionThreatBoard.PostUrl(webhookUrl), payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Discord threat board post returned {Status}.", (int)response.StatusCode);
                return null;
            }
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } messageId && IsSnowflake(messageId)
                ? messageId
                : null;
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord threat board post failed.");
            return null;
        }
    }

    // an edit carries no username: the message keeps the one it was posted with
    public async Task<ThreatBoardEdit> EditThreatBoardAsync(string webhookUrl, string messageId, ThreatBoardEmbed embed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                embeds = new[] { BoardEmbed(embed) },
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
            var client = httpFactory.CreateClient(BoardClient);
            using var response = await client.PatchAsJsonAsync(
                FactionThreatBoard.EditUrl(webhookUrl, messageId), payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return ThreatBoardEdit.Edited;
            }
            var code = await DiscordErrorCodeAsync(response, cancellationToken);
            logger.LogWarning("Discord threat board edit returned {Status} (code {Code}).", (int)response.StatusCode, code);
            return FactionThreatBoard.RefusedEdit((int)response.StatusCode, code);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord threat board edit failed.");
            return ThreatBoardEdit.Failed;
        }
    }

    public async Task<ThreatBoardRemoval> DeleteThreatBoardAsync(string webhookUrl, string messageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpFactory.CreateClient(BoardClient);
            using var response = await client.DeleteAsync(FactionThreatBoard.EditUrl(webhookUrl, messageId), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return ThreatBoardRemoval.Removed;
            }
            var code = await DiscordErrorCodeAsync(response, cancellationToken);
            logger.LogWarning("Discord threat board delete returned {Status} (code {Code}).", (int)response.StatusCode, code);
            return FactionThreatBoard.RefusedDelete((int)response.StatusCode, code);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Discord threat board delete failed.");
            return ThreatBoardRemoval.Failed;
        }
    }

    private static object BoardEmbed(ThreatBoardEmbed embed) => new
    {
        title = embed.Title,
        description = embed.Description,
        color = embed.Color,
        // Discord renders it in each reader's own time zone
        timestamp = embed.AsOfUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        footer = new { text = embed.Footer },
    };

    // Discord's JSON error carries a numeric code beside the HTTP status
    private static async Task<int?> DiscordErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : null;
        }
        catch
        {
            /* ignore */
            return null;
        }
    }

    // Discord rejects empty field values
    private static string Field(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    // UtcNow-stamped dates render in the server's zone (TZ=Europe/Berlin); anything else is already local
    private static DateTime Local(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;

    // single agent -> "<@snowflake>" for use inside an embed field, or null when unpingable
    private async Task<string?> ResolveMentionChipAsync(string? agentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }
        var ids = await ResolveDiscordIdsAsync(new[] { agentId }, cancellationToken);
        return ids.Count > 0 ? $"<@{ids[0]}>" : null;
    }

    // personal categories ping the recipients, one post per batch; broadcast categories ping the configured role
    private async Task<IReadOnlyList<MentionSpec>> ResolveMentionsAsync(NotificationType type,
        IReadOnlyCollection<string>? recipientAgentIds, DiscordWebhookConfig config, int bodyLength,
        CancellationToken cancellationToken)
    {
        if (DiscordRouting.PingsRole(type))
        {
            var roleId = config.Roles.GetValueOrDefault(type);
            return [IsSnowflake(roleId) ? MentionSpec.Role(roleId!) : MentionSpec.None];
        }
        if (DiscordRouting.PingsRecipients(type) && recipientAgentIds is { Count: > 0 })
        {
            var ids = await ResolveDiscordIdsAsync(recipientAgentIds, cancellationToken);
            if (ids.Count > 0)
            {
                return MentionBatches(ids, bodyLength).Select(batch => MentionSpec.Users(batch)).ToList();
            }
        }
        return [MentionSpec.None];
    }

    /// <summary>Splits a ping list into batches that each fit one message beside a body of the given length.</summary>
    public static List<List<string>> MentionBatches(IReadOnlyList<string> discordIds, int bodyLength)
    {
        // the prefix and the body are joined by one space
        var room = MaxMessageChars - bodyLength - 1;
        var batches = new List<List<string>>();
        var batch = new List<string>();
        var used = 0;
        foreach (var id in discordIds)
        {
            // "<@id>", plus a space after the first one
            var cost = id.Length + 3 + (batch.Count > 0 ? 1 : 0);
            if (batch.Count > 0 && (batch.Count >= MaxMessageMentions || used + cost > room))
            {
                batches.Add(batch);
                batch = new List<string>();
                used = 0;
                cost = id.Length + 3;
            }
            batch.Add(id);
            used += cost;
        }
        if (batch.Count > 0)
        {
            batches.Add(batch);
        }
        return batches;
    }

    // agent id -> Discord snowflake; drops empties and the demo placeholder; MentionBatches keeps each post in bounds
    private async Task<List<string>> ResolveDiscordIdsAsync(IReadOnlyCollection<string> agentIds, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.Users
            .Where(u => agentIds.Contains(u.Id))
            .Select(u => u.DiscordId)
            .ToListAsync(cancellationToken);
        return ids.Where(IsSnowflake).Distinct().ToList()!;
    }

    // only numeric snowflakes are pingable (guards "" and the seeded "demo" user)
    private static bool IsSnowflake(string? value)
        => !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);

    public async Task<DiscordWebhookConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(e => e.Key, e => e.Value, cancellationToken);
        return Build(values);
    }

    public async Task SaveConfigAsync(DiscordWebhookConfigInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        // light validation: URLs must be https when present
        var baseUrl = Trim(input.SiteBaseUrl);
        if (baseUrl is not null && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Die Basis-URL muss mit https:// beginnen.");
        }
        foreach (var type in DiscordRouting.RoutableTypes)
        {
            input.Webhooks.TryGetValue(type, out var raw);
            if (Trim(raw) is { } url && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Der Webhook für „{NotificationTypeDisplay.Name(type)}“ muss mit https:// beginnen.");
            }
        }
        foreach (var type in DiscordRouting.RoleRoutableTypes)
        {
            input.Roles.TryGetValue(type, out var rawRole);
            if (Trim(rawRole) is { } role && !role.All(char.IsAsciiDigit))
            {
                throw new InvalidOperationException($"Die Rollen-ID für „{NotificationTypeDisplay.Name(type)}“ darf nur Ziffern enthalten.");
            }
        }
        if (!FactionThreatBoard.IsValidThreshold(input.ThreatBoardThreshold))
        {
            throw new InvalidOperationException(
                $"Die Schwelle der Gefahrenlage muss zwischen {DiscordWebhookConfig.MinThreatBoardThreshold} und "
                + $"{DiscordWebhookConfig.MaxThreatBoardThreshold} liegen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordEnabled, input.Enabled ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.SiteBaseUrl, baseUrl, cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordIncludeHeadline, input.IncludeHeadline ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordThreatBoardThreshold,
            input.ThreatBoardThreshold.ToString(CultureInfo.InvariantCulture), cancellationToken);
        foreach (var type in DiscordRouting.RoutableTypes)
        {
            input.Webhooks.TryGetValue(type, out var raw);
            await SetAsync(db, SystemSettingKeys.DiscordWebhookPrefix + type, Trim(raw), cancellationToken);
        }
        foreach (var type in DiscordRouting.RoleRoutableTypes)
        {
            input.Roles.TryGetValue(type, out var rawRole);
            await SetAsync(db, SystemSettingKeys.DiscordRolePrefix + type, Trim(rawRole), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);

        cache.Remove(CacheKey);
    }

    public async Task<bool> TestAsync(NotificationType type, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        var config = await GetConfigAsync(cancellationToken);
        if (!config.Webhooks.TryGetValue(type, out var url) || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        return await SendAsync(url,
            $"🔔 Test-Benachrichtigung ({NotificationTypeDisplay.Name(type)}) – die Anbindung funktioniert.",
            MentionSpec.None, cancellationToken);
    }

    private async Task<DiscordWebhookConfig> GetCachedConfigAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out DiscordWebhookConfig? cached) && cached is not null)
        {
            return cached;
        }
        var config = await GetConfigAsync(cancellationToken);
        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    // body stays app-authored and identity-free; the optional prefix pings the target, and allowed_mentions is an explicit allow-list so nothing else (e.g. @everyone) can resolve
    private async Task<bool> SendAsync(string url, string content, MentionSpec mention, CancellationToken cancellationToken)
    {
        var payload = new
        {
            content = mention.Prefix.Length > 0 ? $"{mention.Prefix} {content}" : content,
            username = "NOOSE",
            allowed_mentions = mention.AllowedMentions,
        };
        var client = httpFactory.CreateClient("discord");
        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord webhook returned {Status}.", (int)response.StatusCode);
        }
        return response.IsSuccessStatusCode;
    }

    // embed payload with an optional ping prefix; allowed_mentions stays an explicit allow-list
    private async Task<bool> SendEmbedAsync(string url, MentionSpec mention, object embed, CancellationToken cancellationToken)
    {
        var payload = new
        {
            content = mention.Prefix.Length > 0 ? mention.Prefix : (string?)null,
            username = "NOOSE",
            embeds = new[] { embed },
            allowed_mentions = mention.AllowedMentions,
        };
        var client = httpFactory.CreateClient("discord");
        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord webhook (embed) returned {Status}.", (int)response.StatusCode);
        }
        return response.IsSuccessStatusCode;
    }

    // generic per-category notice + login-gated link; NEVER the in-app title (which may carry names/notes/VS content)
    private static string Compose(NotificationType type, string baseUrl, string? href)
    {
        var sb = new StringBuilder(Notice(type));
        // always deep-link to the relevant record; fall back to the category's landing page
        var link = Link(baseUrl, href) ?? Link(baseUrl, FallbackRoute(type));
        if (link is not null)
        {
            sb.Append('\n').Append(link);
        }
        return sb.ToString();
    }

    // categories whose role-ping post may carry the record header/title (opt-in, admin-toggleable)
    private static bool ShouldIncludeHeadline(NotificationType type, DiscordWebhookConfig config, string? headline)
        => config.IncludeHeadline && !string.IsNullOrWhiteSpace(headline)
            && type is NotificationType.Announcement or NotificationType.Recruiting
                or NotificationType.AppointmentScheduled;

    // app-authored header + login-gated link; single-line, capped, mentions stay inert via allowed_mentions
    private static string ComposeHeadline(string headline, string baseUrl, string? href)
    {
        var sb = new StringBuilder(Truncate(headline.Replace('\r', ' ').Replace('\n', ' ').Trim(), 300));
        var link = Link(baseUrl, href);
        if (link is not null)
        {
            sb.Append('\n').Append(link);
        }
        return sb.ToString();
    }

    // category landing page used when a notification carries no record link
    private static string FallbackRoute(NotificationType type) => type switch
    {
        NotificationType.Announcement => "/brett",
        NotificationType.SituationReport => "/lageberichte",
        NotificationType.Recruiting => "/bewerbungen",
        NotificationType.JobAssigned => "/aufgaben",
        NotificationType.JobDueSoon => "/aufgaben",
        NotificationType.MeetingScheduled => "/besprechungen",
        NotificationType.MeetingReminder => "/besprechungen",
        NotificationType.AppointmentScheduled => "/kalender",
        NotificationType.PersonnelEntry => "/personal",
        NotificationType.AgentTerminated => "/personal",
        NotificationType.DecorationAwarded => "/personal",
        // the one outward category: its fallback must stay outside, or a generic push would post an internal
        // destination into the public channel
        NotificationType.PublicWantedPublished => "/gesucht",
        NotificationType.PublicPressPublished => "/presse",
        // the internal desk, not the citizen portal: the ping goes to leadership
        NotificationType.PublicTicketCreated => "/tickets",
        NotificationType.PublicCaptureReported => "/hinweise",
        NotificationType.PublicTipCreated => "/hinweise",
        NotificationType.PingDigest => "/benachrichtigungen",
        NotificationType.AcknowledgmentReminder => "/brett",
        NotificationType.SavedSearchHit => "/suche",
        NotificationType.InactivityNudge => "/abmeldungen",
        NotificationType.FactionThreatBoard => "/fraktionen",
        _ => "/dashboard",
    };

    private static string Notice(NotificationType type) => type switch
    {
        NotificationType.Announcement => "📢 Neue Ankündigung im Schwarzen Brett.",
        NotificationType.Followup => "⏰ Eine Wiedervorlage ist fällig.",
        NotificationType.SituationReport => "📊 Ein neuer Lagebericht liegt vor.",
        NotificationType.Recruiting => "📝 Neue Aktivität im Bewerbungswesen.",
        NotificationType.Mention => "💬 Es gibt eine neue Erwähnung in einer Akte.",
        NotificationType.JobAssigned => "📌 Dir wurde eine Aufgabe zugewiesen.",
        NotificationType.JobDueSoon => "⏰ Eine Aufgabe wird bald fällig.",
        NotificationType.MeetingScheduled => "📅 Eine neue Besprechung wurde angesetzt.",
        NotificationType.MeetingReminder => "⏰ Eine Besprechung beginnt bald.",
        NotificationType.AppointmentScheduled => "📅 Ein neuer Termin wurde angelegt.",
        NotificationType.PersonnelEntry => "📝 Neuer Personalakten-Eintrag.",
        NotificationType.AgentTerminated => "⚠️ Eine Kündigung wurde ausgesprochen.",
        NotificationType.DecorationAwarded => "🎖️ Ein Ehrenzeichen wurde verliehen.",
        NotificationType.PublicWantedPublished => "🔎 Neue öffentliche Fahndung.",
        NotificationType.PublicPressPublished => "📰 Neue Pressemitteilung.",
        NotificationType.PublicTicketCreated => "📨 Ein neues Ticket liegt im Eingang.",
        NotificationType.PublicCaptureReported => "🚨 Ein Bürger meldet die Ergreifung einer gesuchten Person.",
        NotificationType.PublicTipCreated => "💡 Ein neuer Bürgerhinweis liegt im Eingang.",
        NotificationType.PingDigest => "🗞️ Dein Tagesüberblick liegt bereit.",
        NotificationType.AcknowledgmentReminder => "📋 Eine Ankündigung wartet noch auf deine Quittierung.",
        NotificationType.SavedSearchHit => "🔍 Eine deiner gespeicherten Suchen hat neue Treffer.",
        NotificationType.InactivityNudge => "👋 Die Führung bittet dich um eine kurze Rückmeldung.",
        NotificationType.FactionThreatBoard => "🚨 Gefahrenlage der Fraktionen.",
        _ => "🔔 Neue Benachrichtigung.",
    };

    // absolute href as-is; relative href joined onto the configured base
    private static string? Link(string baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }
        return $"{baseUrl.TrimEnd('/')}/{href.TrimStart('/')}";
    }

    private static DiscordWebhookConfig Build(IReadOnlyDictionary<string, string?> values)
    {
        var enabled = string.Equals(values.GetValueOrDefault(SystemSettingKeys.DiscordEnabled), "true", StringComparison.OrdinalIgnoreCase);
        var baseUrl = Trim(values.GetValueOrDefault(SystemSettingKeys.SiteBaseUrl)) ?? DiscordWebhookConfig.DefaultBaseUrl;
        var map = new Dictionary<NotificationType, string?>();
        foreach (var type in DiscordRouting.RoutableTypes)
        {
            map[type] = Trim(values.GetValueOrDefault(SystemSettingKeys.DiscordWebhookPrefix + type));
        }
        var roles = new Dictionary<NotificationType, string?>();
        foreach (var type in DiscordRouting.RoleRoutableTypes)
        {
            // unset falls back to the category default (same pattern as SiteBaseUrl)
            roles[type] = Trim(values.GetValueOrDefault(SystemSettingKeys.DiscordRolePrefix + type))
                ?? DiscordRouting.DefaultRole(type);
        }
        // default on: only an explicit "false" suppresses headers
        var includeHeadline = !string.Equals(values.GetValueOrDefault(SystemSettingKeys.DiscordIncludeHeadline), "false", StringComparison.OrdinalIgnoreCase);
        var threshold = FactionThreatBoard.ParseThreshold(values.GetValueOrDefault(SystemSettingKeys.DiscordThreatBoardThreshold));
        return new DiscordWebhookConfig(enabled, baseUrl, map, roles, includeHeadline, threshold);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }

    // ping prefix + matching allow-list; allow-lists (not parse) keep the ping scoped to exactly these targets
    private readonly record struct MentionSpec(string Prefix, object AllowedMentions)
    {
        private static readonly object SuppressAll = new { parse = Array.Empty<string>() };

        public static MentionSpec None => new(string.Empty, SuppressAll);

        public static MentionSpec Role(string roleId)
            => new($"<@&{roleId}>", new { parse = Array.Empty<string>(), roles = new[] { roleId } });

        public static MentionSpec Users(IReadOnlyList<string> discordIds)
            => new(string.Join(' ', discordIds.Select(id => $"<@{id}>")),
                new { parse = Array.Empty<string>(), users = discordIds });

        // mentions written into the body itself; only the allow-list is attached
        public static MentionSpec Inline(IReadOnlyList<string> discordIds)
            => new(string.Empty, new { parse = Array.Empty<string>(), users = discordIds });
    }
}
