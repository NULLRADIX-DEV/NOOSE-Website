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

// Google slides: pages of an imported PDF, drawn with pdf.js into a canvas at the size they are shown. A thumbnail
// draws small, a projector in full screen draws large, so every size stays sharp. Each deck is loaded once per tab
// and shared by all its slides; a slide draws only once it scrolls into view.

// the legacy build of pdf.js: same output, with polyfills for browsers that are a year or two old
const PDFJS = '6.3.289-legacy';
const lib = new URL('../lib/pdfjs/', import.meta.url);

let pdfjs = null;
const decks = new Map();   // url -> { promise, users }
const hosts = new Map();   // element -> { url, page, eager, visible, drawn, task, timer }
let seen = null;
let sized = null;
let sweeper = null;

function library() {
    if (!pdfjs) {
        pdfjs = import(new URL(`pdf.min.mjs?v=${PDFJS}`, lib).href).then(module => {
            module.GlobalWorkerOptions.workerSrc = new URL(`pdf.worker.min.mjs?v=${PDFJS}`, lib).href;
            return module;
        });
        pdfjs.catch(() => { pdfjs = null; });
    }
    return pdfjs;
}

function open(url) {
    let deck = decks.get(url);
    if (!deck) {
        const promise = library().then(module => module.getDocument({
            url,
            // the deck only shows: no script, no forms, and fonts never go through eval
            isEvalSupported: false,
            enableXfa: false,
            cMapUrl: new URL('cmaps/', lib).href,
            cMapPacked: true,
            standardFontDataUrl: new URL('standard_fonts/', lib).href,
            wasmUrl: new URL('wasm/', lib).href,
            iccUrl: new URL('iccs/', lib).href,
        }).promise);
        deck = { promise, users: 0 };
        decks.set(url, deck);
        // a failed load is tried again by the next slide that needs it
        promise.catch(() => { if (decks.get(url) === deck) { decks.delete(url); } });
    }
    return deck;
}

function use(url) {
    open(url).users++;
}

function unuse(url) {
    const deck = decks.get(url);
    if (!deck) {
        return;
    }
    deck.users--;
    if (deck.users <= 0) {
        // kept a little, so paging back and forth between pages does not load the deck again
        setTimeout(() => {
            if (deck.users <= 0 && decks.get(url) === deck) {
                decks.delete(url);
                deck.promise.then(pdf => pdf.destroy()).catch(() => { });
            }
        }, 60000);
    }
}

function observers() {
    if (seen) {
        return;
    }
    seen = new IntersectionObserver(entries => {
        for (const entry of entries) {
            const state = hosts.get(entry.target);
            if (state) {
                state.visible = entry.isIntersecting;
                if (state.visible) {
                    schedule(entry.target, state);
                }
            }
        }
    }, { rootMargin: '300px' });
    sized = new ResizeObserver(entries => {
        for (const entry of entries) {
            const state = hosts.get(entry.target);
            if (state) {
                schedule(entry.target, state);
            }
        }
    });
    // Blazor removes slides without telling: whatever left the page lets go of its deck
    sweeper = setInterval(sweep, 5000);
}

function sweep() {
    for (const [host, state] of hosts) {
        if (!host.isConnected) {
            release(host, state);
        }
    }
    if (hosts.size === 0 && sweeper) {
        clearInterval(sweeper);
        seen.disconnect();
        sized.disconnect();
        seen = sized = sweeper = null;
    }
}

function release(host, state) {
    clearTimeout(state.timer);
    state.task?.cancel();
    seen?.unobserve(host);
    sized?.unobserve(host);
    hosts.delete(host);
    unuse(state.url);
}

function schedule(host, state) {
    if (!state.visible && !state.eager) {
        return;
    }
    // a resize fires many times in a row; draw once it settles
    clearTimeout(state.timer);
    state.timer = setTimeout(() => draw(host, state), state.drawn ? 120 : 0);
}

async function draw(host, state) {
    const width = host.clientWidth;
    const height = host.clientHeight;
    if (!width || !height || hosts.get(host) !== state) {
        return;
    }
    // print wants more pixels than the screen shows
    const ratio = Math.max(window.devicePixelRatio || 1, state.eager ? 2 : 1);
    const key = `${state.url}#${state.page}@${width}x${height}x${ratio}`;
    if (state.drawn === key) {
        return;
    }
    state.task?.cancel();
    try {
        const pdf = await open(state.url).promise;
        if (hosts.get(host) !== state) {
            return;
        }
        if (state.page < 1 || state.page > pdf.numPages) {
            fail(host, 'Seite fehlt in der Datei');
            return;
        }
        const page = await pdf.getPage(state.page);
        const base = page.getViewport({ scale: 1 });
        const scale = Math.min(width / base.width, height / base.height);
        const viewport = page.getViewport({ scale: scale * ratio });
        const canvas = document.createElement('canvas');
        // the bitmap has the pixels, the stylesheet fits it into the slide: printing, or full screen before the
        // redraw, scales the picture instead of cutting it off
        canvas.width = Math.floor(viewport.width);
        canvas.height = Math.floor(viewport.height);
        // the print intent also draws without animation frames, so a print view in a background tab completes
        const task = page.render({ canvas, viewport, intent: state.eager ? 'print' : 'display' });
        state.task = task;
        await task.promise;
        if (hosts.get(host) !== state || state.task !== task) {
            return;
        }
        // drawn aside and swapped in, so a redraw never flashes empty
        host.replaceChildren(canvas);
        state.drawn = key;
        state.task = null;
    } catch (error) {
        if (error?.name === 'RenderingCancelledException') {
            return;
        }
        fail(host, 'Folie nicht verfügbar');
    }
}

function fail(host, text) {
    const note = document.createElement('span');
    note.className = 'slide-pdf-hinweis';
    note.textContent = text;
    host.replaceChildren(note);
}

// Shows page `page` of the PDF at `url` in `host`. Called again whenever the slide changes; `eager` draws at once
// instead of on scrolling into view, for the print view.
export function seite(host, url, page, eager) {
    if (!host) {
        return;
    }
    observers();
    let state = hosts.get(host);
    if (!state) {
        state = { url: null, page: 0, eager: false, visible: false, drawn: null, task: null, timer: 0 };
        hosts.set(host, state);
        seen.observe(host);
        sized.observe(host);
    }
    if (state.url !== url) {
        use(url);
        if (state.url) {
            unuse(state.url);
        }
    }
    state.url = url;
    state.page = page;
    state.eager = !!eager;
    state.drawn = state.drawn && state.drawn.startsWith(`${url}#${page}@`) ? state.drawn : null;
    schedule(host, state);
}
