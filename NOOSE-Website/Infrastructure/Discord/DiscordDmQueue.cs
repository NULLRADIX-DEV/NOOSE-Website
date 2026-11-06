using System.Threading.Channels;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure.Discord;

/// <summary>One direct message; FallbackType set means an agent (channel fallback), null an applicant (none); FallbackContent is the digest's own channel message.</summary>
public sealed record DiscordDmJob(
    string AgentId, string DiscordId, string Content, NotificationType? FallbackType, string? FallbackHref,
    string? FallbackContent = null);

/// <summary>Process-wide hand-off from write paths to the DM worker; adding never blocks and never calls Discord.</summary>
public interface IDiscordDmQueue
{
    /// <summary>Queue one message; open ones are lost on a restart.</summary>
    void Enqueue(DiscordDmJob job);

    ChannelReader<DiscordDmJob> Reader { get; }
}

/// <inheritdoc cref="IDiscordDmQueue" />
public sealed class DiscordDmQueue : IDiscordDmQueue
{
    private readonly Channel<DiscordDmJob> _channel =
        Channel.CreateUnbounded<DiscordDmJob>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<DiscordDmJob> Reader => _channel.Reader;

    // unbounded, never full
    public void Enqueue(DiscordDmJob job) => _channel.Writer.TryWrite(job);
}
