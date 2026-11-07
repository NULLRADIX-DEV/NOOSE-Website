using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Regulation;

/// <summary>What leadership fills in for a new paragraph; its version 1.</summary>
public sealed record RegulationParagraphInput(
    string Heading,
    RegulationAudience Audience,
    string ContentHtml,
    DateOnly ValidFrom,
    string ChangeNote);

/// <summary>What leadership fills in to replace a paragraph's wording, heading or audience.</summary>
/// <param name="RequiresAcknowledgment">False for a pure correction: every earlier acknowledgment keeps counting.</param>
/// <param name="ExpectedVersion">The version the editor was opened on; a newer one in the meantime refuses the save.</param>
public sealed record RegulationVersionInput(
    string Heading,
    RegulationAudience Audience,
    string ContentHtml,
    DateOnly ValidFrom,
    string ChangeNote,
    bool RequiresAcknowledgment,
    int ExpectedVersion);

/// <summary>A paragraph as the regulation page shows it.</summary>
/// <param name="ShownVersion">The owed current version for a reader who owes it, otherwise the valid one (or the current one when none is valid yet).</param>
/// <param name="ShownValidFrom">"Valid from" of the shown version; in the future it is not in force yet.</param>
/// <param name="ValidVersion">The newest version that has started today, or null when none has.</param>
/// <param name="MayEdit">The reader may write this paragraph (claims-based mirror of the service's fine gate).</param>
public sealed record RegulationParagraphRow(
    string Id,
    string SectionId,
    string Heading,
    RegulationAudience Audience,
    int CurrentVersion,
    int AcknowledgeFromVersion,
    int ShownVersion,
    string ShownHtml,
    DateOnly ShownValidFrom,
    DateTime ShownPublishedAt,
    int? ValidVersion,
    bool MustAcknowledge,
    bool IsRepealed,
    DateTime? RepealedAt,
    string? RepealReason,
    bool MayEdit);

/// <summary>A section with its live paragraphs in order.</summary>
/// <param name="Number">Null for the foreword.</param>
public sealed record RegulationSectionView(
    string Id,
    int? Number,
    string Title,
    bool IsPreamble,
    IReadOnlyList<RegulationParagraphRow> Paragraphs)
{
    /// <summary>"Abschnitt 3" or "Vorwort".</summary>
    public string Label => RegulationRules.SectionLabel(Number);

    /// <summary>"Abschnitt 3: Handlungsvorschriften".</summary>
    public string Caption => RegulationRules.SectionCaption(Number, Title);

    public int OpenCount => Paragraphs.Count(p => p.MustAcknowledge);
}

/// <summary>The whole regulation for one reader.</summary>
/// <param name="LastChangedAt">When the newest version was published; the regulation's "Stand".</param>
/// <param name="Repealed">Repealed paragraphs, newest repeal first; readable, owed by nobody.</param>
public sealed record RegulationDocument(
    IReadOnlyList<RegulationSectionView> Sections,
    IReadOnlyList<RegulationParagraphRow> Repealed,
    DateTime? LastChangedAt,
    bool MayManageSections,
    bool MayAddParagraphs)
{
    public int OpenCount => Sections.Sum(s => s.OpenCount);

    /// <summary>The versions the reader sees as open right now; what "Alle gelesen" sends.</summary>
    public IReadOnlyList<RegulationSeen> OpenSeen
        => Sections.SelectMany(s => s.Paragraphs).Where(p => p.MustAcknowledge)
            .Select(p => new RegulationSeen(p.Id, p.ShownVersion)).ToList();
}

/// <summary>One paragraph version the reader had in front of them when acknowledging.</summary>
public sealed record RegulationSeen(string ParagraphId, int Version);

/// <summary>Outcome of "Alle gelesen": how many were acknowledged and how many changed in the meantime.</summary>
public sealed record RegulationAcknowledgeResult(int Acknowledged, int Changed);

/// <summary>One entry of a paragraph's history; the wording itself is loaded for the shown version only.</summary>
public sealed record RegulationVersionView(
    int Version,
    string Heading,
    RegulationAudience Audience,
    DateOnly ValidFrom,
    string ChangeNote,
    bool RequiresAcknowledgment,
    string? PublishedByName,
    DateTime PublishedAt);

/// <summary>A paragraph with the shown version's text and its history.</summary>
/// <param name="Versions">Every version, newest first.</param>
/// <param name="MyAcknowledgedVersion">The reader's own highest acknowledged version, or null.</param>
public sealed record RegulationParagraphView(
    RegulationParagraphRow Row,
    int? SectionNumber,
    string SectionTitle,
    int ShownVersion,
    string ShownHtml,
    IReadOnlyList<RegulationVersionView> Versions,
    bool MustAcknowledge,
    int? MyAcknowledgedVersion,
    DateTime? MyAcknowledgedAt,
    string? RepealedByName,
    bool MaySeeQuota)
{
    public string SectionLabel => RegulationRules.SectionLabel(SectionNumber);

    public string Label => RegulationRules.ParagraphLabel(SectionNumber, Row.Heading);
}

/// <summary>A paragraph the reader still has to acknowledge.</summary>
/// <param name="Version">The owed minimum version, the one that asked for the acknowledgment.</param>
/// <param name="Since">When exactly that version was published.</param>
public sealed record OpenRegulationParagraph(string ParagraphId, string SectionId, string Label, string Heading, int Version, DateTime Since);

/// <summary>One agent of a paragraph's audience with the highest version they acknowledged.</summary>
public sealed record RegulationAudienceRow(string AgentId, string Codename, int? AcknowledgedVersion, DateTime? AcknowledgedAt);

/// <summary>Today's audience of a paragraph: who has acknowledged a version that counts and who has not.</summary>
public sealed record RegulationQuota(
    int Total,
    int Acknowledged,
    int AcknowledgeFromVersion,
    IReadOnlyList<RegulationAudienceRow> Missing,
    IReadOnlyList<RegulationAudienceRow> Done);

/// <summary>Word comparison of two versions, the older one first.</summary>
public sealed record RegulationComparison(int FromVersion, int ToVersion, HtmlDiffResult Diff);

/// <summary>A paragraph offered by the link dialog and the search.</summary>
public sealed record RegulationPick(string Id, string Label, string Heading);

/// <summary>A trashed section or paragraph; never the wording.</summary>
public sealed record RegulationTrashRow(string Id, bool IsSection, string Title, string? Detail, DateTime? DeletedAt,
    RegulationAudience? Audience);
