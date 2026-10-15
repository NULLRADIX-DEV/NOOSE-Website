namespace NOOSE_Website.Services.Import;

/// <summary>
/// Google sign-in for the import (section <c>GoogleDrive</c>): the OAuth client, the browser API key and the number of
/// the Google Cloud project. All three are public by design - the browser needs them to open the Picker - and none
/// signs anyone in to NOOSE; Discord stays the only login. Without all three the Google sign-in is hidden.
/// </summary>
public sealed class GoogleDriveOptions
{
    public const string Section = "GoogleDrive";

    /// <summary>The only scope asked for: files the agent picks in the Picker, nothing else in their Drive.</summary>
    public const string Scope = "https://www.googleapis.com/auth/drive.file";

    /// <summary>OAuth client id of type "Web application".</summary>
    public string? ClientId { get; set; }

    /// <summary>Browser API key, restricted to the Picker API and the site's address.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Project number (digits) of the Cloud project: the Picker grants the chosen file to it, so the
    /// <see cref="Scope"/> reaches that file.</summary>
    public string? AppId { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(AppId);
}

/// <summary>A file chosen in the Google Picker and the short-lived access token that may read it. Used for one fetch,
/// never stored.</summary>
public sealed record GoogleDrivePick(string FileId, string AccessToken)
{
    // the token stays out of anything that prints the record
    public override string ToString() => $"GoogleDrivePick {{ FileId = {FileId} }}";
}

/// <summary>What the Picker reported: the pick for the server and the file's name for the dialog.</summary>
public sealed record GoogleDriveChoice(GoogleDrivePick Pick, string Name);

/// <summary>An export fetched through the Google sign-in, with the file's name in Drive.</summary>
public sealed record DriveExport(byte[] Bytes, string? Name);
