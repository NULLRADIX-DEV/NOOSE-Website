using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Radio;

/// <summary>What a writer supplies when creating or changing a channel.</summary>
public class RadioChannelInput
{
    public string Frequency { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public RadioScope Scope { get; set; } = RadioScope.Noose;
    public PartnerAgency? Agency { get; set; }
    public string? TaskforceId { get; set; }
    public string? Note { get; set; }
    public bool IsClassified { get; set; }
}

/// <summary>One channel as the plan renders it, with the bound taskforce already resolved to a name.</summary>
public sealed record RadioChannelRow(
    string Id,
    string Frequency,
    string Label,
    RadioScope Scope,
    PartnerAgency? Agency,
    string? TaskforceId,
    string? TaskforceName,
    string? Note,
    bool IsClassified);

/// <summary>A faction frequency, read live from the faction record rather than copied into a channel row; <paramref name="Linkable"/> is false where the viewer may not open the record.</summary>
public sealed record RadioFactionRow(string FactionId, string Name, string Frequency, bool IsClassified, bool Linkable);

/// <summary>Everything /funk shows, already filtered for the viewer.</summary>
public sealed record RadioPlan(
    IReadOnlyList<RadioChannelRow> Channels,
    IReadOnlyList<RadioFactionRow> Factions);
