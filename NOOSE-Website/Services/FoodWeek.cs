using System.Globalization;

namespace NOOSE_Website.Services;

/// <summary>Calendar-week arithmetic of the food limit. One place, so limit reads and approvals always agree on which week is running.</summary>
public static class FoodWeek
{
    /// <summary>Monday of the running limit week.</summary>
    /// <remarks>Local calendar on purpose: the server runs in Europe/Berlin, which is the week an agent sees.</remarks>
    public static DateTime Current() => StartOf(DateTime.Now);

    /// <summary>Monday (date only) of the week the given local moment falls in.</summary>
    public static DateTime StartOf(DateTime local)
    {
        var date = local.Date;
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(date.AddDays(-offset), DateTimeKind.Unspecified);
    }

    /// <summary>German week label, e.g. "KW 47 · 16.11.–22.11.2026".</summary>
    public static string Label(DateTime weekStart)
    {
        var week = ISOWeek.GetWeekOfYear(weekStart);
        var end = weekStart.AddDays(6);
        return $"KW {week} · {weekStart:dd.MM.}–{end:dd.MM.yyyy}";
    }
}
