using System.Threading.Channels;

namespace NOOSE_Website.Infrastructure.Discord;

/// <summary>One unit of work for the role sync worker.</summary>
public abstract record DiscordRoleSyncJob(string Reason);

/// <summary>Sync one agent from the current row.</summary>
public sealed record AgentJob(string AgentId, string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Sync a Discord id whose account is gone; a team lead is never made a resident.</summary>
public sealed record DeletedJob(string DiscordId, bool TeamLead, string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Sync every agent with a Discord id.</summary>
public sealed record AllJob(string Reason) : DiscordRoleSyncJob(Reason);

/// <summary>Bare reasons; the sync service adds the audit-log prefix.</summary>
public static class DiscordRoleSyncReason
{
    public const string AgentChanged = "Änderung am Agenten";
    public const string AccountDeleted = "Konto gelöscht";
    public const string Scheduled = "Regelabgleich";
    public const string Manual = "von Hand angestoßen";
}

/// <summary>Process-wide hand-off from write paths to the worker; adding never blocks and never calls Discord.</summary>
public interface IDiscordRoleSyncQueue
{
    /// <summary>Queue one agent; the worker reads the row fresh when it gets there.</summary>
    void Enqueue(string agentId);

    /// <summary>Queue a deleted account by its Discord id; the team lead flag is read before the row goes.</summary>
    void EnqueueDeleted(string discordId, bool isTeamLead);

    /// <summary>Queue a run over every agent; ignored while one is still waiting.</summary>
    void EnqueueAll(string reason);

    /// <summary>The worker took the waiting full run; the next one may queue again.</summary>
    void MarkFullRunStarted();

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

    public void EnqueueDeleted(string discordId, bool isTeamLead)
        => Add(new DeletedJob(discordId, isTeamLead, DiscordRoleSyncReason.AccountDeleted));

    private int _fullRunWaiting;

    public void EnqueueAll(string reason)
    {
        // a run every five minutes must not pile up behind a slow one
        if (Interlocked.CompareExchange(ref _fullRunWaiting, 1, 0) == 0)
        {
            Add(new AllJob(reason));
        }
    }

    public void MarkFullRunStarted() => Interlocked.Exchange(ref _fullRunWaiting, 0);

    // unbounded, never full
    private void Add(DiscordRoleSyncJob job) => _channel.Writer.TryWrite(job);
}
