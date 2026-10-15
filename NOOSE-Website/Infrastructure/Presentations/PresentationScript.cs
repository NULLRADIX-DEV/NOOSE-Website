using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace NOOSE_Website.Infrastructure.Presentations;

/// <summary>The presentation module (js/praesentation.js), imported once per circuit and shared by every slide.</summary>
/// <remarks>A deck view holds up to 300 thumbnails; one import for all of them, not one per slide.</remarks>
public sealed class PresentationScript(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>Every page imports the module under this one URL, so the browser keeps a single instance of it.</summary>
    public const string ModulePath = "./js/praesentation.js?v=2";

    private Task<IJSObjectReference>? _module;

    /// <summary>Draws a page of an imported deck into <paramref name="host"/>: once it scrolls into view, or at once
    /// with <paramref name="eager"/> (print).</summary>
    public async Task ShowPageAsync(ElementReference host, string url, int page, bool eager)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("seite", host, url, page, eager);
    }

    /// <summary>Waits until <paramref name="count"/> slides of the print view are drawn (or failed), at most
    /// <paramref name="timeout"/>; true when all are there.</summary>
    public async Task<bool> WhenDrawnAsync(int count, TimeSpan timeout)
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<bool>("fertig", timeout + TimeSpan.FromSeconds(5), count, (int)timeout.TotalMilliseconds);
    }

    /// <summary>Enters full screen with <paramref name="element"/>, or leaves it.</summary>
    public async Task ToggleFullscreenAsync(ElementReference element)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("vollbild", element);
    }

    private async Task<IJSObjectReference> ModuleAsync()
    {
        var task = _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
        try
        {
            return await task;
        }
        catch
        {
            // a failed import is tried again on the next call
            if (_module == task)
            {
                _module = null;
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true } loaded)
        {
            try
            {
                await loaded.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                /* the circuit is gone */
            }
        }
    }
}
