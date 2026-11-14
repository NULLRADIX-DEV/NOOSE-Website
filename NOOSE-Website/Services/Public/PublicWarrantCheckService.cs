using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Models.Warrants;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc />
/// <remarks>
/// Answers only for a paper leadership signed, with what that paper prints minus person and subject: kind, case
/// number, validity and signature. A request, a refusal and a deleted warrant are plain misses, like an id nobody
/// ever issued. The person's classification does not matter here, because the check never names the person.
/// </remarks>
public sealed class PublicWarrantCheckService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    IDiscordWebhookService discord) : IPublicWarrantCheckService
{
    private const string QrLabel = "QR-Code zur Echtheitsprüfung";

    public async Task<PublicWarrantCheck?> CheckAsync(string? warrantId, CancellationToken cancellationToken = default)
    {
        // the shape first: a made-up string never reaches the module read or the database
        var canonical = WarrantCheckAddress.Canonical(warrantId);
        if (canonical is null || !await IsLiveAsync(cancellationToken))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await db.Warrants.AsNoTracking().FirstOrDefaultAsync(w => w.Id == canonical, cancellationToken);
        if (warrant is null || !WarrantRules.WasSigned(warrant) || string.IsNullOrWhiteSpace(warrant.CaseNumber))
        {
            return null;
        }

        return new PublicWarrantCheck(
            warrant.Type,
            warrant.CaseNumber,
            WarrantRules.StateAt(warrant, DateTime.UtcNow),
            warrant.ValidFrom!.Value,
            warrant.ValidUntil,
            warrant.CancelledAt,
            string.IsNullOrWhiteSpace(warrant.DeciderName) ? null : warrant.DeciderName.Trim(),
            warrant.DeciderRank is { } rank ? RankDisplay.Name(rank) : warrant.DeciderFunction,
            warrant.DecidedAt);
    }

    public async Task<WarrantCheckLink> GetLinkAsync(string warrantId, CancellationToken cancellationToken = default)
    {
        var config = await discord.GetConfigAsync(cancellationToken);
        var address = WarrantCheckAddress.Of(config.SiteBaseUrl, warrantId);
        var qr = QrCode.Encode(address);
        return new WarrantCheckLink(
            ServiceIdAddress.WithoutScheme(address),
            qr is null ? null : QrCode.Svg(qr, QrLabel),
            await IsLiveAsync(cancellationToken));
    }

    /// <summary>Module on and kill switch off; an unreachable snapshot reads as off.</summary>
    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await modules.IsEnabledAsync(PublicModules.WarrantCheck, cancellationToken);
        }
        catch
        {
            /* best effort */
            return false;
        }
    }
}
