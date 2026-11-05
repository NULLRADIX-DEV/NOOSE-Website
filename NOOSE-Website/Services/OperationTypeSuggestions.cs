namespace NOOSE_Website.Services;

/// <summary>Type suggestions for a deployment: the operation catalog plus the kinds the activity reports used.</summary>
public static class OperationTypeSuggestions
{
    // the kind the old activity template set; it names the form, not the deployment
    private const string TemplateKind = "Vorlage";

    /// <summary>Distinct, sorted, without blanks and without the template kind.</summary>
    public static List<string> Merge(IEnumerable<string> operationTypes, IEnumerable<string> activityKinds)
        => operationTypes.Concat(activityKinds)
            .Where(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v.Trim(), TemplateKind, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
