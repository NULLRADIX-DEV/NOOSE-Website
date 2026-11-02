using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Display labels.</summary>
public static class RankDisplay
{
    public static string Name(Rank? rank) =>
        rank is { } r && EnumLabelText.Get(nameof(Rank), r.ToString()) is { } label ? label : DefaultName(rank);

    /// <summary>Code-defined label, without DB override.</summary>
    public static string DefaultName(Rank? rank) => rank switch
    {
        Rank.Trainee => "Junior Agent",
        Rank.SpecialAgent => "Special Agent",
        Rank.SeniorSpecialAgent => "Senior Special Agent",
        Rank.SpecialAgentInCharge => "Supervisory Special Agent",
        Rank.DeputyDirector => "Deputy Director",
        Rank.Director => "Director",
        _ => "— (kein Rang)",
    };

    /// <summary>All ranks ascending.</summary>
    public static readonly IReadOnlyList<Rank> All = new[]
    {
        Rank.Trainee,
        Rank.SpecialAgent,
        Rank.SeniorSpecialAgent,
        Rank.SpecialAgentInCharge,
        Rank.DeputyDirector,
        Rank.Director,
    };
}

/// <summary>Display labels, chip colours and icons.</summary>
public static class AgentStatusDisplay
{
    public static string Name(AgentStatus status) => status switch
    {
        AgentStatus.Pending => "Ausstehend",
        AgentStatus.Active => "Aktiv",
        AgentStatus.Blocked => "Gesperrt",
        AgentStatus.Applicant => "Bewerber",
        AgentStatus.Terminated => "Gekündigt",
        AgentStatus.Civilian => "Bürger",
        _ => "—",
    };

    public static Color Colour(AgentStatus status) => status switch
    {
        AgentStatus.Active => Color.Success,
        AgentStatus.Pending => Color.Warning,
        AgentStatus.Blocked => Color.Error,
        AgentStatus.Applicant => Color.Info,
        AgentStatus.Terminated => Color.Error,
        // grey: outside the agency
        AgentStatus.Civilian => Color.Default,
        _ => Color.Default,
    };

    public static string Icon(AgentStatus status) => status switch
    {
        AgentStatus.Active => Icons.Material.Filled.CheckCircle,
        AgentStatus.Pending => Icons.Material.Filled.HourglassTop,
        AgentStatus.Blocked => Icons.Material.Filled.Block,
        AgentStatus.Applicant => Icons.Material.Filled.HowToReg,
        AgentStatus.Terminated => Icons.Material.Filled.PersonOff,
        AgentStatus.Civilian => Icons.Material.Filled.Person,
        _ => Icons.Material.Filled.HelpOutline,
    };
}
