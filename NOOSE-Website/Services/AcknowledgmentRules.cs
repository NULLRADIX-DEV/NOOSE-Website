namespace NOOSE_Website.Services;

/// <summary>Deadline choices for a board acknowledgment and when an open one counts as overdue.</summary>
public static class AcknowledgmentRules
{
    /// <summary>Days after publishing until the reminder; the form offers exactly these.</summary>
    public static readonly IReadOnlyList<int> DayChoices = [1, 2, 3, 7, 14];

    /// <summary>Preselected deadline; 0 would mean no reminder at all.</summary>
    public const int DefaultDays = 3;

    /// <summary>Whether the service accepts this choice; 0 = no reminder.</summary>
    public static bool IsValid(int days) => days == 0 || DayChoices.Contains(days);

    /// <summary>The deadline for a choice, or null when nobody is reminded.</summary>
    public static DateTime? DueAt(DateTime publishedUtc, int days) => days > 0 ? publishedUtc.AddDays(days) : null;

    /// <summary>Whether the deadline has passed; the worker reminds from the same moment on.</summary>
    public static bool IsOverdue(DateTime? dueAtUtc, DateTime nowUtc) => dueAtUtc is { } due && due <= nowUtc;

    /// <summary>Label of a choice in the form.</summary>
    public static string ChoiceLabel(int days) => days switch
    {
        0 => "Nicht erinnern",
        1 => "Nach 1 Tag",
        _ => $"Nach {days} Tagen",
    };
}
