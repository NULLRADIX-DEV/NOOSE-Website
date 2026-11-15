using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Resolves and searches @-mention tokens in stored text.</summary>
public interface IMentionService
{
    /// <summary>Resolves mention tokens in text; for a partner viewer pass their agency so unreleased targets are redacted.</summary>
    /// <remarks><paramref name="investigations"/> false leaves investigation references unresolved, for readers that
    /// must never get their titles (FIBI).</remarks>
    Task<IReadOnlyList<MentionSegment>> ResolveAsync(string? text, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, bool investigations = true);

    /// <summary>Batch-resolves mentions; preserves order. For a partner viewer pass their agency so unreleased targets are redacted.</summary>
    Task<IReadOnlyList<IReadOnlyList<MentionSegment>>> ResolveManyAsync(IReadOnlyList<string?> texts, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, bool investigations = true);

    /// <summary>Resolves mention tokens inside stored WYSIWYG HTML and returns the rewritten markup.</summary>
    /// <remarks>Only text nodes are rewritten; <paramref name="plain"/> writes bare text instead of links, for print views.</remarks>
    Task<string> ResolveHtmlAsync(string? html, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, bool plain = false, bool investigations = true);

    /// <summary>Chip labels ("Typ:Id" → display) for the tokens in stored HTML, so the editor can show names
    /// instead of raw tokens. A target the viewer may not see is labelled, never named.</summary>
    Task<IReadOnlyDictionary<string, string>> HtmlLabelsAsync(string? html, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, bool investigations = true);

    /// <summary>Candidates for @-picker autocomplete.</summary>
    /// <remarks>Takes the principal because the record half runs through the global search, whose gates are
    /// principal-shaped. The real-name half still reads <c>MayRealNameSee</c> separately from the classified flag.
    /// A partner only ever gets investigation hits; <paramref name="source"/> drops agents in investigation texts and
    /// investigations in the FIBI chat. <paramref name="contextInvestigationId"/> ranks that file's own hits first.</remarks>
    Task<List<MentionHit>> CandidatesAsync(string? text, ClaimsPrincipal actor, MentionSource source = MentionSource.Default,
        string? contextInvestigationId = null, CancellationToken cancellationToken = default);
}
