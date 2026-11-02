using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Models.Public;

/// <summary>One card on the public board.</summary>
/// <remarks>
/// Structurally carries no record id, no agent identity and no numeric score: what is not on the type cannot be
/// rendered by accident. <c>PublicWantedModelTests</c> holds that shape.
/// </remarks>
public sealed record PublicWantedCard(
    string CaseNumber,
    PublicWantedKind Kind,
    string DisplayName,
    string? AliasText,
    bool HasPhoto,
    HazardLevel HazardLevel,
    DateTime? PublishedAt,
    IReadOnlyList<PublicWantedHint> Hints);

/// <summary>A public wanted profile.</summary>
public sealed record PublicWantedDetail(
    string CaseNumber,
    PublicWantedKind Kind,
    string DisplayName,
    string? AliasText,
    bool HasPhoto,
    HazardLevel HazardLevel,
    DateTime? PublishedAt,
    string? ChargeHtml,
    string? LastArea,
    string? VehicleText,
    DateTime? ExpiresAt,
    IReadOnlyList<PublicWantedHint> Hints);

/// <summary>One warning chip: a label and an allowlisted colour name, nothing else.</summary>
public sealed record PublicWantedHint(string Label, string Colour);

/// <summary>What the outside learns about the money on a head: one number, and whether it is a ceiling.</summary>
/// <remarks>
/// Structurally carries no origin, donor, account, booking or share count. A breakdown would be a public register of
/// which agent staked how much of their own money on whom — the reason <c>PublicVisibility</c> publishes the sum and
/// nothing else.
/// </remarks>
public sealed record PublicBounty(decimal Total, bool IsCap);

/// <summary>One row of the anonymous archive: the fact that someone was caught, not a profile.</summary>
/// <remarks>
/// Its own type rather than nullable fields on <see cref="PublicWantedCard"/>. The archive must carry no hazard level,
/// no accusation, no area and no vehicle, and a shared record would let the board render "gefasst am" and the archive
/// render a hazard level. The case number stays because the photo endpoint is addressed through it — the card itself
/// links nowhere, since /gesucht/{az} answers "not found" for a captured notice.
/// </remarks>
public sealed record PublicWantedArchiveCard(
    string CaseNumber,
    PublicWantedKind Kind,
    string DisplayName,
    bool HasPhoto,
    DateTime CapturedAt);

/// <summary>Everything the outside world may read, in one cached snapshot.</summary>
/// <remarks>
/// Board and archive share one record and therefore one cache key. They come from the same table and are invalidated
/// by exactly the same writes; a second key would double every drop site and create a failure class where one is
/// dropped and the other left standing — and marking a notice as captured moves a row between both lists at once.
/// </remarks>
/// <param name="CapturedTotals">How many captures there are per kind, behind the same belt but without the display
/// cap the <see cref="Archive"/> list carries.</param>
public sealed record PublicWantedBoard(
    IReadOnlyList<PublicWantedCard> Cards,
    IReadOnlyDictionary<string, PublicWantedDetail> ByCaseNumber,
    IReadOnlyList<PublicWantedArchiveCard> Archive,
    IReadOnlyDictionary<string, PublicWantedArchiveCard> CapturedByCaseNumber,
    IReadOnlyDictionary<string, PublicBounty> BountyByCaseNumber,
    IReadOnlyDictionary<PublicWantedKind, int> CapturedTotals,
    IReadOnlyDictionary<string, string>? SearchText = null)
{
    public static IReadOnlyDictionary<string, PublicBounty> NoBounties { get; } =
        new Dictionary<string, PublicBounty>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<PublicWantedKind, int> NoCaptures { get; } =
        new Dictionary<PublicWantedKind, int>();

    public static PublicWantedBoard Empty { get; } =
        new([], new Dictionary<string, PublicWantedDetail>(StringComparer.OrdinalIgnoreCase),
            [], new Dictionary<string, PublicWantedArchiveCard>(StringComparer.OrdinalIgnoreCase), NoBounties,
            NoCaptures);

    /// <summary>Every capture the outside may know about; the archive list shows only the newest of them.</summary>
    /// <remarks>
    /// Kept as a per-kind breakdown rather than one number so the item switch can drop what it owns here too. A
    /// scalar total could not be reduced by <see cref="WithoutItems"/> without counting the rows a second time.
    /// </remarks>
    public int CapturedTotal => CapturedTotals.Values.Sum();

    public PublicWantedDetail? Find(string? caseNumber)
        => caseNumber is not null && ByCaseNumber.TryGetValue(caseNumber, out var entry) ? entry : null;

    /// <summary>The board card of a live notice; null for every other state.</summary>
    public PublicWantedCard? CardFor(string? caseNumber)
        => caseNumber is null
            ? null
            : Cards.FirstOrDefault(c => string.Equals(c.CaseNumber, caseNumber, StringComparison.OrdinalIgnoreCase));

    public PublicWantedArchiveCard? FindCaptured(string? caseNumber)
        => caseNumber is not null && CapturedByCaseNumber.TryGetValue(caseNumber, out var entry) ? entry : null;

    /// <summary>The advertised bounty of a live notice; null when there is none or the module is off.</summary>
    /// <remarks>
    /// Sits on the board rather than on the card so the module switch, which is read outside the content cache like
    /// the board's own, can drop every amount with one <c>with</c> instead of rebuilding each card. The archive gets
    /// none: no money is advertised on a head that has been caught.
    /// </remarks>
    public PublicBounty? BountyFor(string? caseNumber)
        => caseNumber is not null && BountyByCaseNumber.TryGetValue(caseNumber, out var bounty) ? bounty : null;

    /// <summary>The given cards with the biggest advertised bounty first.</summary>
    /// <remarks>
    /// Ranks off <see cref="BountyByCaseNumber"/>, so a board whose bounty module is off keeps publication order
    /// instead of leaking the ranking the hidden amounts would give away. A ceiling loses a tie against a fixed
    /// sum: "bis 5.000 $" promises less than 5.000 $.
    /// </remarks>
    public IReadOnlyList<PublicWantedCard> RankedByBounty(IEnumerable<PublicWantedCard> cards)
        => cards
            .OrderByDescending(c => BountyFor(c.CaseNumber)?.Total ?? 0m)
            .ThenBy(c => BountyFor(c.CaseNumber)?.IsCap ?? false)
            .ThenByDescending(c => c.PublishedAt)
            .ToList();

    /// <summary>Precomputed searchable plain text of one notice; empty when the board carries none.</summary>
    /// <remarks>
    /// Filled once per cache fill so the public search does not strip the markup of every accusation on every
    /// anonymous request. Dropped together with its card in <see cref="WithoutItems"/>, like the bounty.
    /// </remarks>
    public string SearchTextFor(string? caseNumber)
        => caseNumber is not null && SearchText is not null && SearchText.TryGetValue(caseNumber, out var plain)
            ? plain
            : string.Empty;

    /// <summary>The same snapshot without the vehicle and weapon notices; what the item module switch owns.</summary>
    /// <remarks>
    /// One pass over all five collections rather than a second cache key: board, archive and item notices come from
    /// the same table and are invalidated by the same writes, so a second key would double every drop site — the
    /// reason board and archive already share one. Dropping a card without its bounty and archive entry would leave
    /// a plate advertised on a page that no longer lists it.
    /// </remarks>
    public PublicWantedBoard WithoutItems()
    {
        var byCaseNumber = ByCaseNumber
            .Where(e => !WantedKinds.IsItem(e.Value.Kind))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        var capturedByCaseNumber = CapturedByCaseNumber
            .Where(e => !WantedKinds.IsItem(e.Value.Kind))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

        return this with
        {
            Cards = Cards.Where(c => !WantedKinds.IsItem(c.Kind)).ToList(),
            ByCaseNumber = byCaseNumber,
            Archive = Archive.Where(c => !WantedKinds.IsItem(c.Kind)).ToList(),
            CapturedByCaseNumber = capturedByCaseNumber,
            BountyByCaseNumber = BountyByCaseNumber
                .Where(e => byCaseNumber.ContainsKey(e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase),
            CapturedTotals = CapturedTotals
                .Where(e => !WantedKinds.IsItem(e.Key))
                .ToDictionary(e => e.Key, e => e.Value),
            SearchText = SearchText?
                .Where(e => byCaseNumber.ContainsKey(e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>What the anonymous photo endpoint needs to stream a file, and nothing else.</summary>
public sealed record PublicWantedPhoto(string FileNameSaved, string ContentType);

/// <summary>Row of the internal management list.</summary>
/// <remarks>
/// Carries no HTML: an accusation may hold base64 images, and a list of them would be megabytes per render. The
/// editor pulls the one it edits through <c>GetDraftAsync</c>.
/// </remarks>
public sealed record PublicWantedEdit(
    string Id,
    string? CaseNumber,
    PublicWantedKind Kind,
    PublicWantedStatus Status,
    string DisplayName,
    string? PersonCaseNumber,
    HazardLevel HazardLevel,
    bool HasPhoto,
    DateTime? PublishedAt,
    string? PublishedByName,
    DateTime? ExpiresAt,
    DateTime? ModifiedAt,
    int ViewCount,
    /// <summary>Advertised bounty; filled after the projection, so a zero means "none", never "not loaded".</summary>
    decimal Bounty);

/// <summary>The one notice an author is editing, HTML included.</summary>
public sealed record PublicWantedDraft(
    string Id,
    string? CaseNumber,
    /// <summary>Decides what the editor offers: an item notice has no photo and different field labels.</summary>
    PublicWantedKind Kind,
    PublicWantedStatus Status,
    string DisplayName,
    string? AliasText,
    string? LastArea,
    string? VehicleText,
    string? PhotoSourceId,
    DateTime? ExpiresAt,
    string? ChargeHtml,
    bool BountyIsCap,
    /// <summary>What the editor shows in the level picker; derived from the score until an author overrules it.</summary>
    HazardLevel HazardLevel,
    /// <summary>Reward figure shown in the public total; roleplay backdrop, so no booking and no receipt back it.</summary>
    decimal? PublicPaidOut);

/// <summary>One selectable file photo, labelled by upload date rather than by file name.</summary>
public sealed record PublicWantedPhotoOption(string Id, string Label);

/// <summary>One vehicle or weapon of a file, offered as the source of an item notice.</summary>
/// <remarks>
/// The id is only good until the file's profile is saved again — PersonService replaces the profile children
/// wholesale — so it is read once, at draft creation, and never stored. <see cref="Advertised"/> is carried rather
/// than filtered out: an author has to see that a plate is already outside, not miss it from the list.
/// </remarks>
public sealed record PublicWantedItemSource(string Id, PublicWantedKind Kind, string Label, bool Advertised);

/// <summary>What the editor may offer: the file's photos and its recorded areas.</summary>
public sealed record PublicWantedOptions(
    IReadOnlyList<PublicWantedPhotoOption> Photos,
    IReadOnlyList<string> Areas)
{
    public static PublicWantedOptions Empty { get; } = new([], []);
}

/// <summary>What the file page needs for its banner.</summary>
/// <remarks>No id: the banner states a fact, it does not link into the management list, and it renders for partners.</remarks>
public sealed record PublicWantedBanner(
    string? CaseNumber,
    PublicWantedStatus Status,
    DateTime? PublishedAt);

/// <summary>What publishing did: went live, or turned into a request for leadership.</summary>
public enum PublicWantedPublishOutcome
{
    Published = 0,
    Requested = 1,
}

/// <summary>One pending publication request in the approval inbox.</summary>
public sealed record PublicWantedRequestRow(
    string RequestId,
    string WantedId,
    string DisplayName,
    string TargetDesignation,
    string? RequesterName,
    string? Justification,
    DateTime CreatedAt);

/// <summary>Editable fields of a notice; a class so the panel can two-way bind.</summary>
public class PublicWantedInput
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AliasText { get; set; }
    public string? LastArea { get; set; }
    public string? VehicleText { get; set; }
    public string? PhotoSourceId { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Null leaves the stored accusation untouched, "" clears it.</summary>
    public string? ChargeHtml { get; set; }

    /// <summary>Null leaves the stored level alone; a different value makes it the author's, not the score's.</summary>
    public HazardLevel? HazardLevel { get; set; }
}
