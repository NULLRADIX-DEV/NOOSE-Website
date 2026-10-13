namespace NOOSE_Website.Models.Taskforces;

/// <summary>Unread messages by others in one of the agent's taskforces.</summary>
public sealed record TaskforceActivity(string TaskforceId, string Name, int Count, DateTime LastAtUtc, string? LastAuthor);
