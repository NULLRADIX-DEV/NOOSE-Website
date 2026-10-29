using Microsoft.JSInterop;
using NOOSE_Website.Models.Export;

namespace NOOSE_Website.Components.Common.Shared;

/// <summary>Hands an <see cref="ExportFile"/> to the browser as a download, through wwwroot/js/datei.js.</summary>
public static class FileDownload
{
    public static async Task SaveAsync(IJSRuntime js, ExportFile file)
    {
        // ?v= cache-buster: bump it on every datei.js change (dynamic imports bypass asset fingerprinting)
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/datei.js?v=1");
        using var content = new DotNetStreamReference(new MemoryStream(file.Content));
        await module.InvokeVoidAsync("speichern", file.FileName, file.ContentType, content);
    }
}
