using UglyToad.PdfPig;

namespace NOOSE_Website.Services.Import;

/// <summary>What the server reads from an imported deck: its page count and the title Google gave it.</summary>
public sealed record PdfInfo(int PageCount, string? Title);

/// <summary>Checks that bytes are a readable PDF and reads its page count; drawing the pages is the browser's job.</summary>
public static class PdfInspector
{
    /// <summary>The page count and title; a message for the author when the file is no readable PDF.</summary>
    public static PdfInfo Read(byte[] bytes)
    {
        // the header may follow a little junk, but never more than a kilobyte of it
        if (!HasHeader(bytes))
        {
            throw new GoogleExportException("Das ist keine PDF-Datei. In Google: Datei → Herunterladen → PDF-Dokument.");
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

    private static bool HasHeader(byte[] bytes)
    {
        ReadOnlySpan<byte> marker = "%PDF-"u8;
        var window = bytes.AsSpan(0, Math.Min(bytes.Length, 1024));
        return window.IndexOf(marker) >= 0;
    }
}
