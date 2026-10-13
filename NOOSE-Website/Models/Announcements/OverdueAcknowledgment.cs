namespace NOOSE_Website.Models.Announcements;

/// <summary>An announcement past its acknowledgment deadline and who still owes it.</summary>
/// <param name="Codenames">Recipients still owing it, by codename; never real names.</param>
public sealed record OverdueAcknowledgment(string AnnouncementId, string Title, DateTime DueAt,
    IReadOnlyList<string> Codenames);
