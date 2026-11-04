using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Warrants;

/// <summary>What an agent fills in to request a warrant.</summary>
public sealed class WarrantInput
{
    public WarrantType Type { get; set; } = WarrantType.Search;
    public string PersonId { get; set; } = string.Empty;
    public string? CaseId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public List<string> LawIds { get; set; } = new();
    public int ValidityDays { get; set; } = 3;
}

/// <summary>A warrant with its person, optional case and the state computed at read time.</summary>
/// <param name="Laws">The cited paragraphs that still exist; filled for a single warrant only.</param>
public sealed record WarrantView(
    Warrant Warrant,
    WarrantState State,
    string PersonName,
    string PersonCaseNumber,
    string? CaseTitle,
    string? CaseCaseNumber,
    IReadOnlyList<WarrantLaw>? Laws = null);

/// <summary>A cited paragraph the reader may open in the law book.</summary>
public sealed record WarrantLaw(string Id, string Label);

/// <summary>Name and case number of a record a new warrant can concern, resolved under the reader's rights.</summary>
public sealed record WarrantTarget(string Id, string Name, string CaseNumber);

/// <summary>The check address printed on a signed paper.</summary>
/// <param name="Address">Public check address without scheme, as printed under the QR code.</param>
/// <param name="QrSvg">Encoder output for the check address; null when it does not fit.</param>
/// <param name="CheckOnline">False while the public check module is off: the QR code then leads nowhere yet.</param>
public sealed record WarrantCheckLink(string Address, string? QrSvg, bool CheckOnline);
