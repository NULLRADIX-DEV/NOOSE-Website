using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Long ping lists are split so every Discord post stays within its limits.</summary>
public sealed class DiscordMentionBatchTests
{
    // a body like the generic notice plus a link; real snowflakes have 19 digits
    private const int Body = 120;

    private static List<string> Snowflakes(int count)
        => Enumerable.Range(0, count).Select(i => (1000000000000000000L + i).ToString()).ToList();

    private static int MessageLength(IReadOnlyList<string> batch)
        => string.Join(' ', batch.Select(id => $"<@{id}>")).Length + 1 + Body;

    [Fact]
    public void A_short_list_stays_one_post()
    {
        var ids = Snowflakes(5);

        Assert.Equal([ids], DiscordWebhookService.MentionBatches(ids, Body));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(150)]
    [InlineData(250)]
    public void A_long_list_is_split_without_losing_or_reordering_anyone(int count)
    {
        var ids = Snowflakes(count);

        var batches = DiscordWebhookService.MentionBatches(ids, Body);

        Assert.True(batches.Count > 1);
        Assert.Equal(ids, batches.SelectMany(b => b).ToList());
        Assert.All(batches, b =>
        {
            Assert.InRange(b.Count, 1, 100);
            Assert.InRange(MessageLength(b), 1, 2000);
        });
    }

    [Fact]
    public void The_mention_cap_splits_even_short_ids()
    {
        // short ids fit 2000 characters long before the 100-mention cap
        var ids = Enumerable.Range(1, 150).Select(i => i.ToString()).ToList();

        var batches = DiscordWebhookService.MentionBatches(ids, Body);

        Assert.Equal([100, 50], batches.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Nobody_to_ping_means_no_batch()
    {
        Assert.Empty(DiscordWebhookService.MentionBatches([], Body));
    }
}
