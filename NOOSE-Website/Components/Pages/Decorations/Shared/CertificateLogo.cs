using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Pages.Decorations.Shared;

/// <summary>Which crest a certificate prints: the uploaded logo, else the built-in emblem.</summary>
public static class CertificateLogo
{
    public const string Fallback = "NooseIcon.png";

    /// <summary>Image source of the crest; a failed settings read falls back to the emblem.</summary>
    public static async Task<string> SourceAsync(ISystemSettingService settings)
    {
        try
        {
            return (await settings.GetAsync()).HasLogo ? "/system/logo" : Fallback;
        }
        catch
        {
            /* best effort */
            return Fallback;
        }
    }
}
