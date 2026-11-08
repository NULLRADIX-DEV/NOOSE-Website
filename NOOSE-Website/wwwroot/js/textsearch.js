// In-page search helpers for the service regulation page.
// Highlighting uses the CSS Custom Highlight API: ranges are registered with the browser and painted via
// ::highlight(noose-suche), the DOM itself is never touched, so Blazor's diffing stays intact. Browsers without the
// API simply show no highlight; the filtering still works because it happens in C#.

const NAME = 'noose-suche';

// only text inside these is searched by RegulationTextSearch, so only these get highlighted
const SEARCHED = '.dvo-suchbar';

// text from different blocks must not run together into a false match ("…Ende" + "Anfang…")
const BLOCK = 'P,LI,TD,TH,DIV,H1,H2,H3,H4,H5,H6,BLOCKQUOTE,PRE,TR,UL,OL,TABLE,DT,DD';

let observer = null;
let pending = 0;
let active = null; // { container, terms }

// same folding as RegulationTextSearch.Fold: lower case, no accents, ß as ss.
// Appends the folded text of one node to state.out and records, for every folded UTF-16 unit, the node and the
// UTF-16 offset of the character it came from. Iterates by code point, so a surrogate pair is folded as a whole.
function foldInto(state, node) {
    const text = node.nodeValue;
    let i = 0;
    for (const ch of text) {
        const f = ch === 'ß' || ch === 'ẞ' ? 'ss' : ch.normalize('NFD').replace(/\p{Mn}/gu, '').toLowerCase();
        for (let k = 0; k < f.length; k++) {
            state.out += f[k];
            state.nodes.push(node);
            state.starts.push(i);
            state.ends.push(i + ch.length);
        }
        i += ch.length;
    }
}

function blockOf(node) {
    return node.parentElement ? node.parentElement.closest(BLOCK) : null;
}

function rangesIn(root, terms, ranges) {
    const state = { out: '', nodes: [], starts: [], ends: [] };
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let lastBlock;
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        if (!node.nodeValue) {
            continue;
        }
        const block = blockOf(node);
        if (lastBlock !== undefined && block !== lastBlock) {
            // a separator no folded term can contain; it maps to no node and is never part of a match
            state.out += '\n';
            state.nodes.push(null);
            state.starts.push(0);
            state.ends.push(0);
        }
        lastBlock = block;
        foldInto(state, node);
    }
    for (const term of terms) {
        let from = 0;
        for (let at = state.out.indexOf(term, from); at >= 0; at = state.out.indexOf(term, from)) {
            const last = at + term.length - 1;
            const startNode = state.nodes[at];
            const endNode = state.nodes[last];
            if (startNode && endNode) {
                const range = new Range();
                range.setStart(startNode, state.starts[at]);
                range.setEnd(endNode, state.ends[last]);
                ranges.push(range);
            }
            from = at + term.length;
        }
    }
}

function apply() {
    pending = 0;
    if (!active || !(globalThis.CSS && CSS.highlights) || typeof Highlight === 'undefined') {
        return 0;
    }
    const { container, terms } = active;
    if (!container.isConnected) {
        clearHighlight();
        return 0;
    }
    const ranges = [];
    for (const root of container.querySelectorAll(SEARCHED)) {
        rangesIn(root, terms, ranges);
    }
    const highlight = new Highlight();
    for (const range of ranges) {
        highlight.add(range);
    }
    CSS.highlights.set(NAME, highlight);
    return ranges.length;
}

export function clearHighlight() {
    active = null;
    if (observer) {
        observer.disconnect();
        observer = null;
    }
    if (pending) {
        cancelAnimationFrame(pending);
        pending = 0;
    }
    if (globalThis.CSS && CSS.highlights) {
        CSS.highlights.delete(NAME);
    }
}

// terms arrive already folded from C#. Child components (RichHtml resolving mentions) re-render on their own after
// the page has called this, which replaces text nodes and collapses the ranges; the observer re-applies them.
export function highlight(container, terms) {
    clearHighlight();
    if (!container || !terms || terms.length === 0) {
        return 0;
    }
    active = { container, terms };
    observer = new MutationObserver(() => {
        if (!pending) {
            pending = requestAnimationFrame(apply);
        }
    });
    observer.observe(container, { subtree: true, childList: true, characterData: true });
    return apply();
}

// true once the element exists and was scrolled to; the caller retries while a freshly mounted rail renders
export function scrollToId(id) {
    const element = document.getElementById(id);
    if (!element) {
        return false;
    }
    // instant: a jump across a long section should land, not glide for seconds
    element.scrollIntoView({ behavior: 'auto', block: 'start' });
    // keyboard users continue from the paragraph, not from the button that no longer exists
    element.focus({ preventScroll: true });
    return true;
}
