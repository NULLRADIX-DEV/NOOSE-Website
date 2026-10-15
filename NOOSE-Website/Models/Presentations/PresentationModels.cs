using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Presentations;

/// <summary>Title, description and who may see a presentation.</summary>
public sealed class PresentationInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PresentationAccess Access { get; set; }

    /// <summary>Group of a closed presentation.</summary>
    public AnnouncementAudience? Audience { get; set; }
    public string? TargetId { get; set; }
    public Rank? MinRank { get; set; }

    /// <summary>Invited agents of a private presentation; ignored otherwise.</summary>
    public List<string> InviteIds { get; set; } = [];
}

/// <summary>The editable part of one slide.</summary>
public sealed class SlideInput
{
    public SlideLayout Layout { get; set; }
    public string? Title { get; set; }
    public string? BodyHtml { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One presentation in a list.</summary>
public sealed record PresentationListItem(
    string Id,
    string Title,
    string? Description,
    PresentationAccess Access,
    string AccessLabel,
    string OwnerId,
    string OwnerCodename,
    int SlideCount,
    DateTime UpdatedAt);

/// <summary>A presentation with its slides in order, for viewing, presenting and editing.</summary>
/// <param name="MayManage">Owner or leadership with write access: may edit, delete and present.</param>
public sealed record PresentationView(
    Presentation Presentation,
    IReadOnlyList<PresentationSlide> Slides,
    IReadOnlyList<string> InviteIds,
    string OwnerCodename,
    string AccessLabel,
    bool MayManage);
