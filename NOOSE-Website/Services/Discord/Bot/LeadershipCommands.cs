using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Models.Absences;
using NOOSE_Website.Services.Decorations;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>/agent, the user context menu, /abgemeldet, /offen, /inaktiv.</summary>
public sealed class LeadershipCommands(
    BotRecords records,
    IDbContextFactory<AppDbContext> dbFactory,
    IAbsenceService absences,
    IDecorationService decorations,
    IInactivityRadarService inactivity,
    IRequestService requests,
    IPublicWantedService publicWanted,
    IBountyService bounties,
    ITipService tips,
    ITicketService tickets,
    IPersonnelFileService personnel,
    IWarrantService warrants,
    ITaskforceService taskforces,
    IAgentManagementService agentManagement,
    IBewerbungService applications,
    TimeProvider clock)
{
    public const string NoAgent = "Dazu gibt es keinen FIB-Agenten.";

    // ---------------------------------------------------------------- agent profile

    public async Task<BotReply> AgentAsync(BotCall call)
    {
        var codename = call.Options.Text("codename");
        if (codename is null)
        {
            return BotReply.Error("Bitte einen Codenamen angeben.");
        }
        var agents = await SelectableAsync(call.Ct);
        // picked id first, else the one clear (typo-tolerant) codename match
        var agent = agents.FirstOrDefault(u => u.Id == codename) ?? BotFuzzy.Single(agents, codename, a => [a.Codename]);
        return agent is null ? BotReply.Error(NoAgent) : await ProfileAsync(agent, call);
    }

    private async Task<List<Agent>> SelectableAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Users.AsNoTracking().OnlySelectable().Where(u => u.Codename != "").ToListAsync(ct))
            .Where(u => u.Id != Infrastructure.DemoIdentity.AgentId)
            .ToList();
    }

    /// <summary>Right-click on a Discord user; team leads and partners answer like strangers.</summary>
    public async Task<BotReply> ProfileOfDiscordUserAsync(ulong discordUserId, BotCall call)
    {
        var discordId = discordUserId.ToString(CultureInfo.InvariantCulture);
        await using var db = await dbFactory.CreateDbContextAsync(call.Ct);
        var agent = await db.Users.AsNoTracking().OnlySelectable()
            .FirstOrDefaultAsync(u => u.DiscordId == discordId, call.Ct);
        return agent is null || agent.Id == Infrastructure.DemoIdentity.AgentId ? BotReply.Error(NoAgent) : await ProfileAsync(agent, call);
    }

    public async Task<BotReply> AgentSuggestAsync(BotCall call, string option)
    {
        var typed = call.Options.Text(option);
        var agents = BotFuzzy.Rank((await SelectableAsync(call.Ct)).OrderBy(u => u.Codename), typed, a => [a.Codename]).Take(25);
        return BotReply.Autocomplete(agents.Select(a =>
            new NetCord.Rest.ApplicationCommandOptionChoiceProperties(DiscordCards.Clip($"{a.Codename} · {DivisionRules.RankName(a)}", 100), a.Id)));
    }

    private async Task<BotReply> ProfileAsync(Agent agent, BotCall call)
    {
        await records.LogAsync(nameof(Agent), agent.Id);
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        var absent = await absences.GetAbsentOnAsync(today, call.User, call.Ct);
        var unit = DivisionRules.Of(agent);
        IReadOnlyList<Models.Decorations.DecorationAwardView> awards;
        try
        {
            awards = (await decorations.GetForAgentAsync(agent.Id, call.User, call.Ct)).Where(a => !a.IsRevoked).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            awards = [];
        }

        var lines = new List<string>
        {
            DiscordCards.Fields(
                ("Dienstgrad", DivisionRules.RankName(agent)),
                ("Abteilung", unit.IsBoard ? "Board" + (unit.Home is { } h ? $" (Heimat {DivisionDisplay.Name(h)})" : "") : unit.Home is { } home ? DivisionDisplay.Name(home) : "—"),
                ("Dienstnummer", DiscordCards.Inline(agent.BadgeNumber, 20)),
                ("Klarname", call.User.MayRealNameSee() ? DiscordCards.Inline(agent.RealName, 60) : null),
                ("Discord", DiscordWebhookService.IsSnowflake(agent.DiscordId) ? $"<@{agent.DiscordId}>" : null)),
        };
        var flags = new List<string>();
        if (agent.IsHRB)
        {
            flags.Add("🗂️ Administration Division");
        }
        if (agent.IsAdmin && call.User.IsLeadership())
        {
            flags.Add("🛠️ Admin");
        }
        if (absent.TryGetValue(agent.Id, out var until))
        {
            flags.Add($"🌴 abgemeldet bis {DiscordCards.Day(until)}");
        }
        else
        {
            flags.Add("🟢 im Dienst verfügbar");
        }
        lines.Add(string.Join(" · ", flags));

        var parts = new List<NetCord.Rest.IComponentContainerComponentProperties?>
        {
            DiscordCards.HeaderWithThumbnail(DiscordCards.DivisionEmoji(unit.Home), agent.Codename, "Agentenprofil",
                agent.AvatarUrl is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null,
                string.Join("\n", lines)),
        };
        if (awards.Count > 0)
        {
            parts.Add(DiscordCards.Line());
            parts.Add(DiscordCards.Text("**🎖️ Ehrungen**\n" + string.Join("\n", awards.OrderByDescending(a => a.AwardedOn).Take(6)
                .Select(a => $"{a.Symbol} **{DiscordCards.Inline(a.DecorationName, 60)}** · {DiscordCards.When(a.AwardedOn, 'd')}"))));
        }
        parts.Add(DiscordCards.Line());
        parts.Add(DiscordCards.Text(DiscordCards.Footer));
        if (call.User.IsLeadership())
        {
            parts.Add(DiscordCards.Row(DiscordCards.Open(await records.LinkAsync($"/personal/{agent.Id}", call.Ct), "Personalakte")));
        }
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze, parts));
    }

    // ---------------------------------------------------------------- absences

    public async Task<BotReply> AbgemeldetAsync(BotCall call)
    {
        var today = BerlinTime.Today(clock.GetUtcNow().UtcDateTime);
        var rows = await absences.GetListAsync(call.User, AbsenceViewScope.Team, today, today.AddDays(6), call.Ct);
        var now = rows.Where(r => r.FromDate <= today && r.ToDate >= today).OrderBy(r => r.ToDate).ToList();
        var soon = rows.Where(r => r.FromDate > today).OrderBy(r => r.FromDate).ToList();
        string Line(Models.Absences.AbsenceRow r)
            => $"• **{DiscordCards.Inline(r.Codename, 40)}** · {AbsenceCategoryDisplay.Name(r.Category)} · {DiscordCards.Day(r.FromDate)} – {DiscordCards.Day(r.ToDate)}"
               + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $"\n-# {DiscordCards.Inline(r.Reason, 100)}");
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("🌴", "Abmeldungen", $"{now.Count} heute · {soon.Count} in den nächsten Tagen"),
            DiscordCards.Line(),
            DiscordCards.Text("**Heute abgemeldet**\n" + (now.Count == 0 ? "Niemand." : string.Join("\n", now.Take(20).Select(Line)))),
            soon.Count == 0 ? null : DiscordCards.Text("**Demnächst**\n" + string.Join("\n", soon.Take(15).Select(Line))),
            DiscordCards.Line(),
            DiscordCards.Row(DiscordCards.Open(await records.LinkAsync("/abmeldungen", call.Ct), "Abmeldungen öffnen"))));
    }

    // ---------------------------------------------------------------- open items

    public async Task<BotReply> OffenAsync(BotCall call)
    {
        var user = call.User;
        if (!user.IsLeadership() && !user.IsHRB())
        {
            return BotReply.Error("Dieser Befehl ist der Führung vorbehalten.");
        }
        var baseUrl = await records.BaseUrlAsync(call.Ct);
        var lines = new List<string>();

        async Task Add(string emoji, string label, string href, Func<Task<int>> count)
        {
            try
            {
                var n = await count();
                lines.Add($"{emoji} **{n}** [{label}]({baseUrl}{href})" + (n > 0 ? " ⬅️" : ""));
            }
            catch (UnauthorizedAccessException) { /* not for this rank */ }
        }

        if (user.IsLeadership())
        {
            await Add("🎖️", "Beförderungsanträge", "/admin/freigaben", () => personnel.CountOpenPromotionRequestsAsync(user, call.Ct));
            await Add("🧾", "Anträge", "/anfragen", () => requests.GetOpenCountAsync(true, call.Ct));
            await Add("📜", "Anordnungen", "/admin/freigaben", () => warrants.GetOpenCountAsync(user, call.Ct));
            await Add("🛡️", "Taskforce-Anträge", "/admin/freigaben", async () => (await taskforces.GetRequestedAsync(call.Ct)).Count);
            await Add("🆕", "Freigaben neuer Konten", "/admin/freigaben", async () => (await agentManagement.GetPendingAsync(call.Ct)).Count);
        }
        if (user.IsHrbOrLeadership())
        {
            await Add("📨", "Bewerbungen in Arbeit", "/bewerbungen", async () => (await applications.ListAsync(user, call.Ct))
                .Count(b => b.Status is BewerbungStatus.Eingereicht or BewerbungStatus.InSicherheitspruefung or BewerbungStatus.ImTest
                    or BewerbungStatus.ImVorstellungsgespraech));
        }
        await Add("💡", "Bürgerhinweise", "/hinweise", () => tips.GetOpenCountAsync(call.Ct));
        if (user.IsLeadership())
        {
            await Add("🎫", "Bürger-Tickets", "/tickets", () => tickets.GetOpenCountAsync(call.Ct));
        }
        if (user.MayHighestClassification())
        {
            await Add("📢", "Ausschreibungs-Anträge", "/fahndung?tab=oeffentlich", () => publicWanted.GetPendingRequestCountAsync(call.Ct));
            await Add("💰", "Kopfgeld-Anträge", "/fahndung?tab=oeffentlich", () => bounties.GetPendingRequestCountAsync(call.Ct));
        }
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("📥", "Offen bei der Führung", DiscordCards.When(clock.GetUtcNow().UtcDateTime)),
            DiscordCards.Line(),
            DiscordCards.Text(lines.Count == 0 ? "Nichts für dich." : string.Join("\n", lines))));
    }

    // ---------------------------------------------------------------- inactivity

    public async Task<BotReply> InaktivAsync(BotCall call)
    {
        if (!call.User.IsLeadership())
        {
            return BotReply.Error("Das Inaktivitäts-Radar ist der Führung vorbehalten.");
        }
        var radar = await inactivity.GetAsync(call.User, call.Ct);
        var flagged = radar.Rows.Where(r => r.Tier != InactivityTier.None)
            .OrderByDescending(r => r.Tier).ThenByDescending(r => r.InactiveDays).ToList();
        var parts = new List<NetCord.Rest.IComponentContainerComponentProperties?>
        {
            DiscordCards.Header("📡", "Inaktivitäts-Radar", $"{flagged.Count} auffällig von {radar.Rows.Count}"),
            DiscordCards.Line(),
        };
        foreach (var tier in new[] { InactivityTier.Termination, InactivityTier.Note, InactivityTier.FollowUp })
        {
            var group = flagged.Where(r => r.Tier == tier).ToList();
            if (group.Count == 0)
            {
                continue;
            }
            var emoji = tier switch { InactivityTier.Termination => "🔴", InactivityTier.Note => "🟠", _ => "🟡" };
            parts.Add(DiscordCards.Text($"{emoji} **{InactivityDisplay.Tier(tier)}** ({group.Count})\n" + string.Join("\n", group.Take(10).Select(r =>
                $"• **{DiscordCards.Inline(r.Codename, 40)}** · {r.InactiveDays} Tage still · zuletzt {DiscordCards.When(r.LastTraceAt)}"
                + (r.Provisional ? " · vorläufig" : "") + (r.NudgedAt is { } n ? $" · nachgefasst {DiscordCards.When(n)}" : "")))));
        }
        if (flagged.Count == 0)
        {
            parts.Add(DiscordCards.Text("Alle aktiv. 👍"));
        }
        parts.Add(DiscordCards.Row(DiscordCards.Open(await records.LinkAsync("/personal", call.Ct), "Radar öffnen")));
        return BotReply.Of(DiscordCards.Card(DiscordCards.Warning, parts));
    }
}
