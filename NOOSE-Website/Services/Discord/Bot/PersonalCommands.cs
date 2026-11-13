using NetCord;
using NetCord.Rest;
using NOOSE_Website.Models.Absences;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>/fib, /abmeldung, /termine, /quittieren, /aufgaben, /wiedervorlagen and their buttons.</summary>
public sealed class PersonalCommands(
    BotRecords records,
    IJobService jobs,
    IFollowupService followups,
    IAnnouncementService announcements,
    IRegulationService regulation,
    ICalendarService calendar,
    IAbsenceService absences,
    IEmergencyAlarmService alarms,
    TimeProvider clock)
{
    private const int PageSize = 5;

    // ---------------------------------------------------------------- hub

    public async Task<BotReply> HubAsync(BotCall call)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = BerlinTime.Today(now);
        var myJobs = await jobs.GetMyOpenAsync(call.User, call.Ct);
        var due = await followups.GetMineUntilAsync(call.User, BerlinTime.ToUtc(today.AddDays(1), TimeOnly.MinValue), call.Ct);
        var acks = await announcements.GetOpenAcknowledgmentsCountAsync(call.User, call.Ct)
            + await regulation.GetOpenCountAsync(call.User, call.Ct);
        var dates = (await calendar.GetEntriesAsync(BerlinTime.ToUtc(today, TimeOnly.MinValue), BerlinTime.ToUtc(today.AddDays(1), TimeOnly.MinValue),
                call.User, CalendarMode.My, call.Ct))
            .Count(e => e.Source is CalendarSource.Appointment or CalendarSource.Meeting or CalendarSource.Operation && !e.Obsolete);
        var openAlarms = await alarms.GetOpenForMeAsync(call.User, call.Ct);
        var absent = (await absences.GetListAsync(call.User, AbsenceViewScope.Own, today, today, call.Ct)).FirstOrDefault(a => a.AgentId == call.AgentId);
        var agent = call.Actor.Agent;
        var link = await records.LinkAsync("/meine-schicht", call.Ct);

        var lines = new List<string>
        {
            $"📌 **{myJobs.Count}** offene Aufgaben" + (myJobs.Count(j => j.DueDate is { } d && DateOnly.FromDateTime(d) < today) is > 0 and var late ? $" · ⏳ {late} überfällig" : ""),
            $"⏰ **{due.Count}** Wiedervorlagen bis heute",
            $"✍️ **{acks}** offene Kenntnisnahmen",
            $"🗓️ **{dates}** Termine heute",
        };
        if (openAlarms.Count > 0)
        {
            lines.Insert(0, $"🚨 **{openAlarms.Count}** Einsatzalarm wartet auf deine Antwort: **{DiscordCards.Inline(openAlarms[0].Situation, 80)}**");
        }

        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header(DiscordCards.DivisionEmoji(agent.Division), $"Dienst-Zentrale · {agent.Codename}",
                $"{DivisionRules.RankName(agent)} · {DivisionDisplayName(agent)}"
                + (absent is null ? "" : $" · 🌴 abgemeldet bis {DiscordCards.Day(absent.ToDate)}")),
            DiscordCards.Line(),
            DiscordCards.Text(string.Join("\n", lines)),
            DiscordCards.Line(),
            DiscordCards.Row(
                DiscordCards.Button(BotIds.Make("hub", "jobs"), "Aufgaben", "📌", ButtonStyle.Primary),
                DiscordCards.Button(BotIds.Make("hub", "fu"), "Wiedervorlagen", "⏰", ButtonStyle.Primary),
                DiscordCards.Button(BotIds.Make("hub", "ack"), "Quittieren", "✍️", acks > 0 ? ButtonStyle.Success : ButtonStyle.Secondary),
                DiscordCards.Button(BotIds.Make("hub", "dates"), "Termine", "🗓️")),
            DiscordCards.Row(
                absent is null
                    ? DiscordCards.Button(BotIds.Make("habs"), "Abmelden", "🌴")
                    // ending sets the last day to today, so a last day of today has nothing left to end
                    : absent.ToDate > today || absent.FromDate == today
                        ? DiscordCards.Button(BotIds.Make("absend"), "Abmeldung beenden", "🔙")
                        : null,
                openAlarms.Count > 0 ? DiscordCards.Button(BotIds.Make("al", openAlarms[0].Id, "1"), "Alarm: Komme", "✅", ButtonStyle.Success) : null,
                openAlarms.Count > 0 ? DiscordCards.Button(BotIds.Make("al", openAlarms[0].Id, "2"), "Komme nicht", "❌", ButtonStyle.Danger) : null,
                DiscordCards.Open(link, "Meine Schicht")),
            DiscordCards.Text(DiscordCards.Footer + $" · {DiscordCards.When(now)}"),
        };
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    private static string DivisionDisplayName(Data.Entities.Agent agent)
        => DivisionRules.Of(agent).Home is { } home ? DivisionDisplay.Name(home) : "ohne Abteilung";

    // ---------------------------------------------------------------- jobs

    public async Task<BotReply> AufgabenAsync(BotCall call, int page = 0)
    {
        var list = (await jobs.GetMyOpenAsync(call.User, call.Ct))
            .OrderBy(j => j.DueDate ?? DateTime.MaxValue).ThenByDescending(j => j.Priority).ToList();
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var (slice, pageNo, pages) = Page(list, page);
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("📌", "Deine Aufgaben", $"{list.Count} offen" + (pages > 1 ? $" · Seite {pageNo + 1}/{pages}" : "")),
            DiscordCards.Line(),
        };
        if (list.Count == 0)
        {
            parts.Add(DiscordCards.Text("Nichts offen. 🎉"));
        }
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        foreach (var job in slice)
        {
            // a due date is a Berlin calendar day, as the job editor stores it
            var due = job.DueDate is { } d
                ? DateOnly.FromDateTime(d) is var day && day < today
                    ? $" · ⏳ überfällig seit {DiscordCards.Day(day)}"
                    : $" · fällig {DiscordCards.Day(DateOnly.FromDateTime(d))}"
                : "";
            var text = $"**{DiscordCards.Inline(job.Title, 90)}** `{job.CaseNumber}`\n-# {JobPriorityDisplay.Name(job.Priority)} · {JobStatusDisplay.Name(job.Status)}{due}"
                + (job.CreatorCodename is null ? "" : $" · von {DiscordCards.Inline(job.CreatorCodename, 30)}");
            parts.Add(job.MayStatusChange
                ? DiscordCards.WithButton(text, DiscordCards.Button(BotIds.Make("jd", job.Id, pageNo.ToString()), "Erledigt", "✅", ButtonStyle.Success))
                : DiscordCards.WithButton(text, new LinkButtonProperties($"{baseUrl}/aufgaben/{job.Id}", "Öffnen")));
        }
        parts.Add(Pager("pj", pageNo, pages));
        parts.Add(DiscordCards.Row(DiscordCards.Open($"{baseUrl}/aufgaben", "Alle Aufgaben")));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    public async Task<BotReply> JobDoneAsync(BotCall call, string jobId, int page)
    {
        await jobs.StatusSetAsync(jobId, JobStatus.Done, call.User, call.Ct);
        return await AufgabenAsync(call, page);
    }

    // ---------------------------------------------------------------- follow-ups

    public async Task<BotReply> WiedervorlagenAsync(BotCall call, int page = 0)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var until = BerlinTime.ToUtc(BerlinTime.Today(now).AddDays(8), TimeOnly.MinValue);
        var list = (await followups.GetMineUntilAsync(call.User, until, call.Ct)).OrderBy(f => f.DueAt).ToList();
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var (slice, pageNo, pages) = Page(list, page);
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("⏰", "Deine Wiedervorlagen", $"{list.Count(f => f.DueAt <= now)} fällig · {list.Count} in den nächsten 7 Tagen"
                + (pages > 1 ? $" · Seite {pageNo + 1}/{pages}" : "")),
            DiscordCards.Line(),
        };
        if (list.Count == 0)
        {
            parts.Add(DiscordCards.Text("Keine Wiedervorlagen in Sicht."));
        }
        foreach (var item in slice)
        {
            var text = $"{(item.DueAt <= now ? "🔴" : "🟡")} **{DiscordCards.Inline(item.Display, 90)}** · {DiscordCards.When(item.DueAt)}"
                + (string.IsNullOrWhiteSpace(item.Note) ? "" : $"\n-# {DiscordCards.Inline(item.Note, 140)}");
            parts.Add(DiscordCards.WithButton(text, DiscordCards.Button(BotIds.Make("fd", item.Id, pageNo.ToString()), "Erledigt", "✅", ButtonStyle.Success)));
            if (item.Href is not null)
            {
                parts.Add(DiscordCards.Text($"-# [Akte öffnen]({baseUrl}{item.Href})"));
            }
        }
        parts.Add(Pager("pf", pageNo, pages));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    public async Task<BotReply> FollowupDoneAsync(BotCall call, string id, int page)
    {
        await followups.CompleteAsync(id, call.User, call.Ct);
        return await WiedervorlagenAsync(call, page);
    }

    // ---------------------------------------------------------------- acknowledgments

    public async Task<BotReply> QuittierenAsync(BotCall call)
    {
        var board = await announcements.GetOpenAcknowledgmentsAsync(call.User, call.Ct);
        var paragraphs = await regulation.GetOpenForMeAsync(call.User, call.Ct);
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("✍️", "Offene Kenntnisnahmen", $"{board.Count} Ankündigungen · {paragraphs.Count} Absätze der Dienstverordnung"),
            DiscordCards.Line(),
        };
        if (board.Count == 0 && paragraphs.Count == 0)
        {
            parts.Add(DiscordCards.Text("Alles quittiert. ✅"));
        }
        var shownBoard = board.Take(5).ToList();
        var shownParagraphs = paragraphs.Take(8 - shownBoard.Count).ToList();
        foreach (var item in shownBoard)
        {
            parts.Add(DiscordCards.WithButton(
                $"{(item.Important ? "❗" : "📣")} **{DiscordCards.Inline(item.Title, 90)}**\n-# vom {DiscordCards.When(item.CreatedAt, 'd')}"
                + (item.DueAt is { } due ? $" · bis {DiscordCards.When(due)}" : "") + $" · [lesen]({baseUrl}/brett)",
                DiscordCards.Button(BotIds.Make("ack", item.AnnouncementId), "Quittieren", "✅", ButtonStyle.Success)));
        }
        foreach (var item in shownParagraphs)
        {
            parts.Add(DiscordCards.WithButton(
                $"📘 **{DiscordCards.Inline(item.Label, 20)} {DiscordCards.Inline(item.Heading, 80)}**\n-# Fassung {item.Version} seit {DiscordCards.When(item.Since, 'd')} · [lesen]({baseUrl}/dienstverordnung/absatz/{item.ParagraphId})",
                DiscordCards.Button(BotIds.Make("ackr", item.ParagraphId, item.Version.ToString(), "l"), "Kenntnis", "✅", ButtonStyle.Success)));
        }
        var hidden = board.Count + paragraphs.Count - shownBoard.Count - shownParagraphs.Count;
        if (hidden > 0)
        {
            parts.Add(DiscordCards.Text($"-# … und {hidden} weitere auf der Seite"));
        }
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    public async Task<BotReply> AcknowledgeAnnouncementAsync(BotCall call, string id)
    {
        await announcements.AcknowledgeAsync(id, call.User, call.Ct);
        return await QuittierenAsync(call);
    }

    /// <summary>From the list the list comes back; from a /dvo card a short confirmation.</summary>
    public async Task<BotReply> AcknowledgeParagraphAsync(BotCall call, string id, int version, bool fromList)
    {
        await regulation.AcknowledgeAsync(id, version, call.User, call.Ct);
        return fromList ? await QuittierenAsync(call) : BotReply.Done("Zur Kenntnis genommen.");
    }

    // ---------------------------------------------------------------- calendar

    public async Task<BotReply> TermineAsync(BotCall call, string? range = null)
    {
        range ??= call.Options.Text("zeitraum") ?? "woche";
        var now = clock.GetUtcNow().UtcDateTime;
        var today = BerlinTime.Today(now);
        var days = range switch { "heute" => 1, "14" => 14, _ => 7 };
        var entries = (await calendar.GetEntriesAsync(BerlinTime.ToUtc(today, TimeOnly.MinValue), BerlinTime.ToUtc(today.AddDays(days), TimeOnly.MinValue),
                call.User, CalendarMode.My, call.Ct))
            .Where(e => e.Source is CalendarSource.Appointment or CalendarSource.Meeting or CalendarSource.Operation && !e.Obsolete)
            .OrderBy(e => e.StartLocal)
            .ToList();
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("🗓️", "Deine Termine", days == 1 ? "heute" : $"die nächsten {days} Tage"),
            DiscordCards.Line(),
        };
        if (entries.Count == 0)
        {
            parts.Add(DiscordCards.Text("Keine Termine."));
        }
        foreach (var day in entries.GroupBy(e => DateOnly.FromDateTime(e.StartLocal)).Take(14))
        {
            var lines = day.Take(8).Select(e =>
            {
                var start = BerlinTime.ToUtc(DateOnly.FromDateTime(e.StartLocal), TimeOnly.FromDateTime(e.StartLocal));
                var icon = e.Source switch { CalendarSource.Meeting => "🗣️", CalendarSource.Operation => "🎯", _ => "📅" };
                var title = e.Href is null ? $"**{DiscordCards.Inline(e.Title, 80)}**" : $"**[{DiscordCards.Inline(e.Title, 80)}]({baseUrl}{e.Href})**";
                return $"{icon} {(e.WholeDay ? "ganztägig" : DiscordCards.When(start, 't'))} {title}";
            });
            parts.Add(DiscordCards.Text($"**{DiscordCards.Day(day.Key)}**\n" + string.Join("\n", lines)));
        }
        parts.Add(DiscordCards.Row(
            DiscordCards.Button(BotIds.Make("dates", "heute"), "Heute", style: range == "heute" ? ButtonStyle.Primary : ButtonStyle.Secondary),
            DiscordCards.Button(BotIds.Make("dates", "woche"), "Woche", style: range == "woche" ? ButtonStyle.Primary : ButtonStyle.Secondary),
            DiscordCards.Button(BotIds.Make("dates", "14"), "14 Tage", style: range == "14" ? ButtonStyle.Primary : ButtonStyle.Secondary),
            DiscordCards.Open($"{baseUrl}/kalender", "Kalender")));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    // ---------------------------------------------------------------- absence

    public async Task<BotReply> AbmeldungAsync(BotCall call)
    {
        if (call.Options.Sub == "beenden")
        {
            return await EndAbsenceAsync(call);
        }
        return await CreateAbsenceAsync(call, call.Options.Text("von"), call.Options.Text("bis"), call.Options.Text("art"), call.Options.Text("grund"));
    }

    public async Task<BotReply> CreateAbsenceAsync(BotCall call, string? fromText, string? toText, string? kind, string? reason)
    {
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        var from = DiscordDateParser.ParseDay(fromText, today);
        // only "+N" counts from the first day; heute, morgen and dates mean what they say
        var to = toText?.Trim().StartsWith('+') == true
            ? DiscordDateParser.ParseDay(toText, from ?? today)
            : DiscordDateParser.ParseDay(toText, today);
        if (from is null || to is null)
        {
            return BotReply.Error("Das Datum verstehe ich nicht. Beispiele: heute, morgen, +3, 20.11., 20.11.2026.");
        }
        if (to < from)
        {
            return BotReply.Error("Der letzte Tag liegt vor dem ersten.");
        }
        var category = Enum.TryParse<AbsenceCategory>(kind, out var parsed) ? parsed : AbsenceCategory.Vacation;
        var absence = await absences.CreateAsync(new AbsenceInput
        {
            From = from.Value.ToDateTime(TimeOnly.MinValue),
            To = to.Value.ToDateTime(TimeOnly.MinValue),
            Category = category,
            Reason = reason,
        }, call.User, call.Ct);
        var days = to.Value.DayNumber - from.Value.DayNumber + 1;
        return BotReply.Of(DiscordCards.Card(DiscordCards.Success,
            DiscordCards.Header("🌴", "Abmeldung eingetragen", $"{AbsenceCategoryDisplay.Name(category)} · {days} {(days == 1 ? "Tag" : "Tage")}"),
            DiscordCards.Text($"Von {DiscordCards.Day(from.Value)} bis {DiscordCards.Day(to.Value)}"
                + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n> {DiscordCards.Inline(reason, 200)}")),
            DiscordCards.Text("-# Die Führung ist benachrichtigt."),
            DiscordCards.Row(DiscordCards.Open(await records.LinkAsync("/abmeldungen", call.Ct), "Abmeldungen"))));
    }

    public async Task<BotReply> EndAbsenceAsync(BotCall call)
    {
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        var running = (await absences.GetListAsync(call.User, AbsenceViewScope.Own, today, today, call.Ct))
            .FirstOrDefault(a => a.AgentId == call.AgentId && a.FromDate <= today && a.ToDate >= today);
        if (running is null)
        {
            return BotReply.Info("Du bist heute nicht abgemeldet.");
        }
        // started today: nothing to keep, it goes
        if (running.FromDate == today)
        {
            await absences.DeleteAsync(running.Id, call.User, call.Ct);
            return BotReply.Done("Abmeldung zurückgenommen. Willkommen zurück!");
        }
        // an agent may not move the last day into the past, so today is the earliest end
        await absences.RefreshAsync(running.Id, new AbsenceInput
        {
            From = running.FromDate.ToDateTime(TimeOnly.MinValue),
            To = today.ToDateTime(TimeOnly.MinValue),
            Category = running.Category,
            Reason = running.Reason,
        }, call.User, call.Ct);
        return BotReply.Done($"Abmeldung endet heute ({DiscordCards.Day(today)}). Willkommen zurück!");
    }

    public static ModalProperties AbsenceModal()
        => new(BotIds.Make("mabs"), "Abmeldung eintragen")
        {
            new LabelProperties("Erster Tag", new TextInputProperties("von", TextInputStyle.Short).WithPlaceholder("heute, morgen, 20.11.").WithRequired(true).WithMaxLength(20)),
            new LabelProperties("Letzter Tag", new TextInputProperties("bis", TextInputStyle.Short).WithPlaceholder("morgen, +3, 24.11.").WithRequired(true).WithMaxLength(20)),
            new LabelProperties("Art", new StringMenuProperties("art")
            {
                new StringMenuSelectOptionProperties("Urlaub", nameof(AbsenceCategory.Vacation)) { Default = true },
                new StringMenuSelectOptionProperties("Arbeit", nameof(AbsenceCategory.Work)),
                new StringMenuSelectOptionProperties("Krank", nameof(AbsenceCategory.Sick)),
                new StringMenuSelectOptionProperties("RP-Pause", nameof(AbsenceCategory.RpBreak)),
                new StringMenuSelectOptionProperties("Sonstiges", nameof(AbsenceCategory.Misc)),
            }),
            new LabelProperties("Grund (optional)", new TextInputProperties("grund", TextInputStyle.Paragraph).WithRequired(false).WithMaxLength(300)),
        };

    // ---------------------------------------------------------------- helpers

    private static (IEnumerable<T> Slice, int Page, int Pages) Page<T>(IReadOnlyList<T> list, int page)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        var current = Math.Clamp(page, 0, pages - 1);
        return (list.Skip(current * PageSize).Take(PageSize), current, pages);
    }

    private static ActionRowProperties? Pager(string action, int page, int pages)
        => pages <= 1
            ? null
            : DiscordCards.Row(
                DiscordCards.Button(BotIds.Make(action, (page - 1).ToString()), "Zurück", "◀️").WithDisabled(page == 0),
                DiscordCards.Button(BotIds.Make(action, (page + 1).ToString()), "Weiter", "▶️").WithDisabled(page >= pages - 1));
}
