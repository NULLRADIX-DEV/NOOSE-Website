using System.Diagnostics.CodeAnalysis;

namespace NOOSE_Website.Services;

/// <summary>Reads typed, printed or linked case numbers the way the agency issues them today.</summary>
public static class CaseNumbers
{
    private const string LegacyPrefix = "NOOSE";

    /// <summary>Trims, then maps a legacy NOOSE- or mis-cased agency prefix to the current one.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        var dash = text.IndexOf('-');
        if (dash <= 0)
        {
            return text;
        }

        // old posters keep resolving
        var head = text.AsSpan(0, dash);
        return head.Equals(CaseNumberService.AgencyPrefix, StringComparison.OrdinalIgnoreCase)
            || head.Equals(LegacyPrefix, StringComparison.OrdinalIgnoreCase)
                ? CaseNumberService.AgencyPrefix + text[dash..].ToUpperInvariant()
                : text;
    }
}
