using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>404 and error page answer every visitor; only an active agent gets the internal shell and its menu.</summary>
/// <remarks>
/// Both pages are anonymous and used to sit in <c>MainLayout</c>, whose drawer rendered the internal menu for any
/// signed-in account. A citizen passes <see cref="Policies.InternalAgent"/> (it only shuts out partners, the page
/// gate in <c>_Imports.razor</c> does the rest), so a mistyped URL showed them every internal area.
/// </remarks>
public sealed class StatusPageShellTests
{
    private static string Components([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "Components"));

    private static string Read(params string[] parts)
    {
        var file = Path.Combine(new[] { Components() }.Concat(parts).ToArray());
        Assert.True(File.Exists(file), $"Datei nicht gefunden: {file}");
        return File.ReadAllText(file);
    }

    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddNooseAuthorization()
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    private static async Task<bool> PassesAsync(ClaimsPrincipal user, string policy)
        => (await Authorization.AuthorizeAsync(user, policy)).Succeeded;

    public static TheoryData<string, ClaimsPrincipal, bool> Visitors() => new()
    {
        { "anonym", ClaimsPrincipalBuilder.Anonymous(), false },
        { "Bürger", ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian).Build(), false },
        { "Bewerber", ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Applicant).Build(), false },
        { "ausstehend", ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Pending).Build(), false },
        { "gesperrt", ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Blocked).Build(), false },
        { "gekündigt", ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Terminated).Build(), false },
        { "Agent", ClaimsPrincipalBuilder.Agent().Build(), true },
        { "Partner", ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(), true },
        { "Demo", ClaimsPrincipalBuilder.Agent().AsDemo().Build(), true },
    };

    [Theory]
    [MemberData(nameof(Visitors))]
    public async Task Only_an_active_account_passes_the_shell_gate(string who, ClaimsPrincipal user, bool expected)
        => Assert.True(await PassesAsync(user, Policies.ActiveAgent) == expected, who);

    [Fact]
    public async Task A_citizen_passes_InternalAgent_so_it_cannot_be_the_shell_gate()
        => Assert.True(await PassesAsync(ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian).Build(), Policies.InternalAgent));

    [Theory]
    [InlineData("NotFound.razor")]
    [InlineData("Error.razor")]
    public void Status_page_picks_its_shell_by_visitor(string page)
    {
        var source = Read("Pages", page);
        Assert.Matches(new Regex(@"^@layout StatusPageLayout\s*$", RegexOptions.Multiline), source);
        Assert.DoesNotContain("@layout MainLayout", source);
    }

    [Theory]
    [InlineData("NotFound.razor")]
    [InlineData("Error.razor")]
    public void Status_page_sends_only_an_active_agent_to_the_dashboard(string page)
    {
        var source = Read("Pages", page);
        Assert.Single(Regex.Matches(source, "/dashboard"));
        Assert.Matches(new Regex(
            @"<AuthorizeView Policy=""@Policies\.ActiveAgent""[^>]*>\s*<Authorized[^>]*>\s*<MudButton Href=""/dashboard"""),
            source);
    }

    [Fact]
    public void Status_page_shell_gives_only_an_active_agent_the_internal_layout()
    {
        var source = Read("Layout", "StatusPageLayout.razor");
        Assert.Matches(new Regex(
            @"<AuthorizeView Policy=""@Policies\.ActiveAgent""[^>]*>\s*<Authorized[^>]*>\s*<LayoutView Layout=""typeof\(MainLayout\)"">\s*@Body\s*</LayoutView>\s*</Authorized>\s*" +
            @"<NotAuthorized[^>]*>\s*<LayoutView Layout=""typeof\(PublicSiteLayout\)"">\s*@Body\s*</LayoutView>\s*</NotAuthorized>"),
            source);
    }

    [Fact]
    public void Internal_menu_and_palette_render_only_for_an_active_agent()
    {
        var source = Read("Layout", "MainLayout.razor");
        Assert.Matches(new Regex(@"<AuthorizeView Policy=""@Policies\.ActiveAgent""[^>]*>\s*<Authorized[^>]*>\s*<NavMenu />"), source);
        Assert.Matches(new Regex(@"<AuthorizeView Policy=""@Policies\.ActiveAgent""[^>]*>\s*<Authorized[^>]*>\s*<CommandPalette "), source);
    }
}
