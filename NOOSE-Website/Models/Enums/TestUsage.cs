namespace NOOSE_Website.Models.Enums;

/// <summary>Where a catalog test may be assigned: to applicants, to agents, or to both.</summary>
public enum TestUsage
{
    Bewerbung = 0,
    Intern = 1,
    Beide = 2,
}

/// <summary>Display labels and the two assignment filters.</summary>
public static class TestUsageDisplay
{
    public static string Name(TestUsage usage) => usage switch
    {
        TestUsage.Bewerbung => "Bewerbung",
        TestUsage.Intern => "Intern",
        TestUsage.Beide => "Bewerbung und intern",
        _ => "—",
    };

    public static readonly IReadOnlyList<TestUsage> All = [TestUsage.Bewerbung, TestUsage.Intern, TestUsage.Beide];

    /// <summary>Whether the test may be given to an application.</summary>
    public static bool ForApplicants(TestUsage usage) => usage is TestUsage.Bewerbung or TestUsage.Beide;

    /// <summary>Whether the test may be given to an agent.</summary>
    public static bool ForAgents(TestUsage usage) => usage is TestUsage.Intern or TestUsage.Beide;
}
