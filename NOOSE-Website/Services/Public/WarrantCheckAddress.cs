namespace NOOSE_Website.Services.Public;

/// <summary>Address of the public warrant check, as the QR code on the printed paper carries it.</summary>
/// <remarks>
/// Keyed by the warrant id, not the case number: the case numbers count up, so a check by number could be walked to
/// learn how many warrants exist and when they were signed. The id is a random GUID that only the paper carries.
/// </remarks>
public static class WarrantCheckAddress
{
    /// <summary>Route prefix of the public check; singular, so it never shadows the internal <c>/anordnungen</c>.</summary>
    public const string Prefix = "/anordnung";

    /// <summary>Site-relative path of the check for one warrant.</summary>
    public static string Path(string warrantId) => $"{Prefix}/{Uri.EscapeDataString(warrantId)}";

    /// <summary>Absolute address on the configured site, never the request host.</summary>
    public static string Of(string? configuredBase, string warrantId)
        => WantedShortLink.SiteBase(configuredBase) + Path(warrantId);

    /// <summary>The canonical id when the value has the shape of a warrant id, else null.</summary>
    public static string? Canonical(string? warrantId)
    {
        var trimmed = warrantId?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length == 36 && Guid.TryParseExact(trimmed, "D", out var id)
            ? id.ToString()
            : null;
    }
}
