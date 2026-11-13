using Microsoft.EntityFrameworkCore;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>/akte, /fahndung, /kopfgeld, /suche, /fraktion, /route and the record cards they share.</summary>
public sealed class RecordCommands(
    BotRecords records,
    BotScratch scratch,
    IPersonService people,
    IFactionService factions,
    ISearchService search,
    IPublicWantedService publicWanted,
    ISystemSettingService systemSettings,
    IFileStorageService files,
    IDbContextFactory<AppDbContext> dbFactory)
{
    private const long MaxPhotoBytes = 8 * 1024 * 1024;
    private const long MaxPhotosTotal = 8 * 1024 * 1024;
    private const int SearchPageSize = 6;

    private static readonly string[] CardTypes = [nameof(Person), nameof(Faction)];

    public async Task<BotReply> AkteAsync(BotCall call)
    {
        var (record, problem) = await records.ResolveAsync(call.Options.Text("suche"), call, CardTypes);
        if (record is null)
        {
            return BotReply.Error(problem!);
        }
        return record.Type == nameof(Faction)
            ? await FactionCardAsync(record.Id, call)
            : await PersonCardAsync(record.Id, call);
    }

    public Task<BotReply> AkteSuggestAsync(BotCall call) => records.SuggestAsync(call.Options.Text("suche"), call, CardTypes);

    public Task<BotReply> PersonSuggestAsync(BotCall call, string option)
        => records.SuggestAsync(call.Options.Text(option), call, [nameof(Person)]);

    public Task<BotReply> FactionSuggestAsync(BotCall call)
        => records.SuggestAsync(call.Options.Text("name"), call, [nameof(Faction)]);

    // ---------------------------------------------------------------- person

    public async Task<BotReply> PersonCardAsync(string personId, BotCall call)
    {
        var person = await people.GetDetailAsync(personId, call.Scope, call.Ct);
        if (person is null)
        {
            return BotReply.Error("Diese Akte gibt es nicht oder du darfst sie nicht sehen.");
        }
        await records.LogAsync(nameof(Person), person.Id);

        var link = await records.RecordLinkAsync(nameof(Person), person.Id, call.Ct);
        var photo = LoadPhoto(person.Photos.Where(p => !p.IsDeleted).OrderByDescending(p => p.CreatedAt)
            .Select(p => (p.FileNameSaved, p.ContentType, p.SizeBytes)).FirstOrDefault());
        var hazard = HazardLevelLogic.From(person.ThreatScore);

        var status = new List<string>
        {
            $"{DiscordCards.ClassificationEmoji(person.Classification)} {ClassificationDisplay.Name(person.Classification)}",
            $"{DiscordCards.LifeEmoji(person.EffectiveLifeStatus)} {LifeStatusDisplay.Name(person.EffectiveLifeStatus)}",
        };
        if (person.ThreatScore is { } score)
        {
            status.Add($"{DiscordCards.HazardEmoji(hazard)} Gefahr {HazardLevelLogic.Name(hazard)} ({score})");
        }
        if (person.IsClassified || person.IsTRUClassified || person.IsHRBClassified)
        {
            status.Add("🔒 VS");
        }
        if (person.IsArchived)
        {
            status.Add("🗄️ archiviert");
        }

        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.HeaderWithThumbnail("👤", person.Name, $"{person.CaseNumber} · Personenakte",
                photo is null ? null : "attachment://" + photo.Name, string.Join(" · ", status)),
        };

        var threshold = (await systemSettings.GetAsync(call.Ct)).WantedBoardMinHazard;
        if (WantedBoard.IsOnBoard(person, threshold))
        {
            parts.Add(DiscordCards.Line());
            var wanted = $"🚨 **Gesucht** – {DiscordCards.Inline(WantedBoard.Reason(person, threshold))}";
            if (!string.IsNullOrWhiteSpace(person.WantedReason))
            {
                wanted += $"\n> {DiscordCards.Inline(person.WantedReason, 300)}";
            }
            var notice = await NoticeForAsync(person.CaseNumber, call);
            if (notice is not null)
            {
                wanted += $"\n📢 Öffentlich ausgeschrieben · {DiscordCards.Inline(PublicWantedStatusLabel(notice.Status))}"
                    + (notice.Bounty > 0 ? $" · 💰 Kopfgeld **{DiscordCards.Money(notice.Bounty)}**" : string.Empty);
            }
            parts.Add(DiscordCards.Text(wanted));
        }

        var details = DiscordCards.Fields(
            ("Aliase", Join(person.Aliases.Select(a => a.AliasName), 6)),
            ("Telefon", Join(person.PhoneNumbers.Select(p => p.Number + (string.IsNullOrWhiteSpace(p.Designation) ? "" : $" ({p.Designation})")), 4)),
            ("Fahrzeuge", Join(person.Vehicles.Select(v => DiscordCards.Inline(v.Designation, 60) + (string.IsNullOrWhiteSpace(v.LicensePlate) ? "" : $" `{DiscordCards.Clip(DiscordCards.OneLine(v.LicensePlate).Replace("`", ""), 20)}`")), 4, escape: false)));
        var affiliations = await people.GetAffiliationsAsync(person.Id, call.Scope, call.Ct);
        var current = affiliations.Where(a => a.EndedAt is null).Take(5)
            .Select(a => $"{DiscordCards.Inline(a.Name, 60)}" + (a.IsLead ? " 👑" : "") + (string.IsNullOrWhiteSpace(a.Role) ? "" : $" ({DiscordCards.Inline(a.Role, 40)})"))
            .ToList();
        if (current.Count > 0)
        {
            details += (details.Length > 0 ? "\n" : "") + "**Zugehörig:** " + string.Join(", ", current);
        }
        if (details.Length > 0)
        {
            parts.Add(DiscordCards.Line());
            parts.Add(DiscordCards.Text(details));
        }
        var description = DiscordCards.PlainFromHtml(person.Description, 500);
        if (description.Length > 0)
        {
            parts.Add(DiscordCards.Text(">>> " + DiscordCards.Escape(description)));
        }
        parts.Add(DiscordCards.Line());
        parts.Add(DiscordCards.Text(DiscordCards.Footer + $" · Stand {DiscordCards.When(person.ModifiedAt ?? person.CreatedAt)}"));
        parts.Add(RecordActions(nameof(Person), person.Id, link));

        return BotReply.Of([DiscordCards.Card(DiscordCards.ForClassification(person.Classification), parts)],
            photo is null ? null : [photo]);
    }

    // ---------------------------------------------------------------- faction

    public async Task<BotReply> FraktionAsync(BotCall call)
    {
        var (record, problem) = await records.ResolveAsync(call.Options.Text("name"), call, [nameof(Faction)]);
        return record is null ? BotReply.Error(problem!) : await FactionCardAsync(record.Id, call);
    }

    public async Task<BotReply> FactionCardAsync(string factionId, BotCall call)
    {
        var faction = await factions.GetDetailAsync(factionId, call.Scope, call.Ct);
        if (faction is null)
        {
            return BotReply.Error("Diese Akte gibt es nicht oder du darfst sie nicht sehen.");
        }
        await records.LogAsync(nameof(Faction), faction.Id);
        var members = await factions.GetMembersAsync(faction.Id, call.Scope, call.Ct);
        var link = await records.RecordLinkAsync(nameof(Faction), faction.Id, call.Ct);
        var candidates = faction.Photos.Where(p => !p.IsDeleted)
            .OrderByDescending(p => p.IsTitleImage).ThenByDescending(p => p.CreatedAt)
            .Take(4)
            .ToList();
        // Discord takes only so much per message: stop adding photos once the budget is used
        var photos = new List<BotFile>();
        long total = 0;
        foreach (var candidate in candidates)
        {
            if (total + candidate.SizeBytes > MaxPhotosTotal)
            {
                continue;
            }
            if (LoadPhoto((candidate.FileNameSaved, candidate.ContentType, candidate.SizeBytes), "bild" + photos.Count) is { } file)
            {
                photos.Add(file);
                total += file.Content.Length;
            }
        }

        var hazard = HazardLevelLogic.From(faction.ThreatScore);
        var status = new List<string> { $"{DiscordCards.ClassificationEmoji(faction.Classification)} {ClassificationDisplay.Name(faction.Classification)}" };
        if (faction.ThreatScore is { } score)
        {
            status.Add($"{DiscordCards.HazardEmoji(hazard)} Bedrohung {score}/100");
        }
        if (faction.IsStateFaction)
        {
            status.Add("🏛️ staatlich");
        }
        if (faction.IsClassified || faction.IsTRUClassified || faction.IsHRBClassified)
        {
            status.Add("🔒 VS");
        }

        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.HeaderWithThumbnail("🏴", faction.Name,
                $"{faction.CaseNumber} · Fraktion" + (string.IsNullOrWhiteSpace(faction.Kind) ? "" : " · " + DiscordCards.Inline(faction.Kind, 40)),
                photos.Count > 0 ? "attachment://" + photos[0].Name : null, string.Join(" · ", status)),
            DiscordCards.Line(),
        };

        var leads = members.Where(m => m.IsLead).Select(m => m.Person?.Name).OfType<string>().Take(4).ToList();
        var oldest = FactionRecency.Oldest(faction);
        parts.Add(DiscordCards.Text(DiscordCards.Fields(
            ("Mitglieder", $"{members.Count} erfasst" + (faction.EstimatedMemberCount is { } est ? $" · ~{est} geschätzt" : "")),
            ("Führung", leads.Count == 0 ? null : string.Join(", ", leads.Select(n => DiscordCards.Inline(n, 50)))),
            ("Funk", DiscordCards.Inline(faction.Radio, 40)),
            ("Anwesen", DiscordCards.Inline(faction.Estate, 120)),
            ("Erkennungsfarbe", DiscordCards.Inline(faction.RecognitionColor, 40)),
            ("Älteste Pflege", $"{FactionRecencyFacetDisplay.Name(oldest)} {DiscordCards.When(FactionRecency.RefreshedAt(faction, oldest))}"))));

        if (faction.DrugRoutes.Count > 0)
        {
            parts.Add(DiscordCards.Text("**🧪 Drogenrouten:** " + string.Join(", ", faction.DrugRoutes.Take(8).Select(r => DiscordCards.Inline(r.Designation, 40)))));
        }
        var description = DiscordCards.PlainFromHtml(faction.Description, 400);
        if (description.Length > 0)
        {
            parts.Add(DiscordCards.Text(">>> " + DiscordCards.Escape(description)));
        }
        if (photos.Count > 1)
        {
            parts.Add(DiscordCards.Gallery(photos.Skip(1).Select(p => "attachment://" + p.Name)));
        }
        parts.Add(DiscordCards.Line());
        parts.Add(DiscordCards.Text(DiscordCards.Footer + $" · Stand {DiscordCards.When(faction.ModifiedAt ?? faction.CreatedAt)}"));
        parts.Add(RecordActions(nameof(Faction), faction.Id, link));
        return BotReply.Of([DiscordCards.Card(DiscordCards.ForClassification(faction.Classification), parts)], photos);
    }

    // ---------------------------------------------------------------- wanted

    public async Task<BotReply> FahndungAsync(BotCall call)
    {
        var name = call.Options.Text("name");
        if (name is not null)
        {
            var (record, problem) = await records.ResolveAsync(name, call, [nameof(Person)]);
            return record is null ? BotReply.Error(problem!) : await PersonCardAsync(record.Id, call);
        }
        return BotReply.Of([await WantedBoardCardAsync(call)]);
    }

    public async Task<ComponentContainerProperties> WantedBoardCardAsync(BotCall call)
    {
        var threshold = (await systemSettings.GetAsync(call.Ct)).WantedBoardMinHazard;
        var list = (await people.GetListAsync(call.Scope, cancellationToken: call.Ct))
            .Where(p => WantedBoard.IsOnBoard(p, threshold))
            .OrderByDescending(p => p.IsWanted).ThenByDescending(p => p.ThreatScore ?? 0)
            .ToList();
        var bounties = await BountiesByCaseAsync(call);
        var lines = list.Take(15).Select(p =>
        {
            var hazard = HazardLevelLogic.From(p.ThreatScore);
            var bounty = bounties.TryGetValue(p.CaseNumber, out var b) && b > 0 ? $" · 💰 {DiscordCards.Money(b)}" : "";
            return $"{DiscordCards.HazardEmoji(hazard)} **{DiscordCards.Inline(p.Name, 60)}** `{p.CaseNumber}`{bounty}\n-# {DiscordCards.Inline(WantedBoard.Reason(p, threshold), 80)}";
        }).ToList();
        var link = await records.LinkAsync("/fahndung", call.Ct);
        return DiscordCards.Card(DiscordCards.Danger,
            DiscordCards.Header("🚨", "Fahndungstafel", $"{list.Count} gesucht · ab Gefahr {HazardLevelLogic.Name(threshold)}"),
            DiscordCards.Line(),
            DiscordCards.Text(lines.Count == 0 ? "Niemand steht gerade auf der Tafel." : string.Join("\n", lines)
                + (list.Count > 15 ? $"\n-# … und {list.Count - 15} weitere" : "")),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer + $" · {DiscordCards.When(DateTime.UtcNow)}"),
            DiscordCards.Row(DiscordCards.Open(link, "Fahndung öffnen")));
    }

    public async Task<BotReply> KopfgeldAsync(BotCall call)
    {
        var notices = (await publicWanted.GetAllAsync(call.User, call.Ct))
            .Where(n => n.Bounty > 0 && n.Status == PublicWantedStatus.Veroeffentlicht)
            .OrderByDescending(n => n.Bounty)
            .Take(10)
            .ToList();
        var lines = notices.Select((n, i) =>
            $"**{i + 1}.** {DiscordCards.HazardEmoji(n.HazardLevel)} **{DiscordCards.Inline(n.DisplayName, 60)}** · 💰 **{DiscordCards.Money(n.Bounty)}**"
            + (n.PersonCaseNumber is null ? "" : $" `{n.PersonCaseNumber}`"));
        var link = await records.LinkAsync("/gesucht", call.Ct);
        return BotReply.Of(DiscordCards.Card(DiscordCards.Warning,
            DiscordCards.Header("💰", "Kopfgelder", "die höchsten offenen Ausschreibungen"),
            DiscordCards.Line(),
            DiscordCards.Text(notices.Count == 0 ? "Gerade ist kein Kopfgeld ausgeschrieben." : string.Join("\n", lines)),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer),
            DiscordCards.Row(DiscordCards.Open(link, "Öffentliche Fahndung"))));
    }

    private async Task<Dictionary<string, decimal>> BountiesByCaseAsync(BotCall call)
    {
        try
        {
            return (await publicWanted.GetAllAsync(call.User, call.Ct))
                .Where(n => n.PersonCaseNumber is not null && n.Status == PublicWantedStatus.Veroeffentlicht)
                .GroupBy(n => n.PersonCaseNumber!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(n => n.Bounty), StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<PublicWantedEdit?> NoticeForAsync(string caseNumber, BotCall call)
    {
        try
        {
            return (await publicWanted.GetAllAsync(call.User, call.Ct))
                .Where(n => string.Equals(n.PersonCaseNumber, caseNumber, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.Status == PublicWantedStatus.Veroeffentlicht)
                .ThenByDescending(n => n.PublishedAt)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string PublicWantedStatusLabel(PublicWantedStatus status) => PublicWantedStatusDisplay.Name(status);

    // ---------------------------------------------------------------- search

    public async Task<BotReply> SucheAsync(BotCall call)
    {
        var text = call.Options.Text("text");
        if (text is null || text.Length < 2)
        {
            return BotReply.Error("Bitte mindestens zwei Zeichen eingeben.");
        }
        var results = await search.SearchAsync(new SearchCriteria { Text = text, Fuzzy = true }, call.User, call.Ct);
        var hits = results.Groups.SelectMany(g => g.Hit).ToList();
        var key = scratch.Put(new SearchState(call.AgentId, text, hits));
        return await SearchPageAsync(key, 0, null, call);
    }

    /// <summary>One page of a stored search; category null means all.</summary>
    public async Task<BotReply> SearchPageAsync(string key, int page, string? category, BotCall call)
    {
        var state = scratch.Get<SearchState>(key);
        if (state is null || state.OwnerId != call.AgentId)
        {
            return BotReply.Error("Diese Suche ist abgelaufen, bitte neu suchen.");
        }
        var filtered = category is null ? state.Hits : state.Hits.Where(h => h.Category == category).ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)SearchPageSize));
        page = Math.Clamp(page, 0, pages - 1);
        var baseUrl = await records.BaseUrlAsync(call.Ct);

        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("🔎", $"Suche: {state.Text}", $"{state.Hits.Count} Treffer"
                + (category is null ? "" : $" · {RecordTypeDisplay.Plural(category)}: {filtered.Count}") + $" · Seite {page + 1}/{pages}"),
            DiscordCards.Line(),
        };
        if (filtered.Count == 0)
        {
            parts.Add(DiscordCards.Text("Nichts gefunden, das du sehen darfst."));
        }
        foreach (var hit in filtered.Skip(page * SearchPageSize).Take(SearchPageSize))
        {
            var route = hit.Href ?? SearchNavigation.For(hit);
            var line = $"{BotRecords.Emoji(hit.Category)} **{DiscordCards.Inline(hit.Title, 80)}**"
                + (string.IsNullOrWhiteSpace(hit.CaseNumber) ? "" : $" `{hit.CaseNumber}`")
                + $"\n-# {RecordTypeDisplay.Name(hit.Category)}"
                + (string.IsNullOrWhiteSpace(hit.Snippet) ? "" : " · " + DiscordCards.Inline(hit.Snippet, 140));
            parts.Add(route is null
                ? DiscordCards.Text(line)
                : DiscordCards.WithButton(line, new LinkButtonProperties(baseUrl + route, "Öffnen")));
        }

        var categories = state.Hits.GroupBy(h => h.Category).OrderByDescending(g => g.Count()).Take(24).ToList();
        if (categories.Count > 1)
        {
            var menu = new StringMenuProperties(BotIds.Make("sc", key))
            {
                new StringMenuSelectOptionProperties($"Alle ({state.Hits.Count})", "*") { Default = category is null },
            };
            foreach (var group in categories)
            {
                menu.Add(new StringMenuSelectOptionProperties($"{RecordTypeDisplay.Plural(group.Key)} ({group.Count()})", group.Key)
                    { Default = group.Key == category, Emoji = EmojiProperties.Standard(BotRecords.Emoji(group.Key)) });
            }
            parts.Add(menu.WithPlaceholder("Nach Art filtern"));
        }
        parts.Add(DiscordCards.Row(
            DiscordCards.Button(BotIds.Make("s", key, (page - 1).ToString(), category ?? "*"), "Zurück", "◀️").WithDisabled(page == 0),
            DiscordCards.Button(BotIds.Make("s", key, (page + 1).ToString(), category ?? "*"), "Weiter", "▶️").WithDisabled(page >= pages - 1),
            DiscordCards.Open($"{baseUrl}/suche?q={Uri.EscapeDataString(state.Text)}", "Volle Suche")));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    public sealed record SearchState(string OwnerId, string Text, IReadOnlyList<SearchHit> Hits);

    // ---------------------------------------------------------------- routes

    public async Task<BotReply> RouteAsync(BotCall call)
    {
        var name = call.Options.Text("name");
        if (name is null)
        {
            return BotReply.Error("Bitte eine Route angeben.");
        }
        var conflicts = await factions.GetDrugRouteConflictsAsync(null, [name], call.User, call.Ct);
        var holder = conflicts.FirstOrDefault(c => c.HolderId is not null);
        if (holder is null && BotFuzzy.Single(await VisibleRoutesAsync(call), name, r => [r]) is { } corrected
            && !string.Equals(corrected, name, StringComparison.OrdinalIgnoreCase))
        {
            // typed with a typo: answer for the one route it clearly meant
            name = corrected;
            holder = (await factions.GetDrugRouteConflictsAsync(null, [name], call.User, call.Ct)).FirstOrDefault(c => c.HolderId is not null);
        }
        var parts = new List<IComponentContainerComponentProperties?> { DiscordCards.Header("🧪", name, "Drogenroute"), DiscordCards.Line() };
        // a holder the caller may not see comes back without id, and reads like no holder at all
        if (holder?.HolderName is null)
        {
            parts.Add(DiscordCards.Text("Diese Route hält derzeit keine Fraktion, die du sehen darfst."));
            return BotReply.Of(DiscordCards.Card(DiscordCards.Steel, parts));
        }
        var link = await records.RecordLinkAsync(nameof(Faction), holder.HolderId!, call.Ct);
        parts.Add(DiscordCards.Text($"Gehalten von **{DiscordCards.Inline(holder.HolderName, 80)}**"));
        parts.Add(DiscordCards.Row(
            DiscordCards.Button(BotIds.Make("card", nameof(Faction), holder.HolderId), "Fraktionskarte", "🏴", ButtonStyle.Primary),
            link is null ? null : DiscordCards.Open(link)));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    public async Task<BotReply> RouteSuggestAsync(BotCall call)
    {
        var routes = (await VisibleRoutesAsync(call)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase);
        return BotReply.Autocomplete(BotFuzzy.Rank(routes, call.Options.Text("name"), r => [r])
            .Select(r => new ApplicationCommandOptionChoiceProperties(DiscordCards.Clip(r, 100), DiscordCards.Clip(r, 100))));
    }

    /// <summary>Route names held by factions the caller may see.</summary>
    private async Task<List<string>> VisibleRoutesAsync(BotCall call)
    {
        var visible = (await factions.GetListAsync(call.Scope, cancellationToken: call.Ct)).Select(f => f.Id).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(call.Ct);
        return (await db.FactionDrugRoutes.AsNoTracking()
                .Where(r => visible.Contains(r.FactionId))
                .Select(r => r.Designation)
                .ToListAsync(call.Ct))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Open, follow-up, comment and watch buttons under a record card.</summary>
    public static ActionRowProperties? RecordActions(string type, string id, string? link)
        => DiscordCards.Row(
            link is null ? null : DiscordCards.Open(link, "Öffnen"),
            DiscordCards.Button(BotIds.Make("fu", type, id), "Wiedervorlage", "⏰"),
            DiscordCards.Button(BotIds.Make("note", type, id), "Notiz", "📝"),
            DiscordCards.Button(BotIds.Make("watch", type, id), "Beobachten", "👁️"));

    private BotFile? LoadPhoto((string FileNameSaved, string ContentType, long SizeBytes) photo, string baseName = "foto")
    {
        if (string.IsNullOrEmpty(photo.FileNameSaved) || photo.SizeBytes > MaxPhotoBytes)
        {
            return null;
        }
        var ext = photo.ContentType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => null,
        };
        if (ext is null)
        {
            return null;
        }
        try
        {
            using var stream = files.OpenRead(photo.FileNameSaved);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new BotFile($"{baseName}.{ext}", memory.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string? Join(IEnumerable<string> values, int max, bool escape = true)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list.Count == 0)
        {
            return null;
        }
        var shown = list.Take(max).Select(v => escape ? DiscordCards.Inline(v, 60) : v);
        return string.Join(", ", shown) + (list.Count > max ? $" (+{list.Count - max})" : "");
    }
}
