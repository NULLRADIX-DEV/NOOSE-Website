using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;

namespace NOOSE_Website.Services;

public class CaseNumberService : ICaseNumberService
{
    /// <summary>Agency part in front of every issued case number.</summary>
    public const string AgencyPrefix = "FIB";

    public async Task<string> NextAsync(AppDbContext db, string prefix, CancellationToken cancellationToken = default)
    {
        // Race-safety needs the caller's enclosing transaction: commit releases the counter row-lock.
        // Without it, parallel inserts read the same number and crash the unique index, so fail fast.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Aktenzeichen-Vergabe erfordert eine umschließende Transaktion des Aufrufers (Race-Sicherheit).");
        }

        var year = DateTime.UtcNow.Year;
        // Atomic counter increment on the caller's context so counter and record commit together.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AktenzeichenZaehler (Praefix, Jahr, LetzteNummer) VALUES ({prefix}, {year}, 1) ON DUPLICATE KEY UPDATE LetzteNummer = LetzteNummer + 1;",
            cancellationToken);
        var number = (await db.CaseNumberCounter.AsNoTracking()
            .FirstAsync(z => z.Prefix == prefix && z.Year == year, cancellationToken)).LastNumber;
        return $"{AgencyPrefix}-{prefix}-{year}-{number:0000}";
    }
}
