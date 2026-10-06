using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Law/legal-basis module: leadership-curated paragraphs, readable by all active agents and linkable to records.</summary>
public interface ILawService
{
    Task<List<Law>> GetListAsync(CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);
    Task<Law?> GetAsync(string id, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);

    /// <summary>Search by code/paragraph/title for autocomplete.</summary>
    Task<List<Law>> SearchAsync(string? searchText, int max = 20, CancellationToken cancellationToken = default);

    Task<Law> CreateAsync(LawInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RefreshAsync(string id, LawInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deleted paragraphs for the trash.</summary>
    Task<List<Law>> GetTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>Restore a paragraph; it comes back internal.</summary>
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>All books the viewer can see, catalog order.</summary>
    Task<List<LawBookSummary>> GetBooksAsync(CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);

    /// <summary>One book with its visible paragraphs; null if unknown or empty for a partner.</summary>
    Task<LawBookContent?> GetBookAsync(string abbreviation, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);

    Task<LawBook> CreateBookAsync(LawBookInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Update a book; a new abbreviation moves its paragraphs along.</summary>
    Task UpdateBookAsync(string id, LawBookInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Delete an empty book.</summary>
    Task DeleteBookAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deleted books for the trash.</summary>
    Task<List<LawBook>> GetBookTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreBookAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
