using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Presentations;

namespace NOOSE_Website.Services;

/// <summary>Rich-text columns whose base64 images move into files on save; internal carriers only.</summary>
/// <remarks>
/// The delivery endpoint is internal-agent-only, so a public page — press, warnings, FAQ, wanted, faction
/// profile — would show a broken picture; those columns are deliberately missing here and keep their base64.
/// The same list is the allowlist of the read gate: every type named here is answered by
/// <see cref="Visibility.IsRecordVisibleAsync"/>, and anything not named stays invisible rather than open.
/// </remarks>
public static class RichTextImageFields
{
    private static readonly Dictionary<Type, string[]> Fields = new()
    {
        [typeof(Document)] = ["ContentHtml"],
        [typeof(AgentActivity)] = ["ContentHtml"],
        [typeof(Meeting)] = ["MinutesHtml"],
        [typeof(MeetingAgendaItem)] = ["NotesHtml"],
        [typeof(Announcement)] = ["Content"],
        [typeof(AgentNote)] = ["Text"],
        [typeof(PresentationSlide)] = ["BodyHtml"],
    };

    /// <summary>The registered carrier types, for the invariants that hold the table against the model.</summary>
    public static IReadOnlyCollection<Type> Types => Fields.Keys;

    /// <summary>Rich-text property names of that entity; empty when the carrier keeps its images inline.</summary>
    public static IReadOnlyList<string> For(object entity) => For(entity.GetType());

    /// <summary>Rich-text property names of that type; empty when the carrier keeps its images inline.</summary>
    public static IReadOnlyList<string> For(Type type)
        => Fields.TryGetValue(type, out var fields) ? fields : [];

    /// <summary>Whether a carrier type is registered at all.</summary>
    public static bool IsRegistered(string entityType)
        => Fields.Keys.Any(t => t.Name == entityType);
}
