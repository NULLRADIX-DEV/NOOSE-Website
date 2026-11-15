using NetCord.Rest;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>Sample cards behind the test buttons.</summary>
public class NotificationSamplesTests
{
    private const string BaseUrl = "https://fibhq.com";
    private const string Tester = "123456789012345678";
    private const string Role = "223456789012345678";
    private static readonly DateTime Moment = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    // the board has no test
    public static IEnumerable<object[]> Testable => DiscordRouting.RoutableTypes
        .Where(t => t != NotificationType.FactionThreatBoard)
        .Select(t => new object[] { t });

    private static IEnumerable<object> Walk(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            yield return item;
            IEnumerable<object> children = item switch
            {
                ComponentContainerProperties container => container.Components,
                ComponentSectionProperties section => section.Components.Cast<object>().Append(section.Accessory),
                ActionRowProperties row => row.Components,
                _ => [],
            };
            foreach (var child in Walk(children))
            {
                yield return child;
            }
        }
    }

    private static string Text(IEnumerable<IMessageComponentProperties> card)
        => string.Join("\n", Walk(card).OfType<TextDisplayProperties>().Select(t => t.Content));

    private static List<string> CustomIds(IEnumerable<IMessageComponentProperties> card)
        => Walk(card).OfType<ButtonProperties>().Select(b => b.CustomId).ToList();

    [Theory]
    [MemberData(nameof(Testable))]
    public void Every_category_has_a_marked_sample_that_fits(NotificationType type)
    {
        var card = NotificationSamples.Card(type, BaseUrl, Tester, Role, Moment);

        var text = Text(card);
        Assert.Contains("Test", text);
        Assert.True(text.Length <= DiscordCards.MaxText);
        Assert.All(CustomIds(card), id => Assert.True(id.Length <= 100));
    }

    [Fact]
    public void Samples_show_the_buttons_of_their_category()
    {
        Assert.Contains("fib:cjd:test", CustomIds(NotificationSamples.Card(NotificationType.JobAssigned, BaseUrl, Tester, Role, Moment)));
        Assert.Contains("fib:cack:test", CustomIds(NotificationSamples.Card(NotificationType.Announcement, BaseUrl, Tester, Role, Moment)));
        Assert.Contains("fib:ctip:test", CustomIds(NotificationSamples.Card(NotificationType.PublicTipCreated, BaseUrl, Tester, Role, Moment)));
        Assert.Contains("fib:al:test:1", CustomIds(NotificationSamples.Card(NotificationType.EmergencyAlarm, BaseUrl, Tester, Role, Moment)));
    }

    [Fact]
    public void Samples_show_the_ping_chip_of_their_category()
    {
        Assert.Contains($"<@&{Role}>", Text(NotificationSamples.Card(NotificationType.Announcement, BaseUrl, Tester, Role, Moment)));
        Assert.Contains($"<@{Tester}>", Text(NotificationSamples.Card(NotificationType.JobAssigned, BaseUrl, Tester, Role, Moment)));
        Assert.DoesNotContain("<@", Text(NotificationSamples.Card(NotificationType.JobAssigned, BaseUrl, null, Role, Moment)));
    }

    [Fact]
    public void The_regulation_sample_is_marked_and_fits()
    {
        var messages = NotificationSamples.Regulation(Role, Tester, BaseUrl);

        Assert.NotEmpty(messages);
        var all = string.Join("\n", messages.Select(Text));
        Assert.Contains("TEST", all);
        Assert.Contains($"<@&{Role}>", all);
        Assert.Contains("Sonstiges", all);
        Assert.All(messages, m => Assert.True(Text(m).Length <= DiscordCards.MaxText));
    }
}
