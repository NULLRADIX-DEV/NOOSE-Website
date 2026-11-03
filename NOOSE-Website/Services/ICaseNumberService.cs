using NOOSE_Website.Data;

namespace NOOSE_Website.Services;

/// <summary>Issues sequential, human-readable case numbers (FIB-{Prefix}-{Year}-{Number}) per record type.</summary>
public interface ICaseNumberService
{
    /// <summary>Race-safely increments the prefix's yearly counter on the passed context (so it stays in the caller's transaction) and returns the formatted case number.</summary>
    Task<string> NextAsync(AppDbContext db, string prefix, CancellationToken cancellationToken = default);
}
