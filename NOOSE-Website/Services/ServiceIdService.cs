using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc />
public sealed class ServiceIdService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordWebhookService discord,
    IPublicModuleService modules,
    IAccessLogService accessLog) : IServiceIdService
{
    private const string QrLabel = "QR-Code zur Echtheitsprüfung";

    public async Task<ServiceIdResult> GetAsync(string agentId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);

        var access = ServiceIdRules.Check(viewer, agent);
        if (access != ServiceIdAccess.Allowed || agent is null)
        {
            return new ServiceIdResult(access, null);
        }

        var number = ServiceIdRules.Number(agent)!;
        var config = await discord.GetConfigAsync(cancellationToken);
        var address = ServiceIdAddress.Of(config.SiteBaseUrl, number);
        var qr = QrCode.Encode(address);
        var online = false;
        try
        {
            online = await modules.IsEnabledAsync(PublicModules.ServiceId, cancellationToken);
        }
        catch
        {
            /* best effort */
        }

        var card = new ServiceIdCard(
            agent.Id,
            AgentNameDisplay.Pick(agent.Codename, agent.RealName, viewer.MayRealNameSee()),
            RankDisplay.Name(agent.Rank),
            DivisionRules.Of(agent) is { IsNone: false } unit ? DivisionRules.LongLabel(unit) : null,
            number,
            AgentAvatar.Url(agent.AvatarFileName),
            AgentAvatar.Initials(agent.Codename),
            agent.ReleasedAt,
            ServiceIdAddress.WithoutScheme(address),
            qr is null ? null : QrCode.Svg(qr, QrLabel),
            online);

        // somebody else's card is a look into their file, like the printed personnel file
        if (!string.Equals(viewer.GetAgentId(), agent.Id, StringComparison.Ordinal))
        {
            await accessLog.LogViewAsync(nameof(Data.Entities.Agent), agent.Id, cancellationToken);
        }
        return new ServiceIdResult(access, card);
    }
}
