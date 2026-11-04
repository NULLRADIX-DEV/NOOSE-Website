using System.Security.Claims;
using NOOSE_Website.Models.Calendar;

namespace NOOSE_Website.Services;

/// <summary>The personal calendar feed: a secret link per agent that serves exactly what that agent sees on /kalender.</summary>
public interface ICalendarFeedService
{
    /// <summary>State of the actor's own feed; never the link.</summary>
    Task<CalendarFeedStatus> GetStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Creates the actor's link, or replaces it so the old one stops working at once. The only place the link is returned.</summary>
    Task<CalendarFeedLink> CreateAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Kills the actor's link.</summary>
    Task RevokeAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Reduces every title in the actor's feed to a neutral label, or lifts that again.</summary>
    Task SetNeutralTitlesAsync(bool neutral, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The ICS document for a presented secret; null for every kind of miss, so the caller answers one uniform 404.</summary>
    Task<string?> RenderAsync(string? token, CancellationToken cancellationToken = default);
}
