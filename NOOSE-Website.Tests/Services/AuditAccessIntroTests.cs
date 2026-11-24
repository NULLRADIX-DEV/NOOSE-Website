using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The intro text of the access log panel on the real policies.</summary>
public class AuditAccessIntroTests
{
    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging().AddNooseAuthorization().BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    private static ClaimsPrincipal Director() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // LeadershipPage only
    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static async Task<bool> MayOpenPanelAsync(ClaimsPrincipal user)
        => (await Authorization.AuthorizeAsync(user, Policies.LeadershipPage)).Succeeded;

    [Fact]
    public async Task ACounterIntelViewerGetsTheLink()
    {
        Assert.True(await MayOpenPanelAsync(Director()));
        Assert.True(await AuditAccessIntro.LinksCounterIntelAsync(Authorization, Director()));
    }

    [Fact]
    public async Task ALeadershipPageViewerWithoutCounterIntelGetsNoLink()
    {
        Assert.True(await MayOpenPanelAsync(Supervision()));
        Assert.False(await AuditAccessIntro.LinksCounterIntelAsync(Authorization, Supervision()));
    }

    [Fact]
    public async Task TheLinkTargetIsTheOverviewTheViewerMayOpen()
    {
        var access = RouteAccessTestHost.Create();

        Assert.Contains(AuditAccessIntro.CounterIntelRoute, await access.OpenableAsync(Director(), [AuditAccessIntro.CounterIntelRoute]));
        Assert.DoesNotContain(AuditAccessIntro.CounterIntelRoute, await access.OpenableAsync(Supervision(), [AuditAccessIntro.CounterIntelRoute]));
    }
}
