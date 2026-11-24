using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Reliability answers for tests that are not about the questions.</summary>
public static class ReliabilityAnswers
{
    public static List<ReliabilityAnswer> AllNo()
        => ReliabilityQuestions.All.Select(q => new ReliabilityAnswer { Key = q.Key, Answer = false }).ToList();
}
