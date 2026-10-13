namespace NOOSE_Website.Models.Announcements;

/// <summary>A board entry the agent still has to acknowledge.</summary>
/// <param name="DueAt">Acknowledgment deadline; null when the announcement sets none.</param>
public sealed record OpenAcknowledgment(string AnnouncementId, string Title, bool Important, DateTime CreatedAt,
    DateTime? DueAt = null);
