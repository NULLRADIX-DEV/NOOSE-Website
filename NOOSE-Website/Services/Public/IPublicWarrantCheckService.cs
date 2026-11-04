using NOOSE_Website.Models.Public;
using NOOSE_Website.Models.Warrants;

namespace NOOSE_Website.Services.Public;

/// <summary>The anonymous check behind <c>/anordnung/{Id}</c> and the QR code that leads there.</summary>
/// <remarks>
/// Gated by the <c>AnordnungPruefung</c> module. Whoever asks is nobody, so there is one answer for every miss.
/// </remarks>
public interface IPublicWarrantCheckService
{
    /// <summary>The check for a warrant id, or null for every miss: module off, kill switch, unknown, unsigned or malformed id.</summary>
    Task<PublicWarrantCheck?> CheckAsync(string? warrantId, CancellationToken cancellationToken = default);

    /// <summary>Address and QR code for the printed paper; the caller has already checked that the viewer may print it.</summary>
    Task<WarrantCheckLink> GetLinkAsync(string warrantId, CancellationToken cancellationToken = default);
}
