using System.Text.Json;

namespace NOOSE_Website.Models.Recruiting;

/// <summary>A person the applicant names as a reference.</summary>
public sealed class ApplicationReference
{
    public string Name { get; set; } = string.Empty;
    public DateTime? BirthDate { get; set; }
    public string? Phone { get; set; }
}

/// <summary>Cleans, stores and reads the reference persons of an application.</summary>
public static class ApplicationReferences
{
    public const int MaxCount = 5;
    public const int MaxNameLength = 200;
    public const int MaxPhoneLength = 32;

    /// <summary>Trimmed rows with a name, capped in count and length.</summary>
    public static List<ApplicationReference> Normalize(IEnumerable<ApplicationReference>? references)
        => (references ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name))
            .Take(MaxCount)
            .Select(r => new ApplicationReference
            {
                Name = Cut(r.Name.Trim(), MaxNameLength),
                BirthDate = r.BirthDate?.Date,
                Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : Cut(r.Phone.Trim(), MaxPhoneLength),
            })
            .ToList();

    /// <summary>JSON for the column; null when there is nothing to store.</summary>
    public static string? Serialize(IEnumerable<ApplicationReference>? references)
    {
        var clean = Normalize(references);
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    /// <summary>Reads the column; broken or empty JSON gives an empty list.</summary>
    public static IReadOnlyList<ApplicationReference> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ApplicationReference>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>One line per reference, e.g. for the FIBI context.</summary>
    public static string Describe(ApplicationReference r)
    {
        var parts = new List<string> { r.Name };
        if (r.BirthDate is { } born)
        {
            parts.Add($"geb. {born:dd.MM.yyyy}");
        }
        if (!string.IsNullOrWhiteSpace(r.Phone))
        {
            parts.Add($"Tel. {r.Phone}");
        }
        return string.Join(", ", parts);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
