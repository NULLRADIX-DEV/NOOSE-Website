namespace NOOSE_Website.Models.Enums;

/// <summary>Why two person records look like the same person; lower = stronger.</summary>
public enum DuplicateReason
{
    SameName = 0,
    SamePhone = 1,
    SoundsAlike = 2,
    SpelledAlike = 3,
}

/// <summary>Display labels for duplicate reasons.</summary>
public static class DuplicateReasonDisplay
{
    public static string Name(DuplicateReason reason) => reason switch
    {
        DuplicateReason.SameName => "gleicher Name",
        DuplicateReason.SamePhone => "gleiche Telefonnummer",
        DuplicateReason.SoundsAlike => "klingt gleich",
        DuplicateReason.SpelledAlike => "ähnlich geschrieben",
        _ => "ähnlich",
    };
}
