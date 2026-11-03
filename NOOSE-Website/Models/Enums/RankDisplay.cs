using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Display labels.</summary>
public static class RankDisplay
{
    /// <summary>Neutral title for anything that leaves the house or is stored.</summary>
    public static string Name(Rank? rank) =>
        rank is { } r && EnumLabelText.Get(nameof(Rank), r.ToString()) is { } label ? label : DefaultName(rank);

    /// <summary>Title as the house sees it: the HRT has its own for levels 2 to 5. Internal display only, outward text uses <see cref="Name(Rank?)"/>.</summary>
    public static string Name(Rank? rank, Division? division)
        => division == Division.Hrt && HrtTitle(rank) is { } title ? title : Name(rank);

    /// <summary>HRT title of a rank; null outside the HRT levels (Special Agent up to Section Chief).</summary>
    public static string? HrtTitle(Rank? rank) => rank switch
    {
        Rank.SpecialAgent => "Operator",
        Rank.SeniorSpecialAgent => "Senior Operator",
        Rank.SpecialAgentInCharge => "Leading Operator",
        Rank.SectionChief => "Commanding Operator",
        _ => null,
    };

    /// <summary>Code-defined label, without DB override.</summary>
    public static string DefaultName(Rank? rank) => rank switch
    {
        Rank.Trainee => "Trainee",
        Rank.SpecialAgent => "Special Agent",
        Rank.SeniorSpecialAgent => "Senior Special Agent",
        Rank.SpecialAgentInCharge => "Special Agent in Charge",
        Rank.SectionChief => "Section Chief",
        Rank.AssistantDirector => "Assistant Director",
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
        Rank.SectionChief,
        Rank.AssistantDirector,
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
