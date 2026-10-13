using System.Security.Claims;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Board / agency broadcasts. Simple entries are open to any active agent; targeted broadcast features (audience, push, acknowledgment) are leadership-only and enforced server-side.</summary>
public interface IAnnouncementService
{
    /// <summary>Announcements visible to the caller (important first, then newest). Leadership sees all.</summary>
    Task<List<AnnouncementRow>> GetBoardAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Announcement detail, or null if the caller may not see it.</summary>
    Task<AnnouncementView?> GetDetailAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deleted announcements (trash, leadership).</summary>
    Task<List<Announcement>> GetTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>Create an announcement; broadcast features are leadership-only. Acknowledgment snapshots the recipients; push notifies them (except the author).</summary>
    Task<Announcement> CreateAsync(AnnouncementInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Edit title/content/important; creator or leadership only. Broadcast settings are fixed.</summary>
    Task RefreshAsync(string id, AnnouncementInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Acknowledge as the caller; sets their acknowledgment timestamp.</summary>
    Task AcknowledgeAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Count of announcements the caller still must acknowledge (for the nav badge).</summary>
    Task<int> GetOpenAcknowledgmentsCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The announcements the caller still must acknowledge, important first, then oldest.</summary>
    Task<List<OpenAcknowledgment>> GetOpenAcknowledgmentsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Announcements past their deadline that the caller manages, with who still owes them; latest deadline first.</summary>
    Task<List<OverdueAcknowledgment>> GetOverdueAcknowledgmentsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Reminds, once per announcement, whoever still owes an acknowledgment past its deadline; returns how many were sent. Worker only.</summary>
    Task<int> RemindOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>Board list/card row for an announcement (public codenames, never real names).</summary>
public sealed class AnnouncementRow
{
    public string Id { get; set; } = string.Empty;
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Important { get; set; }
    public AnnouncementAudience Audience { get; set; }
    /// <summary>Display text of the audience (resolved taskforce name or minimum rank).</summary>
    public string TargetDisplay { get; set; } = string.Empty;
    public bool AsBroadcast { get; set; }
    public bool AcknowledgmentRequired { get; set; }
    /// <summary>Acknowledgment deadline; null = no reminder.</summary>
    public DateTime? AcknowledgmentDueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatorCodename { get; set; }

    /// <summary>The caller has an open acknowledgment for this announcement.</summary>
    public bool MustAcknowledge { get; set; }
    /// <summary>The caller has already acknowledged.</summary>
    public bool AlreadyAcknowledged { get; set; }
    /// <summary>Number of recipients who have acknowledged.</summary>
    public int AcknowledgedCount { get; set; }
    /// <summary>Total number of recipients required to acknowledge.</summary>
    public int TotalCount { get; set; }
    /// <summary>The caller may edit/delete and see the acknowledgment list (creator/leadership).</summary>
    public bool MayManage { get; set; }
}

/// <summary>Announcement detail view: header plus (for managers) the acknowledgment list.</summary>
public sealed class AnnouncementView
{
    public AnnouncementRow Row { get; init; } = default!;
    public IReadOnlyList<AcknowledgmentRow> Acknowledgments { get; init; } = Array.Empty<AcknowledgmentRow>();
}

/// <summary>One acknowledgment-list row (codename + timestamp; null = still open).</summary>
public sealed record AcknowledgmentRow(string Codename, DateTime? AcknowledgedAt);
