using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Only the demo instance turns anonymous visitors into the demo agent; production never does.</summary>
public class DemoModeMiddlewareTests
{
    private static IConfiguration Config(string? autoSetup)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DemoInstance.ConfigKey] = autoSetup })
            .Build();

    private static async Task<ClaimsPrincipal> UserAfterAsync(string? autoSetup, string path, ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (user is not null)
        {
            context.User = user;
        }
        var middleware = new DemoModeMiddleware(_ => Task.CompletedTask, Config(autoSetup));

        await middleware.InvokeAsync(context);

        return context.User;
    }

    [Fact]
    public async Task Demo_instance_shows_an_anonymous_visitor_as_the_demo_agent()
        => Assert.True((await UserAfterAsync("true", "/dashboard")).IsDemo());

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task Production_keeps_an_anonymous_visitor_anonymous(string? autoSetup)
    {
        var user = await UserAfterAsync(autoSetup, "/dashboard");

        Assert.False(user.IsDemo());
        Assert.NotEqual(true, user.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/_blazor")]
    [InlineData("/health")]
    public async Task Demo_instance_leaves_login_and_framework_paths_anonymous(string path)
        => Assert.False((await UserAfterAsync("true", path)).IsDemo());

    [Fact]
    public async Task Demo_instance_keeps_a_signed_in_agent()
    {
        var agent = ClaimsPrincipalBuilder.Agent("a1").Build();

        var user = await UserAfterAsync("true", "/dashboard", agent);

        Assert.Same(agent, user);
    }
}
