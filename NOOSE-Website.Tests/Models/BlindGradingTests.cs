using System.Reflection;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Models;

/// <summary>The blind grading queue: stable codes, nothing naming the applicant, and the "next" walk.</summary>
public sealed class BlindGradingTests
{
    private static BlindSubmission Row(string id)
        => new(id, BlindGrading.CodeFor(id), "Test", new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), false, 1);

    [Fact]
    public void The_code_is_stable_short_and_not_the_id()
    {
        var id = Guid.NewGuid().ToString();

        Assert.Equal(BlindGrading.CodeFor(id), BlindGrading.CodeFor(id));
        Assert.Equal(6, BlindGrading.CodeFor(id).Length);
        Assert.DoesNotContain(BlindGrading.CodeFor(id), id.ToUpperInvariant().Replace("-", ""));
    }

    [Fact]
    public void A_blind_row_carries_nothing_that_names_the_applicant()
    {
        // widening this record is how the name would slip onto the blind page
        var members = typeof(BlindSubmission).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal("AssignmentId,AttemptCount,Code,CompletedAt,TestTitle,TimedOut", string.Join(",", members));
    }

    [Fact]
    public void Next_walks_on_and_wraps_around()
    {
        IReadOnlyList<BlindSubmission> queue = [Row("a"), Row("b"), Row("c")];

        Assert.Equal("b", BlindGrading.NextAfter(queue, "a"));
        Assert.Equal("a", BlindGrading.NextAfter(queue, "c"));
        Assert.Equal("a", BlindGrading.NextAfter(queue, null));
        // the current one was just graded and left the queue
        Assert.Equal("a", BlindGrading.NextAfter(queue, "x"));
    }

    [Fact]
    public void Next_is_null_when_nothing_else_waits()
    {
        Assert.Null(BlindGrading.NextAfter([Row("a")], "a"));
        Assert.Null(BlindGrading.NextAfter([], "a"));
    }
}
