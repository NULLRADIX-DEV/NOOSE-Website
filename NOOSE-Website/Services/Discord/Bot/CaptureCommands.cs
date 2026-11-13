using NetCord;
using NetCord.Rest;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Models.Operations;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>/notiz, /wiedervorlage, /aufgabe, /schusswaffe, /alarm, watching, and "An Akte hängen".</summary>
public sealed class CaptureCommands(
    BotRecords records,
    BotScratch scratch,
    ICommentService comments,
    IFollowupService followups,
    IJobService jobs,
    IFirearmUseService firearms,
    IEmergencyAlarmService alarms,
    IWatchlistService watchlist,
    ITextImageService textImages,
    IHttpClientFactory httpFactory,
    TimeProvider clock)
{
    public const string CdnClient = "discord-cdn";
    private const int MaxCommentChars = 3500;
    private const long MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Record types a comment or follow-up may hang on from Discord.</summary>
    public static readonly string[] AttachTypes =
    [
        nameof(Person), nameof(Faction), "PersonGroup", "Party", "Operation", "Case", "Taskforce", "Document",
    ];

    // ---------------------------------------------------------------- note

    public async Task<BotReply> NotizAsync(BotCall call)
    {
        var (record, problem) = await records.ResolveAsync(call.Options.Text("akte"), call, AttachTypes);
        return record is null ? BotReply.Error(problem!) : await NoteAsync(call, record.Type, record.Id, call.Options.Text("text"));
    }

    public Task<BotReply> AkteSuggestAsync(BotCall call) => records.SuggestAsync(call.Options.Text("akte"), call, AttachTypes);

    public async Task<BotReply> NoteAsync(BotCall call, string type, string id, string? text)
    {
        if (!AttachTypes.Contains(type))
        {
            return BotReply.Error("An diese Akte lässt sich aus Discord nichts hängen.");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return BotReply.Error("Bitte einen Text angeben.");
        }
        await comments.CreateAsync(type, id, DiscordCards.Clip(text.Trim(), MaxCommentChars), call.User, call.Ct);
        return await ConfirmOnRecordAsync(call, type, id, "📝", "Notiz angehängt");
    }

    public static ModalProperties NoteModal(string type, string id)
        => new(BotIds.Make("mnote", type, id), "Notiz an die Akte")
        {
            new LabelProperties("Kommentar", new TextInputProperties("text", TextInputStyle.Paragraph).WithRequired(true).WithMaxLength(1800)),
        };

    // ---------------------------------------------------------------- follow-up

    public async Task<BotReply> WiedervorlageAsync(BotCall call)
    {
        var (record, problem) = await records.ResolveAsync(call.Options.Text("akte"), call, AttachTypes);
        return record is null
            ? BotReply.Error(problem!)
            : await FollowupAsync(call, record.Type, record.Id, call.Options.Text("wann"), call.Options.Text("notiz"));
    }

    public async Task<BotReply> FollowupAsync(BotCall call, string type, string id, string? when, string? note)
    {
        if (!AttachTypes.Contains(type))
        {
            return BotReply.Error("An diese Akte lässt sich aus Discord nichts hängen.");
        }
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        var due = DiscordDateParser.ParseMoment(when, today, new TimeOnly(18, 0));
        if (due is null)
        {
            return BotReply.Error("Den Zeitpunkt verstehe ich nicht. Beispiele: morgen, +7, 20.11. 18:00.");
        }
        await followups.CreateAsync(type, id, new FollowupInput(due.Value, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), null), call.User, call.Ct);
        return await ConfirmOnRecordAsync(call, type, id, "⏰", $"Wiedervorlage {DiscordCards.When(due.Value, 'f')} gesetzt");
    }

    public static ModalProperties FollowupModal(string type, string id)
        => new(BotIds.Make("mfu", type, id), "Wiedervorlage setzen")
        {
            new LabelProperties("Wann", new TextInputProperties("wann", TextInputStyle.Short).WithPlaceholder("morgen, +7, 20.11. 18:00").WithRequired(true).WithMaxLength(30)),
            new LabelProperties("Notiz (optional)", new TextInputProperties("notiz", TextInputStyle.Paragraph).WithRequired(false).WithMaxLength(500)),
        };

    // ---------------------------------------------------------------- watch

    public async Task<BotReply> WatchAsync(BotCall call, string type, string id)
    {
        await watchlist.FollowAsync(type, id, call.User, call.Ct);
        return BotReply.Done("Du beobachtest diese Akte jetzt und bekommst Änderungen in die Glocke.");
    }

    // ---------------------------------------------------------------- job

    public async Task<BotReply> AufgabeAsync(BotCall call)
    {
        var agentId = call.Options.Text("agent");
        var title = call.Options.Text("titel");
        if (agentId is null || title is null)
        {
            return BotReply.Error("Bitte Agent und Titel angeben.");
        }
        DateTime? due = null;
        if (call.Options.Text("faellig") is { } dueText)
        {
            var day = DiscordDateParser.ParseDay(dueText, BerlinTime.Today(clock.GetUtcNow().UtcDateTime));
            if (day is null)
            {
                return BotReply.Error("Das Fälligkeitsdatum verstehe ich nicht.");
            }
            due = day.Value.ToDateTime(TimeOnly.MinValue);
        }
        var resolved = await ResolveAgentAsync(agentId, call);
        if (resolved is null)
        {
            return BotReply.Error(LeadershipCommands.NoAgent);
        }
        var job = await jobs.CreateAsync(new JobInput
        {
            Title = title,
            Description = call.Options.Text("beschreibung"),
            DueDate = due,
        }, [resolved.Value.Id], call.User, call.Ct);
        var link = await records.LinkAsync($"/aufgaben/{job.Id}", call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Success,
            DiscordCards.Header("📌", job.Title, $"{job.CaseNumber} · Aufgabe an {DiscordCards.Inline(resolved.Value.Codename, 40)}"),
            DiscordCards.Text(due is null ? "Ohne Fälligkeit." : $"Fällig {DiscordCards.Day(DateOnly.FromDateTime(due.Value))}"),
            DiscordCards.Row(DiscordCards.Open(link, "Aufgabe öffnen"))));
    }

    private async Task<(string Id, string Codename)?> ResolveAgentAsync(string input, BotCall call)
    {
        var all = await records.SelectableAgentsAsync(call.Ct);
        var byId = all.Where(a => a.Id == input).ToList();
        if (byId.Count == 1)
        {
            return byId[0];
        }
        // typed instead of picked: the one clear codename, typos forgiven
        var hit = BotFuzzy.Single(all.Select(a => new[] { a.Id, a.Codename }).ToList(), input, a => [a[1]]);
        return hit is null ? null : (hit[0], hit[1]);
    }

    // ---------------------------------------------------------------- firearm, alarm

    public async Task<BotReply> SchusswaffeAsync(BotCall call)
    {
        var reason = call.Options.Text("begruendung");
        if (reason is null)
        {
            return BotReply.Error("Bitte eine kurze Begründung angeben.");
        }
        await firearms.ReportAsync(reason, call.User, call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Warning,
            DiscordCards.Header("🔫", "Schusswaffengebrauch gemeldet", DiscordCards.When(clock.GetUtcNow().UtcDateTime, 'f')),
            DiscordCards.Text("Die Führung deiner Abteilung und das Board sind benachrichtigt. Die Meldung steht in deiner Personalakte.")));
    }

    public async Task<BotReply> AlarmAsync(BotCall call)
    {
        Division? target = Enum.TryParse<Division>(call.Options.Text("abteilung"), out var d) ? d : null;
        var id = await alarms.TriggerAsync(new EmergencyAlarmInput(call.Options.Text("lage") ?? string.Empty,
            call.Options.Text("ort"), target, call.Options.Text("grund")), call.User, call.Ct);
        var link = await records.LinkAsync($"/einsatzalarme/{id}", call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Danger,
            DiscordCards.Header("🚨", "Einsatzalarm ausgelöst", target is { } t ? $"nur {DivisionDisplay.Name(t)}" : "alle verfügbaren Agenten"),
            DiscordCards.Text("Die Gerufenen sind benachrichtigt. Die Rückmeldungen siehst du auf der Alarmtafel und auf der Seite."),
            DiscordCards.Row(DiscordCards.Open(link, "Alarm öffnen"))));
    }

    public async Task<BotReply> AlarmAnswerAsync(BotCall call, string alarmId, string answer)
    {
        var value = answer == "1" ? EmergencyAlarmAnswer.Coming : EmergencyAlarmAnswer.NotComing;
        await alarms.RespondAsync(alarmId, value, call.User, call.Ct);
        return value == EmergencyAlarmAnswer.Coming
            ? BotReply.Done("Du kommst. Die Alarmtafel ist aktualisiert.")
            : BotReply.Info("Du kommst nicht. Die Alarmtafel ist aktualisiert.");
    }

    // ---------------------------------------------------------------- attach a Discord message

    /// <summary>What the right-clicked message held, kept for the modal round trip.</summary>
    public sealed class AttachState
    {
        public required string Content { get; init; }
        public required string Author { get; init; }
        public required DateTime SentAt { get; init; }
        public required string JumpLink { get; init; }
        public required ulong OwnerDiscordId { get; init; }
        public required IReadOnlyList<(string Url, string? ContentType, long Size)> Images { get; init; }
        public string? Remark { get; set; }
    }

    public BotReply AttachStart(AttachState state)
    {
        if (string.IsNullOrWhiteSpace(state.Content) && state.Images.Count == 0)
        {
            return BotReply.Error("Diese Nachricht hat weder Text noch Bilder.");
        }
        var key = scratch.Put(state);
        return BotReply.OpenModal(new ModalProperties(BotIds.Make("mattach", key), "An Akte hängen")
        {
            new TextDisplayProperties("-# " + DiscordCards.Inline(state.Content, 300) + (state.Images.Count > 0 ? $" · 🖼️ {state.Images.Count} Bild(er)" : "")),
            new LabelProperties("Akte", new TextInputProperties("akte", TextInputStyle.Short)
                .WithPlaceholder("Name oder Aktenzeichen, z. B. FIB-P-2026-0012").WithRequired(true).WithMaxLength(100)),
            new LabelProperties("Anmerkung (optional)", new TextInputProperties("anmerkung", TextInputStyle.Paragraph).WithRequired(false).WithMaxLength(500)),
        });
    }

    public async Task<BotReply> AttachSubmitAsync(BotCall call, string key, string? recordInput, string? remark)
    {
        var state = scratch.Get<AttachState>(key);
        if (state is null || state.OwnerDiscordId != call.Context.UserId)
        {
            return BotReply.Error("Das hat zu lange gedauert, bitte noch einmal über das Menü starten.");
        }
        state.Remark = remark;
        var (record, problem) = await records.ResolveAsync(recordInput, call, AttachTypes);
        if (record is not null)
        {
            return await AttachAsync(call, state, record.Type, record.Id);
        }
        // several hits: let the agent pick
        var hits = await records.CandidatesAsync(recordInput, call, AttachTypes);
        if (hits.Count < 2)
        {
            return BotReply.Error(problem!);
        }
        var menu = new StringMenuProperties(BotIds.Make("pick", key));
        foreach (var hit in hits.Take(20))
        {
            menu.Add(new StringMenuSelectOptionProperties(DiscordCards.Clip($"{DiscordCards.OneLine(hit.Name)} ({hit.CaseNumber})", 100), $"{hit.Category}:{hit.TargetId}")
                { Description = RecordTypeDisplay.Name(hit.Category), Emoji = EmojiProperties.Standard(BotRecords.Emoji(hit.Category)) });
        }
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("📎", "Welche Akte?", $"{hits.Count} Treffer"),
            menu.WithPlaceholder("Akte wählen")));
    }

    public async Task<BotReply> AttachPickAsync(BotCall call, string key, string? picked)
    {
        var state = scratch.Get<AttachState>(key);
        if (state is null || state.OwnerDiscordId != call.Context.UserId)
        {
            return BotReply.Error("Das hat zu lange gedauert, bitte noch einmal über das Menü starten.");
        }
        var cut = picked?.IndexOf(':') ?? -1;
        if (cut <= 0)
        {
            return BotReply.Error("Bitte eine Akte wählen.");
        }
        return await AttachAsync(call, state, picked![..cut], picked[(cut + 1)..]);
    }

    private async Task<BotReply> AttachAsync(BotCall call, AttachState state, string type, string id)
    {
        if (!AttachTypes.Contains(type))
        {
            return BotReply.Error("An diese Akte lässt sich aus Discord nichts hängen.");
        }
        var tokens = new List<string>();
        foreach (var image in state.Images.Take(6))
        {
            var bytes = await DownloadAsync(image.Url, image.Size, call.Ct);
            if (bytes is null)
            {
                continue;
            }
            var token = await textImages.SaveAsync(type, id, bytes, image.ContentType ?? "image/png", call.User, call.Ct);
            if (token is not null)
            {
                tokens.Add(token);
            }
        }

        var head = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Remark))
        {
            head.Add(state.Remark.Trim());
            head.Add(string.Empty);
        }
        head.Add($"Aus Discord übernommen – {state.Author}, {TimeZoneInfo.ConvertTimeFromUtc(state.SentAt, BerlinTime.Zone):dd.MM.yyyy HH:mm} Uhr:");
        var tail = new List<string> { state.JumpLink };
        tail.AddRange(tokens);
        // only the quote is cut: the link and the picture tokens always survive
        var room = MaxCommentChars - string.Join("\n", head).Length - string.Join("\n", tail).Length - 2;
        var quote = string.IsNullOrWhiteSpace(state.Content)
            ? null
            : DiscordCards.Clip(string.Join("\n", state.Content.Trim().Split('\n').Select(l => "> " + l)), Math.Max(0, room));
        var text = string.Join("\n", head.Concat(string.IsNullOrEmpty(quote) ? [] : [quote]).Concat(tail));
        var skipped = state.Images.Count - tokens.Count;

        await comments.CreateAsync(type, id, text, call.User, call.Ct);
        return await ConfirmOnRecordAsync(call, type, id, "📎",
            "An die Akte gehängt" + (tokens.Count > 0 ? $" · 🖼️ {tokens.Count} Bild(er)" : "") + (skipped > 0 ? $" · {skipped} Bild(er) übersprungen" : ""));
    }

    /// <summary>Only Discord's own CDN, only https, no redirect, size capped.</summary>
    private async Task<byte[]?> DownloadAsync(string url, long size, CancellationToken ct)
    {
        if (size > MaxImageBytes || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net"))
        {
            return null;
        }
        try
        {
            using var response = await httpFactory.CreateClient(CdnClient).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxImageBytes)
                {
                    return null;
                }
            }
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<BotReply> ConfirmOnRecordAsync(BotCall call, string type, string id, string emoji, string text)
    {
        var link = await records.RecordLinkAsync(type, id, call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Success,
            DiscordCards.Text($"{emoji} **{text}**\n-# {RecordTypeDisplay.Name(type)}"),
            link is null ? null : DiscordCards.Row(DiscordCards.Open(link, "Akte öffnen"))));
    }
}
