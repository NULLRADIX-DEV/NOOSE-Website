using System.Security.Claims;
using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Components.Account;

/// <summary>Static HTTP endpoints for the login/logout flow; can't live in interactive components because OAuth needs real HTTP redirects.</summary>
public static class IdentityComponentsEndpointRouteBuilderExtensions
{
    /// <summary>Rate-limiting policy name for login start (configured in Program.cs).</summary>
    public const string LoginRateLimitPolicy = "noose-login";

    public static IEndpointConventionBuilder MapNooseAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/Account");

        // login start: triggers the Discord OAuth flow
        group.MapPost("/PerformExternalLogin", async (
            [FromServices] SignInManager<Agent> signInManager,
            [FromServices] IAuthenticationSchemeProvider schemeProvider,
            [FromForm] string? returnUrl,
            [FromForm] string? source,
            [FromForm] string? inviteToken) =>
        {
            // scheme is absent when Discord isn't configured yet
            var schema = await schemeProvider.GetSchemeAsync(DiscordAuthenticationDefaults.AuthenticationScheme);
            if (schema is null)
            {
                return RedirectToLoginPage(
                    "Discord-Login ist noch nicht konfiguriert. Bitte Client-ID und Secret in den User Secrets hinterlegen.");
            }

            // carry the entry context (public application vs invite) into the callback
            var redirectUrl = $"/Account/ExternalLogin?returnUrl={Uri.EscapeDataString(returnUrl ?? "/dashboard")}";
            if (!string.IsNullOrWhiteSpace(source))
                redirectUrl += $"&source={Uri.EscapeDataString(source)}";
            if (!string.IsNullOrWhiteSpace(inviteToken))
                redirectUrl += $"&inviteToken={Uri.EscapeDataString(inviteToken)}";

            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                DiscordAuthenticationDefaults.AuthenticationScheme, redirectUrl);
            return Results.Challenge(properties, [DiscordAuthenticationDefaults.AuthenticationScheme]);
        }).RequireRateLimiting(LoginRateLimitPolicy);

        // callback after successful Discord authentication
        group.MapGet("/ExternalLogin", async (
            [FromServices] SignInManager<Agent> signInManager,
            [FromServices] UserManager<Agent> userManager,
            [FromServices] IConfiguration configuration,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] IAgentInviteService inviteService,
            [FromServices] IPublicModuleService publicModules,
            [FromServices] IAgentManagementService agentManagement,
            [FromQuery] string? returnUrl,
            [FromQuery] string? remoteError,
            [FromQuery] string? source,
            [FromQuery] string? inviteToken) =>
        {
            var logger = loggerFactory.CreateLogger("NOOSE.ExternalLogin");
            // empty (not just null) would make LocalRedirect throw
            returnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/dashboard" : returnUrl;

            if (remoteError is not null)
            {
                return RedirectToLoginPage($"Discord meldete einen Fehler: {remoteError}");
            }

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return RedirectToLoginPage("Externe Anmeldedaten konnten nicht gelesen werden.");
            }

            var agent = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (agent is null)
            {
                var isBootstrap = ReadBootstrapAdminIds(configuration).Contains(info.ProviderKey);
                if (isBootstrap)
                {
                    agent = await CreateAgentAsync(userManager, info, configuration, logger, AgentStatus.Pending);
                }
                else if (string.Equals(source, "invite", StringComparison.OrdinalIgnoreCase))
                {
                    // agent onboarding via secret invite link only
                    if (await inviteService.ValidateAsync(inviteToken) is null)
                    {
                        return RedirectToLoginPage("Einladungslink ungültig oder bereits verwendet.");
                    }
                    agent = await CreateAgentAsync(userManager, info, configuration, logger, AgentStatus.Pending);
                    if (agent is not null)
                    {
                        try { await inviteService.ConsumeAsync(inviteToken!, agent.Id); }
                        catch { /* best effort: account exists, invite race lost */ }
                    }
                }
                else if (string.Equals(source, "bewerbung", StringComparison.OrdinalIgnoreCase))
                {
                    // the module switch has to hold here too: hiding the button leaves this endpoint reachable
                    if (!await publicModules.IsEnabledAsync(PublicModules.Careers))
                    {
                        return RedirectToLoginPage(await publicModules.OfflineTextAsync(PublicModules.Careers));
                    }
                    agent = await CreateAgentAsync(userManager, info, configuration, logger, AgentStatus.Applicant);
                }
                else if (string.Equals(source, "buerger", StringComparison.OrdinalIgnoreCase))
                {
                    // public area: the account gets no agency rights at all, and only new sign-ups read the switch
                    if (!await publicModules.IsEnabledAsync(PublicModules.CitizenRegistration))
                    {
                        return RedirectToLoginPage(await publicModules.OfflineTextAsync(PublicModules.CitizenRegistration));
                    }
                    agent = await CreateAgentAsync(userManager, info, configuration, logger, AgentStatus.Civilian);
                }
                else
                {
                    // invite-only: a plain login can no longer self-register as an agent
                    return Results.LocalRedirect("/karriere");
                }

                if (agent is null)
                {
                    return RedirectToLoginPage("Das FIB-Konto konnte nicht angelegt werden.");
                }
            }
            else
            {
                await RefreshMasterDataAsync(userManager, agent, info);
                await EnsureBootstrapAdminSafeAsync(userManager, agent, configuration, logger);

                // a returning applicant who follows an invite link becomes a pending agent; a hired partner applied
                // without ever leaving status Active, so it reaches the same conversion from there
                if ((agent.Status == AgentStatus.Applicant
                        || (agent.Status == AgentStatus.Active && agent.PartnerAgency is not null))
                    && string.Equals(source, "invite", StringComparison.OrdinalIgnoreCase)
                    && await inviteService.RedeemForExistingAsync(inviteToken, agent.Id))
                {
                    return Results.Redirect("/Account/Ausstehend");
                }

                // a citizen who applies keeps the account and gains the applicant portal; the branch
                // above only ever ran for a brand-new login, so an existing citizen had no way in
                if (agent.Status == AgentStatus.Civilian
                    && string.Equals(source, "bewerbung", StringComparison.OrdinalIgnoreCase))
                {
                    // the module switch has to hold here too, exactly as it does for a new sign-up
                    if (!await publicModules.IsEnabledAsync(PublicModules.Careers))
                    {
                        return RedirectToLoginPage(await publicModules.OfflineTextAsync(PublicModules.Careers));
                    }
                    agent = await agentManagement.StartApplicationAsync(agent.Id);
                }
            }

            // the cases that sign in must stay in step with AgentStatusRules.MayHoldSession, or the circuit
            // revalidation evicts the account seconds after it arrives (AgentStatusRulesTests pins it)
            switch (agent.Status)
            {
                case AgentStatus.Active:
                    await signInManager.SignInAsync(agent, isPersistent: true);
                    // honor an explicit deep link; otherwise use the user's custom start page
                    if (returnUrl == "/dashboard" && TryGetStartRoute(agent, out var startRoute))
                    {
                        return Results.LocalRedirect(startRoute);
                    }
                    return Results.LocalRedirect(returnUrl);
                case AgentStatus.Applicant:
                    await signInManager.SignInAsync(agent, isPersistent: true);
                    return Results.LocalRedirect("/portal");
                case AgentStatus.Civilian:
                    await signInManager.SignInAsync(agent, isPersistent: true);
                    return Results.LocalRedirect("/buerger");
                case AgentStatus.Pending:
                    return Results.Redirect("/Account/Ausstehend");
                default:
                    return Results.Redirect("/Account/Gesperrt");
            }
        });
        // deliberately NOT rate limited: this is Discord's callback, reachable only with the state the challenge
        // above issued. Limiting it too spent two permits per sign-in and halved the real ceiling.

        group.MapPost("/Logout", async (
            [FromServices] SignInManager<Agent> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return Results.LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/Account/Login" : returnUrl);
        });

        return group;
    }

    private static IResult RedirectToLoginPage(string error)
        => Results.Redirect($"/Account/Login?fehler={Uri.EscapeDataString(error)}");

    /// <summary>Reads the user's custom start route from saved nav preferences; guards against open redirects.</summary>
    private static bool TryGetStartRoute(Agent agent, out string route)
    {
        route = "/dashboard";
        if (string.IsNullOrWhiteSpace(agent.NavPreferencesJson))
        {
            return false;
        }
        try
        {
            var prefs = System.Text.Json.JsonSerializer.Deserialize<NavPreferences>(agent.NavPreferencesJson);
            var start = prefs?.StartRoute;
            // local relative path only
            if (!string.IsNullOrWhiteSpace(start) && start.StartsWith('/') && !start.StartsWith("//"))
            {
                route = start;
                return true;
            }
        }
        catch
        {
            /* ignore */
        }
        return false;
    }

    /// <summary>Reads all bootstrap-admin Discord IDs from config (single <c>Bootstrap:AdminDiscordId</c> and list <c>Bootstrap:AdminDiscordIds</c>).</summary>
    private static HashSet<string> ReadBootstrapAdminIds(IConfiguration configuration)
        => BootstrapAdmins.Ids(configuration);

    private static async Task<Agent?> CreateAgentAsync(
        UserManager<Agent> userManager, ExternalLoginInfo info, IConfiguration configuration, ILogger logger,
        AgentStatus intendedStatus)
    {
        var discordId = info.ProviderKey;
        var username = info.Principal.FindFirstValue(ClaimTypes.Name);
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);

        var isBootstrapAdmin = ReadBootstrapAdminIds(configuration).Contains(discordId);

        var agent = new Agent
        {
            UserName = discordId, // Discord snowflake (digits only) satisfies identity name rules
            Email = email,
            EmailConfirmed = email is not null,
            // codename/real name/badge stay empty; an admin assigns master data, Discord name is never used as codename
            DiscordId = discordId,
            DiscordUsername = username,
            AvatarUrl = ExtractAvatarUrl(info.Principal),
            RegisteredAt = DateTime.UtcNow,
            Status = isBootstrapAdmin ? AgentStatus.Active : intendedStatus,
            Rank = isBootstrapAdmin ? Rank.Director : null,
            IsAdmin = isBootstrapAdmin,
            ReleasedAt = isBootstrapAdmin ? DateTime.UtcNow : null,
        };

        var create = await userManager.CreateAsync(agent);
        if (!create.Succeeded)
        {
            logger.LogError("Agent anlegen fehlgeschlagen ({DiscordId}): {Fehler}",
                discordId, string.Join("; ", create.Errors.Select(e => e.Description)));
            return null;
        }

        var addLogin = await userManager.AddLoginAsync(agent, info);
        if (!addLogin.Succeeded)
        {
            logger.LogError("Discord-Login verknüpfen fehlgeschlagen ({DiscordId}): {Fehler}",
                discordId, string.Join("; ", addLogin.Errors.Select(e => e.Description)));
            await userManager.DeleteAsync(agent);
            return null;
        }

        logger.LogInformation("Neuer Agent registriert: {DiscordUsername} ({DiscordId}), Status {Status}.",
            agent.DiscordUsername ?? discordId, discordId, agent.Status);
        return agent;
    }

    /// <summary>Keeps internal Discord master data (username, avatar) fresh on each login; the codename is never filled from Discord.</summary>
    private static async Task RefreshMasterDataAsync(UserManager<Agent> userManager, Agent agent, ExternalLoginInfo info)
    {
        var username = info.Principal.FindFirstValue(ClaimTypes.Name);
        var avatar = ExtractAvatarUrl(info.Principal);

        var modified = false;
        if (username is not null && username != agent.DiscordUsername) { agent.DiscordUsername = username; modified = true; }
        if (avatar is not null && avatar != agent.AvatarUrl) { agent.AvatarUrl = avatar; modified = true; }

        if (modified)
        {
            await userManager.UpdateAsync(agent);
        }
    }

    /// <summary>Promotes an already-registered agent to active admin if their Discord ID is a bootstrap admin; keeps an existing rank.</summary>
    private static async Task EnsureBootstrapAdminSafeAsync(
        UserManager<Agent> userManager, Agent agent, IConfiguration configuration, ILogger logger)
    {
        if (agent.DiscordId is null || !ReadBootstrapAdminIds(configuration).Contains(agent.DiscordId))
            return;

        if (agent is { IsAdmin: true, Status: AgentStatus.Active })
            return;

        agent.IsAdmin = true;
        agent.Status = AgentStatus.Active;
        agent.ReleasedAt ??= DateTime.UtcNow;
        agent.Rank ??= Rank.Director;

        var update = await userManager.UpdateAsync(agent);
        if (update.Succeeded)
            logger.LogInformation("Bootstrap-Admin befördert: {DiscordId}.", agent.DiscordId);
        else
            logger.LogError("Bootstrap-Admin-Beförderung fehlgeschlagen ({DiscordId}): {Fehler}",
                agent.DiscordId, string.Join("; ", update.Errors.Select(e => e.Description)));
    }

    private static string? ExtractAvatarUrl(ClaimsPrincipal principal)
        => principal.FindFirstValue("urn:discord:avatar:url")
           ?? principal.FindFirstValue("urn:discord:avatar");
}
