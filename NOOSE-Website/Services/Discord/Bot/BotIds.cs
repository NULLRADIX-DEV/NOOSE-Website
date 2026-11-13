namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>custom_id format "fib:action:arg1:arg2"; it carries ids only, never rights.</summary>
public static class BotIds
{
    public const string Prefix = "fib";
    private const int MaxLength = 100;

    public static string Make(string action, params string?[] args)
    {
        var id = string.Join(':', new[] { Prefix, action }.Concat(args.Select(a => a ?? string.Empty)));
        if (id.Length > MaxLength)
        {
            throw new InvalidOperationException("custom_id too long");
        }
        return id;
    }

    /// <summary>The action and its arguments, or null for a foreign id.</summary>
    public static (string Action, string[] Args)? Parse(string? customId)
    {
        if (string.IsNullOrEmpty(customId))
        {
            return null;
        }
        var parts = customId.Split(':');
        if (parts.Length < 2 || parts[0] != Prefix)
        {
            return null;
        }
        return (parts[1], parts[2..]);
    }

    public static string Arg(this string[] args, int index) => index < args.Length ? args[index] : string.Empty;
}

/// <summary>Short-lived memory for state that does not fit a custom_id (a search text, a message to attach).</summary>
public sealed class BotScratch(Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public string Put<T>(T value) where T : class
    {
        var key = Guid.NewGuid().ToString("N")[..12];
        Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(cache, "bot:" + key, value, Lifetime);
        return key;
    }

    public T? Get<T>(string key) where T : class
        => Microsoft.Extensions.Caching.Memory.CacheExtensions.TryGetValue(cache, "bot:" + key, out T? value) ? value : null;
}
