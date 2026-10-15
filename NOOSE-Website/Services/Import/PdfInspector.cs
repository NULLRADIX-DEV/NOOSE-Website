using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using UglyToad.PdfPig;

namespace NOOSE_Website.Services.Import;

/// <summary>What the server reads from an imported deck: its page count and the title Google gave it.</summary>
public sealed record PdfInfo(int PageCount, string? Title);

/// <summary>Checks that bytes are a readable PDF and reads its page count; drawing the pages is the browser's job.</summary>
/// <remarks>
/// <see cref="Read"/> runs the PDF parser and must never run inside the web process: PdfPig walks the page tree
/// recursively, and a deck with a few thousand nested page nodes ends in a stack overflow, which no catch stops - the
/// whole app would go down. The web process uses <see cref="IsolatedPdfInspector"/>, which runs it in a child process.
/// </remarks>
public static class PdfInspector
{
    public const string NotAPdf = "Das ist keine PDF-Datei. In Google: Datei → Herunterladen → PDF-Dokument.";

    /// <summary>The page count and title; a message for the author when the file is no readable PDF.</summary>
    public static PdfInfo Read(byte[] bytes)
    {
        if (!HasHeader(bytes))
        {
            throw new GoogleExportException(NotAPdf);
        }
        try
        {
            using var document = PdfDocument.Open(bytes);
            var pages = document.NumberOfPages;
            if (pages <= 0)
            {
                throw new GoogleExportException("Die PDF-Datei hat keine Seiten.");
            }
            var title = document.Information.Title?.Trim();
            return new PdfInfo(pages, string.IsNullOrEmpty(title) ? null : title);
        }
        catch (GoogleExportException)
        {
            throw;
        }
        catch (Exception)
        {
            // a broken or password-protected file: the parser's own wording would mean nothing to the author
            throw new GoogleExportException("Die PDF-Datei lässt sich nicht lesen. Lade sie in Google neu herunter.");
        }
    }

    /// <summary>The PDF header; it may follow a little junk, but never more than a kilobyte of it.</summary>
    public static bool HasHeader(byte[] bytes)
    {
        ReadOnlySpan<byte> marker = "%PDF-"u8;
        var window = bytes.AsSpan(0, Math.Min(bytes.Length, 1024));
        return window.IndexOf(marker) >= 0;
    }
}

/// <summary>Reads a deck's page count and title.</summary>
public interface IPdfInspector
{
    Task<PdfInfo> InspectAsync(byte[] pdf, CancellationToken cancellationToken = default);
}

/// <summary>How to start this app as the child process that inspects one PDF.</summary>
public sealed record PdfInspectorCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>This app again: through the dotnet host in the container, as its own apphost when run locally.</summary>
    public static PdfInspectorCommand ForThisApp()
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        var entry = Assembly.GetEntryAssembly()?.Location;
        return string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entry)
                ? new PdfInspectorCommand(host, [entry])
                : new PdfInspectorCommand(host, []);
    }
}

/// <inheritdoc cref="IPdfInspector" />
/// <remarks>
/// The parser runs in a child process (<see cref="PdfInspectorProcess"/>) with a time limit and a capped heap: a
/// hostile file can crash, hang or bloat only that process. Two at a time, so a burst of imports cannot fork the
/// server into the ground; the import quota is taken before, so each one costs its author a slot.
/// </remarks>
public sealed class IsolatedPdfInspector(PdfInspectorCommand command, ILogger<IsolatedPdfInspector> logger) : IPdfInspector
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(20);

    private const string Unreadable = "Die PDF-Datei lässt sich nicht lesen. Lade sie in Google neu herunter.";
    private static readonly SemaphoreSlim Slots = new(2);

    public async Task<PdfInfo> InspectAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        if (!PdfInspector.HasHeader(pdf))
        {
            throw new GoogleExportException(PdfInspector.NotAPdf);
        }
        await Slots.WaitAsync(cancellationToken);
        var path = Path.Combine(Path.GetTempPath(), $"noose-folien-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(path, pdf, cancellationToken);
            var start = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in command.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
            start.ArgumentList.Add(PdfInspectorProcess.Switch);
            start.ArgumentList.Add(path);
            // 512 MB of heap is plenty for counting pages and nothing a bloated file may take from the server
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x20000000";

            using var process = Process.Start(start) ?? throw new GoogleExportException(Unreadable);
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeLimit);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning("Inspecting an imported PDF took longer than {Limit}.", TimeLimit);
                throw new GoogleExportException("Die PDF-Datei ist zu aufwendig aufgebaut. Lade sie in Google neu herunter.");
            }
            var answer = Parse(await output);
            if (answer is { Error: { } message })
            {
                throw new GoogleExportException(message);
            }
            if (process.ExitCode != 0 || answer is not { Pages: > 0 })
            {
                var detail = await errors;
                logger.LogWarning("Inspecting an imported PDF failed with exit code {Code}: {Detail}",
                    process.ExitCode, detail.Length > 500 ? detail[..500] : detail);
                throw new GoogleExportException(Unreadable);
            }
            return new PdfInfo(answer.Pages, answer.Title);
        }
        finally
        {
            Slots.Release();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                /* the temp folder is cleaned anyway */
            }
        }
    }

    private static PdfInspectorProcess.Answer? Parse(string output)
    {
        try
        {
            return string.IsNullOrWhiteSpace(output) ? null : JsonSerializer.Deserialize<PdfInspectorProcess.Answer>(output.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            /* already gone */
        }
    }
}

/// <summary>The child side of <see cref="IsolatedPdfInspector"/>: reads one PDF, writes its page count and title as
/// JSON to standard output and exits. Started as <c>NOOSE-Website --pdf-info &lt;file&gt;</c>, before anything of the
/// web app is built.</summary>
public static class PdfInspectorProcess
{
    public const string Switch = "--pdf-info";

    public sealed record Answer(int Pages, string? Title, string? Error);

    public static int Run(string path)
    {
        try
        {
            var info = PdfInspector.Read(File.ReadAllBytes(path));
            Console.Out.Write(JsonSerializer.Serialize(new Answer(info.PageCount, info.Title, null)));
            return 0;
        }
        catch (GoogleExportException ex)
        {
            Console.Out.Write(JsonSerializer.Serialize(new Answer(0, null, ex.Message)));
            return 2;
        }
        catch (Exception)
        {
            return 3;
        }
    }
}
