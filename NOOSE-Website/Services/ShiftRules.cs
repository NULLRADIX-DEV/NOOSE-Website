using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Directives;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Models.Shift;

namespace NOOSE_Website.Services;

/// <summary>Rules of the "Meine Schicht" page: how far it looks ahead and back, and how a visit moves its window.</summary>
public static class ShiftRules
{
    public const string Route = "/meine-schicht";

    /// <summary>Looks closer together than this are one visit, so a reload keeps the block.</summary>
    public static readonly TimeSpan SameVisit = TimeSpan.FromMinutes(30);

    /// <summary>Furthest the block reaches back; a long absence gets a week, not a pile.</summary>
    public static readonly TimeSpan MaxLookBack = TimeSpan.FromDays(7);

    /// <summary>What a first visit is shown.</summary>
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(1);

    /// <summary>Follow-ups due within this span count as upcoming.</summary>
    public static readonly TimeSpan UpcomingFollowups = TimeSpan.FromDays(7);

    /// <summary>Rows per list; the tile still counts all of them.</summary>
    public const int ListCap = 10;

    /// <summary>Records a look at the page and returns the window it shows.</summary>
    public static ShiftWindow Stamp(NavPreferences prefs, DateTime nowUtc)
    {
        // a second tab or a reload stays inside the visit and keeps the boundary
        if (prefs.ShiftSeenUtc is { } seen && nowUtc - seen < SameVisit)
        {
            if (nowUtc > seen)
            {
                prefs.ShiftSeenUtc = nowUtc;
            }
        }
        else
        {
            prefs.ShiftBoundaryUtc = prefs.ShiftSeenUtc;
            prefs.ShiftSeenUtc = nowUtc;
        }
        return WindowFor(prefs.ShiftBoundaryUtc, nowUtc);
    }

    /// <summary>The window for a stored boundary, capped at <see cref="MaxLookBack"/>.</summary>
    public static ShiftWindow WindowFor(DateTime? boundaryUtc, DateTime nowUtc)
    {
        if (boundaryUtc is not { } boundary)
        {
            return new ShiftWindow(nowUtc - FirstLookBack, ShiftWindowKind.FirstVisit);
        }
        var floor = nowUtc - MaxLookBack;
        return boundary < floor
            ? new ShiftWindow(floor, ShiftWindowKind.Capped)
            : new ShiftWindow(boundary, ShiftWindowKind.SinceLastVisit);
    }

    /// <summary>Board entries and directives in one list: directives and important entries first, the longest waiting leading each group.</summary>
    public static IReadOnlyList<ShiftAcknowledgment> MergeAcknowledgments(IReadOnlyList<OpenAcknowledgment> board, IReadOnlyList<OpenDirective> directives)
    {
        var rows = directives
            .Select(d => new ShiftAcknowledgment($"/dienstanweisungen/{d.DirectiveId}", $"{d.Label} {d.Title}",
                Important: true, IsDirective: true, d.Since, DueAt: null))
            .Concat(board.Select(b => new ShiftAcknowledgment($"/brett/{b.AnnouncementId}", b.Title,
                b.Important, IsDirective: false, b.CreatedAt, b.DueAt)));
        // directives are listed first, so the stable sort keeps them ahead on equal waiting time
        return rows
            .OrderByDescending(r => r.IsDirective || r.Important)
            .ThenBy(r => r.Since)
            .ToList();
    }

    /// <summary>Where "Zu quittieren" leads: the directives when only they are open, else the board.</summary>
    public static string AcknowledgmentsHref(int boardCount, int directiveCount)
        => directiveCount > 0 && boardCount == 0 ? "/dienstanweisungen" : "/brett";
}
