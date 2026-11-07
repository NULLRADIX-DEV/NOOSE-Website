using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Directives;

/// <summary>What leadership fills in to issue a directive; the first version.</summary>
public sealed record DirectiveInput(
    string Title,
    string? Subject,
    DocumentClassification Classification,
    string ContentHtml,
    DateOnly ValidFrom,
    string ChangeNote);

/// <summary>What leadership fills in to replace a directive's wording.</summary>
/// <param name="RequiresAcknowledgment">False for a pure correction: every earlier acknowledgment keeps counting.</param>
/// <param name="ExpectedVersion">The version the editor was opened on; a newer one in the meantime refuses the save.</param>
/// <param name="SecrecyReason">Only required when the new classification loosens the old one.</param>
public sealed record DirectiveVersionInput(
    string Title,
    string? Subject,
    DocumentClassification Classification,
    string ContentHtml,
    DateOnly ValidFrom,
    string ChangeNote,
    bool RequiresAcknowledgment,
    int ExpectedVersion,
    string? SecrecyReason = null);

/// <summary>A directive as listed, with what the reader owes.</summary>
/// <param name="ValidFrom">The "valid from" of the current version; in the future, <paramref name="ValidVersion"/> is the older one or null.</param>
/// <param name="ValidVersion">The newest version that has started today, or null when none has.</param>
/// <param name="AcknowledgeFromVersion">The first version whose acknowledgment still counts.</param>
public sealed record DirectiveRow(
    string Id,
    int Number,
    string Label,
    string Title,
    string? Subject,
    DocumentClassification Classification,
    int CurrentVersion,
    int AcknowledgeFromVersion,
    DateOnly ValidFrom,
    int? ValidVersion,
    bool IsRepealed,
    DateTime? RepealedAt,
    bool MustAcknowledge);

/// <summary>One entry of a directive's history; the wording itself is loaded for the shown version only.</summary>
public sealed record DirectiveVersionView(
    int Version,
    DateOnly ValidFrom,
    string ChangeNote,
    bool RequiresAcknowledgment,
    string? PublishedByName,
    DateTime PublishedAt);

/// <summary>A directive with the shown version's text; named like WarrantView because DirectiveDetail is the page.</summary>
/// <param name="ShownVersion">The version whose text is in <paramref name="ShownHtml"/>.</param>
/// <param name="Versions">Every version, newest first.</param>
/// <param name="MyAcknowledgedVersion">The reader's own highest acknowledged version, or null.</param>
public sealed record DirectiveView(
    DirectiveRow Row,
    int ShownVersion,
    string ShownHtml,
    IReadOnlyList<DirectiveVersionView> Versions,
    bool MustAcknowledge,
    int? MyAcknowledgedVersion,
    DateTime? MyAcknowledgedAt,
    string? RepealedByName,
    string? RepealReason,
    bool MayManage,
    bool MaySeeQuota);

/// <summary>A directive the reader still has to acknowledge.</summary>
/// <param name="Version">The owed minimum version, the one that asked for the acknowledgment.</param>
/// <param name="Since">When exactly that version was published.</param>
public sealed record OpenDirective(string DirectiveId, string Label, string Title, int Version, DateTime Since);

/// <summary>One agent of a directive's audience with the highest version they acknowledged.</summary>
public sealed record DirectiveAudienceRow(string AgentId, string Codename, int? AcknowledgedVersion, DateTime? AcknowledgedAt);

/// <summary>Today's target group of a directive: who has acknowledged a version that counts and who has not.</summary>
public sealed record DirectiveQuota(
    int Total,
    int Acknowledged,
    int AcknowledgeFromVersion,
    IReadOnlyList<DirectiveAudienceRow> Missing,
    IReadOnlyList<DirectiveAudienceRow> Done);

/// <summary>Word comparison of two versions, the older one first.</summary>
public sealed record DirectiveComparison(int FromVersion, int ToVersion, HtmlDiffResult Diff);

/// <summary>A directive offered by the link dialog.</summary>
public sealed record DirectivePick(string Id, string Label, string Title);
