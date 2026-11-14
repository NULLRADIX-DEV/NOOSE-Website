using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Investigations;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Pages.Investigations.Shared;

/// <summary>The plain fields of a file that may carry @-references, and the deep-link marks of its entries.</summary>
public static class InvestigationTexts
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MentionSegment>> None =
        new Dictionary<string, IReadOnlyList<MentionSegment>>();

    /// <summary>Resolves every text holding a token in one call, for this viewer.</summary>
    /// <remarks>
    /// Keyed by the text itself rather than the entry id: after a reload an edited field can never pick up the
    /// segments of its old wording, it simply is not in the map until the next batch. Texts without a token cost
    /// no query at all, which is the common case.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<MentionSegment>>> ResolveAsync(
        IMentionService mentions, ClaimsPrincipal viewer, IEnumerable<string?> texts, CancellationToken cancellationToken = default)
    {
        var withTokens = texts.OfType<string>().Where(t => MentionParser.Parse(t).Count > 0).Distinct(StringComparer.Ordinal).ToList();
        if (withTokens.Count == 0)
        {
            return None;
        }
        // the same scope as every other mention panel; the partner agency keeps unreleased records redacted
        var segments = await mentions.ResolveManyAsync(withTokens.Cast<string?>().ToList(), viewer.MayClassifiedRead(),
            viewer.GetAgentId(), cancellationToken, viewer.GetPartnerAgency());
        var map = new Dictionary<string, IReadOnlyList<MentionSegment>>(StringComparer.Ordinal);
        for (var i = 0; i < withTokens.Count; i++)
        {
            map[withTokens[i]] = segments[i];
        }
        return map;
    }

    /// <summary>All five reference-carrying fields of what this viewer reads of the file, for the print view.</summary>
    public static IEnumerable<string?> All(InvestigationDetail d)
        => new[] { d.Indictment }
            .Concat(d.Notes.Select(n => (string?)n.Text))
            .Concat(d.Interrogations.Select(v => v.Summary))
            .Concat(d.Evidence.Select(e => e.Description))
            .Concat(d.People.Select(p => p.Note));

    /// <summary>The element id a deep link scrolls to; the prefix also keeps a GUID that starts with a digit a valid selector.</summary>
    public static string Anchor(string entryId) => $"eintrag-{entryId}";

    /// <summary>Background of the entry a deep link points at, as on the radio plan.</summary>
    public static string? HighlightStyle(string entryId, string? highlight)
        => highlight is not null && entryId == highlight ? "background:var(--mud-palette-action-default-hover);" : null;

    /// <summary>The tab holding this entry in what the viewer reads, or null when the viewer's file has no such entry.</summary>
    /// <remarks>Only ids already in the filtered detail count, so a hidden, deleted or made-up id leads nowhere and says nothing.</remarks>
    public static string? TabOf(InvestigationDetail d, string? entryId)
    {
        if (string.IsNullOrEmpty(entryId))
        {
            return null;
        }
        if (d.Charges.Any(x => x.Id == entryId))
        {
            return InvestigationTabs.Charges;
        }
        if (d.People.Any(x => x.Id == entryId))
        {
            return InvestigationTabs.People;
        }
        if (d.Evidence.Any(x => x.Id == entryId))
        {
            return InvestigationTabs.Evidence;
        }
        if (d.Interrogations.Any(x => x.Id == entryId))
        {
            return InvestigationTabs.Interrogations;
        }
        if (d.Tasks.Any(x => x.Id == entryId))
        {
            return InvestigationTabs.Tasks;
        }
        return d.Notes.Any(x => x.Id == entryId) ? InvestigationTabs.Notes : null;
    }
}
