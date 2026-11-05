using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Notification titles of an emergency alarm, which double as the Discord headline.</summary>
public class EmergencyAlarmTextTests
{
    [Fact]
    public void Title_SituationOnly()
        => Assert.Equal("Einsatzalarm: Geiselnahme", EmergencyAlarmText.Title("Geiselnahme", null));

    [Fact]
    public void Title_AppendsLocation()
        => Assert.Equal("Einsatzalarm: Geiselnahme · Maze Bank", EmergencyAlarmText.Title("Geiselnahme", "Maze Bank"));

    [Fact]
    public void Title_BlankLocation_IsLeftOut()
        => Assert.Equal("Einsatzalarm: Geiselnahme", EmergencyAlarmText.Title("Geiselnahme", "  "));

    [Fact]
    public void Title_LongestInput_CutAtMaxTitle()
    {
        var title = EmergencyAlarmText.Title(new string('L', EmergencyAlarmRules.MaxSituation),
            new string('O', EmergencyAlarmRules.MaxLocation));

        Assert.Equal(EmergencyAlarmText.MaxTitle, title.Length);
        Assert.StartsWith("Einsatzalarm: LLL", title);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void Title_ExactlyMaxTitle_StaysWhole()
    {
        var situation = new string('L', EmergencyAlarmText.MaxTitle - "Einsatzalarm: ".Length);

        var title = EmergencyAlarmText.Title(situation, null);

        Assert.Equal(EmergencyAlarmText.MaxTitle, title.Length);
        Assert.EndsWith("L", title);
    }

    [Fact]
    public void RepingTitle_SaysAgain_AndKeepsLocation()
        => Assert.Equal("Einsatzalarm (erneut): Geiselnahme · Maze Bank",
            EmergencyAlarmText.RepingTitle("Geiselnahme", "Maze Bank"));

    [Fact]
    public void ByAgentTitle_NamesTriggererAndSituation_WithinMax()
    {
        Assert.Equal("Gefahr im Verzug: Rookie hat einen Einsatzalarm ausgelöst – Geiselnahme",
            EmergencyAlarmText.ByAgentTitle("Rookie", "Geiselnahme"));

        var longTitle = EmergencyAlarmText.ByAgentTitle("Rookie", new string('L', EmergencyAlarmRules.MaxSituation));
        Assert.Equal(EmergencyAlarmText.MaxTitle, longTitle.Length);
        Assert.Contains("Rookie", longTitle);
    }
}
