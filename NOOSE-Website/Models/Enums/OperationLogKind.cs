namespace NOOSE_Website.Models.Enums;

/// <summary>Kind of an operation log line.</summary>
public enum OperationLogKind
{
    /// <summary>Situation report from the field.</summary>
    Situation = 0,
    /// <summary>Request for support or resources.</summary>
    Request = 1,
    /// <summary>Outcome of a step.</summary>
    Result = 2,
}

/// <summary>Display labels and colors.</summary>
public static class OperationLogKindDisplay
{
    public static string Name(OperationLogKind kind) => kind switch
    {
        OperationLogKind.Situation => "Lagemeldung",
        OperationLogKind.Request => "Anforderung",
        OperationLogKind.Result => "Ergebnis",
        _ => "—",
    };

    public static string Icon(OperationLogKind kind) => kind switch
    {
        OperationLogKind.Request => MudBlazor.Icons.Material.Filled.Campaign,
        OperationLogKind.Result => MudBlazor.Icons.Material.Filled.Flag,
        _ => MudBlazor.Icons.Material.Filled.RadioButtonChecked,
    };

    public static MudBlazor.Color Color(OperationLogKind kind) => kind switch
    {
        OperationLogKind.Request => MudBlazor.Color.Warning,
        OperationLogKind.Result => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Info,
    };

    public static readonly IReadOnlyList<OperationLogKind> All = new[]
    {
        OperationLogKind.Situation,
        OperationLogKind.Request,
        OperationLogKind.Result,
    };
}
