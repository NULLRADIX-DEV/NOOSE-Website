using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The full stage matrix of a food request; every write path checks against this table.</summary>
public class FoodTransitionTests
{
    private static readonly (FoodRequestStatus From, FoodRequestStatus To)[] Allowed =
    [
        (FoodRequestStatus.Requested, FoodRequestStatus.Approved),
        (FoodRequestStatus.Requested, FoodRequestStatus.Rejected),
        (FoodRequestStatus.Requested, FoodRequestStatus.Withdrawn),
        (FoodRequestStatus.Approved, FoodRequestStatus.Issued),
        (FoodRequestStatus.Approved, FoodRequestStatus.Requested),
        (FoodRequestStatus.Approved, FoodRequestStatus.Rejected),
        (FoodRequestStatus.Rejected, FoodRequestStatus.Approved),
        (FoodRequestStatus.Issued, FoodRequestStatus.Approved),
    ];

    [Fact]
    public void Matrix_MatchesTheAllowedEdgesExactly()
    {
        foreach (var from in FoodRequestStatusDisplay.All)
        {
            foreach (var to in FoodRequestStatusDisplay.All)
            {
                var expected = Allowed.Contains((from, to));
                Assert.Equal(expected, FoodService.IsTransitionAllowed(from, to));
            }
        }
    }

    [Fact]
    public void NoStageMayTransitionToItself()
    {
        foreach (var status in FoodRequestStatusDisplay.All)
        {
            Assert.False(FoodService.IsTransitionAllowed(status, status));
        }
    }

    [Fact]
    public void Withdrawn_IsTerminal()
    {
        foreach (var to in FoodRequestStatusDisplay.All)
        {
            Assert.False(FoodService.IsTransitionAllowed(FoodRequestStatus.Withdrawn, to));
        }
        Assert.True(FoodRequestStatusDisplay.IsTerminal(FoodRequestStatus.Withdrawn));
    }

    [Fact]
    public void Issued_CanOnlyGoBackToApproved()
    {
        foreach (var to in FoodRequestStatusDisplay.All)
        {
            Assert.Equal(to == FoodRequestStatus.Approved, FoodService.IsTransitionAllowed(FoodRequestStatus.Issued, to));
        }
    }

    [Fact]
    public void OnlyApprovalAndIssue_ConsumeLimit()
    {
        Assert.True(FoodRequestStatusDisplay.ConsumesLimit(FoodRequestStatus.Approved));
        Assert.True(FoodRequestStatusDisplay.ConsumesLimit(FoodRequestStatus.Issued));
        Assert.False(FoodRequestStatusDisplay.ConsumesLimit(FoodRequestStatus.Requested));
        Assert.False(FoodRequestStatusDisplay.ConsumesLimit(FoodRequestStatus.Rejected));
        Assert.False(FoodRequestStatusDisplay.ConsumesLimit(FoodRequestStatus.Withdrawn));
    }

    [Fact]
    public void OnlyRequested_CountsAsOpen()
    {
        Assert.True(FoodRequestStatusDisplay.IsOpen(FoodRequestStatus.Requested));
        foreach (var status in FoodRequestStatusDisplay.All.Where(s => s != FoodRequestStatus.Requested))
        {
            Assert.False(FoodRequestStatusDisplay.IsOpen(status));
        }
    }

    [Fact]
    public void EveryStage_HasALabelAndAnIcon()
    {
        foreach (var status in FoodRequestStatusDisplay.All)
        {
            Assert.NotEqual("—", FoodRequestStatusDisplay.Name(status));
            Assert.False(string.IsNullOrWhiteSpace(FoodRequestStatusDisplay.Icon(status)));
        }
        Assert.Equal("—", FoodRequestStatusDisplay.Name((FoodRequestStatus)99));
    }

    [Fact]
    public void All_CoversEveryStageExactlyOnce()
    {
        var declared = Enum.GetValues<FoodRequestStatus>();
        Assert.Equal(declared.Length, FoodRequestStatusDisplay.All.Count);
        Assert.Equal(declared.Length, FoodRequestStatusDisplay.All.Distinct().Count());
    }

    [Fact]
    public void EveryKind_HasALabelAndAnIcon()
    {
        Assert.Equal(Enum.GetValues<FoodKind>().Length, FoodKindDisplay.All.Count);
        foreach (var kind in FoodKindDisplay.All)
        {
            Assert.NotEqual("—", FoodKindDisplay.Name(kind));
            Assert.False(string.IsNullOrWhiteSpace(FoodKindDisplay.Icon(kind)));
        }
    }
}
