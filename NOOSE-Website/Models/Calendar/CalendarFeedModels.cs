namespace NOOSE_Website.Models.Calendar;

/// <summary>State of one agent's calendar feed as the profile shows it; never carries the link.</summary>
public sealed record CalendarFeedStatus(bool Active, DateTime? CreatedAtUtc, bool NeutralTitles);

/// <summary>A freshly created link: the only moment the full address exists in readable form.</summary>
/// <param name="Url">Absolute address on the configured site.</param>
public sealed record CalendarFeedLink(string Url)
{
    /// <summary>Keeps the secret out of logs and exception text.</summary>
    public override string ToString() => nameof(CalendarFeedLink);
}
