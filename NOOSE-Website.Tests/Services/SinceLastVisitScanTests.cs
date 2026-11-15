using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services;

/// <summary>Structural guard for "new since your last visit" on every record that logs its views and has a timeline section.</summary>
public sealed class SinceLastVisitScanTests
{
    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    private static List<(string Name, string Text)> RecordsWithATimelineSection()
    {
        var root = SourceRoot();
        Assert.True(Directory.Exists(root), $"Quellordner nicht gefunden: {root}");
        return Directory
            .EnumerateFiles(Path.Combine(root, "Components", "Pages"), "*.razor", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetRelativePath(root, f), Text: File.ReadAllText(f)))
            // without a logged view there is no previous visit to measure against
            .Where(f => f.Text.Contains("Slug=\"historie\"", StringComparison.Ordinal)
                && f.Text.Contains("<TimelinePanel", StringComparison.Ordinal)
                && f.Text.Contains("AccessLog.LogViewAsync(", StringComparison.Ordinal))
            .ToList();
    }

    private static string Attribute(string tag, string name)
        => Regex.Match(tag, $"{name}=\"([^\"]+)\"").Groups[1].Value;

    private static string Tag(string text, string component)
        => Regex.Match(text, $"<{component}\\b[^>]*/>").Value;

    [Fact]
    public void The_eight_records_are_found()
    {
        // if this ever drops, the guards below have quietly stopped guarding anything
        var names = RecordsWithATimelineSection().Select(r => Path.GetFileName(r.Name)).Order().ToArray();

        Assert.Equal(
            ["CaseDetail.razor", "DrugRouteDetail.razor", "FactionDetail.razor", "GroupDetail.razor", "OperationDetail.razor",
             "PartyDetail.razor", "PersonDetail.razor", "TaskforceDetail.razor"],
            names);
    }

    [Fact]
    public void Every_record_reads_the_previous_visit_and_forgets_it_on_the_next_record()
    {
        var offenders = RecordsWithATimelineSection()
            .Where(r => !r.Text.Contains("_since = await AccessLog.PreviousVisitAsync(", StringComparison.Ordinal)
                // the page is reused across ids; a missing reset would carry A's visit onto B
                || !Regex.IsMatch(r.Text, @"_since = null;\s*await LoadAsync\(\);"))
            .Select(r => r.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_timeline_marks_what_is_new()
    {
        var offenders = RecordsWithATimelineSection()
            .Where(r => Attribute(Tag(r.Text, "TimelinePanel"), "NewSince") != "_since")
            .Select(r => r.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_record_shows_the_line_for_itself()
    {
        var offenders = RecordsWithATimelineSection()
            .Where(r =>
            {
                var bar = Tag(r.Text, "SinceLastVisitBar");
                var timeline = Tag(r.Text, "TimelinePanel");
                return bar.Length == 0
                    || Attribute(bar, "Since") != "_since"
                    || Attribute(bar, "EntityType") != Attribute(timeline, "EntityType")
                    || Attribute(bar, "EntityId") != Attribute(timeline, "EntityId");
            })
            .Select(r => r.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_line_is_gated_like_the_timeline_section_itself()
    {
        // the line counts timeline entries; a partner without the timeline must not read them off the line
        const string gate = @"@if \(TabOn\(""historie""\)\)\s*\{\s*";
        var offenders = RecordsWithATimelineSection()
            .Where(r => !Regex.IsMatch(r.Text, gate + "<SinceLastVisitBar")
                || !Regex.IsMatch(r.Text, gate + @"<RecordSection[^>]*Slug=""historie"""))
            .Select(r => r.Name)
            .ToArray();

        Assert.Empty(offenders);
    }
}
