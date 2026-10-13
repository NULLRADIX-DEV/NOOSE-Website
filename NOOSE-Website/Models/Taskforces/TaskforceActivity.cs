namespace NOOSE_Website.Models.Taskforces;

/// <summary>Messages by others in one of the agent's taskforces since a given instant.</summary>
public sealed record TaskforceActivity(string TaskforceId, string Name, int Count, DateTime LastAtUtc, string? LastAuthor);
