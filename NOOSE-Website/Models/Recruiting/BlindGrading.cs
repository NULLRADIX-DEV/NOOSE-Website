using System.Security.Cryptography;
using System.Text;

namespace NOOSE_Website.Models.Recruiting;

/// <summary>A handed-in applicant test waiting for its grading, with nothing that names the applicant.</summary>
public record BlindSubmission(string AssignmentId, string Code, string TestTitle, DateTime CompletedAt, bool TimedOut, int AttemptCount);

/// <summary>Grading without the applicant's name, so the grader is not swayed by who wrote it.</summary>
public static class BlindGrading
{
    /// <summary>Stable short code for a submission; hashed so it carries no case number.</summary>
    public static string CodeFor(string assignmentId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assignmentId)))[..6];

    public static string Label(string code) => $"Abgabe {code}";

    /// <summary>Oldest hand-in first; the one after <paramref name="currentId"/>, wrapping around, or null.</summary>
    public static string? NextAfter(IReadOnlyList<BlindSubmission> queue, string? currentId)
    {
        var index = queue.Select(s => s.AssignmentId).ToList().IndexOf(currentId ?? string.Empty);
        for (var step = 1; step <= queue.Count; step++)
        {
            var candidate = queue[(index + step) % queue.Count];
            if (candidate.AssignmentId != currentId)
            {
                return candidate.AssignmentId;
            }
        }
        return null;
    }
}
