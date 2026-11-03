using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Limits, validity and the computed state of a warrant; pure, so service and pages read the same answer.</summary>
public static class WarrantRules
{
    public const string CaseNumberPrefix = "AN";
    public const int SubjectMaxLength = 1000;
    public const int JustificationMaxLength = 2000;
    public const int NoteMaxLength = 1000;
    public const int MaxLaws = 10;
    public const int DefaultValidityDays = 3;

    // a single citation may not eat the whole snapshot column
    private const int LawLabelMaxLength = 190;

    /// <summary>The validity periods a requester and a signer may choose, in days.</summary>
    public static readonly IReadOnlyList<int> ValidityChoices = [1, 3, 7, 14];

    public static bool IsValidityChoice(int days) => ValidityChoices.Contains(days);

    public static string ValidityLabel(int days) => days == 1 ? "1 Tag" : $"{days} Tage";

    /// <summary>The state a reader sees at <paramref name="nowUtc"/>; a signed warrant without an end date counts as run out.</summary>
    public static WarrantState StateAt(Warrant warrant, DateTime nowUtc) => warrant.Status switch
    {
        WarrantStatus.Requested => WarrantState.Requested,
        WarrantStatus.Rejected => WarrantState.Rejected,
        WarrantStatus.Cancelled => WarrantState.Cancelled,
        WarrantStatus.Signed when warrant.ValidUntil is { } until && until > nowUtc => WarrantState.Valid,
        // unknown stages fail closed: never shown as valid
        _ => WarrantState.Expired,
    };

    /// <summary>Narrows a query to one reader-facing state, with expiry evaluated against <paramref name="nowUtc"/>.</summary>
    public static IQueryable<Warrant> WhereState(this IQueryable<Warrant> query, WarrantState state, DateTime nowUtc) => state switch
    {
        WarrantState.Requested => query.Where(w => w.Status == WarrantStatus.Requested),
        WarrantState.Valid => query.Where(w => w.Status == WarrantStatus.Signed && w.ValidUntil != null && w.ValidUntil > nowUtc),
        WarrantState.Expired => query.Where(w => w.Status == WarrantStatus.Signed && (w.ValidUntil == null || w.ValidUntil <= nowUtc)),
        WarrantState.Rejected => query.Where(w => w.Status == WarrantStatus.Rejected),
        WarrantState.Cancelled => query.Where(w => w.Status == WarrantStatus.Cancelled),
        _ => query.Where(_ => false),
    };

    /// <summary>True once leadership signed it, whether it is still valid, ran out or was cancelled since.</summary>
    public static bool WasSigned(Warrant warrant)
        => (warrant.Status is WarrantStatus.Signed or WarrantStatus.Cancelled) && warrant.ValidFrom is not null;

    /// <summary>The citation of one paragraph as it goes onto the paper.</summary>
    public static string LawLabel(Law law)
    {
        var head = $"{law.Paragraph} {law.LawBook}".Trim();
        var label = string.IsNullOrWhiteSpace(law.Title) ? head : $"{head} – {law.Title.Trim()}";
        return label.Length <= LawLabelMaxLength ? label : string.Concat(label.AsSpan(0, LawLabelMaxLength), "…");
    }

    /// <summary>The stored ids of the cited paragraphs, in citation order.</summary>
    public static IReadOnlyList<string> LawIdsOf(Warrant warrant)
        => warrant.LawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The snapshot lines of the legal basis.</summary>
    public static IReadOnlyList<string> LegalBasisLines(Warrant warrant)
        => warrant.LegalBasis.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
