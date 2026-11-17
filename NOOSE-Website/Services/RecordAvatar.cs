using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Models.Abstractions;

namespace NOOSE_Website.Services;

/// <summary>Single source for a record's gallery photo URL and for its profile picture; no component builds the photo route itself. Mirrors <see cref="AgentAvatar" />.</summary>
public static class RecordAvatar
{
    // record type -> photo endpoint; Person is absent on purpose: its gallery carries no title image,
    // so a person file has photos but no profile picture
    private static readonly Dictionary<string, string> Endpoints = new(StringComparer.Ordinal)
    {
        [nameof(Faction)] = "/dateien/fraktionen/foto",
        [nameof(PersonGroup)] = "/dateien/personengruppen/foto",
        [nameof(Party)] = "/dateien/parteien/foto",
        [nameof(DrugRoute)] = "/dateien/drogenrouten/foto",
    };

    /// <summary>Serving route for one gallery photo; null when the type has no gallery or no photo is given.</summary>
    public static string? Url(string entityType, string? photoId)
        => string.IsNullOrWhiteSpace(photoId) || !Endpoints.TryGetValue(entityType, out var endpoint)
            ? null
            : $"{endpoint}/{photoId}";

    /// <summary>Serving route for one gallery photo, the person gallery included.</summary>
    public static string? GalleryUrl(string entityType, string? photoId)
        => entityType == nameof(Data.Entities.People.Person) && !string.IsNullOrWhiteSpace(photoId)
            ? $"/dateien/personen/foto/{photoId}"
            : Url(entityType, photoId);

    /// <summary>The record's profile picture: the id of its title image, or null while no photo carries the mark.</summary>
    public static string? ProfileId(IEnumerable<IRecordPhoto>? photos)
        => photos?.FirstOrDefault(p => p.IsTitleImage)?.Id;

    /// <summary>The photo that inherits the mark when the title image goes: the oldest remaining one.</summary>
    public static T? Successor<T>(IEnumerable<T> remaining) where T : class, IRecordPhoto, IAuditable
        => remaining.OrderBy(p => p.CreatedAt).FirstOrDefault();
}
