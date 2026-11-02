namespace NOOSE_Website.Models.Common;

/// <summary>A Discord channel a record can be shared into, as the sharer sees it: no webhook.</summary>
public sealed record ShareChannel(string Id, string Name);

/// <summary>A share channel as the settings show it; the webhook only masked, never in full.</summary>
public sealed record ShareChannelSetting(string Id, string Name, string MaskedWebhook);

/// <summary>One row of the share-channel settings; an empty URL keeps the stored webhook.</summary>
public sealed class ShareChannelInput
{
    /// <summary>Id of an existing channel; null for a new one.</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>New webhook URL; null or empty keeps the one already stored.</summary>
    public string? WebhookUrl { get; set; }
}

/// <summary>One share: which record goes into which channel, with an optional short note.</summary>
public sealed record ChannelShareRequest(string RecordType, string RecordId, string ChannelId, string? Note);
