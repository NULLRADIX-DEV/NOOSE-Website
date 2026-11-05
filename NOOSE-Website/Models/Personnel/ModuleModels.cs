using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Personnel;

/// <summary>Create/edit training module input.</summary>
public class ModuleInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsMandatory { get; set; }

    public int Sorting { get; set; }
}

/// <summary>An agent's onboarding plan as the personnel file shows it.</summary>
public record ProbationView(
    string Id,
    string? MentorAgentId,
    string? MentorCodename,
    DateTime StartedAt,
    DateTime EndsAt,
    DateTime? ClosedAt,
    ProbationOutcome? Outcome,
    string? ClosedByName,
    int MandatoryDone,
    int MandatoryTotal,
    IReadOnlyList<string> OpenModules,
    ProbationLight Light)
{
    public bool IsOpen => ClosedAt is null;
}

/// <summary>A training module plus the viewed agent's completion status.</summary>
public record AgentModuleStatus(
    TrainingModule Module,
    string? CompletionId,
    DateTime? CompletedAt,
    string? CompleterName,
    string? Note)
{
    public bool IsCompleted => CompletionId is not null;
}
