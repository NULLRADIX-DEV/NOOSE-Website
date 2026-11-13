using NetCord;
using NetCord.Rest;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>/gesetz, /dvo, /fibi, /lage, /funk.</summary>
public sealed class KnowledgeCommands(
    BotRecords records,
    ILawService laws,
    IRegulationService regulation,
    INooseiChatService fibi,
    IPublicSituationService situation,
    IRadioService radio)
{
    // ---------------------------------------------------------------- laws

    public async Task<BotReply> GesetzAsync(BotCall call)
    {
        var bookKey = call.Options.Text("buch");
        if (bookKey is null)
        {
            return BotReply.Error("Bitte ein Gesetzbuch angeben.");
        }
        var book = await FindBookAsync(bookKey, call);
        if (book is null)
        {
            return BotReply.Error($"Das Gesetzbuch „{DiscordCards.Inline(bookKey, 40)}“ gibt es nicht.");
        }
        var paragraph = call.Options.Text("paragraf")?.TrimStart('§').Trim();
        var link = await records.LinkAsync($"/gesetze/buch/{Uri.EscapeDataString(book.Book.Abbreviation)}", call.Ct);

        if (paragraph is null)
        {
            var lines = book.Paragraphs.Take(25).Select(l => $"`§ {DiscordCards.Clip(DiscordCards.OneLine(l.Paragraph).Replace("`", ""), 10)}` {DiscordCards.Inline(l.Title, 70)}");
            return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
                DiscordCards.Header("⚖️", $"{book.Book.Abbreviation} · {book.Book.Name}", $"{book.Book.ParagraphCount} Paragrafen"),
                DiscordCards.Line(),
                DiscordCards.Text(string.Join("\n", lines) + (book.Paragraphs.Count > 25 ? $"\n-# … und {book.Paragraphs.Count - 25} weitere" : "")),
                DiscordCards.Line(),
                DiscordCards.Text(DiscordCards.Footer),
                DiscordCards.Row(DiscordCards.Open(link, "Gesetzbuch öffnen"))));
        }

        var law = book.Paragraphs.FirstOrDefault(l => string.Equals(l.Paragraph.Trim(), paragraph, StringComparison.OrdinalIgnoreCase))
            ?? book.Paragraphs.FirstOrDefault(l => l.Id == paragraph)
            // a title instead of a number, typos forgiven
            ?? BotFuzzy.Single(book.Paragraphs, paragraph, l => [l.Title]);
        if (law is null)
        {
            return BotReply.Error($"§ {DiscordCards.Inline(paragraph, 10)} gibt es im {DiscordCards.Inline(book.Book.Abbreviation, 20)} nicht.");
        }
        var text = DiscordCards.PlainFromHtml(law.Text, 2800);
        var lawLink = await records.LinkAsync($"/gesetze/{law.Id}", call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("⚖️", $"§ {law.Paragraph} {book.Book.Abbreviation} – {law.Title}",
                string.IsNullOrWhiteSpace(law.Section) ? book.Book.Name : $"{book.Book.Name} · {DiscordCards.Inline(law.Section, 60)}"),
            DiscordCards.Line(),
            DiscordCards.Text(text.Length == 0 ? "_Kein Text hinterlegt._" : DiscordCards.Escape(text)),
            string.IsNullOrWhiteSpace(law.Sentence) ? null : DiscordCards.Line(),
            string.IsNullOrWhiteSpace(law.Sentence) ? null : DiscordCards.Text($"**⛓️ Strafmaß:** {DiscordCards.Inline(law.Sentence, 300)}"),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer + (law.IsPublic ? " · öffentlich" : " · intern")),
            DiscordCards.Row(DiscordCards.Open(lawLink, "Paragraf öffnen"), DiscordCards.Open(link, "Gesetzbuch"))));
    }

    public async Task<BotReply> GesetzSuggestAsync(BotCall call)
    {
        if (call.Options.Focused == "paragraf")
        {
            var bookKey = call.Options.Text("buch");
            if (bookKey is null)
            {
                return BotReply.Autocomplete([]);
            }
            var book = await FindBookAsync(bookKey, call);
            var typed = call.Options.Text("paragraf")?.TrimStart('§').Trim();
            return BotReply.Autocomplete(BotFuzzy.Rank(book?.Paragraphs ?? [], typed, l => [l.Paragraph, l.Title])
                .Select(l => new ApplicationCommandOptionChoiceProperties(DiscordCards.Clip($"§ {l.Paragraph} {DiscordCards.OneLine(l.Title)}", 100), DiscordCards.Clip(l.Paragraph, 100))));
        }
        var books = await laws.GetBooksAsync(call.Ct);
        return BotReply.Autocomplete(BotFuzzy.Rank(books, call.Options.Text("buch"), b => [b.Abbreviation, b.Name])
            .Select(b => new ApplicationCommandOptionChoiceProperties(DiscordCards.Clip($"{b.Abbreviation} – {b.Name}", 100), b.Abbreviation)));
    }

    /// <summary>The book by its abbreviation, else the one clear typo-tolerant match on abbreviation or name.</summary>
    private async Task<Models.Common.LawBookContent?> FindBookAsync(string key, BotCall call)
    {
        var book = await laws.GetBookAsync(key, cancellationToken: call.Ct);
        if (book is not null)
        {
            return book;
        }
        var match = BotFuzzy.Single(await laws.GetBooksAsync(call.Ct), key, b => [b.Abbreviation, b.Name]);
        return match is null ? null : await laws.GetBookAsync(match.Abbreviation, cancellationToken: call.Ct);
    }

    // ---------------------------------------------------------------- service regulation

    public async Task<BotReply> DvoAsync(BotCall call)
    {
        var input = call.Options.Text("stichwort");
        if (input is null)
        {
            return BotReply.Error("Bitte ein Stichwort angeben.");
        }
        var id = input.StartsWith("dvo:", StringComparison.Ordinal) ? input[4..] : null;
        if (id is null)
        {
            var picks = await regulation.SearchAsync(input, call.User, 10, call.Ct);
            if (picks.Count == 0)
            {
                return BotReply.Error("Dazu steht nichts in der Dienstverordnung, das du lesen darfst.");
            }
            if (picks.Count > 1)
            {
                var link = await records.LinkAsync("/dienstverordnung", call.Ct);
                var menu = new StringMenuProperties(BotIds.Make("dvo"));
                foreach (var pick in picks.Take(10))
                {
                    menu.Add(new StringMenuSelectOptionProperties(DiscordCards.Clip($"{pick.Label} {DiscordCards.OneLine(pick.Heading)}", 100), pick.Id));
                }
                return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
                    DiscordCards.Header("📘", "Dienstverordnung", $"{picks.Count} Absätze zu „{DiscordCards.Inline(input, 40)}“"),
                    DiscordCards.Text(string.Join("\n", picks.Take(10).Select(p => $"• **{DiscordCards.Inline(p.Label, 20)}** {DiscordCards.Inline(p.Heading, 80)}"))),
                    menu.WithPlaceholder("Absatz anzeigen"),
                    DiscordCards.Row(DiscordCards.Open(link, "Dienstverordnung öffnen"))));
            }
            id = picks[0].Id;
        }
        return await DvoParagraphAsync(id, call);
    }

    public async Task<BotReply> DvoParagraphAsync(string paragraphId, BotCall call)
    {
        var view = await regulation.GetParagraphAsync(paragraphId, null, call.User, call.Ct);
        if (view is null)
        {
            return BotReply.Error("Diesen Absatz gibt es nicht oder du darfst ihn nicht lesen.");
        }
        var link = await records.LinkAsync($"/dienstverordnung/absatz/{view.Row.Id}", call.Ct);
        var acknowledged = view.MustAcknowledge
            ? view.MyAcknowledgedVersion == view.ShownVersion ? "✅ von dir zur Kenntnis genommen" : "⚠️ Kenntnisnahme offen"
            : null;
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("📘", $"{view.Label} {view.Row.Heading}", $"{view.SectionLabel} {DiscordCards.Inline(view.SectionTitle, 60)} · Fassung {view.ShownVersion}"),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Escape(DiscordCards.PlainFromHtml(view.ShownHtml, 3000))),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer + (acknowledged is null ? "" : " · " + acknowledged)),
            DiscordCards.Row(
                DiscordCards.Open(link, "Absatz öffnen"),
                view.MustAcknowledge && view.MyAcknowledgedVersion != view.ShownVersion
                    ? DiscordCards.Button(BotIds.Make("ackr", view.Row.Id, view.ShownVersion.ToString()), "Zur Kenntnis genommen", "✅", ButtonStyle.Success)
                    : null)));
    }

    public async Task<BotReply> DvoSuggestAsync(BotCall call)
    {
        var text = call.Options.Text("stichwort") ?? string.Empty;
        if (text.Length < 2)
        {
            return BotReply.Autocomplete([]);
        }
        var picks = await regulation.SearchAsync(text, call.User, 20, call.Ct);
        return BotReply.Autocomplete(picks.Select(p =>
            new ApplicationCommandOptionChoiceProperties(DiscordCards.Clip($"{p.Label} {DiscordCards.OneLine(p.Heading)}", 100), "dvo:" + p.Id)));
    }

    // ---------------------------------------------------------------- FIBI

    public async Task<BotReply> FibiAsync(BotCall call, string? conversationId = null, string? question = null)
    {
        question ??= call.Options.Text("frage");
        if (string.IsNullOrWhiteSpace(question))
        {
            return BotReply.Error("Bitte eine Frage stellen.");
        }
        if (!fibi.IsConfigured)
        {
            return BotReply.Error("FIBI ist gerade nicht eingerichtet.");
        }
        var turn = await fibi.AskAsync(conversationId, question, call.User, cancellationToken: call.Ct);
        var answer = turn.Answer;
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var sources = new List<IActionRowComponentProperties?>();
        foreach (var source in answer.Sources.Take(4))
        {
            if (SearchCatalog.Route(source.Type, source.Id) is { } route)
            {
                sources.Add(new LinkButtonProperties(baseUrl + route, DiscordCards.Clip(string.IsNullOrWhiteSpace(source.Name) ? RecordTypeDisplay.Name(source.Type) : DiscordCards.OneLine(source.Name), 70), EmojiProperties.Standard(BotRecords.Emoji(source.Type))));
            }
        }
        var text = DiscordCards.Clip(answer.Text, 3300);
        var footer = $"-# 🤖 FIBI · {turn.QuotaTokens:N0} Kontingent-Token"
            + (answer.Truncated ? " · gekürzt" : "")
            + (answer.Degraded || turn.Degraded ? " · ohne Aktenzugriff" : "");
        return BotReply.Of(DiscordCards.Card(DiscordCards.Sand,
            DiscordCards.Header("🤖", "FIBI", DiscordCards.Inline(question, 180)),
            DiscordCards.Line(),
            DiscordCards.Text(answer.IsError ? "⚠️ " + text : text),
            answer.UnsupportedNote is null ? null : DiscordCards.Text("-# ⚠️ " + DiscordCards.Inline(answer.UnsupportedNote, 300)),
            DiscordCards.Line(),
            DiscordCards.Text(footer),
            DiscordCards.Row(sources),
            DiscordCards.Row(
                DiscordCards.Button(BotIds.Make("fibi", turn.ConversationId), "Nachfragen", "💬", ButtonStyle.Primary),
                DiscordCards.Open($"{baseUrl}/ki-assistent", "Im Browser weiter"))));
    }

    // ---------------------------------------------------------------- situation

    public async Task<BotReply> LageAsync(BotCall call)
    {
        var state = await situation.GetPublishedAsync(call.Ct);
        var link = await records.LinkAsync("/lage", call.Ct);
        if (state is null)
        {
            return BotReply.Of(DiscordCards.Card(DiscordCards.Steel, DiscordCards.Header("🛰️", "Gefahrenlage", "keine Stufe gesetzt"),
                DiscordCards.Row(DiscordCards.Open(link, "Lage öffnen"))));
        }
        var level = state.Level;
        var trend = state.Previous is { } previous
            ? previous < level ? "📈 verschärft" : previous > level ? "📉 entschärft" : null
            : null;
        return BotReply.Of(DiscordCards.Card(DiscordCards.ForSituation(level),
            DiscordCards.Text($"# {DiscordCards.SituationEmoji(level)} DEFCON {DiscordCards.Defcon(level)}\n**{PublicSituationLevelDisplay.Name(level)}**"
                + (state.Since is { } since ? $" · seit {DiscordCards.When(since)}" : "") + (trend is null ? "" : $" · {trend}")),
            DiscordCards.Line(),
            DiscordCards.Text(string.IsNullOrWhiteSpace(state.Note)
                ? DiscordCards.Escape(PublicSituationLevelDisplay.Hint(level))
                : ">>> " + DiscordCards.Escape(DiscordCards.PlainFromHtml(state.Note, 1500))),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer),
            DiscordCards.Row(DiscordCards.Open(link, "Lage öffnen"))));
    }

    // ---------------------------------------------------------------- radio

    public async Task<BotReply> FunkAsync(BotCall call)
    {
        var plan = await radio.GetPlanAsync(call.User, call.Ct);
        var filter = call.Options.Text("kanal");
        var picked = plan.Channels.Where(c => c.Id == filter).ToList();
        var channels = picked.Count > 0 ? picked : BotFuzzy.Rank(plan.Channels, filter, c => [c.Frequency, c.Label]).ToList();
        var factionsList = picked.Count > 0 ? [] : BotFuzzy.Rank(plan.Factions, filter, f => [f.Frequency, f.Name]).ToList();
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("📻", "Funkplan", filter is null ? $"{plan.Channels.Count} Kanäle · {plan.Factions.Count} Fraktionsfrequenzen" : $"Filter „{DiscordCards.Inline(filter, 30)}“"),
            DiscordCards.Line(),
        };
        foreach (var group in channels.GroupBy(c => c.Scope).Take(6))
        {
            parts.Add(DiscordCards.Text($"**{RadioScopeDisplay.Name(group.Key)}**\n" + string.Join("\n", group.Take(12).Select(c =>
                $"`{DiscordCards.Clip(c.Frequency, 12),-8}` {DiscordCards.Inline(c.Label, 60)}" + (c.IsClassified ? " 🔒" : "")
                + (c.TaskforceName is null ? "" : $" · {DiscordCards.Inline(c.TaskforceName, 40)}")
                + (string.IsNullOrWhiteSpace(c.Note) ? "" : $"\n-# {DiscordCards.Inline(c.Note, 100)}")))));
        }
        if (factionsList.Count > 0)
        {
            parts.Add(DiscordCards.Text("**🏴 Fraktionen**\n" + string.Join("\n", factionsList.Take(15).Select(f =>
                $"`{DiscordCards.Clip(f.Frequency, 12),-8}` {DiscordCards.Inline(f.Name, 60)}" + (f.IsClassified ? " 🔒" : "")))));
        }
        if (channels.Count == 0 && factionsList.Count == 0)
        {
            parts.Add(DiscordCards.Text("Kein Kanal passt."));
        }
        parts.Add(DiscordCards.Line());
        parts.Add(DiscordCards.Text(DiscordCards.Footer));
        parts.Add(DiscordCards.Row(DiscordCards.Open(await records.LinkAsync("/funk", call.Ct), "Funkplan öffnen")));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Steel, parts));
    }

    public async Task<BotReply> FunkSuggestAsync(BotCall call)
    {
        var plan = await radio.GetPlanAsync(call.User, call.Ct);
        return BotReply.Autocomplete(BotFuzzy.Rank(plan.Channels, call.Options.Text("kanal"), c => [c.Frequency, c.Label])
            .Select(c => new ApplicationCommandOptionChoiceProperties(DiscordCards.Clip($"{c.Frequency} · {DiscordCards.OneLine(c.Label)}", 100), c.Id)));
    }
}
