using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>The anonymous check behind <c>/ausweis/{Nummer}</c>.</summary>
/// <remarks>
/// Gated by the <c>Dienstausweis</c> module. Whoever asks is nobody, so there is one answer for every miss.
/// </remarks>
public interface IPublicServiceIdService
{
    /// <summary>The check for a badge number, or null for every miss: module off, kill switch, unknown or malformed number.</summary>
    Task<PublicServiceIdCheck?> CheckAsync(string? number, CancellationToken cancellationToken = default);
}
