using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ISystemSettingService" />
public partial class SystemSettingService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    IWebHostEnvironment env,
    IOptions<FileUploadOptions> uploadOptions) : ISystemSettingService
{
    private const string CacheKey = "SystemKonfiguration";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    // served via endpoint
    private string LogoBasePath => Path.Combine(env.ContentRootPath, "App_Data", "uploads", "system");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColourRegex();

    public async Task<SystemConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out SystemConfiguration? configuration) && configuration is not null)
        {
            return configuration;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var values = await db.SystemSettings
                .ToDictionaryAsync(e => e.Key, e => e.Value, cancellationToken);

            // normalise on read too, so a hand-edited row cannot invert yellow and red
            var (window, yellow, red) = AttendanceAnomalyLogic.Coherent(
                ParseCount(values.GetValueOrDefault(SystemSettingKeys.MeetingWindowSize), MeetingAnomalyDefaults.WindowSize),
                ParseCount(values.GetValueOrDefault(SystemSettingKeys.MeetingAnomalyYellow), MeetingAnomalyDefaults.Yellow),
                ParseCount(values.GetValueOrDefault(SystemSettingKeys.MeetingAnomalyRed), MeetingAnomalyDefaults.Red));

            configuration = new SystemConfiguration(
                MaintenanceModeActive: string.Equals(values.GetValueOrDefault(SystemSettingKeys.MaintenanceModeActive), "true", StringComparison.OrdinalIgnoreCase),
                MaintenanceModeText: Empty(values.GetValueOrDefault(SystemSettingKeys.MaintenanceModeText)),
                BannerText: Empty(values.GetValueOrDefault(SystemSettingKeys.BannerText)),
                BannerLevel: Empty(values.GetValueOrDefault(SystemSettingKeys.BannerLevel)) ?? BannerLevels.Info,
                ThemePrimary: Empty(values.GetValueOrDefault(SystemSettingKeys.ThemePrimary)),
                ThemeSecondary: Empty(values.GetValueOrDefault(SystemSettingKeys.ThemeSecondary)),
                ThemeTertiary: Empty(values.GetValueOrDefault(SystemSettingKeys.ThemeTertiary)),
                LogoFileName: Empty(values.GetValueOrDefault(SystemSettingKeys.LogoFileName)),
                LogoContentType: Empty(values.GetValueOrDefault(SystemSettingKeys.LogoContentType)),
                DemoModeActive: string.Equals(values.GetValueOrDefault(SystemSettingKeys.DemoModeActive), "true", StringComparison.OrdinalIgnoreCase),
                WantedBoardMinHazard: ParseHazard(values.GetValueOrDefault(SystemSettingKeys.WantedBoardMinHazard)),
                MeetingWindowSize: window,
                MeetingAnomalyYellow: yellow,
                MeetingAnomalyRed: red);
        }
        catch (Exception)
        {
            return Default();
        }

        cache.Set(CacheKey, configuration, CacheDuration);
        return configuration;
    }

    public async Task SaveAsync(SystemConfigurationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        foreach (var colour in new[] { input.ThemePrimary, input.ThemeSecondary, input.ThemeTertiary })
        {
            if (!string.IsNullOrWhiteSpace(colour) && !HexColourRegex().IsMatch(colour.Trim()))
            {
                throw new InvalidOperationException($"„{colour}“ ist keine gültige Farbe – bitte als Hex-Wert angeben (z. B. #71614A).");
            }
        }
        if (!BannerLevels.All.Contains(input.BannerLevel))
        {
            input.BannerLevel = BannerLevels.Info;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // demo mode is bootstrap-admin-only; a normal admin may save everything else, but never flip this
        var currentDemo = string.Equals(
            await ValueAsync(db, SystemSettingKeys.DemoModeActive, cancellationToken), "true", StringComparison.OrdinalIgnoreCase);
        if (input.DemoModeActive != currentDemo)
        {
            Permission.RequireBootstrapAdmin(actor);
        }

        await SetAsync(db, SystemSettingKeys.MaintenanceModeActive, input.MaintenanceModeActive ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.MaintenanceModeText, Empty(input.MaintenanceModeText), cancellationToken);
        await SetAsync(db, SystemSettingKeys.BannerText, Empty(input.BannerText), cancellationToken);
        await SetAsync(db, SystemSettingKeys.BannerLevel, input.BannerLevel, cancellationToken);
        await SetAsync(db, SystemSettingKeys.ThemePrimary, Empty(input.ThemePrimary)?.Trim(), cancellationToken);
        await SetAsync(db, SystemSettingKeys.ThemeSecondary, Empty(input.ThemeSecondary)?.Trim(), cancellationToken);
        await SetAsync(db, SystemSettingKeys.ThemeTertiary, Empty(input.ThemeTertiary)?.Trim(), cancellationToken);
        await SetAsync(db, SystemSettingKeys.DemoModeActive, input.DemoModeActive ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.WantedBoardMinHazard, HazardValid(input.WantedBoardMinHazard).ToString(), cancellationToken);

        var (window, yellow, red) = AttendanceAnomalyLogic.Coherent(
            input.MeetingWindowSize, input.MeetingAnomalyYellow, input.MeetingAnomalyRed);
        await SetAsync(db, SystemSettingKeys.MeetingWindowSize, window.ToString(), cancellationToken);
        await SetAsync(db, SystemSettingKeys.MeetingAnomalyYellow, yellow.ToString(), cancellationToken);
        await SetAsync(db, SystemSettingKeys.MeetingAnomalyRed, red.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        cache.Remove(CacheKey);
    }

    public async Task LogoSetAsync(Stream content, string originalName, string contentType, long sizeBytes, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        var options = uploadOptions.Value;
        if (!options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Nur Bilddateien (JPG, PNG, WebP, GIF) sind als Logo erlaubt.");
        }
        if (sizeBytes > options.MaxBytes)
        {
            throw new InvalidOperationException($"Das Logo ist zu groß (max. {options.MaxBytes / (1024 * 1024)} MB).");
        }

        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(extension) || extension.Length > 12 || extension.Skip(1).Any(c => !char.IsLetterOrDigit(c)))
        {
            extension = ".bin";
        }
        var fileName = $"logo-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";

        Directory.CreateDirectory(LogoBasePath);
        var target = Path.Combine(LogoBasePath, fileName);
        await using (var fs = File.Create(target))
        {
            await content.CopyToAsync(fs, cancellationToken);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var altes = await ValueAsync(db, SystemSettingKeys.LogoFileName, cancellationToken);
        await SetAsync(db, SystemSettingKeys.LogoFileName, fileName, cancellationToken);
        await SetAsync(db, SystemSettingKeys.LogoContentType, contentType, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        AlteLogoFileDelete(altes);
        cache.Remove(CacheKey);
    }

    public async Task LogoRemoveAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var altes = await ValueAsync(db, SystemSettingKeys.LogoFileName, cancellationToken);
        await SetAsync(db, SystemSettingKeys.LogoFileName, null, cancellationToken);
        await SetAsync(db, SystemSettingKeys.LogoContentType, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        AlteLogoFileDelete(altes);
        cache.Remove(CacheKey);
    }

    public async Task<(Stream Content, string ContentType)?> LogoOpenAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await GetAsync(cancellationToken);
        if (!configuration.HasLogo)
        {
            return null;
        }
        var path = FilePathHelper.SafePath(LogoBasePath, configuration.LogoFileName!);
        if (!File.Exists(path))
        {
            return null;
        }
        return (File.OpenRead(path), configuration.LogoContentType ?? "application/octet-stream");
    }

    private static SystemConfiguration Default()
        => new(false, null, null, BannerLevels.Info, null, null, null, null, null, false, HazardLevel.Critical,
               MeetingAnomalyDefaults.WindowSize, MeetingAnomalyDefaults.Yellow, MeetingAnomalyDefaults.Red);

    // unparseable or missing falls back to the code default
    private static int ParseCount(string? value, int fallback)
        => int.TryParse(value, out var n) ? n : fallback;

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // stored as the enum name; unknown/missing falls back to the strictest default
    private static HazardLevel ParseHazard(string? value)
        => Enum.TryParse<HazardLevel>(value, out var level) && Enum.IsDefined(level) ? level : HazardLevel.Critical;

    private static HazardLevel HazardValid(HazardLevel level)
        => Enum.IsDefined(level) ? level : HazardLevel.Critical;

    private static async Task<string?> ValueAsync(AppDbContext db, string key, CancellationToken cancellationToken)
        => (await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken))?.Value;

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }

    private void AlteLogoFileDelete(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        try
        {
            var path = FilePathHelper.SafePath(LogoBasePath, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            /* ignore */
        }
    }
}
