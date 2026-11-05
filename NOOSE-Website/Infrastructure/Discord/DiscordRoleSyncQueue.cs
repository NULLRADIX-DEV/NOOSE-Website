using System.Threading.Channels;

namespace NOOSE_Website.Infrastructure.Discord;

/// <summary>One unit of work for the role sync worker.</summary>
public abstract record DiscordRoleSyncJob(string Reason);

/// <summary>Sync one agent from the current row.</summary>
public sealed record AgentJob(string AgentId, string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Sync a Discord id whose account is gone.</summary>
public sealed record DeletedJob(string DiscordId, string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Sync every agent with a Discord id.</summary>
public sealed record AllJob(string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Bare reasons; the sync service adds the audit-log prefix.</summary>
public static class DiscordRoleSyncReason
{
    public const string AgentChanged = "Änderung am Agenten";
    public const string AccountDeleted = "Konto gelöscht";
    public const string Daily = "Tagesabgleich";
    public const string Manual = "von Hand angestoßen";
}

/// <summary>Process-wide hand-off from write paths to the worker; adding never blocks and never calls Discord.</summary>
public interface IDiscordRoleSyncQueue
{
    /// <summary>Queue one agent; the worker reads the row fresh when it gets there.</summary>
    void Enqueue(string agentId);

    /// <summary>Queue a deleted account by its Discord id.</summary>
    void EnqueueDeleted(string discordId);

    /// <summary>Queue a run over every agent.</summary>
    void EnqueueAll(string reason);

    ChannelReader<DiscordRoleSyncJob> Reader { get; }
}

/// <inheritdoc cref="IDiscordRoleSyncQueue" />
public sealed class DiscordRoleSyncQueue : IDiscordRoleSyncQueue
{
    private readonly Channel<DiscordRoleSyncJob> _channel =
        Channel.CreateUnbounded<DiscordRoleSyncJob>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<DiscordRoleSyncJob> Reader => _channel.Reader;

    public void Enqueue(string agentId)
        => Add(new AgentJob(agentId, DiscordRoleSyncReason.AgentChanged));

    public void EnqueueDeleted(string discordId)
        => Add(new DeletedJob(discordId, DiscordRoleSyncReason.AccountDeleted));

    public void EnqueueAll(string reason)
        => Add(new AllJob(reason));

    // unbounded, never full
    private void Add(DiscordRoleSyncJob job) => _channel.Writer.TryWrite(job);
}
