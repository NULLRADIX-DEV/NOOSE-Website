using NOOSE_Website.Infrastructure.Handbook.Content;

namespace NOOSE_Website.Infrastructure.Handbook;

/// <summary>The handbook as it ships with the code; the seeder writes it into the database.</summary>
/// <remarks>
/// Written for the agent using the site. Direct address, short sentences, the names of buttons in italics - and a
/// term is explained the first time it appears rather than assumed.
/// <para>
/// Two kinds of content are deliberately not prose: <c>DiagramKey</c> names a drawing that lives in
/// Components/Pages/Handbook/Diagrams, because the HTML filter allows no <c>svg</c> and would strip a real one on the
/// first save; and steps are rows, so a walkthrough keeps its shape no matter what an author does to the text around it.
/// </para>
/// <para>
/// The text itself lives one file per chapter under Content/, because a single list of seventy articles is neither
/// reviewable nor editable. This file only declares the shapes and puts the book together.
/// </para>
/// <para>
/// Raise <see cref="Revision"/> when rewording an existing article. The seeder then rewrites rows nobody has edited
/// and leaves edited ones alone; a new article needs no bump because it is recognised by its missing key.
/// </para>
/// </remarks>
public static class HandbookContent
{
    /// <summary>Revision of the shipped text. Raise it after rewording an existing article, chapter or term.</summary>
    public const int Revision = 21;

    public sealed record SeededStep(string Icon, string Title, string Text);

    /// <param name="Key">Stable handle; renaming one orphans the old row and creates a second.</param>
    public sealed record SeededArticle(
        string Key,
        string Slug,
        string Title,
        string Summary,
        string ContentHtml,
        string? RoleplayHtml = null,
        string? DiagramKey = null,
        string? NavKey = null,
        SeededStep[]? Steps = null);

    public sealed record SeededChapter(
        string Key,
        string Slug,
        string Title,
        string Description,
        string Icon,
        SeededArticle[] Articles);

    public sealed record SeededTerm(
        string Key,
        string Term,
        string ShortDefinition,
        string? Synonyms = null,
        string? ExplanationHtml = null,
        string? ArticleKey = null);

    /// <summary>The chapters in reading order; the rail follows this list.</summary>
    public static readonly IReadOnlyList<SeededChapter> Chapters =
    [
        GettingStartedChapter.Chapter,
        RecordsChapter.Chapter,
        InvestigationChapter.Chapter,
        WantedChapter.Chapter,
        DutyChapter.Chapter,
        PersonnelChapter.Chapter,
        ToolsChapter.Chapter,
        DutyRegulationChapter.Chapter,
        EquipmentChapter.Chapter,
    ];

    /// <summary>The glossary, alphabetical on the page rather than here.</summary>
    public static readonly IReadOnlyList<SeededTerm> Terms = GlossaryContent.Terms;
}
