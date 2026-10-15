namespace NOOSE_Website.Models.Enums;

/// <summary>Where an imported document came from; decides how "Aktualisieren" fetches it again.</summary>
public enum ImportSourceKind
{
    /// <summary>A shared Google link, fetched by the server.</summary>
    Link = 1,

    /// <summary>A file the author uploaded; refreshing needs a new upload.</summary>
    Upload = 2,

    /// <summary>A file picked through Google sign-in; refreshing needs a new sign-in.</summary>
    Drive = 3,
}
