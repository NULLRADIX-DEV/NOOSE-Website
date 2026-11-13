using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Turns a Discord user into a FIB agent with fresh rights, or a neutral refusal.</summary>
public interface IDiscordActorResolver
{
    Task<(DiscordActor? Actor, BotRefusal? Refusal)> ResolveAsync(BotContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiscordActorResolver" />
public sealed class DiscordActorResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    IAgentPrincipalService principals,
    IDiscordRoleSyncService roleSync) : IDiscordActorResolver
{
    public const string NotLinked = "Dein Discord-Konto ist mit keinem FIB-Agenten verknüpft. Melde dich einmal auf fibhq.com mit Discord an.";
    public const string NotAllowed = "Der FIB-Bot steht nur aktiven FIB-Agenten zur Verfügung.";
    public const string WrongServer = "Der FIB-Bot arbeitet nur auf dem FIB-Server und in der Direktnachricht.";

    public async Task<(DiscordActor? Actor, BotRefusal? Refusal)> ResolveAsync(BotContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.GuildId is { } guildId)
        {
            // no configured server means no server at all: anyone could invite the bot elsewhere
            var configured = (await roleSync.GetSettingsAsync(cancellationToken)).GuildId?.Trim();
            if (string.IsNullOrEmpty(configured)
                || !string.Equals(configured, guildId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return (null, new BotRefusal(WrongServer));
            }
        }

        var discordId = context.UserId.ToString(CultureInfo.InvariantCulture);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.DiscordId == discordId, cancellationToken);
        if (agent is null)
        {
            return (null, new BotRefusal(NotLinked));
        }
        // team leads and partners stay invisible, demo never acts
        if (agent.Id == DemoIdentity.AgentId || !AgentSelection.IsSelectable(agent))
        {
            return (null, new BotRefusal(NotAllowed));
        }

        var principal = await principals.BuildAsync(agent.Id, cancellationToken);
        if (principal is null || !principal.IsInternalAgent() || !principal.MayWrite())
        {
            return (null, new BotRefusal(NotAllowed));
        }
        return (new DiscordActor(agent, principal), null);
    }
}

/// <summary>Per Discord user, at most <see cref="PerMinute"/> interactions in a sliding minute; in memory, per host.</summary>
public sealed class DiscordBotRateLimiter(TimeProvider clock)
{
    public const int PerMinute = 20;
    private readonly ConcurrentDictionary<ulong, Queue<DateTimeOffset>> _hits = new();

    public bool TryEnter(ulong userId)
    {
        var now = clock.GetUtcNow();
        var queue = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }
            if (queue.Count >= PerMinute)
            {
                return false;
            }
            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Live state of the gateway for the settings page; process-wide.</summary>
public sealed class DiscordBotStatus
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public DateTime? ReadyAt { get; set; }
    public string? BotName { get; set; }
    public ulong? ApplicationId { get; set; }
    public int GuildCount { get; set; }
    public int RegisteredCommands { get; set; }
    public DateTime? RegisteredAt { get; set; }
    public string? LastError { get; set; }
}
