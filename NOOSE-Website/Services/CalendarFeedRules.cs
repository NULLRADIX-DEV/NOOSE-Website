using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <summary>Who holds a calendar feed, which entries leave the site in it, and what they are called out there.</summary>
public static class CalendarFeedRules
{
    public const int DaysBack = 30;
    public const int DaysAhead = 180;
    public const string SiteRoute = "/kalender-abo";

    private const int MaxSummary = 200;

    /// <summary>Same circle as the service ID card: an active in-house agent, no partner, no supervision, no demo.</summary>
    public static bool MayHold(ClaimsPrincipal principal)
        => principal.IsInternalAgent() && !principal.IsTeamLead() && !principal.IsDemo();

    /// <summary>Stored-row twin of <see cref="MayHold(ClaimsPrincipal)"/>; checked on every fetch.</summary>
    public static bool MayHold(Agent agent)
        => agent.Status == AgentStatus.Active && agent.PartnerAgency is null && !agent.IsTeamLead;

    /// <summary>Whether this kind of entry is part of the feed at all.</summary>
    public static bool IsIncluded(CalendarSource source) => source is
        CalendarSource.Appointment or CalendarSource.Meeting or CalendarSource.Job
        or CalendarSource.Followup or CalendarSource.Absence or CalendarSource.Operation;

    /// <summary>Address of the feed for one secret on the configured site, never the request host.</summary>
    public static string FeedUrl(string? configuredBase, string token)
        => WantedShortLink.SiteBase(configuredBase) + SiteRoute + "/" + token + ".ics";

    /// <summary>The secret out of the last path segment, tolerating the .ics suffix; null when it has no secret shape.</summary>
    public static string? TokenFromSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return null;
        }
        var bare = segment.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;
        return CalendarFeedToken.HasValidShape(bare) ? bare : null;
    }

    /// <summary>Title shown in the calendar app.</summary>
    public static string Title(CalendarEntry entry, bool neutralTitles)
    {
        var neutral = Neutral(entry.Source);
        if (neutralTitles || entry.Source is CalendarSource.Followup or CalendarSource.Operation)
        {
            return neutral;
        }
        var text = MentionParser.Strip(entry.Title).Trim();
        if (text.Length == 0)
        {
            return neutral;
        }
        return text.Length <= MaxSummary ? text : string.Concat(text.AsSpan(0, MaxSummary - 1), "…");
    }

    public static string Neutral(CalendarSource source) => source switch
    {
        CalendarSource.Appointment => "Termin",
        CalendarSource.Meeting => "Besprechung",
        CalendarSource.Job => "Aufgabe fällig",
        CalendarSource.Followup => "Wiedervorlage",
        CalendarSource.Operation => "Operation",
        CalendarSource.Absence => "Abgemeldet",
        _ => "Termin",
    };

    /// <summary>The events of the feed from the entries the aggregator returned for this agent, in date order.</summary>
    public static IReadOnlyList<IcsEvent> Project(IEnumerable<CalendarEntry> entries, bool neutralTitles,
        string siteBase, string uidHost, TimeZoneInfo zone, DateTime nowUtc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<(DateTime Sort, IcsEvent Event)>();
        foreach (var entry in entries)
        {
            // obsolete = cancelled, done or excused; the app should not show them
            if (!IsIncluded(entry.Source) || entry.Obsolete || !seen.Add(entry.Id))
            {
                continue;
            }

            // a record link only where the title is readable too
            var url = !neutralTitles && entry.Source is not (CalendarSource.Followup or CalendarSource.Operation)
                      && !string.IsNullOrEmpty(entry.Href) && entry.Href.StartsWith('/')
                ? siteBase + entry.Href
                : null;
            var ics = entry.WholeDay
                ? new IcsEvent(Uid(entry.Id, uidHost), nowUtc, Title(entry, neutralTitles),
                    entry.StartLocal.Date, entry.EndLocal?.Date.AddDays(1), true, url)
                : new IcsEvent(Uid(entry.Id, uidHost), nowUtc, Title(entry, neutralTitles),
                    ToUtc(entry.StartLocal, zone), entry.EndLocal is { } end ? ToUtc(end, zone) : null, false, url);
            events.Add((ics.Start, ics));
        }
        return events.OrderBy(e => e.Sort).ThenBy(e => e.Event.Uid, StringComparer.Ordinal).Select(e => e.Event).ToList();
    }

    /// <summary>Stable per entry, so the app updates an event instead of duplicating it.</summary>
    public static string Uid(string entryId, string host) => $"{entryId}@{host}";

    /// <summary>Unspecified wall-clock time of the zone back to UTC; a time skipped by the clock change moves an hour on.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, zone), DateTimeKind.Utc);
    }
}
