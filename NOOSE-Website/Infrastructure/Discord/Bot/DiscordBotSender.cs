using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <inheritdoc cref="IDiscordBotSender" />
public sealed class DiscordBotSender(GatewayClient client) : IDiscordBotSender
{
    public async Task<ulong> SendAsync(ulong channelId, IReadOnlyList<IMessageComponentProperties> card,
        AllowedMentionsProperties mentions, ulong? replyTo = null, CancellationToken cancellationToken = default)
    {
        var message = await client.Rest.SendMessageAsync(channelId, new MessageProperties
        {
            Components = card,
            Flags = MessageFlags.IsComponentsV2,
            AllowedMentions = mentions,
            MessageReference = Reply(replyTo),
        }, cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task SendTextAsync(ulong channelId, string content, AllowedMentionsProperties mentions,
        ulong? replyTo = null, CancellationToken cancellationToken = default)
        => await client.Rest.SendMessageAsync(channelId, new MessageProperties
        {
            Content = content,
            AllowedMentions = mentions,
            MessageReference = Reply(replyTo),
        }, cancellationToken: cancellationToken);

    public async Task EditAsync(ulong channelId, ulong messageId, IReadOnlyList<IMessageComponentProperties> card,
        CancellationToken cancellationToken = default)
        => await client.Rest.ModifyMessageAsync(channelId, messageId, m =>
        {
            m.Components = card;
            m.Flags = MessageFlags.IsComponentsV2;
            m.AllowedMentions = AllowedMentionsProperties.None;
        }, cancellationToken: cancellationToken);

    public async Task DeleteAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
        => await client.Rest.DeleteMessageAsync(channelId, messageId, cancellationToken: cancellationToken);

    // survive deleted card
    private static MessageReferenceProperties? Reply(ulong? messageId)
        => messageId is { } id ? MessageReferenceProperties.Reply(id, failIfNotExists: false) : null;
}
