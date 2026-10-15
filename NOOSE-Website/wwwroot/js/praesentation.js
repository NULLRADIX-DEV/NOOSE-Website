// Full screen for the slide stage of the presentation pages; a second call leaves it again.
export function vollbild(element) {
    if (document.fullscreenElement) {
        document.exitFullscreen().catch(() => { });
        return;
    }
    if (element && typeof element.requestFullscreen === 'function') {
        element.requestFullscreen().catch(() => { });
    }
}
