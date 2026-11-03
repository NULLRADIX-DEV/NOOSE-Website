using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure;

/// <summary>Demo instance only: when Demo:AutoSetup is set, seeds demo data and turns demo mode on at startup so the public demo needs no admin login. Env-gated; never enabled on production.</summary>
public static class DemoAutoSetup
{
    /// <summary>Seeds + enables demo mode if the env flag is set; idempotent, safe to run every start.</summary>
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("Demo:AutoSetup"))
        {
            return;
        }

        // synthetic bootstrap-admin only passes the permission guards; the audit/read-only
        // interceptors resolve the actor independently (System at startup -> writes allowed).
        var actor = BuildActor();

        var demoData = services.GetRequiredService<IDemoDataService>();
        var settings = services.GetRequiredService<ISystemSettingService>();

        // example data is cosmetic; a failure here must not keep the demo instance from starting
        try
        {
            var added = await demoData.SeedAsync(actor, cancellationToken);
            logger.LogInformation("Demo-AutoSetup: {Count} Datensaetze geseedet.", added);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo-AutoSetup: Seeding fehlgeschlagen, Instanz startet ohne die neuen Beispieldaten.");
        }

        var current = await settings.GetAsync(cancellationToken);
        if (!current.DemoModeActive)
        {
            await settings.SaveAsync(new SystemConfigurationInput
            {
                MaintenanceModeActive = current.MaintenanceModeActive,
                MaintenanceModeText = current.MaintenanceModeText,
                BannerText = current.BannerText,
                BannerLevel = current.BannerLevel,
                ThemePrimary = current.ThemePrimary,
                ThemeSecondary = current.ThemeSecondary,
                ThemeTertiary = current.ThemeTertiary,
                DemoModeActive = true,
            }, actor, cancellationToken);
            logger.LogInformation("Demo-AutoSetup: Demo-Modus aktiviert.");
        }
    }

    private static ClaimsPrincipal BuildActor()
    {
        var identity = new ClaimsIdentity("DemoAutoSetup");
        identity.AddClaim(new Claim(AgentClaimTypes.IsAdmin, "true"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsBootstrap, "true"));
        // an active internal identity as well: seeding runs through services whose guards ask for more than admin
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, DemoIdentity.AgentId));
        identity.AddClaim(new Claim(AgentClaimTypes.Status, Models.Enums.AgentStatus.Active.ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Rank, ((int)Models.Enums.Rank.Director).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Division, ((int)Models.Enums.Division.Cid).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.IsTRU, "false"));
        return new ClaimsPrincipal(identity);
    }
}
