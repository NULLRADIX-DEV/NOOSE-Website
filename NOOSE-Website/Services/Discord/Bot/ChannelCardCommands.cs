using System.Globalization;
using NetCord.Rest;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Buttons of the cards in shared channels; they answer privately and never touch the card.</summary>
public sealed class ChannelCardCommands(
    BotRecords records,
    IJobService jobs,
    IAnnouncementService announcements,
    ITipService tips,
    ITicketService tickets,
    IBewerbungService applications,
    INotificationService notifications)
{
    /// <summary>How many of the bell's rows are searched; the service caps it.</summary>
    private const int OwnWindow = 200;

    /// <summary>Discord takes 512 characters in a link button.</summary>
    private const int MaxLinkChars = 512;

    public Task<BotReply> JobDoneAsync(BotCall call, string jobId)
        => RunAsync(() => jobs.StatusSetAsync(jobId, JobStatus.Done, call.User, call.Ct), "Aufgabe erledigt.");

    public Task<BotReply> AcknowledgeAsync(BotCall call, string announcementId)
        => RunAsync(() => announcements.AcknowledgeAsync(announcementId, call.User, call.Ct), "Zur Kenntnis genommen.");

    public Task<BotReply> TipAsync(BotCall call, string tipId)
        => RunAsync(() => tips.AssignSelfAsync(tipId, call.User, call.Ct), "Du hast den Hinweis übernommen.");

    public Task<BotReply> TicketAsync(BotCall call, string ticketId)
        => RunAsync(() => tickets.AssignSelfAsync(ticketId, call.User, call.Ct), "Du hast das Ticket übernommen.");

    public Task<BotReply> ApplicationAsync(BotCall call, string applicationId)
        => RunAsync(() => applications.AssignSelfAsync(applicationId, call.User, call.Ct), "Du hast die Bewerbung übernommen.");

    /// <summary>The type comes from a custom id; anything but a number has nothing to show.</summary>
    public Task<BotReply> DetailsAsync(BotCall call, string type, string recordId)
        => int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? DetailsAsync(call, number, recordId)
            : Task.FromResult(Nothing());

    /// <summary>The clicker's own newest notice for the record; nothing the clicker's bell does not show.</summary>
    public async Task<BotReply> DetailsAsync(BotCall call, int type, string recordId)
    {
        if (!Enum.IsDefined(typeof(NotificationType), type) || string.IsNullOrWhiteSpace(recordId))
        {
            return Nothing();
        }
        var wanted = (NotificationType)type;
        var suffix = "/" + recordId;
        var row = (await notifications.GetOwnAsync(call.User, OwnWindow, call.Ct))
            .Where(n => n.Type == wanted && PathOf(n.Href).EndsWith(suffix, StringComparison.Ordinal))
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefault();
        if (row is null)
        {
            return Nothing();
        }
        if (row.ReadAt is null)
        {
            await notifications.AsReadMarkAsync(row.Id, call.User, call.Ct);
        }
        return BotReply.Of(DiscordCards.Card(DiscordCards.Bronze,
            DiscordCards.Header("🔎", "Details"),
            DiscordCards.Line(),
            DiscordCards.Text($"**{DiscordCards.Escape(row.Title)}**\n-# {DiscordCards.When(row.CreatedAt)}"),
            await OpenRowAsync(row.Href, call.Ct)));
    }

    /// <summary>A service refusal is the answer; everything else is the handler's to report.</summary>
    private static async Task<BotReply> RunAsync(Func<Task> action, string done)
    {
        try
        {
            await action();
            return BotReply.Done(done);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return BotReply.Error(ex.Message);
        }
    }

    private static BotReply Nothing() => BotReply.Info("Für dich gibt es dazu nichts.");

    /// <summary>The href without query or fragment.</summary>
    private static string PathOf(string? href)
    {
        var path = href ?? string.Empty;
        var cut = path.IndexOfAny(['?', '#']);
        return cut < 0 ? path : path[..cut];
    }

    /// <summary>The link button of a relative href; an absolute one leads off the site.</summary>
    private async Task<ActionRowProperties?> OpenRowAsync(string? href, CancellationToken ct)
    {
        if (href is null || !href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }
        var link = await records.LinkAsync(href, ct);
        return link.Length > MaxLinkChars ? null : DiscordCards.Row(DiscordCards.Open(link, "Öffnen"));
    }
}
