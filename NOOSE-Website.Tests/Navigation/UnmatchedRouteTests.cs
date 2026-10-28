using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NOOSE_Website.Authorization;
using NOOSE_Website.Components;
using NOOSE_Website.Components.Pages;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>An unknown address gets the 404 page with its whole shell, still as 404 and noindex.</summary>
/// <remarks>
/// Runs the real App, router, status pages and public shell on Kestrel, with a module service that answers late like
/// a cold cache. On .NET 10 a page rendered by <c>UseStatusCodePagesWithReExecute</c> does not wait for such a load
/// (the renderer drops pending tasks of a re-executed request, dotnet/aspnetcore#64034), so the tab bar went out with
/// only "Start". <see cref="UnmatchedRoute"/> takes those addresses in their own request instead.
/// </remarks>
public sealed class UnmatchedRouteTests(UnmatchedRouteTests.Site site) : IClassFixture<UnmatchedRouteTests.Site>
{
    private const string LateTab = "Nachgeladener Reiter";

    [Theory]
    [InlineData("/gibt-es-nicht")]
    [InlineData("/ordner/unter/seite")]
    [InlineData("/datei.js")]
    public async Task Unknown_address_waits_for_the_shell(string path)
    {
        using var response = await site.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("noindex, nofollow", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("Seite nicht gefunden", body);
        Assert.Contains(LateTab, body);
    }

    [Fact]
    public void Catch_all_stays_out_of_the_interactive_router()
        => Assert.True(typeof(UnmatchedRoute).IsDefined(typeof(ExcludeFromInteractiveRoutingAttribute), inherit: false),
            "a routable catch-all would swallow every unknown link inside a circuit");

    [Fact]
    public async Task Re_executed_status_page_still_skips_late_loads_on_this_runtime()
    {
        using var response = await site.Client.GetAsync("/leere-404");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Seite nicht gefunden", body);
        // red once the runtime waits here too (dotnet/aspnetcore#64034, .NET 11): UnmatchedRoute can go then
        Assert.DoesNotContain(LateTab, body);
    }

    public sealed class Site : IAsyncLifetime
    {
        private WebApplication _app = default!;

        public HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var snapshot = new PublicModuleSnapshot(false,
            [
                new PublicModuleState("nachgeladen", LateTab, "", "", "/nachgeladen", PublicModuleGroup.Service, 0,
                    IsEnabled: true, "", Available: true),
            ]);
            var modules = Substitute.For<IPublicModuleService>();
            modules.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => Late(snapshot));
            modules.NavEntriesAsync(Arg.Any<CancellationToken>()).Returns(_ => Late(snapshot.NavEntries()));

            builder.Services.AddRazorComponents();
            builder.Services.AddMudServices();
            builder.Services.AddAuthentication();
            builder.Services.AddNooseAuthorization();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(Substitute.For<INotificationService>());

            _app = builder.Build();
            // the part of the Program.cs pipeline a 404 runs through
            _app.UseMiddleware<PublicIndexingMiddleware>();
            _app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            _app.UseAuthentication();
            _app.UseAuthorization();
            _app.UseAntiforgery();
            _app.MapGet("/leere-404", () => Results.NotFound());
            _app.MapRazorComponents<App>();
            await _app.StartAsync();

            var address = _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            Client = new HttpClient { BaseAddress = new Uri(address) };
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static async Task<T> Late<T>(T value)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            return value;
        }
    }
}
