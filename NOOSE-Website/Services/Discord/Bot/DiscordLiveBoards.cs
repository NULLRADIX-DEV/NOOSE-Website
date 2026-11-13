using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>A rendered board and the hash that decides whether Discord needs an edit.</summary>
public sealed record BoardCard(IReadOnlyList<IMessageComponentProperties> Components, string Hash);

/// <summary>Renders the live alarm and wanted boards and keeps their message ids; no rights of a viewer, the channel is internal by admin choice.</summary>
public sealed class DiscordLiveBoards(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicWantedService publicWanted,
    ISystemSettingService systemSettings,
    IDiscordWebhookService webhook)
{
    private const int MaxBoards = 50;

    // ---------------------------------------------------------------- alarm board

    /// <summary>The alarm as a live card, or null when it does not exist.</summary>
    public async Task<(BoardCard Card, bool IsOpen, DateTime TriggeredAt)?> RenderAlarmAsync(string alarmId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var alarm = await db.EmergencyAlarms.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alarmId, ct);
        if (alarm is null)
        {
            return null;
        }
        var rows = await db.EmergencyAlarmRecipients.AsNoTracking()
            .Where(r => r.AlarmId == alarmId)
            .Select(r => new { r.Answer, Codename = r.Agent!.Codename })
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var open = EmergencyAlarmRules.IsOpen(alarm, now);
        var coming = rows.Where(r => r.Answer == EmergencyAlarmAnswer.Coming).Select(r => r.Codename).OrderBy(n => n).ToList();
        var notComing = rows.Where(r => r.Answer == EmergencyAlarmAnswer.NotComing).Select(r => r.Codename).OrderBy(n => n).ToList();
        var waiting = rows.Count(r => r.Answer is null);
        var baseUrl = (await webhook.GetConfigAsync(ct)).SiteBaseUrl.TrimEnd('/');

        string Names(List<string> names) => names.Count == 0 ? "—" : string.Join(", ", names.Take(30).Select(n => DiscordCards.Inline(n, 30)))
            + (names.Count > 30 ? $" (+{names.Count - 30})" : "");

        var state = open
            ? $"⏱️ läuft ab {DiscordCards.When(alarm.Expiry)}"
            : alarm.EndedAt is { } ended ? $"⏹️ beendet {DiscordCards.When(ended)}" : $"⌛ abgelaufen {DiscordCards.When(alarm.Expiry)}";
        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Text($"# 🚨 EINSATZALARM\n**{DiscordCards.Inline(alarm.Situation, 200)}**"
                + (string.IsNullOrWhiteSpace(alarm.Location) ? "" : $"\n📍 {DiscordCards.Inline(alarm.Location, 150)}")),
            DiscordCards.Text($"-# ausgelöst von {DiscordCards.Inline(alarm.TriggeredByName, 40)} {DiscordCards.When(alarm.TriggeredAt)}"
                + $" · {(alarm.Target is { } t ? DivisionDisplay.Name(t) : "alle Abteilungen")}"
                + (alarm.IsEmergency ? " · ⚠️ Gefahr im Verzug" : "") + $" · {state}"),
            DiscordCards.Line(),
            DiscordCards.Text($"✅ **Kommen ({coming.Count})**\n{Names(coming)}\n\n❌ **Kommen nicht ({notComing.Count})**\n{Names(notComing)}\n\n⏳ **Offen:** {waiting}"),
            DiscordCards.Line(),
            DiscordCards.Row(
                DiscordCards.Button(BotIds.Make("al", alarm.Id, "1"), "Komme", "✅", ButtonStyle.Success).WithDisabled(!open),
                DiscordCards.Button(BotIds.Make("al", alarm.Id, "2"), "Komme nicht", "❌", ButtonStyle.Danger).WithDisabled(!open),
                DiscordCards.Open($"{baseUrl}/einsatzalarme/{alarm.Id}", "Alarm öffnen")),
        };
        var card = DiscordCards.Card(open ? DiscordCards.Danger : DiscordCards.Steel, parts);
        var hash = Hash($"{alarm.Id}|{open}|{alarm.EndedAt}|{string.Join(",", coming)}|{string.Join(",", notComing)}|{waiting}|{alarm.Situation}|{alarm.Location}");
        IMessageComponentProperties[] alarmBoard = [card];
        DiscordCards.FitText(alarmBoard);
        return (new BoardCard(alarmBoard, hash), open, alarm.TriggeredAt);
    }

    public async Task<int> CountOpenAlarmsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EmergencyAlarms.AsNoTracking().CountAsync(a => a.EndedAt == null && a.Expiry > now, ct);
    }

    /// <summary>"channelId|messageId|hash" of an alarm's board, or null.</summary>
    public async Task<(ulong Channel, ulong Message, string Hash)?> GetAlarmBoardAsync(string alarmId, CancellationToken ct)
        => (await ReadAlarmBoardsAsync(ct)).TryGetValue(alarmId, out var value) && Parse(value) is { } parsed ? parsed : null;

    public async Task SetAlarmBoardAsync(string alarmId, ulong channel, ulong message, string hash, CancellationToken ct)
    {
        var boards = await ReadAlarmBoardsAsync(ct);
        boards[alarmId] = $"{channel}|{message}|{hash}";
        // the oldest fall off; a board that old no longer changes
        var kept = boards.TakeLast(MaxBoards).ToDictionary(p => p.Key, p => p.Value);
        await WriteAsync(SystemSettingKeys.DiscordBotAlarmBoards, JsonSerializer.Serialize(kept), ct);
    }

    private async Task<Dictionary<string, string>> ReadAlarmBoardsAsync(CancellationToken ct)
    {
        var json = await ReadAsync(SystemSettingKeys.DiscordBotAlarmBoards, ct);
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    // ---------------------------------------------------------------- wanted board

    /// <summary>The wanted board as every internal agent sees it: no VS, no archive.</summary>
    public async Task<BoardCard> RenderWantedAsync(CancellationToken ct)
    {
        var threshold = (await systemSettings.GetAsync(ct)).WantedBoardMinHazard;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var people = (await db.People.AsNoTracking()
                .Where(p => !p.IsArchived && !p.IsClassified && !p.IsTRUClassified && !p.IsHRBClassified)
                .Where(p => p.IsWanted || p.ThreatScore != null)
                .Select(p => new { p.Name, p.CaseNumber, p.IsWanted, p.ThreatScore, p.WantedReason })
                .ToListAsync(ct))
            .Where(p => WantedBoard.IsOnBoard(p.IsWanted, p.ThreatScore, threshold))
            .OrderByDescending(p => p.IsWanted).ThenByDescending(p => p.ThreatScore ?? 0)
            .ToList();
        var board = await publicWanted.GetBoardAsync(ct);
        var baseUrl = (await webhook.GetConfigAsync(ct)).SiteBaseUrl.TrimEnd('/');

        var lines = people.Take(20).Select(p =>
        {
            var hazard = HazardLevelLogic.From(p.ThreatScore);
            var bounty = board.BountyByCaseNumber.TryGetValue(p.CaseNumber, out var b) && b.Total > 0
                ? $" · 💰 **{DiscordCards.Money(b.Total)}**" : "";
            var why = p.IsWanted
                ? string.IsNullOrWhiteSpace(p.WantedReason) ? "manuell ausgeschrieben" : DiscordCards.Inline(p.WantedReason, 80)
                : $"Gefahr {HazardLevelLogic.Name(hazard)}";
            return $"{DiscordCards.HazardEmoji(hazard)} **{DiscordCards.Inline(p.Name, 50)}** `{p.CaseNumber}`{bounty}\n-# {why}";
        }).ToList();
        var topBounties = board.BountyByCaseNumber.Where(b => b.Value.Total > 0)
            .OrderByDescending(b => b.Value.Total).Take(5)
            .Select((b, i) => $"**{i + 1}.** `{b.Key}` · {DiscordCards.Money(b.Value.Total)}{(b.Value.IsCap ? " (Höchstbetrag)" : "")}")
            .ToList();

        var parts = new List<IComponentContainerComponentProperties?>
        {
            DiscordCards.Text($"# 🚨 Fahndungstafel\n-# {people.Count} gesucht · ab Gefahr {HazardLevelLogic.Name(threshold)} · aktualisiert {DiscordCards.When(DateTime.UtcNow)}"),
            DiscordCards.Line(),
            DiscordCards.Text(lines.Count == 0 ? "Niemand steht gerade auf der Tafel." : string.Join("\n", lines)
                + (people.Count > 20 ? $"\n-# … und {people.Count - 20} weitere" : "")),
            topBounties.Count == 0 ? null : DiscordCards.Line(),
            topBounties.Count == 0 ? null : DiscordCards.Text("💰 **Höchste Kopfgelder**\n" + string.Join("\n", topBounties)),
            DiscordCards.Line(),
            DiscordCards.Text(DiscordCards.Footer),
            DiscordCards.Row(DiscordCards.Open($"{baseUrl}/fahndung", "Fahndung öffnen"), DiscordCards.Open($"{baseUrl}/gesucht", "Öffentliche Fahndung")),
        };
        var hash = Hash(string.Join("|", lines) + "#" + string.Join("|", topBounties) + "#" + threshold + "#" + people.Count);
        IMessageComponentProperties[] wantedBoard = [DiscordCards.Card(DiscordCards.Danger, parts)];
        DiscordCards.FitText(wantedBoard);
        return new BoardCard(wantedBoard, hash);
    }

    public async Task<(ulong Channel, ulong Message, string Hash)?> GetWantedBoardAsync(CancellationToken ct)
        => Parse(await ReadAsync(SystemSettingKeys.DiscordBotWantedBoardState, ct));

    public Task SetWantedBoardAsync(ulong channel, ulong message, string hash, CancellationToken ct)
        => WriteAsync(SystemSettingKeys.DiscordBotWantedBoardState, $"{channel}|{message}|{hash}", ct);

    public Task ClearWantedBoardAsync(CancellationToken ct) => WriteAsync(SystemSettingKeys.DiscordBotWantedBoardState, null, ct);

    // ---------------------------------------------------------------- command list

    /// <summary>Channel, message ids in posting order and hash of the command list, or null.</summary>
    public async Task<(ulong Channel, IReadOnlyList<ulong> Messages, string Hash)?> GetCommandsBoardAsync(CancellationToken ct)
    {
        var parts = (await ReadAsync(SystemSettingKeys.DiscordBotCommandsBoardState, ct))?.Split('|');
        if (parts is not { Length: 3 } || !ulong.TryParse(parts[0], out var channel))
        {
            return null;
        }
        var messages = new List<ulong>();
        foreach (var id in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ulong.TryParse(id, out var message))
            {
                return null;
            }
            messages.Add(message);
        }
        return (channel, messages, parts[2]);
    }

    public Task SetCommandsBoardAsync(ulong channel, IEnumerable<ulong> messages, string hash, CancellationToken ct)
        => WriteAsync(SystemSettingKeys.DiscordBotCommandsBoardState, $"{channel}|{string.Join(",", messages)}|{hash}", ct);

    public Task ClearCommandsBoardAsync(CancellationToken ct) => WriteAsync(SystemSettingKeys.DiscordBotCommandsBoardState, null, ct);

    // ---------------------------------------------------------------- storage

    private static (ulong, ulong, string)? Parse(string? value)
    {
        var parts = value?.Split('|');
        return parts is { Length: 3 } && ulong.TryParse(parts[0], out var channel) && ulong.TryParse(parts[1], out var message)
            ? (channel, message, parts[2])
            : null;
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    // worker state: ExecuteUpdate, no audit row (like the threat board)
    private async Task WriteAsync(string key, string? value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (value is null)
        {
            await db.SystemSettings.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
            return;
        }
        var updated = await db.SystemSettings.Where(s => s.Key == key).ExecuteUpdateAsync(s => s.SetProperty(e => e.Value, value), ct);
        if (updated == 0)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            await db.SaveChangesAsync(ct);
        }
    }
}
