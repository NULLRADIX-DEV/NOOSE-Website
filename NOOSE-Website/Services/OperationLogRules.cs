using System.Globalization;
using System.Security.Claims;
using System.Text;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who may write the operation log, when, and how it reads as Ablauf text. Sole source of these rules.</summary>
public static class OperationLogRules
{
    /// <summary>Max length of one line.</summary>
    public const int MaxText = 1000;

    /// <summary>Max length of the location.</summary>
    public const int MaxLocation = 128;

    /// <summary>Same limit as the Ablauf column.</summary>
    public const int MaxExpiry = 4000;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Internal writers who are leadership, HRT or assigned to the operation.</summary>
    public static bool MayWrite(ClaimsPrincipal actor, bool isAssigned)
        => actor.MayWrite() && actor.IsInternalAgent() && (actor.IsLeadership() || actor.IsTRU() || isAssigned);

    /// <summary>Running takes reports, ended takes addenda, planned takes nothing.</summary>
    public static bool IsOpen(OperationStatus status) => status != OperationStatus.Planned;

    /// <summary>A line written after the operation ended.</summary>
    public static bool IsAddendum(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Aborted;

    /// <summary>One line per entry, oldest first, in local time.</summary>
    public static string ToExpiryText(IEnumerable<OperationLogEntry> entries, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var sb = new StringBuilder();
        foreach (var e in entries.OrderBy(e => e.CreatedAt))
        {
            var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc), zone);
            sb.Append(at.ToString("dd.MM. HH:mm", German));
            sb.Append(" · ").Append(OperationLogKindDisplay.Name(e.Kind));
            if (e.IsAddendum)
            {
                sb.Append(" (Nachtrag)");
            }
            if (!string.IsNullOrWhiteSpace(e.Location))
            {
                sb.Append(" · ").Append(e.Location.Trim());
            }
            sb.Append(" · ").Append(string.IsNullOrWhiteSpace(e.AuthorName) ? "Unbekannt" : e.AuthorName);
            sb.Append(": ").Append(e.Text.Trim());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The Ablauf after taking over the log: replaced, or appended below a blank line.</summary>
    public static string Merge(string? existing, string log, bool append)
        => append && !string.IsNullOrWhiteSpace(existing) ? existing.TrimEnd() + "\n\n" + log : log;
}
