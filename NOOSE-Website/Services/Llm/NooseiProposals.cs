using System.Text.Json;
using System.Text.Json.Serialization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>What a NOOSEI proposal would write.</summary>
public enum NooseiProposalKind
{
    Followup,
    Tag,
    Job,
    Watch,
    Link,
}

/// <summary>Where a proposal stands; only the agent's click moves it.</summary>
public enum NooseiProposalState
{
    Open,
    Executed,
    Dismissed,
}

/// <summary>One line of a proposal card, already in German.</summary>
public sealed record NooseiProposalLine(string Label, string Value);

/// <summary>What the click writes, as resolved when NOOSEI proposed it. Checked again on every click.</summary>
public sealed record NooseiProposalArgs
{
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }

    /// <summary>Link target, or the reference record of a job.</summary>
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }

    public DateOnly? Due { get; init; }

    /// <summary>Followup note, job description or link label.</summary>
    public string? Text { get; init; }

    public string? Title { get; init; }
    public IReadOnlyList<string> AgentIds { get; init; } = [];
    public IReadOnlyList<string> TagIds { get; init; } = [];
    public JobPriority Priority { get; init; } = JobPriority.Normal;
    public bool Restricted { get; init; }
}

/// <summary>A write NOOSEI proposed. Nothing happens until the agent executes it with their own rights.</summary>
public sealed record NooseiProposal(
    string Id, NooseiProposalKind Kind, string Title, IReadOnlyList<NooseiProposalLine> Lines, NooseiProposalArgs Args)
{
    public NooseiProposalState State { get; init; } = NooseiProposalState.Open;
    public DateTime? DecidedAt { get; init; }

    /// <summary>Where the written result lives, once executed.</summary>
    public string? ResultHref { get; init; }

    /// <summary>What went only partly right, shown on the card.</summary>
    public string? Warning { get; init; }

    [JsonIgnore]
    public bool IsOpen => State == NooseiProposalState.Open;
}

/// <summary>What a proposal tool hands the service: the model's arguments, parsed but not yet trusted.</summary>
public sealed record NooseiProposalDraft(NooseiProposalKind Kind)
{
    /// <summary>German record type name, as the schema enum offers it.</summary>
    public string? Type { get; init; }
    public string? Id { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }

    /// <summary>yyyy-MM-dd; the model has no clock, so <see cref="InDays"/> is the usual way.</summary>
    public string? DueDate { get; init; }
    public int? InDays { get; init; }

    public string? Text { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Priority { get; init; }
    public bool Restricted { get; init; }
}

/// <summary>Stored form of the proposals under one answer.</summary>
public static class NooseiProposalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string? Serialize(IReadOnlyList<NooseiProposal> proposals)
        => proposals.Count == 0 ? null : JsonSerializer.Serialize(proposals, Options);

    /// <summary>A corrupted row renders without cards rather than failing the conversation.</summary>
    public static IReadOnlyList<NooseiProposal> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<NooseiProposal>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Collects one turn's proposals by tool call id; the calls of a round run concurrently.</summary>
/// <remarks>Ids repeat across rounds (the transport numbers from zero), hence a queue per id.</remarks>
public sealed class NooseiProposalTray
{
    /// <summary>Most cards under one answer.</summary>
    public const int Max = 5;

    private readonly Dictionary<string, Queue<NooseiProposal>> _byCall = new(StringComparer.Ordinal);
    private int _taken;

    public bool TryReserve()
    {
        lock (_byCall)
        {
            if (_taken >= Max)
            {
                return false;
            }
            _taken++;
            return true;
        }
    }

    public void Release()
    {
        lock (_byCall)
        {
            _taken--;
        }
    }

    public void Fill(string callId, NooseiProposal proposal)
    {
        lock (_byCall)
        {
            if (!_byCall.TryGetValue(callId, out var queue))
            {
                _byCall[callId] = queue = new Queue<NooseiProposal>();
            }
            queue.Enqueue(proposal);
        }
    }

    /// <summary>The proposals of the given tool rows, in that order; a call the gateway gave up on has none.</summary>
    public IReadOnlyList<NooseiProposal> For(IEnumerable<string> callIds)
    {
        lock (_byCall)
        {
            var left = _byCall.ToDictionary(e => e.Key, e => new Queue<NooseiProposal>(e.Value), StringComparer.Ordinal);
            var result = new List<NooseiProposal>();
            foreach (var id in callIds)
            {
                if (left.TryGetValue(id, out var queue) && queue.Count > 0)
                {
                    result.Add(queue.Dequeue());
                }
            }
            return result;
        }
    }
}
