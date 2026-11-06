using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>
/// One Chromium for the whole run. The app under test runs elsewhere (demo mode, see scripts/handy-tests-local.sh
/// and .github/workflows/handy-tests.yml); its address comes from <c>NOOSE_E2E_BASEURL</c>.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const string BaseUrlVariable = "NOOSE_E2E_BASEURL";
    public const string ArtifactsVariable = "NOOSE_E2E_ARTIFACTS";
    public const string BaselineUrlVariable = "NOOSE_E2E_BASELINE_URL";

    /// <summary>Base address of the app under test, without trailing slash; null when the variable is missing.</summary>
    public static string? BaseUrl
    {
        get
        {
            var url = Environment.GetEnvironmentVariable(BaseUrlVariable);
            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }
    }

    /// <summary>
    /// Base address of a master build for the desktop comparison, without trailing slash; null when the variable is
    /// missing.
    /// </summary>
    public static string? BaselineUrl
    {
        get
        {
            var url = Environment.GetEnvironmentVariable(BaselineUrlVariable);
            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }
    }

    /// <summary>Where screenshots of failing routes go (uploaded as a CI artifact).</summary>
    public static string ArtifactsDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(ArtifactsVariable);
            return string.IsNullOrWhiteSpace(dir) ? Path.Combine(AppContext.BaseDirectory, "e2e-artifacts") : dir;
        }
    }

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        if (BaseUrl is null)
            return; // every test is skipped, no browser needed
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    /// <summary>A fresh context (own cookies, own circuit) with one page at the given viewport.</summary>
    public Task<IPage> NewPageAsync(int width, int height) => NewPageAsync(width, height, BaseUrl!);

    /// <summary>A fresh context with one page at the given viewport against another app (e.g. the master baseline).</summary>
    public async Task<IPage> NewPageAsync(int width, int height, string baseUrl)
    {
        if (_browser is null)
            throw new InvalidOperationException($"{BaseUrlVariable} is not set.");
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            Locale = "de-DE",
            TimezoneId = "Europe/Berlin",
            ReducedMotion = ReducedMotion.Reduce,
        });
        return await context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "E2E";
}

/// <summary>A fact that is skipped unless <c>NOOSE_E2E_BASEURL</c> points at a running app.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (E2EFixture.BaseUrl is null)
            Skip = $"{E2EFixture.BaseUrlVariable} is not set (no app under test).";
    }
}

/// <summary>
/// A theory that is skipped unless both <c>NOOSE_E2E_BASEURL</c> (branch) and <c>NOOSE_E2E_BASELINE_URL</c> (master)
/// point at a running app.
/// </summary>
public sealed class E2EBaselineTheoryAttribute : SkippableTheoryAttribute
{
    public E2EBaselineTheoryAttribute()
    {
        if (E2EFixture.BaseUrl is null)
            Skip = $"{E2EFixture.BaseUrlVariable} is not set (no app under test).";
        else if (E2EFixture.BaselineUrl is null)
            Skip = $"{E2EFixture.BaselineUrlVariable} is not set (no master build to compare with).";
    }
}

/// <summary>
/// A theory that is skipped unless <c>NOOSE_E2E_BASEURL</c> points at a running app. A case may also skip itself at
/// run time with <c>Skip.If</c> (Xunit.SkippableFact), e.g. when the demo data has no record of a kind.
/// </summary>
public sealed class E2ETheoryAttribute : SkippableTheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (E2EFixture.BaseUrl is null)
            Skip = $"{E2EFixture.BaseUrlVariable} is not set (no app under test).";
    }
}
