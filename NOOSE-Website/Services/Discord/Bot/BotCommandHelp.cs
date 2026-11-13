using System.Text;
using NetCord;
using NetCord.Rest;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>
/// The command list for the commands channel, written from <see cref="BotCommandCatalog"/> itself: a new or changed
/// command changes the list, so it can never drift from what Discord offers. One message per section keeps each
/// well under Discord's text limit.
/// </summary>
public static class BotCommandHelp
{
    /// <summary>The list as messages in posting order, and one hash over all of them.</summary>
    public static (IReadOnlyList<BoardCard> Messages, string Hash) Render()
    {
        var commands = BotCommandCatalog.All().ToDictionary(c => c.Name);
        var messages = new List<BoardCard>();
        var all = new StringBuilder();
        for (var i = 0; i < BotCommandCatalog.Sections.Count; i++)
        {
            var (emoji, title, names) = BotCommandCatalog.Sections[i];
            var entries = names.Where(commands.ContainsKey).Select(n => Entry(commands[n])).ToList();
            var parts = new List<IComponentContainerComponentProperties?>();
            if (i == 0)
            {
                parts.Add(DiscordCards.Text("# 🤖 Befehle des FIB-Bots\n"
                    + "Tippe `/` und den Namen, Discord zeigt die Felder an. `feld:…` musst du ausfüllen, `[feld]` kannst du. "
                    + "Antworten siehst nur du; mit `teilen: True` sieht sie der ganze Kanal. Du siehst und änderst immer nur, "
                    + "was du auch auf fibhq.com darfst.\n-# Diese Liste aktualisiert sich von selbst."));
                parts.Add(DiscordCards.Line(large: true));
            }
            parts.Add(DiscordCards.Text($"## {emoji} {title}"));
            parts.Add(DiscordCards.Text(string.Join("\n\n", entries)));
            IMessageComponentProperties[] message = [DiscordCards.Card(DiscordCards.Bronze, parts)];
            DiscordCards.FitText(message);
            var text = $"{title}\n{string.Join("\n", entries)}";
            all.Append(text).Append('\u0001');
            messages.Add(new BoardCard(message, DiscordLiveBoards.Hash(text)));
        }
        return (messages, DiscordLiveBoards.Hash(all.ToString()));
    }

    /// <summary>One command: how to type it, what it does, what every field means.</summary>
    public static string Entry(ApplicationCommandProperties command) => command switch
    {
        SlashCommandProperties slash when (slash.Options ?? []).Any(o => o.Type == ApplicationCommandOptionType.SubCommand)
            => string.Join("\n\n", slash.Options!.Where(o => o.Type == ApplicationCommandOptionType.SubCommand)
                .Select(sub => Slash($"{slash.Name} {sub.Name}", $"{slash.Description}: {sub.Description}", sub.Options))),
        SlashCommandProperties slash => Slash(slash.Name, slash.Description, slash.Options),
        UserCommandProperties user => $"**{user.Name}**\n{ContextHelp(user.Name)}\n-# Rechtsklick auf ein Mitglied → Apps → {user.Name}",
        MessageCommandProperties message => $"**{message.Name}**\n{ContextHelp(message.Name)}\n-# Rechtsklick auf eine Nachricht → Apps → {message.Name}",
        _ => $"**{command.Name}**",
    };

    private static string Slash(string name, string description, IEnumerable<ApplicationCommandOptionProperties>? options)
    {
        var fields = (options ?? []).ToList();
        var usage = string.Concat(fields.Select(f => f.Required == true ? $" {f.Name}:…" : $" [{f.Name}]"));
        var text = new StringBuilder($"**`/{name}{usage}`**\n{description}");
        foreach (var field in fields)
        {
            var notes = new List<string> { field.Required == true ? "Pflicht" : "optional" };
            if (field.Autocomplete == true)
            {
                notes.Add("mit Vorschlägen");
            }
            if (field.Choices?.ToList() is { Count: > 0 } choices)
            {
                notes.Add("Auswahl: " + string.Join(", ", choices.Select(c => c.Name)));
            }
            if (field.Type == ApplicationCommandOptionType.Boolean)
            {
                notes.Add("True oder False");
            }
            // "Grund (optional)" would read "(optional) (optional)" here
            var hint = field.Description.EndsWith(" (optional)", StringComparison.Ordinal) ? field.Description[..^11] : field.Description;
            text.Append($"\n-# • `{field.Name}`: {hint} ({string.Join(", ", notes)})");
        }
        return text.ToString();
    }

    private static string ContextHelp(string name) => name switch
    {
        BotCommandCatalog.AgentProfile => "Agentenprofil des Mitglieds: Dienstgrad, Abteilung, Abmeldung, Ehrungen.",
        BotCommandCatalog.AttachToRecord => "Hängt die Nachricht samt Bildern an eine Akte; Akte und Anmerkung trägst du im Fenster danach ein.",
        _ => "",
    };
}
