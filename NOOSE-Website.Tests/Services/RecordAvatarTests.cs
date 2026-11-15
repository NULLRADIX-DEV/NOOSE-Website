using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The one place that knows a record's photo route and which photo is its profile picture.</summary>
public class RecordAvatarTests
{
    [Theory]
    [InlineData(nameof(Faction), "/dateien/fraktionen/foto/ph1")]
    [InlineData(nameof(PersonGroup), "/dateien/personengruppen/foto/ph1")]
    [InlineData(nameof(Party), "/dateien/parteien/foto/ph1")]
    [InlineData("DrugRoute", "/dateien/drogenrouten/foto/ph1")]
    public void Url_ServesTheRecordTypesGallery(string entityType, string expected)
        => Assert.Equal(expected, RecordAvatar.Url(entityType, "ph1"));

    // A type without a gallery gets null, never a guessed route: an <img> with no src
    // beats one pointing at another record type's endpoint.
    [Theory]
    [InlineData("Person")]
    [InlineData("Operation")]
    [InlineData("Nonsense")]
    public void Url_ReturnsNull_ForATypeWithoutAGallery(string entityType)
        => Assert.Null(RecordAvatar.Url(entityType, "ph1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Url_ReturnsNull_WithoutAPhoto(string? photoId)
        => Assert.Null(RecordAvatar.Url(nameof(Faction), photoId));

    [Fact]
    public void ProfileId_IsTheTitleImage()
    {
        IRecordPhoto[] photos =
        [
            new PartyPhoto { Id = "plain" },
            new PartyPhoto { Id = "title", IsTitleImage = true },
        ];

        Assert.Equal("title", RecordAvatar.ProfileId(photos));
    }

    [Fact]
    public void ProfileId_IsNull_WhenNoPhotoCarriesTheMark()
        => Assert.Null(RecordAvatar.ProfileId(new IRecordPhoto[] { new PartyPhoto { Id = "plain" } }));

    [Fact]
    public void ProfileId_IsNull_WithoutPhotos()
    {
        Assert.Null(RecordAvatar.ProfileId(null));
        Assert.Null(RecordAvatar.ProfileId([]));
    }

    [Fact]
    public void Successor_IsTheOldestRemainingPhoto()
    {
        var photos = new[]
        {
            new FactionPhoto { Id = "young", CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new FactionPhoto { Id = "old", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        Assert.Equal("old", RecordAvatar.Successor(photos)?.Id);
    }

    [Fact]
    public void Successor_IsNull_WhenNothingRemains()
        => Assert.Null(RecordAvatar.Successor(Array.Empty<FactionPhoto>()));
}
