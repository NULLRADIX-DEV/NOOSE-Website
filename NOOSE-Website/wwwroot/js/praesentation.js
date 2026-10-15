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
let worker = null;
const decks = new Map();   // url -> deck { url, users, failed, dropped, idle, task, promise }
const hosts = new Map();   // element -> { url, page, eager, deck, visible, reported, drawn, settled, gen, task, timer, picture, waits }
let seen = null;
let sized = null;
let sweeper = null;
let printing = 0;
const printQueue = [];

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

function load(url) {
    const deck = { url, users: 0, failed: false, dropped: false, idle: 0, task: null, promise: null };
    deck.promise = library().then(module => {
        // one worker for every deck of the tab; without it pdf.js starts a worker per document
        worker ??= new module.PDFWorker({ name: 'noose-folien' });
        deck.task = module.getDocument({
            url,
            worker,
            // the deck only shows: no forms, no annotation layer. 6.x has no eval path left; the flag stays so a
            // later build cannot quietly bring one back
            isEvalSupported: false,
            enableXfa: false,
            cMapUrl: new URL('cmaps/', lib).href,
            cMapPacked: true,
            standardFontDataUrl: new URL('standard_fonts/', lib).href,
            wasmUrl: new URL('wasm/', lib).href,
            iccUrl: new URL('iccs/', lib).href,
        });
        if (deck.dropped) {
            deck.task.destroy().catch(() => { });
        }
        return deck.task.promise;
    });
    deck.promise.catch(() => {
        // the next draw of a slide that needs it loads it afresh
        deck.failed = true;
        deck.task?.destroy().catch(() => { });
        if (decks.get(url) === deck) {
            decks.delete(url);
        }
    });
    return deck;
}

// The slide holds the deck object it counted on, so a reload after a failure never miscounts another one.
function acquire(url) {
    let deck = decks.get(url);
    if (!deck) {
        deck = load(url);
        decks.set(url, deck);
    }
    deck.users++;
    clearTimeout(deck.idle);
    return deck;
}

function release(deck) {
    if (!deck) {
        return;
    }
    deck.users--;
    if (deck.users > 0) {
        return;
    }
    // kept a little, so paging back and forth between pages does not load the deck again; destroying the loading
    // task also stops a download still running
    deck.idle = setTimeout(() => {
        if (deck.users > 0) {
            return;
        }
        if (decks.get(deck.url) === deck) {
            decks.delete(deck.url);
        }
        deck.dropped = true;
        deck.task?.destroy().catch(() => { });
    }, deck.failed ? 0 : 60000);
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
                state.reported = true;
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
            forget(host, state);
        }
    }
    if (hosts.size === 0 && sweeper) {
        clearInterval(sweeper);
        seen.disconnect();
        sized.disconnect();
        seen = sized = sweeper = null;
    }
}

function forget(host, state) {
    clearTimeout(state.timer);
    state.gen++;
    state.task?.cancel();
    if (state.picture) {
        URL.revokeObjectURL(state.picture);
    }
    seen?.unobserve(host);
    sized?.unobserve(host);
    hosts.delete(host);
    release(state.deck);
}

// until the first observer report: the slide's own position
function nearView(host) {
    if (!host.isConnected) {
        return false;
    }
    const box = host.getBoundingClientRect();
    return box.bottom > -300 && box.top < window.innerHeight + 300 && box.right > -300 && box.left < window.innerWidth + 300;
}

// back in view: retry owed slides
function wake() {
    for (const [host, state] of hosts) {
        // a running render finishes by itself
        if (!state.settled && !state.task && host.isConnected) {
            schedule(host, state);
        }
    }
}

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
        wake();
    }
});
window.addEventListener('focus', wake);
window.addEventListener('pageshow', wake);

function schedule(host, state) {
    if (!state.visible && !state.eager && (state.reported || !nearView(host))) {
        return;
    }
    clearTimeout(state.timer);
    if (state.eager) {
        // the print view draws every slide at once: a few at a time, or a long deck exhausts the tab's memory
        state.timer = setTimeout(() => queuePrint(host, state), 0);
        return;
    }
    // a resize fires many times in a row; draw once it settles
    state.timer = setTimeout(() => draw(host, state), state.drawn ? 120 : 0);
}

function queuePrint(host, state) {
    if (!printQueue.some(item => item.host === host)) {
        printQueue.push({ host, state });
    }
    pumpPrint();
}

function pumpPrint() {
    while (printing < 2 && printQueue.length > 0) {
        const { host, state } = printQueue.shift();
        printing++;
        draw(host, state).finally(() => {
            printing--;
            pumpPrint();
        });
    }
}

async function draw(host, state) {
    const width = host.clientWidth;
    const height = host.clientHeight;
    if (hosts.get(host) !== state) {
        return;
    }
    if (!width || !height) {
        // not laid out yet
        if (host.isConnected && state.waits < 20) {
            state.waits++;
            clearTimeout(state.timer);
            state.timer = setTimeout(() => schedule(host, state), 250);
        }
        return;
    }
    state.waits = 0;
    const { url, page: number, eager } = state;
    // print wants more pixels than the screen shows, but not without limit: a long deck prints many pages
    const ratio = eager ? Math.min(Math.max(window.devicePixelRatio || 1, 1.5), 2) : (window.devicePixelRatio || 1);
    const key = `${url}#${number}@${width}x${height}x${ratio}`;
    // every draw supersedes the ones before it, whichever finishes first
    const gen = ++state.gen;
    const stale = () => gen !== state.gen || hosts.get(host) !== state;
    state.task?.cancel();
    state.task = null;
    if (state.drawn === key) {
        return;
    }
    if (state.deck.failed) {
        const fresh = acquire(url);
        release(state.deck);
        state.deck = fresh;
    }
    let page = null;
    try {
        const pdf = await state.deck.promise;
        if (stale()) {
            return;
        }
        if (number < 1 || number > pdf.numPages) {
            fail(host, state, 'Seite fehlt in der Datei');
            return;
        }
        page = await pdf.getPage(number);
        if (stale()) {
            return;
        }
        const base = page.getViewport({ scale: 1 });
        const scale = Math.min(width / base.width, height / base.height);
        const viewport = page.getViewport({ scale: scale * ratio });
        // the bitmap has the pixels, the stylesheet fits it into the slide: printing, or full screen before the
        // redraw, scales the picture instead of cutting it off
        const canvas = document.createElement('canvas');
        canvas.width = Math.floor(viewport.width);
        canvas.height = Math.floor(viewport.height);
        // the print intent also draws without animation frames, so a print view in a background tab completes
        const task = page.render({ canvas, viewport, intent: eager ? 'print' : 'display' });
        state.task = task;
        await task.promise;
        if (stale()) {
            return;
        }
        state.task = null;
        if (eager) {
            // a picture instead of the canvas: the browser keeps it compressed, a canvas holds every pixel
            const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
            canvas.width = canvas.height = 0;
            if (stale() || !blob) {
                return;
            }
            const picture = URL.createObjectURL(blob);
            const img = document.createElement('img');
            img.alt = '';
            // loaded, not decoded: a hidden tab decodes only once it shows, and the print view must not wait for that
            await new Promise(resolve => {
                img.onload = img.onerror = resolve;
                setTimeout(resolve, 2000);
                img.src = picture;
            });
            if (stale()) {
                URL.revokeObjectURL(picture);
                return;
            }
            host.replaceChildren(img);
            if (state.picture) {
                URL.revokeObjectURL(state.picture);
            }
            state.picture = picture;
        } else {
            // drawn aside and swapped in, so a redraw never flashes empty
            host.replaceChildren(canvas);
        }
        state.drawn = key;
        state.settled = true;
    } catch (error) {
        if (stale() || error?.name === 'RenderingCancelledException') {
            return;
        }
        fail(host, state, 'Folie nicht verfügbar');
    } finally {
        // the page's parsed content and decoded pictures go once drawn; a redraw parses again
        if (page && !eager && !stale()) {
            page.cleanup();
        }
    }
}

function fail(host, state, text) {
    const note = document.createElement('span');
    note.className = 'slide-pdf-hinweis';
    note.textContent = text;
    host.replaceChildren(note);
    state.drawn = null;
    state.settled = true;
}

// Shows page `page` of the PDF at `url` in `host`. Called again whenever the slide changes; `eager` draws at once
// instead of on scrolling into view, for the print view. False when the element is gone.
export function seite(host, url, page, eager) {
    if (!host || !host.isConnected) {
        return false;
    }
    observers();
    let state = hosts.get(host);
    if (!state) {
        state = {
            url: null, page: 0, eager: false, deck: null, visible: false, reported: false, drawn: null, settled: false,
            gen: 0, task: null, timer: 0, picture: null, waits: 0,
        };
        hosts.set(host, state);
        seen.observe(host);
        sized.observe(host);
    }
    if (state.url !== url) {
        const deck = acquire(url);
        release(state.deck);
        state.deck = deck;
    }
    if (state.url !== url || state.page !== page) {
        state.settled = false;
    }
    state.url = url;
    state.page = page;
    state.eager = !!eager;
    state.waits = 0;
    state.drawn = state.drawn && state.drawn.startsWith(`${url}#${page}@`) ? state.drawn : null;
    schedule(host, state);
    return true;
}

// Resolves once `count` slides of the print view are drawn or failed, at the latest after `timeout` ms; the print
// view opens the print dialog only then.
export function fertig(count, timeout) {
    const started = Date.now();
    return new Promise(resolve => {
        const check = () => {
            let done = 0;
            for (const [host, state] of hosts) {
                if (state.eager && state.settled && host.isConnected) {
                    done++;
                }
            }
            if (done >= count || Date.now() - started >= timeout) {
                resolve(done >= count);
            } else {
                setTimeout(check, 200);
            }
        };
        check();
    });
}
