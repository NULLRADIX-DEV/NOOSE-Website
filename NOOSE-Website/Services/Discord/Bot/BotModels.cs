using System.Globalization;
using System.Security.Claims;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Data.Entities;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Where and by whom an interaction came in; only ids and the display name, never the token.</summary>
public sealed record BotContext(ulong UserId, string UserName, ulong? GuildId, ulong? ChannelId)
{
    public bool IsDm => GuildId is null;
}

/// <summary>A resolved FIB agent with the principal built fresh for this interaction.</summary>
public sealed record DiscordActor(Agent Agent, ClaimsPrincipal Principal);

/// <summary>Why the bot refused before running anything; the text is all the user gets.</summary>
public sealed record BotRefusal(string Text);

/// <summary>An attachment sent with a reply.</summary>
public sealed record BotFile(string Name, byte[] Content);

/// <summary>What a handler answers: a components-v2 card, a modal, or autocomplete choices.</summary>
public sealed class BotReply
{
    public IReadOnlyList<IMessageComponentProperties> Components { get; init; } = [];
    public IReadOnlyList<BotFile> Files { get; init; } = [];
    public ModalProperties? Modal { get; init; }
    public IReadOnlyList<ApplicationCommandOptionChoiceProperties>? Choices { get; init; }

    /// <summary>The answer is an error; a shared (public) deferral turns into a private one.</summary>
    public bool IsError { get; init; }

    public static BotReply Of(params IMessageComponentProperties[] components) => new() { Components = components };

    public static BotReply Of(IEnumerable<IMessageComponentProperties> components, IEnumerable<BotFile>? files = null)
        => new() { Components = components.ToList(), Files = files?.ToList() ?? [] };

    public static BotReply Error(string text) => new() { Components = [DiscordCards.Notice("⚠️", text, DiscordCards.Danger)], IsError = true };

    public static BotReply Info(string text) => new() { Components = [DiscordCards.Notice("ℹ️", text, DiscordCards.Bronze)] };

    public static BotReply Done(string text) => new() { Components = [DiscordCards.Notice("✅", text, DiscordCards.Success)] };

    public static BotReply OpenModal(ModalProperties modal) => new() { Modal = modal };

    public static BotReply Autocomplete(IEnumerable<ApplicationCommandOptionChoiceProperties> choices)
        => new() { Choices = choices.Take(25).ToList() };
}

/// <summary>Flat view of slash-command options (subcommand options lifted to the top).</summary>
public sealed class BotOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public BotOptions(IReadOnlyList<ApplicationCommandInteractionDataOption>? options)
    {
        Walk(options);
    }

    public BotOptions(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            _values[key] = value;
        }
    }

    /// <summary>The chosen subcommand, if any.</summary>
    public string? Sub { get; private set; }

    /// <summary>The option the user is typing in (autocomplete).</summary>
    public string? Focused { get; private set; }

    public string? Text(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public bool Flag(string name) => _values.TryGetValue(name, out var value) && bool.TryParse(value, out var on) && on;

    public long? Number(string name)
        => _values.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public ulong? Id(string name)
        => _values.TryGetValue(name, out var value) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private void Walk(IReadOnlyList<ApplicationCommandInteractionDataOption>? options)
    {
        if (options is null)
        {
            return;
        }
        foreach (var option in options)
        {
            if (option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
            {
                Sub = Sub is null ? option.Name : Sub + " " + option.Name;
                Walk(option.Options);
                continue;
            }
            _values[option.Name] = option.Value ?? string.Empty;
            if (option.Focused)
            {
                Focused = option.Name;
            }
        }
    }
}
