namespace NOOSE_Website.Models.Enums;

/// <summary>The national situation level the agency states on /lage.</summary>
/// <remarks>
/// An editorial statement, deliberately not <see cref="HazardLevel"/>: that one is
/// <c>HazardLevelLogic.From(score)</c>, the level of a single record derived from its threat score. Sharing the enum
/// would be a standing invitation to compute one from the other, and an aggregate over the score stock would both
/// reach into classified files and export the scoring mechanic in a derived shape. Same reason
/// <see cref="PublicFactionStanding"/> is never derived from a faction's internal classification.
/// </remarks>
public enum PublicSituationLevel
{
    // ascending severity, shown as DEFCON 5 down to DEFCON 1; the number is never stored, only the key
    Niedrig = 0,
    Erhoeht = 1,
    Hoch = 2,
    // an older build cannot parse this key and reads it as silence
    Schwer = 3,
    Kritisch = 4,
}

/// <summary>Labels, colours and the allowlist that turns a stored name back into a level.</summary>
public static class PublicSituationLevelDisplay
{
    /// <summary>The value written to the settings row. A stable key, never the German label.</summary>
    /// <remarks>
    /// A name rather than the number, because a bare "2" says nothing to whoever reads the settings table. But
    /// deliberately its own string, the way <c>WarnhinweisColourChoice</c> and <c>PublicIconChoice</c> separate their
    /// stored Name from their German Label: the label is UI text somebody will reword one day, and if the stored
    /// value followed it, every existing row would stop parsing and /lage would go dark without a single test
    /// failing. <c>PublicSituationLevelTests</c> pins these five strings for that reason.
    /// </remarks>
    public static string Key(PublicSituationLevel level) => level switch
    {
        PublicSituationLevel.Niedrig => "Niedrig",
        PublicSituationLevel.Erhoeht => "Erhoeht",
        PublicSituationLevel.Hoch => "Hoch",
        PublicSituationLevel.Schwer => "Schwer",
        PublicSituationLevel.Kritisch => "Kritisch",
        _ => string.Empty,
    };

    /// <summary>What a level is called in the UI and on the public page; free to be reworded.</summary>
    public static string Name(PublicSituationLevel level) => level switch
    {
        PublicSituationLevel.Niedrig => "DEFCON 5",
        PublicSituationLevel.Erhoeht => "DEFCON 4",
        PublicSituationLevel.Hoch => "DEFCON 3",
        PublicSituationLevel.Schwer => "DEFCON 2",
        PublicSituationLevel.Kritisch => "DEFCON 1",
        _ => "—",
    };

    /// <summary>Fixed hex per level, independent of the MudBlazor palette; each clears 4.5:1 on #1C1C1C and #242424.</summary>
    public static string Hex(PublicSituationLevel level) => level switch
    {
        PublicSituationLevel.Niedrig => "#43C04D",
        PublicSituationLevel.Erhoeht => "#B5D334",
        PublicSituationLevel.Hoch => "#FFCC1F",
        PublicSituationLevel.Schwer => "#FF8226",
        PublicSituationLevel.Kritisch => "#FF4D4D",
        _ => "#A8A29A",
    };

    /// <summary>Text on a filled level chip.</summary>
    public const string OnHex = "#1C1C1C";

    /// <summary>Level colour as text or icon colour.</summary>
    public static string TextStyle(PublicSituationLevel level) => $"color:{Hex(level)};";

    /// <summary>Filled chip: level colour behind dark text.</summary>
    public static string FilledStyle(PublicSituationLevel level)
        => $"background-color:{Hex(level)};color:{OnHex};";

    /// <summary>Outlined chip: level colour on text and border.</summary>
    public static string OutlineStyle(PublicSituationLevel level)
        => $"color:{Hex(level)};border-color:{Hex(level)};";

    /// <summary>One short line describing what the level means for a reader.</summary>
    public static string Hint(PublicSituationLevel level) => level switch
    {
        PublicSituationLevel.Niedrig => "Keine besonderen Vorkommnisse. Das FIB arbeitet im Regelbetrieb.",
        PublicSituationLevel.Erhoeht =>
            "Erhöhte Wachsamkeit empfohlen. Das FIB verstärkt die Aufklärung und beobachtet die Lage genauer.",
        PublicSituationLevel.Hoch =>
            "Konkrete Gefährdungslage. Das FIB ist in erhöhter Einsatzbereitschaft, Anweisungen der Behörden beachten.",
        PublicSituationLevel.Schwer =>
            "Schwere Gefährdungslage. Das FIB hat seine Einsatzkräfte in Bereitschaft versetzt, "
            + "Anweisungen der Behörden befolgen und gefährdete Bereiche meiden.",
        PublicSituationLevel.Kritisch =>
            "Akute Gefahr. Das FIB ist im Großeinsatz, öffentliche Bereiche meiden und Anweisungen sofort befolgen.",
        _ => string.Empty,
    };

    /// <summary>Every level, DEFCON 5 first and DEFCON 1 last; the order of the picker.</summary>
    public static readonly IReadOnlyList<PublicSituationLevel> All = new[]
    {
        PublicSituationLevel.Niedrig,
        PublicSituationLevel.Erhoeht,
        PublicSituationLevel.Hoch,
        PublicSituationLevel.Schwer,
        PublicSituationLevel.Kritisch,
    };

    /// <summary>The stored key as a level, or null for anything unknown.</summary>
    /// <remarks>
    /// An allowlist rather than <c>Enum.Parse</c>: the value comes out of a hand-editable key/value row, and a stray
    /// entry would otherwise throw on an [AllowAnonymous] page — the same failure class as an unparsable query value.
    /// Null means "nothing said", which the read path keeps as silence instead of turning into a level. Matches the
    /// stored key only, never the label — that separation is the whole point of having both.
    /// </remarks>
    public static PublicSituationLevel? Parse(string? key)
    {
        foreach (var level in All)
        {
            if (string.Equals(Key(level), key, StringComparison.Ordinal))
            {
                return level;
            }
        }
        return null;
    }
}
