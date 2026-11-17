using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Financing;
using NOOSE_Website.Infrastructure.Shares;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Administration;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <summary>Open-item counts behind the navigation badges, shared by drawer and app bar menus of one circuit.</summary>
public sealed class NavBadgeState(
    IAgentManagementService agentService,
    ITaskforceService taskforceService,
    IPersonnelFileService personnelFileService,
    IRequestService requestService,
    IPublicWantedService publicWanted,
    IBountyService bounty,
    IAnnouncementService boardService,
    IRegulationService regulationService,
    IFinancingService financingService,
    IWarrantService warrantService,
    ITipService tipService,
    IAdministrationChatService administrationChat,
    SharesBroadcaster sharesBroadcaster,
    AcknowledgmentBroadcaster acknowledgmentBroadcaster,
    FinancingBroadcaster financingBroadcaster,
    TipsBroadcaster tipsBroadcaster,
    AdministrationChatBroadcaster administrationChatBroadcaster,
    ILogger<NavBadgeState> logger) : IDisposable
{
    // reloads from several broadcasters must not interleave their writes
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private ClaimsPrincipal? _user;
    private Task? _initialized;
    private bool _subscribed;

    /// <summary>Fired after any count changed; handlers marshal onto their own renderer.</summary>
    public event Action? Changed;

    /// <summary>Open items behind a catalog badge key, 0 when unknown.</summary>
    public int Count(string? badgeKey) => badgeKey is not null && _counts.TryGetValue(badgeKey, out var n) ? n : 0;

    /// <summary>Loads once per circuit; live subscribes to the broadcasters, prerender passes false.</summary>
    public Task InitializeAsync(ClaimsPrincipal user, bool live)
    {
        // partners get no badges at all
        if (user.IsPartner())
        {
            return Task.CompletedTask;
        }
        _user ??= user;
        if (live && !_subscribed)
        {
            _subscribed = true;
            sharesBroadcaster.Modified += OnSharesModified;
            acknowledgmentBroadcaster.Modified += OnAcknowledgmentModified;
            financingBroadcaster.Modified += OnFinancingModified;
            tipsBroadcaster.Modified += OnTipsModified;
            administrationChatBroadcaster.Modified += OnAdministrationChatModified;
            administrationChatBroadcaster.ReadMarked += OnAdministrationChatRead;
        }
        return _initialized ??= ReloadAsync(LoadSharesAsync, LoadAcknowledgmentsAsync, LoadRegulationAsync,
            LoadFinancingAsync, LoadTipsAsync, LoadAdministrationChatAsync);
    }

    private async Task ReloadAsync(params Func<ClaimsPrincipal, Task>[] loaders)
    {
        if (_user is not { } user)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            foreach (var load in loaders)
            {
                await load(user);
            }
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    // decorative badge: a DB error here must not tear the prerender or the circuit
    private Task LoadAcknowledgmentsAsync(ClaimsPrincipal user)
        => Set("acknowledgments", () => boardService.GetOpenAcknowledgmentsCountAsync(user), "Quittierungs-Badge");

    private Task LoadRegulationAsync(ClaimsPrincipal user)
        => Set("regulation", () => regulationService.GetOpenCountAsync(user), "Badge der Dienstverordnung");

    // sums open inbox items (same sources as the shares page)
    private Task LoadSharesAsync(ClaimsPrincipal user) => Set("shares", async () =>
    {
        var open = 0;

        // classification-upgrade requests are decided from Senior Special Agent up
        if (user.MayHighestClassification())
        {
            open += await requestService.GetOpenCountAsync(user.IsLeadership());
            // added here rather than inside GetOpenCountAsync: the dashboard reads that one unconditionally for
            // every agent and labels the number "Hochstufung"
            open += await publicWanted.GetPendingRequestCountAsync();
            open += await bounty.GetPendingRequestCountAsync();
        }

        // registrations, name changes, profile pictures, taskforce and promotion requests are leadership-only
        if (user.IsLeadership())
        {
            open += (await agentService.GetPendingAsync()).Count
                + (await agentService.GetPendingNameChangesAsync()).Count
                + (await agentService.GetPendingAvatarsAsync()).Count
                + (await taskforceService.GetRequestedAsync()).Count
                + await personnelFileService.CountOpenPromotionRequestsAsync(user)
                // zero for anyone who may not sign
                + await warrantService.GetOpenCountAsync(user);
        }
        return open;
    }, "Freigaben-Badge");

    // the entry is visible to every agent, but only deciders get a count
    private Task LoadFinancingAsync(ClaimsPrincipal user)
        => Set("financing", () => user.IsLeadership() ? financingService.GetOpenCountAsync() : Task.FromResult(0),
            "Finanzierungs-Badge");

    // members who may write carry a read mark
    private Task LoadAdministrationChatAsync(ClaimsPrincipal user)
        => Set("administration-chat", async () => user.MayAdministrationWrite()
            ? (await administrationChat.GetReadStateAsync(user)).Unread
            : 0, "Administration-Badge");

    // every internal agent works the tip inbox
    private Task LoadTipsAsync(ClaimsPrincipal user)
        => Set("tips", () => user.IsInternalAgent() ? tipService.GetOpenCountAsync() : Task.FromResult(0), "Hinweis-Badge");

    private async Task Set(string key, Func<Task<int>> count, string what)
    {
        try
        {
            _counts[key] = await count();
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "{Badge} konnte nicht geladen werden.", what);
        }
    }

    // a handler must never throw back into the broadcaster's invocation chain, or the live
    // subscribers later in the chain are skipped and the triggering page catches the fault
    private void Fire(params Func<ClaimsPrincipal, Task>[] loaders)
        => _ = Task.Run(async () =>
        {
            try { await ReloadAsync(loaders); }
            catch (Exception ex) { /* best effort */ logger.LogWarning(ex, "Navigations-Badges konnten nicht neu geladen werden."); }
        });

    private void OnSharesModified() => Fire(LoadSharesAsync);

    private void OnFinancingModified() => Fire(LoadFinancingAsync);

    private void OnTipsModified(string tipId, string caseNumber, TipMessageAudience? audience) => Fire(LoadTipsAsync);

    private void OnAdministrationChatModified(string room) => Fire(LoadAdministrationChatAsync);

    // only the own mark counts, another reader changes nothing here
    private void OnAdministrationChatRead(string agentId)
    {
        if (string.Equals(agentId, _user?.GetAgentId(), StringComparison.Ordinal))
        {
            Fire(LoadAdministrationChatAsync);
        }
    }

    // only react to this agent's own acknowledgment signal
    private void OnAcknowledgmentModified(string agentId)
    {
        if (string.Equals(agentId, _user?.GetAgentId(), StringComparison.Ordinal))
        {
            Fire(LoadAcknowledgmentsAsync, LoadRegulationAsync);
        }
    }

    public void Dispose()
    {
        sharesBroadcaster.Modified -= OnSharesModified;
        acknowledgmentBroadcaster.Modified -= OnAcknowledgmentModified;
        financingBroadcaster.Modified -= OnFinancingModified;
        tipsBroadcaster.Modified -= OnTipsModified;
        administrationChatBroadcaster.Modified -= OnAdministrationChatModified;
        administrationChatBroadcaster.ReadMarked -= OnAdministrationChatRead;
        _gate.Dispose();
    }
}
