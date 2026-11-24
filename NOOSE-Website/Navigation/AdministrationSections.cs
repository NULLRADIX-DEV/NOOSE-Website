using MudBlazor;

namespace NOOSE_Website.Navigation;

/// <summary>The sections of the Administration workspace; menu, page and help buttons all read this one list.</summary>
public static class AdministrationSections
{
    /// <summary>One section: its own address, menu entry and caption group.</summary>
    /// <param name="Slug">Path segment under <see cref="Root"/>; empty for the start section.</param>
    /// <param name="HelpKey">Nav key of the handbook article that explains the section's group.</param>
    public sealed record Section(string Slug, string Label, string Icon, string Group, string HelpKey, string Description);

    public const string Root = "/administration";

    private const string General = "Allgemeines";
    private const string Recruiting = "Personalgewinnung";
    private const string Personnel = "Personalwesen";
    private const string Discipline = "Disziplinarwesen";
    private const string Public = "Öffentlichkeit & Parlament";
    private const string History = "Verlauf";

    public static readonly IReadOnlyList<Section> All =
    [
        new("informationen", "Informationen", Icons.Material.Filled.Info, General, "administration",
            "Zuständigkeiten, Abläufe und Ansprechpartner der Administration"),
        new("", "Chat", Icons.Material.Filled.Forum, General, "administration",
            "Interner Chat der Administration Division"),
        new("agents", "Zugeteilte Agents", Icons.Material.Filled.ManageAccounts, General, "administration",
            "Wer zur Administration Division und zum Board gehört"),
        new("beziehungen", "Personalbeziehungen", Icons.Material.Filled.Favorite, General, "administration",
            "Gemeldete Ehen, Beziehungen und Verwandtschaft unter Agents"),
        new("sicherheitspruefungen", "Sicherheitsüberprüfungen", Icons.Material.Filled.Security, Recruiting,
            "administration.sicherheitspruefungen", "Laufende und entschiedene Sicherheitsüberprüfungen"),
        new("beurteilungen", "Beurteilungen", Icons.Material.Filled.Grading, Personnel, "administration.beurteilungen",
            "Letzte Beurteilung je Agent, überfällige und negative zuerst"),
        new("befoerderungen", "Beförderungen", Icons.Material.Filled.TrendingUp, Personnel, "administration.beurteilungen",
            "Offene Beförderungsanträge"),
        new("versetzungen", "Versetzungen", Icons.Material.Filled.SwapHoriz, Personnel, "administration.beurteilungen",
            "Versetzungsanträge, Anhörung und Versetzungsverlauf"),
        new("auszeichnungen", "Auszeichnungen", Icons.Material.Filled.MilitaryTech, Personnel, "administration.beurteilungen",
            "Vorschläge für Ehrenzeichen"),
        new("dienstzeugnisse", "Dienstzeugnisse", Icons.Material.Filled.WorkspacePremium, Personnel,
            "administration.beurteilungen", "Dienstzeugnisse ausstellen und drucken"),
        new("schusswaffengebrauch", "Schusswaffengebrauch", Icons.Material.Filled.GpsFixed, Personnel,
            "administration.beurteilungen", "Gemeldeter Schusswaffengebrauch aller Agents"),
        new("ermittlungsverfahren", "Ermittlungsverfahren", Icons.Material.Filled.Gavel, Discipline,
            "administration.ermittlungsverfahren", "Laufende Disziplinarverfahren"),
        new("verfuegungen", "Disziplinarverfügungen", Icons.Material.Filled.Description, Discipline,
            "administration.ermittlungsverfahren", "Abgeschlossene Verfahren mit Verfügung oder Einstellung"),
        new("presse", "Öffentlichkeitsarbeit", Icons.Material.Filled.Feed, Public, "administration.presse",
            "Pressemitteilungen schreiben und veröffentlichen"),
        new("parlament", "Parlamentarische Arbeit", Icons.Material.Filled.AccountBalance, Public, "administration.presse",
            "Parlamentarische Anfragen mit Frist"),
        new("buergertickets", "Bürger-Tickets", Icons.Material.Filled.Forum, Public, "administration.presse",
            "Anliegen und Beschwerden von Bürgern lesen"),
        new("zeitstrahl", "Zeitstrahl", Icons.Material.Filled.Timeline, History, "administration",
            "Was in der Administration zuletzt geschah"),
        new("wiedervorlagen", "Wiedervorlagen", Icons.Material.Filled.EventRepeat, History, "administration",
            "Wiedervorlagen der Administration"),
        new("kommentare", "Kommentare", Icons.Material.Filled.Comment, History, "administration",
            "Kommentare der Administration"),
    ];

    public static string Route(Section section) => section.Slug.Length == 0 ? Root : $"{Root}/{section.Slug}";

    public static string Route(string slug) => slug.Length == 0 ? Root : $"{Root}/{slug}";

    public static string Key(Section section) => section.Slug.Length == 0 ? "administration" : "administration." + section.Slug;

    public static Section? BySlug(string? slug)
        => All.FirstOrDefault(s => string.Equals(s.Slug, slug ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    /// <summary>Menu entries other than the one per group that carries the group's article.</summary>
    public static IEnumerable<string> SecondaryKeys
        => All.Select(Key).Where(k => !All.Select(s => s.HelpKey).Contains(k, StringComparer.Ordinal));
}
