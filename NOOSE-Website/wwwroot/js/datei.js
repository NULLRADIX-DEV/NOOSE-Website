// Hands a file the server built to the browser as a download.
// The bytes arrive as a stream reference, so a large export does not travel as one interop message.

/// Save the streamed content under the given name.
export async function speichern(name, typ, inhalt) {
    const blob = new Blob([await inhalt.arrayBuffer()], { type: typ });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    // the click has already handed the blob to the download; the URL is no longer needed
    URL.revokeObjectURL(url);
}
