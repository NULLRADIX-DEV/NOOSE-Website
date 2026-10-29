using MudBlazor;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Export;

namespace NOOSE_Website.Tests.Models;

/// <summary>The list half of the export: which lists, which columns, and in what order the rows leave.</summary>
public class ListExportTests
{
    private sealed record Row(string Name, int Score);

    private static readonly IReadOnlyList<ExportColumn<Row>> Columns =
    [
        new("Name", r => r.Name),
        new("Score", r => r.Score == 0 ? null : r.Score.ToString()),
    ];

    [Fact]
    public void Head_IsTheColumnTitles()
    {
        Assert.Equal(["Name", "Score"], ListExport.Head(Columns));
    }

    [Fact]
    public void Rows_FollowTheItemOrder_AndNullBecomesEmpty()
    {
        var rows = ListExport.Rows([new Row("Bravo", 7), new Row("Alpha", 0)], Columns)
            .Select(r => r.ToArray()).ToArray();

        Assert.Equal(["Bravo", "7"], rows[0]);
        Assert.Equal(["Alpha", ""], rows[1]);
    }

    [Theory]
    [InlineData(nameof(Person), "personen")]
    [InlineData("Faction", "fraktionen")]
    [InlineData("PersonGroup", "personengruppen")]
    [InlineData("Party", "parteien")]
    [InlineData(nameof(Case), "vorgaenge")]
    [InlineData("Operation", "operationen")]
    public void TheSixRecordLists_AreExportable(string type, string slug)
    {
        Assert.True(ExportableRecords.Contains(type));
        Assert.Equal(slug, ExportableRecords.Slug(type));
    }

    [Theory]
    [InlineData("Taskforce")]
    [InlineData("Agent")]
    [InlineData(nameof(Comment))]
    [InlineData("")]
    public void EveryOtherType_IsNot(string type)
    {
        Assert.False(ExportableRecords.Contains(type));
        Assert.Throws<ArgumentException>(() => ExportableRecords.Slug(type));
    }

    [Fact]
    public void ListLogEntry_NamesThePluralAndTheCount()
    {
        Assert.Equal("Personen-Liste (37)", ExportLog.List(nameof(Person), 37));
        Assert.Equal("Vorgänge-Liste (0)", ExportLog.List(nameof(Case), 0));
    }

    [Fact]
    public void Hazard_ReadsLikeTheChipWithItsScore()
    {
        Assert.Equal($"{HazardLevelLogic.Name(HazardLevel.Critical)} (82)", ExportText.Hazard(HazardLevel.Critical, 82));
        Assert.Equal("", ExportText.Hazard(HazardLevel.No, null));
    }

    [Fact]
    public void Secrecy_IsTheChipLabel_OnlyForAClassifiedRow()
    {
        Assert.Equal(DocumentClassificationDisplay.ChipLabel(DocumentClassification.Tru),
            ExportText.Secrecy(true, DocumentClassification.Tru));
        Assert.Equal("", ExportText.Secrecy(false, DocumentClassification.Tru));
    }

    [Fact]
    public void FlagAndDates_ReadAsOnScreen()
    {
        Assert.Equal("Ja", ExportText.Flag(true));
        Assert.Equal("", ExportText.Flag(false));
        Assert.Equal("29.10.2026 13:05", ExportText.Stamp(new DateTime(2026, 10, 29, 13, 5, 0)));
        Assert.Equal("29.10.2026", ExportText.Day(new DateTime(2026, 10, 29, 13, 5, 0)));
        Assert.Equal("", ExportText.Day(null));
    }

    [Fact]
    public void DossierLogEntry_NamesTypeAndId()
    {
        Assert.Equal("Person:abc", ExportLog.Dossier(nameof(Person), "abc"));
    }

    // the export reads the grid's FilteredItems; it must be the filtered AND sorted rows across all pages,
    // or the file disagrees with the screen after a MudBlazor upgrade
    [Fact]
    public void GridFilteredItems_AreFilteredAndSorted()
    {
#pragma warning disable BL0005 // parameters set outside a render on purpose: no renderer in these tests
        var grid = new MudDataGrid<Row>
        {
            Items = [new Row("Charlie", 3), new Row("Alpha", 1), new Row("Bravo", 2), new Row("Delta", 4)],
            QuickFilter = r => r.Score != 2,
        };
#pragma warning restore BL0005
        grid.SortDefinitions["Name"] = new SortDefinition<Row>("Name", true, 0, r => r.Name);

        Assert.Equal(["Delta", "Charlie", "Alpha"], grid.FilteredItems.Select(r => r.Name));
    }
}
