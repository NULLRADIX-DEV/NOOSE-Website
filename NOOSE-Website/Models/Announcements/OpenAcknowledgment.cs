namespace NOOSE_Website.Models.Announcements;

/// <summary>A board entry the agent still has to acknowledge.</summary>
public sealed record OpenAcknowledgment(string AnnouncementId, string Title, bool Important, DateTime CreatedAt);
