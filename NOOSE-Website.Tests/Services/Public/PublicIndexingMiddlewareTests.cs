using Microsoft.AspNetCore.Http;
using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The second indexing layer: a crawler that ignores robots.txt still gets a noindex header.</summary>
public class PublicIndexingMiddlewareTests
{
    private static async Task<string> HeaderForAsync(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var middleware = new PublicIndexingMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        return context.Response.Headers["X-Robots-Tag"].ToString();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/karriere")]
    [InlineData("/gesucht/FIB-F-2026-0001")]
    [InlineData("/datenschutz")]
    public async Task Public_routes_get_no_header(string path)
        => Assert.Equal(string.Empty, await HeaderForAsync(path));

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/personen/abc")]
    [InlineData("/einstellungen")]
    [InlineData("/buerger")]
    [InlineData("/not-found")]
    public async Task Internal_routes_get_noindex(string path)
        => Assert.Equal("noindex, nofollow", await HeaderForAsync(path));

    [Fact]
    public async Task The_pipeline_continues_either_way()
    {
        var reached = 0;
        var middleware = new PublicIndexingMiddleware(_ =>
        {
            reached++;
            return Task.CompletedTask;
        });

        var publicContext = new DefaultHttpContext();
        publicContext.Request.Path = "/";
        await middleware.InvokeAsync(publicContext);

        var internalContext = new DefaultHttpContext();
        internalContext.Request.Path = "/dashboard";
        await middleware.InvokeAsync(internalContext);

        Assert.Equal(2, reached);
    }

    [Fact]
    public async Task Setting_the_header_twice_does_not_duplicate_it()
    {
        // the status-code re-execute runs the middleware again on the same response
        var context = new DefaultHttpContext();
        context.Request.Path = "/dashboard";
        var middleware = new PublicIndexingMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(context);

        Assert.Single(context.Response.Headers["X-Robots-Tag"].ToArray());
    }
}
