namespace NOOSE_Website.Services;

/// <summary>Allowed Roman-numeral badge numbers.</summary>
public static class BadgeNumbers
{
    public static IReadOnlyList<string> All { get; } =
    [
        "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
        "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX",
        "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX",
        "XXXI", "XXXII", "XXXIII", "XXXIV", "XXXV", "XXXVI", "XXXVII", "XXXVIII", "XXXIX", "XL",
    ];

    public static bool IsAllowed(string? value)
        => string.IsNullOrWhiteSpace(value)
            || All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return All.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }
}
