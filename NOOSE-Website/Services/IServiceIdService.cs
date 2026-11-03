using System.Security.Claims;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>The printable service ID card of an agent.</summary>
public interface IServiceIdService
{
    /// <summary>The card of the agent for this viewer; the verdict explains a missing card.</summary>
    /// <remarks>Self, leadership and the Administration Division only, see <see cref="ServiceIdRules"/>.</remarks>
    Task<ServiceIdResult> GetAsync(string agentId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);
}
