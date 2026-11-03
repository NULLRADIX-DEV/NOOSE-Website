using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Audience, classification and briefing labels name the HRT and the Administration Division, never the old unit names.</summary>
public class UnitLabelTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTaskforces = new Dictionary<string, string>();

    [Theory]
    [InlineData(AnnouncementAudience.TruUnit, "Nur HRT")]
    [InlineData(AnnouncementAudience.HrbUnit, "Nur Administration Division")]
    public void AccessLabel_ClosedToAUnit_NamesTheUnit(AnnouncementAudience audience, string expected)
        => Assert.Equal(expected,
            PresentationService.AccessLabel(PresentationAccess.Closed, audience, null, null, NoTaskforces));

    [Fact]
    public void AccessLabel_NoLabelMentionsTheOldUnitNames()
    {
        foreach (var audience in AnnouncementAudienceDisplay.All)
        {
            var label = PresentationService.AccessLabel(PresentationAccess.Closed, audience, null, Rank.SpecialAgent, NoTaskforces);

            Assert.DoesNotContain("TRU", label, StringComparison.Ordinal);
            Assert.DoesNotContain("HRB", label, StringComparison.Ordinal);
        }
    }

    /// <summary>The JSON keys stay RequireTru and RequireHrb; only the words on screen changed.</summary>
    [Fact]
    public void CounterIntelSummary_NamesTheFlagsLikeTheUnits()
    {
        var summary = CounterIntelRuleDisplay.Summary(new CounterIntelRuleDefinition
        {
            RequireTru = true,
            RequireHrb = false,
        });

        Assert.Contains("nur HRT", summary, StringComparison.Ordinal);
        Assert.Contains("ohne Administration", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("TRU", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("HRB", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskforceRole_TheTacticalLeadReadsHrtLead()
        => Assert.Equal("HRT-Lead", TaskforceRoleDisplay.Name(TaskforceRole.TruLead));

    [Fact]
    public void AudienceDisplay_NoLabelMentionsTheOldUnitNames()
    {
        foreach (var audience in AnnouncementAudienceDisplay.All)
        {
            Assert.DoesNotContain("TRU", AnnouncementAudienceDisplay.Name(audience), StringComparison.Ordinal);
            Assert.DoesNotContain("HRB", AnnouncementAudienceDisplay.Name(audience), StringComparison.Ordinal);
        }

        foreach (var classification in Enum.GetValues<DocumentClassification>())
        {
            Assert.DoesNotContain("TRU", DocumentClassificationDisplay.Label(classification), StringComparison.Ordinal);
            Assert.DoesNotContain("HRB", DocumentClassificationDisplay.Label(classification), StringComparison.Ordinal);
            Assert.DoesNotContain("TRU", DocumentClassificationDisplay.ChipLabel(classification), StringComparison.Ordinal);
            Assert.DoesNotContain("HRB", DocumentClassificationDisplay.ChipLabel(classification), StringComparison.Ordinal);
        }
    }
}
