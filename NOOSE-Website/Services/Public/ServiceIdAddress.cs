namespace NOOSE_Website.Services.Public;

/// <summary>Address of the public ID check, as the QR code on the card carries it.</summary>
public static class ServiceIdAddress
{
    /// <summary>Route prefix of the public check.</summary>
    public const string Prefix = "/ausweis";

    /// <summary>Site-relative path of the check for one badge number.</summary>
    public static string Path(string number) => $"{Prefix}/{Uri.EscapeDataString(number)}";

    /// <summary>Absolute address on the configured site, never the request host.</summary>
    public static string Of(string? configuredBase, string number)
        => WantedShortLink.SiteBase(configuredBase) + Path(number);

    public static string WithoutScheme(string url)
    {
        var at = url.IndexOf("://", StringComparison.Ordinal);
        return at < 0 ? url : url[(at + 3)..];
    }
}
