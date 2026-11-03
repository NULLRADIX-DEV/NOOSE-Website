using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Public;

/// <summary>An official statement of the agency, reachable at <c>/presse/{CaseNumber}</c> once published.</summary>
/// <remarks>
/// Two copies of everything the public reads, one meaning each: Title/Teaser/DraftHtml are the working copy an author
/// may change at any time, ContentTitle/ContentTeaser/ContentHtml are what the outside world reads and are only ever
/// written by publishing. The headline is snapshotted too, unlike an editorial page: a release is a dated statement, and
/// a saved typo fix must not silently rewrite the headline that already went out.
/// <para>
/// <see cref="CaseNumber"/> is minted at the first publication and unique, so a draft has no public address at all
/// rather than a status that hides it. Unlike the page slug it may carry a database unique index: a counter number is
/// never reused, so a soft-deleted row can keep its own forever.
/// </para>
/// </remarks>
[Table("Pressemitteilungen")]
public class Pressemitteilung : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Public file number (FIB-PM-…); null until the first publication.</summary>
    [Column("Aktenzeichen")]
    public string? CaseNumber { get; set; }

    /// <summary>Working copy of the headline; never leaves the house.</summary>
    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Working copy of the summary. Plain text, so a list renders no markup.</summary>
    [Column("Teaser")]
    public string Teaser { get; set; } = string.Empty;

    /// <summary>The working copy of the body; never leaves the house.</summary>
    [Column("EntwurfHtml")]
    public string? DraftHtml { get; set; }

    /// <summary>Headline the public reads; written by publishing only.</summary>
    [Column("InhaltTitel")]
    public string? ContentTitle { get; set; }

    /// <summary>Summary the public reads; written by publishing only.</summary>
    [Column("InhaltTeaser")]
    public string? ContentTeaser { get; set; }

    /// <summary>Body the public reads; written by publishing only.</summary>
    [Column("InhaltHtml")]
    public string? ContentHtml { get; set; }

    [Column("Status")]
    public PressReleaseStatus Status { get; set; } = PressReleaseStatus.Entwurf;

    [Column("VeroeffentlichtAm")]
    public DateTime? PublishedAt { get; set; }
    [Column("VeroeffentlichtVonId")]
    public string? PublishedById { get; set; }
    public Agent? PublishedBy { get; set; }

    /// <summary>When the public channel was told; the idempotency token of the push.</summary>
    /// <remarks>
    /// A status change cannot play that role here: retract, correct, publish again is a legitimate round trip, and it
    /// must not reach the channel a second time.
    /// </remarks>
    [Column("DiscordGepushtAm")]
    public DateTime? DiscordPushedAt { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }

    [Column("IstGeloescht")]
    public bool IsDeleted { get; set; }
    [Column("GeloeschtAm")]
    public DateTime? DeletedAt { get; set; }
    [Column("GeloeschtVonId")]
    public string? DeletedById { get; set; }
}
