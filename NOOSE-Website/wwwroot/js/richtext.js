// quill interop
import {
    ladeEntwurfsDb, entwurfSchreiben, entwurfLesen, entwurfLoeschen, entwurfAufraeumen, istAbgelaufen,
    vormerken, abmelden, ENTWURF_VERZOEGERUNG,
} from './entwurf.js?v=1';

let quillLadenPromise = null;
let tabellenModulPromise = null; // table handler
let groessenRegistriert = false;
let erwaehnungRegistriert = false;
let bildtextRegistriert = false;
let kastenRegistriert = false;
let trennerRegistriert = false;
const SCHRIFTGROESSEN = ['0.75em', '1.5em', '2.5em']; // inline font-size values
const SCROLL_TOLERANZ = 2; // ignore sub-pixel drift
const ERWAEHNUNG_BLOT = 'erwaehnung';
const ERWAEHNUNG_FENSTER = 60; // chars scanned back from the caret
const ERWAEHNUNG_VERZOEGERUNG = 120;
const ERWAEHNUNG_ABFRAGE = /@([^\s@{}]*)$/;
const ERWAEHNUNG_TOKEN = /@\{(\w+:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}/g;
const ERWAEHNUNG_TASTEN = ['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'];

// register style-based size so font-size survives sanitization
function registriereGroessen() {
    if (groessenRegistriert || !window.Quill) {
        return;
    }
    const SizeStyle = window.Quill.import('attributors/style/size');
    SizeStyle.whitelist = SCHRIFTGROESSEN;
    window.Quill.register(SizeStyle, true);
    groessenRegistriert = true;
}

function beschrifteAuswahl(leiste, format, labels, titel) {
    const auswahl = leiste.querySelector('select.ql-' + format);
    const picker = leiste.querySelector('.ql-picker.ql-' + format);
    if (!auswahl || !picker) {
        return;
    }
    for (const option of auswahl.options) {
        option.dataset.label = labels[option.value] || labels[''];
    }
    for (const item of picker.querySelectorAll('.ql-picker-item')) {
        item.dataset.label = labels[item.dataset.value || ''] || labels[''];
    }
    const label = picker.querySelector('.ql-picker-label');
    if (!label) {
        return;
    }
    label.title = titel;
    label.setAttribute('aria-label', titel);
    const aktualisieren = () => {
        label.dataset.label = labels[auswahl.value] || labels[''];
    };
    auswahl.addEventListener('change', aktualisieren);
    aktualisieren();
}

// on the narrow-screen strip the picker dropdowns sit inside overflow-x:auto and would be clipped:
// re-anchor them against the viewport (the toolbar carries no transform, so fixed works here)
function loeseDropdownsAusScrollStrip(leiste) {
    const schmal = window.matchMedia('(max-width: 960px)');
    for (const picker of leiste.querySelectorAll('.ql-picker')) {
        const beschriftung = picker.querySelector('.ql-picker-label');
        const auswahl = picker.querySelector('.ql-picker-options');
        if (!beschriftung || !auswahl) {
            continue;
        }
        beschriftung.addEventListener('click', () => {
            if (beschriftung.closest('.mud-dialog')) {
                // inside a dialog the toolbar wraps instead (transform would pin position:fixed to the dialog)
                return;
            }
            if (!schmal.matches) {
                // wide again: hand positioning back to quill's own absolute rules
                auswahl.style.position = '';
                auswahl.style.top = '';
                auswahl.style.left = '';
                auswahl.style.zIndex = '';
                return;
            }
            // quill toggles ql-expanded on the same click; measure after the handler ran
            requestAnimationFrame(() => {
                if (!picker.classList.contains('ql-expanded') || !picker.isConnected) {
                    return;
                }
                const suche = beschriftung.getBoundingClientRect();
                auswahl.style.position = 'fixed';
                auswahl.style.top = (suche.bottom + 4) + 'px';
                auswahl.style.left = Math.max(4, Math.min(suche.left, window.innerWidth - auswahl.offsetWidth - 8)) + 'px';
                auswahl.style.zIndex = '12';
            });
        });
    }
}

function beschrifteToolbar(leiste, tableHandler) {
    const werkzeuge = [
        ['button.ql-bold', 'Fett (Strg+B)'],
        ['button.ql-italic', 'Kursiv (Strg+I)'],
        ['button.ql-underline', 'Unterstreichen (Strg+U)'],
        ['button.ql-strike', 'Durchstreichen'],
        ['button.ql-list[value="ordered"]', 'Nummerierte Liste'],
        ['button.ql-list[value="bullet"]', 'Aufzählung'],
        ['button.ql-list[value="check"]', 'Checkliste'],
        ['button.ql-indent[value="-1"]', 'Einzug verringern'],
        ['button.ql-indent[value="+1"]', 'Einzug erhöhen'],
        ['.ql-align .ql-picker-label', 'Ausrichtung'],
        ['button.ql-blockquote', 'Zitat'],
        ['button.ql-code-block', 'Codeblock'],
        ['button.ql-link', 'Link einfügen (Strg+K)'],
        ['button.ql-image', 'Bild einfügen'],
        ['button.ql-clean', 'Formatierung entfernen'],
        ['button.ql-noose-suchen', 'Suchen und Ersetzen (Strg+F)'],
        ['button.ql-noose-vollbild', 'Vollbild (Esc beendet)'],
        ['button.ql-noose-kasten-hinweis', 'Hinweis-Kasten'],
        ['button.ql-noose-kasten-warnung', 'Warnung-Kasten'],
        ['button.ql-noose-kasten-info', 'Info-Kasten'],
        ['button.ql-noose-trenner', 'Trennlinie'],
        ['button.ql-noose-toc', 'Inhaltsverzeichnis einfügen'],
        ['.ql-color .ql-picker-label', 'Textfarbe'],
        ['.ql-background .ql-picker-label', 'Hintergrundfarbe'],
    ];
    if (tableHandler) {
        werkzeuge.push(['.ql-' + tableHandler.toolName + ' .ql-picker-label', 'Tabelle einfügen']);
    }
    for (const [selector, titel] of werkzeuge) {
        for (const element of leiste.querySelectorAll(selector)) {
            element.title = titel;
            element.setAttribute('aria-label', titel);
        }
    }
    const ausrichtung = { '': 'Linksbündig', center: 'Zentriert', right: 'Rechtsbündig', justify: 'Blocksatz' };
    for (const element of leiste.querySelectorAll('.ql-align .ql-picker-item')) {
        const titel = ausrichtung[element.dataset.value || ''] || 'Ausrichtung';
        element.title = titel;
        element.setAttribute('aria-label', titel);
    }
    beschrifteAuswahl(leiste, 'header', {
        '': 'Text',
        '1': 'Überschrift 1',
        '2': 'Überschrift 2',
        '3': 'Überschrift 3',
    }, 'Textstil');
    beschrifteAuswahl(leiste, 'size', {
        '': 'Normal',
        '0.75em': 'Klein',
        '1.5em': 'Groß',
        '2.5em': 'Sehr groß',
    }, 'Schriftgröße');
}

// Stored html carries the bare @{Typ:Id} token, same as every plain-text field — a frozen label would leak
// the name of a classified record into search snippets, Discord embeds and LLM context. The chip below is
// view-only: built on load, dissolved back into the token on read.
function registriereErwaehnung() {
    if (erwaehnungRegistriert || !window.Quill) {
        return;
    }
    const Embed = window.Quill.import('blots/embed');
    class ErwaehnungBlot extends Embed {
        static create(wert) {
            const knoten = super.create();
            knoten.setAttribute('data-erwaehnung', wert.token);
            knoten.textContent = '@' + wert.beschriftung;
            return knoten;
        }
        static value(knoten) {
            return {
                token: knoten.getAttribute('data-erwaehnung'),
                beschriftung: (knoten.textContent || '').replace(/^@/, ''),
            };
        }
    }
    ErwaehnungBlot.blotName = ERWAEHNUNG_BLOT;
    ErwaehnungBlot.tagName = 'SPAN';
    ErwaehnungBlot.className = 'erwaehnung';
    window.Quill.register(ErwaehnungBlot, true);
    erwaehnungRegistriert = true;
}

function maskiere(text) {
    return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// token -> chip; an unresolvable token stays literal text so nothing is silently dropped
function tokenZuChip(html, beschriftungen) {
    if (!html || html.indexOf('@{') < 0) {
        return html;
    }
    return html.replace(ERWAEHNUNG_TOKEN, (treffer, schluessel) => {
        const beschriftung = beschriftungen && beschriftungen[schluessel];
        return beschriftung
            ? '<span class="erwaehnung" data-erwaehnung="' + maskiere(schluessel) + '">@' + maskiere(beschriftung) + '</span>'
            : treffer;
    });
}

// chip -> token, on a clone: quill's embed wraps the label in guard text nodes, which no regex survives
function chipZuToken(wurzel) {
    if (!wurzel.querySelector('[data-erwaehnung]')) {
        return wurzel.innerHTML;
    }
    const klon = wurzel.cloneNode(true);
    for (const knoten of Array.from(klon.querySelectorAll('[data-erwaehnung]'))) {
        const token = '@{' + knoten.getAttribute('data-erwaehnung') + '}';
        knoten.parentNode.replaceChild(document.createTextNode(token), knoten);
    }
    return klon.innerHTML;
}

// quill focuses hidden nodes; suppress the induced scroll
function entschaerfeFokus(node) {
    if (!node || node.__nooseFokusOhneScroll || typeof node.focus !== 'function') {
        return;
    }
    const originalFokus = node.focus;
    node.focus = function (optionen) {
        return originalFokus.call(this, Object.assign({}, optionen, { preventScroll: true }));
    };
    node.__nooseFokusOhneScroll = true;
}

// window plus every scrollable ancestor (dialog body)
function sammleScrollStand(node) {
    const stand = [{ ziel: window, x: window.scrollX, y: window.scrollY }];
    let eltern = node ? node.parentElement : null;
    while (eltern && eltern !== document.body && eltern !== document.documentElement) {
        if (eltern.scrollHeight > eltern.clientHeight || eltern.scrollWidth > eltern.clientWidth) {
            stand.push({ ziel: eltern, x: eltern.scrollLeft, y: eltern.scrollTop });
        }
        eltern = eltern.parentElement;
    }
    return stand;
}

// only correct real drift
function stelleScrollStandHer(stand) {
    for (const eintrag of stand) {
        if (eintrag.ziel === window) {
            if (Math.abs(window.scrollY - eintrag.y) > SCROLL_TOLERANZ
                || Math.abs(window.scrollX - eintrag.x) > SCROLL_TOLERANZ) {
                window.scrollTo(eintrag.x, eintrag.y);
            }
            continue;
        }
        if (Math.abs(eintrag.ziel.scrollTop - eintrag.y) > SCROLL_TOLERANZ) {
            eintrag.ziel.scrollTop = eintrag.y;
        }
        if (Math.abs(eintrag.ziel.scrollLeft - eintrag.x) > SCROLL_TOLERANZ) {
            eintrag.ziel.scrollLeft = eintrag.x;
        }
    }
}

// second layer for engines without preventScroll
function haengeScrollWaechterAn(element) {
    if (!element || element.__nooseScrollWaechter) {
        return;
    }
    // capture runs before quill's own handler
    element.addEventListener('paste', () => {
        const stand = sammleScrollStand(element);
        requestAnimationFrame(() => stelleScrollStandHer(stand));
        // quill finishes inside setTimeout(1)
        setTimeout(() => stelleScrollStandHer(stand), 40);
    }, true);
    element.__nooseScrollWaechter = true;
}

// what quill's image blot writes instead of a src outside its own http/https/data whitelist
const QUILL_ABGEWIESEN = '//:0';

// pasted screenshots are the real size driver; the row keeps whatever arrives here
const BILD_MAX_KANTE = 2000;
const BILD_MAX_BYTES = 1024 * 1024;

// scales down and re-encodes as webp, the original on any doubt
async function verkleinereBild(datei) {
    if (!datei || datei.type === 'image/gif') {
        return datei; // canvas would kill the animation
    }
    try {
        const quelle = await ladeBitmap(datei);
        const kante = Math.max(quelle.width, quelle.height);
        if (kante <= BILD_MAX_KANTE && datei.size <= BILD_MAX_BYTES) {
            if (quelle.close) {
                quelle.close();
            }
            return datei;
        }
        const faktor = Math.min(1, BILD_MAX_KANTE / kante);
        const breite = Math.max(1, Math.round(quelle.width * faktor));
        const hoehe = Math.max(1, Math.round(quelle.height * faktor));
        const flaeche = document.createElement('canvas');
        flaeche.width = breite;
        flaeche.height = hoehe;
        flaeche.getContext('2d').drawImage(quelle, 0, 0, breite, hoehe);
        if (quelle.close) {
            quelle.close();
        }
        const kleiner = await new Promise((fertig) => flaeche.toBlob(fertig, 'image/webp', 0.86));
        // null without webp support, and a bigger file is never worth it
        return kleiner && kleiner.size < datei.size ? kleiner : datei;
    } catch (e) {
        return datei; // scaling must never block a paste
    }
}

function ladeBitmap(datei) {
    if (window.createImageBitmap) {
        return window.createImageBitmap(datei);
    }
    return new Promise((fertig, fehler) => {
        const quelle = new Image();
        const adresse = URL.createObjectURL(datei);
        quelle.onload = () => {
            URL.revokeObjectURL(adresse);
            fertig(quelle);
        };
        quelle.onerror = () => {
            URL.revokeObjectURL(adresse);
            fehler(new Error('bitmap'));
        };
        quelle.src = adresse;
    });
}

function alsDatenUrl(blob) {
    return new Promise((fertig, fehler) => {
        const leser = new FileReader();
        leser.onload = () => fertig(leser.result);
        leser.onerror = () => fehler(leser.error);
        leser.readAsDataURL(blob);
    });
}

// data uri of the first image on the clipboard, null when there is none or it cannot be read
function liesZwischenablageBild(daten) {
    const bild = Array.from((daten && daten.files) || []).find(d => d.type.startsWith('image/'));
    if (!bild) {
        return Promise.resolve(null);
    }
    return verkleinereBild(bild).then(alsDatenUrl).catch(() => null);
}

function zaehleBilder(html) {
    return (html.match(/<img\b/gi) || []).length;
}

// quill 1.x drops pasted image files (screenshots)
function haengeBildEinfuegungAn(element) {
    element.addEventListener('paste', (ereignis) => {
        const daten = ereignis.clipboardData;
        const editor = element.__nooseQuill;
        if (!editor || !daten) {
            return;
        }
        const html = daten.getData('text/html');
        if (html) {
            // Word, Outlook and a copied web image put html AND the bitmap on the clipboard. The html carries the
            // surrounding text, so it has to be pasted — but its src is a file:// path, a cid: or a foreign URL,
            // and quill as well as the server sanitizer drop those, which lost the picture on save. The bitmap is
            // kept as the fallback for exactly that, and only for a single picture: one bitmap cannot stand in for
            // two of them.
            element.__nooseZwischenbild = zaehleBilder(html) === 1 ? liesZwischenablageBild(daten) : null;
            return; // html pastes go through the img matcher
        }
        element.__nooseZwischenbild = null;
        const bild = Array.from(daten.files || []).find(d => d.type.startsWith('image/'));
        if (!bild) {
            return;
        }
        ereignis.preventDefault();
        ereignis.stopPropagation();
        verkleinereBild(bild).then(alsDatenUrl).then((dataUrl) => {
            const bereich = editor.getSelection(true);
            editor.insertEmbed(bereich.index, 'image', dataUrl, 'user');
            editor.setSelection(bereich.index + 1, 0, 'silent');
        }).catch(() => { /* ignore */ });
    }, true); // capture runs before quill's own handler
}

// pasted images with external or blob src: inline as data uri — links rot, blob: dies with the tab
function registriereBildMatcher(editor, element) {
    editor.clipboard.addMatcher('img', (node, delta) => {
        const quelle = (node.getAttribute('src') || '').trim();
        if (!quelle || quelle.startsWith('data:')) {
            return delta;
        }
        // matchers stay sync; replace async, original src is the fallback
        setTimeout(() => ersetzeDurchDataUrl(editor, quelle, element), 1);
        return delta;
    });
}

// a src this page can never read; fetching it only costs a round trip and a console error
function istUnerreichbar(quelle) {
    return /^(file|cid):/i.test(quelle) || /^[a-z]:[\\/]/i.test(quelle);
}

const PROFILE_KLASSEN = /^(ql-|noose-|erwaehnung$|dokument-html$)/;

// Word and web pastes bring markup the server would strip later; the editor cleans it up front, against the
// same lists the sanitizer uses, so what is typed already looks like what is stored.
function haengeEinfuegeSauberungAn(element, editor, profil) {
    if (!profil) {
        return;
    }
    const tags = new Set((profil.tags || []).map((t) => String(t).toUpperCase()));
    const attrs = new Set((profil.attributes || []).map((a) => String(a).toLowerCase()));
    const css = new Set((profil.cssProperties || []).map((p) => String(p).toLowerCase()));

    const istBallast = (name) => name === 'SCRIPT' || name === 'STYLE' || name === 'META'
        || name === 'LINK' || name === 'TITLE' || name === 'HEAD';

    // stylings Word writes as inline css, mapped onto the formats the editor actually has
    const semantikAusStil = (knoten, deklarationen) => {
        const marken = [];
        for (const [eigenschaft, wert] of deklarationen) {
            const klein = String(wert || '').toLowerCase();
            if (eigenschaft === 'font-weight' && (klein.includes('bold') || parseInt(klein, 10) >= 600)) {
                marken.push('strong');
            } else if (eigenschaft === 'font-style' && klein.includes('italic')) {
                marken.push('em');
            } else if (eigenschaft === 'text-decoration' && klein.includes('underline')) {
                marken.push('u');
            } else if (eigenschaft === 'text-decoration' && klein.includes('line-through')) {
                marken.push('s');
            } else if (eigenschaft === 'text-align' && ['center', 'right', 'justify'].includes(klein)) {
                knoten.classList.add('ql-align-' + klein);
            }
        }
        return marken;
    };

    // the translated declarations must not stay behind as inline css
    const UEBERSETZT = new Set(['font-weight', 'font-style', 'text-decoration']);

    const filtereAttribute = (knoten) => {
        const deklarationen = Array.from(knoten.style).map((p) => [p.toLowerCase(), knoten.style.getPropertyValue(p)]);
        const marken = semantikAusStil(knoten, deklarationen);
        for (const attribut of Array.from(knoten.attributes)) {
            if (!attrs.has(attribut.name.toLowerCase())) {
                knoten.removeAttribute(attribut.name);
            }
        }
        const klassen = (knoten.getAttribute('class') || '').split(/\s+/).filter((k) => PROFILE_KLASSEN.test(k));
        if (klassen.length > 0) {
            knoten.setAttribute('class', klassen.join(' '));
        } else {
            knoten.removeAttribute('class');
        }
        const behalten = deklarationen.filter(([p]) => css.has(p) && !(marken.length > 0 && UEBERSETZT.has(p)));
        if (behalten.length > 0) {
            knoten.setAttribute('style', behalten.map(([p, w]) => p + ':' + w).join(';'));
        } else {
            knoten.removeAttribute('style');
        }
        wickleInMarken(knoten, marken);
    };

    // a span becomes the mark itself, anything else gets its content wrapped, so quill finds a real format
    const wickleInMarken = (knoten, marken) => {
        if (marken.length === 0) {
            return;
        }
        const dokument = knoten.ownerDocument;
        let innen;
        if (knoten.tagName === 'SPAN') {
            const ersatz = dokument.createElement(marken[0]);
            while (knoten.firstChild) {
                ersatz.appendChild(knoten.firstChild);
            }
            knoten.replaceWith(ersatz);
            innen = ersatz;
        } else {
            innen = dokument.createElement(marken[0]);
            while (knoten.firstChild) {
                innen.appendChild(knoten.firstChild);
            }
            knoten.appendChild(innen);
        }
        for (const marke of marken.slice(1)) {
            const weiter = dokument.createElement(marke);
            while (innen.firstChild) {
                weiter.appendChild(innen.firstChild);
            }
            innen.appendChild(weiter);
            innen = weiter;
        }
    };

    // a glyph like "1." or "a)" is a numbered list; a bullet character is not
    const NUMMERIERT = /^([0-9]+|[a-z]+|[ivxlcdm]+)[.)]$/i;

    // Word marks every list paragraph with mso-list and puts the glyph in a hidden span of its own. Only that
    // span goes. Removing every ELEMENT carrying an mso- property - which is what this did - deleted whole Word
    // tables and ordinary paragraphs along with it; the properties themselves are inline css the allowlist in
    // filtereAttribute drops anyway.
    const wandleWordListen = (wurzel) => {
        let liste = null;
        let art = '';
        for (const absatz of Array.from(wurzel.querySelectorAll('p'))) {
            const stil = (absatz.getAttribute('style') || '') + ' ' + (absatz.getAttribute('class') || '');
            if (!/mso-list/i.test(stil)) {
                liste = null;
                continue;
            }
            // "mso-list:l0 level2 lfo1" - the level is the indent Quill stores as a class on the item
            const ebeneTreffer = /level(\d+)/i.exec(stil);
            const ebene = Math.min(Math.max(parseInt(ebeneTreffer ? ebeneTreffer[1] : '1', 10) || 1, 1), 8);
            const marke = absatz.querySelector('[style*="mso-list"]');
            const zeichen = marke ? (marke.textContent || '').trim() : '';
            const punktArt = NUMMERIERT.test(zeichen) ? 'ol' : 'ul';
            if (marke) {
                marke.remove();
            }
            const punkt = absatz.ownerDocument.createElement('li');
            if (ebene > 1) {
                punkt.setAttribute('class', 'ql-indent-' + (ebene - 1));
            }
            while (absatz.firstChild) {
                punkt.appendChild(absatz.firstChild);
            }
            absatz.replaceWith(punkt);
            if (liste && art === punktArt) {
                liste.appendChild(punkt);
                continue;
            }
            art = punktArt;
            liste = punkt.ownerDocument.createElement(punktArt);
            punkt.before(liste);
            liste.appendChild(punkt);
        }
    };

    const saeubere = (eltern) => {
        for (const kind of Array.from(eltern.childNodes)) {
            if (kind.nodeType === Node.COMMENT_NODE) {
                kind.remove();
                continue;
            }
            if (kind.nodeType !== Node.ELEMENT_NODE) {
                continue;
            }
            const name = kind.tagName.toUpperCase();
            if (istBallast(name)) {
                kind.remove();
                continue;
            }
            if (!tags.has(name)) {
                // unknown wrapper: keep what is inside, drop the tag
                saeubere(kind);
                while (kind.firstChild) {
                    eltern.insertBefore(kind.firstChild, kind);
                }
                kind.remove();
                continue;
            }
            filtereAttribute(kind);
            saeubere(kind);
        }
    };

    const einfuegungSaeubern = (html) => {
        const flaeche = document.createElement('template');
        flaeche.innerHTML = html;
        // list detection needs Word's original styles, cleaning removes them
        wandleWordListen(flaeche.content);
        saeubere(flaeche.content);
        return flaeche.innerHTML;
    };

    element.addEventListener('paste', (ereignis) => {
        const daten = ereignis.clipboardData;
        const html = daten ? daten.getData('text/html') : '';
        if (!html) {
            return; // plain text and bitmaps keep the regular path
        }
        const sauber = einfuegungSaeubern(html);
        ereignis.preventDefault();
        ereignis.stopImmediatePropagation();
        // At the caret, never as the whole document. dangerouslyPasteHTML has two shapes, and the one taking the
        // html FIRST calls setContents - it replaces everything the editor holds. Pasting a sentence into a report
        // wiped the report. The index form inserts, so the selection has to go first, the way a paste behaves.
        const bereich = editor.getSelection(true) || { index: editor.getLength(), length: 0 };
        if (bereich.length > 0) {
            editor.deleteText(bereich.index, bereich.length, 'user');
        }
        editor.clipboard.dangerouslyPasteHTML(bereich.index, sauber, 'user');
    }, true);

    // Strg+Shift+V: insert what was copied as plain text
    element.addEventListener('keydown', (ereignis) => {
        if (!(ereignis.ctrlKey || ereignis.metaKey) || !ereignis.shiftKey
            || String(ereignis.key || '').toLowerCase() !== 'v') {
            return;
        }
        if (!navigator.clipboard || !navigator.clipboard.readText) {
            return;
        }
        ereignis.preventDefault();
        navigator.clipboard.readText().then((text) => {
            const bereich = editor.getSelection(true);
            const inhalt = String(text || '').replace(/\r\n/g, '\n');
            editor.insertText(bereich.index, inhalt, 'user');
            editor.setSelection(bereich.index + inhalt.length, 0, 'silent');
        }).catch(() => { /* ignore */ });
    }, true);
}

// fetch + swap every matching img blot
async function ersetzeDurchDataUrl(editor, quelle, element) {
    // the bitmap of the same paste: the only readable copy when the src itself is not
    const rueckfall = element && element.__nooseZwischenbild
        ? await element.__nooseZwischenbild.catch(() => null)
        : null;
    let dataUrl;
    try {
        if (istUnerreichbar(quelle)) {
            throw new Error('src unreachable');
        }
        const antwort = await fetch(quelle);
        if (!antwort.ok) {
            throw new Error('src http ' + antwort.status);
        }
        const blob = await antwort.blob();
        if (!blob.type.startsWith('image/')) {
            return;
        }
        dataUrl = await alsDatenUrl(await verkleinereBild(blob));
    } catch (e) {
        if (!rueckfall) {
            return; // cors, offline, no bitmap: keep the original src
        }
        dataUrl = rueckfall;
    }
    // the original src first, then the rewrite: quill resolves a relative src against the page and keeps it,
    // but replaces anything outside its http/https/data whitelist with QUILL_ABGEWIESEN before we get here
    if (!tauscheBlots(editor, quelle, dataUrl)) {
        tauscheBlots(editor, QUILL_ABGEWIESEN, dataUrl);
    }
}

// swap every connected img blot carrying that src; false when the document holds none
function tauscheBlots(editor, domQuelle, dataUrl) {
    const Delta = window.Quill.import('delta');
    let getauscht = false;
    for (const img of Array.from(editor.root.querySelectorAll('img'))) {
        if ((img.getAttribute('src') || '') !== domQuelle || !img.isConnected) {
            continue;
        }
        const blot = window.Quill.find(img);
        if (!blot) {
            continue;
        }
        try {
            const index = editor.getIndex(blot);
            editor.updateContents(new Delta().retain(index).insert({ image: dataUrl }).delete(1), 'user');
            getauscht = true;
        } catch (e) {
            /* blot already gone */
        }
    }
    return getauscht;
}

function ladeQuill() {
    if (window.Quill) {
        return Promise.resolve();
    }
    if (quillLadenPromise) {
        return quillLadenPromise;
    }
    quillLadenPromise = new Promise((resolve, reject) => {
        if (!document.querySelector('link[data-quill-css]')) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = 'lib/quill/quill.snow.css';
            link.setAttribute('data-quill-css', '');
            document.head.appendChild(link);
        }
        const script = document.createElement('script');
        script.src = 'lib/quill/quill.min.js';
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('Quill konnte nicht geladen werden.'));
        document.head.appendChild(script);
    });
    return quillLadenPromise;
}

// load table module
function ladeTabellenModul() {
    if (tabellenModulPromise) {
        return tabellenModulPromise;
    }
    tabellenModulPromise = (async () => {
        try {
            await ladeQuill(); // quill first
            if (!document.querySelector('link[data-quill-table-css]')) {
                const link = document.createElement('link');
                link.rel = 'stylesheet';
                link.href = 'lib/quill/table-module.css';
                link.setAttribute('data-quill-table-css', '');
                document.head.appendChild(link);
            }
            const mod = await import('../lib/quill/table-module.js');
            const TableHandler = mod.default;
            window.Quill.register({ ['modules/' + TableHandler.moduleName]: TableHandler }, true);
            if (typeof mod.rewirteFormats === 'function') {
                // fix list formats
                mod.rewirteFormats();
            }
            return TableHandler;
        } catch (e) {
            // fallback: no tables
            console.error('table module failed', e);
            return null;
        }
    })();
    return tabellenModulPromise;
}

const KI_KORREKTUR = 'noosei-korrigieren';
const KI_SCHREIBEN = 'noosei-schreiben';
const MAX_KI_ZEICHEN = 400000;

let kiSymboleRegistriert = false;

function registriereKiSymbole() {
    if (kiSymboleRegistriert || !window.Quill) {
        return;
    }
    // same registry the table module uses; ql-stroke lets app.css theme them like every other tool
    const symbole = window.Quill.import('ui/icons');
    symbole[KI_KORREKTUR] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" stroke-width="1.6" d="M3 12.5 6.5 3l3.5 9.5M4.2 9.8h4.6M12 12.5l1.2-2.6 2.6-1.2-2.6-1.2L12 4.9l-1.2 2.6L8.2 8.7l2.6 1.2Z"/></svg>';
    symbole[KI_SCHREIBEN] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" stroke-width="1.6" d="M12.6 2.6 15.4 5.4 6.2 14.6 2.8 15.2 3.4 11.8Z"/><path class="ql-stroke" fill="none" stroke-width="1.6" d="M10.9 4.3l2.8 2.8"/></svg>';
    kiSymboleRegistriert = true;
}

// Quill 1.3.7 has no delta-to-html API, so an offscreen editor renders a selection for us.
let schattenEditor = null;

function htmlAusBereich(editor, index, laenge) {
    if (!schattenEditor) {
        const huelle = document.createElement('div');
        huelle.style.cssText = 'position:absolute;left:-99999px;top:0;width:800px;';
        huelle.setAttribute('aria-hidden', 'true');
        document.body.appendChild(huelle);
        const ziel = document.createElement('div');
        huelle.appendChild(ziel);
        schattenEditor = new window.Quill(ziel, { modules: { toolbar: false } });
        entschaerfeFokus(schattenEditor.root);
    }
    schattenEditor.setContents(editor.getContents(index, laenge), 'silent');
    // tokens, not chips: NOOSEI must see what the field stores, and TextAssistService counts them
    const html = chipZuToken(schattenEditor.root);
    schattenEditor.setText('', 'silent'); // never hold a copy of the document
    return html;
}

// Base64 screenshots are the real size driver and the server never sends images to the model,
// so they are swapped for indices before marshalling and restored on apply. The index lives in a data
// attribute, not in src: src is a URI attribute, and the server sanitizer drops every scheme it does not
// know — which deleted the picture from the corrected document instead of preserving it.
function bilderAuslagern(zustand, html) {
    zustand.bilder = [];
    return html.replace(/<img\b[^>]*>/gi, (treffer) => {
        const quelle = /\ssrc\s*=\s*"([^"]*)"/i.exec(treffer);
        if (!quelle) {
            return treffer;
        }
        zustand.bilder.push(quelle[1]);
        return treffer.replace(quelle[0], ' data-noosei-bild="' + (zustand.bilder.length - 1) + '"');
    });
}

function bilderZurueck(zustand, html) {
    return html.replace(/\s*data-noosei-bild="(\d+)"/gi, (treffer, nummer) => {
        const quelle = zustand.bilder[Number(nummer)];
        return quelle ? ' src="' + quelle + '"' : '';
    });
}

// watches the caret for a trailing @query and reports it with the coordinates the picker renders at
function haengeErwaehnungAn(element, editor, zustand) {
    const melde = () => {
        zustand.timer = null;
        if (zustand.tot || !zustand.dotnetRef) {
            return;
        }
        const bereich = editor.getSelection();
        if (!bereich) {
            // no range means the editor lost focus — clicking a candidate does that, so leave the list standing
            return;
        }
        if (bereich.length > 0) {
            zustand.dotnetRef.invokeMethodAsync('OnMentionQuery', null, 0, 0, 0, 0).catch(() => { });
            return;
        }
        const von = Math.max(0, bereich.index - ERWAEHNUNG_FENSTER);
        const treffer = ERWAEHNUNG_ABFRAGE.exec(editor.getText(von, bereich.index - von));
        if (!treffer || treffer[1].length < 1) {
            zustand.dotnetRef.invokeMethodAsync('OnMentionQuery', null, 0, 0, 0, 0).catch(() => { });
            return;
        }
        const start = bereich.index - treffer[0].length;
        const masse = editor.getBounds(bereich.index);
        zustand.dotnetRef.invokeMethodAsync('OnMentionQuery', treffer[1], start, treffer[0].length,
            Math.round(masse.bottom + element.offsetTop), Math.round(masse.left + element.offsetLeft))
            .catch(() => { });
    };

    const anstossen = () => {
        if (zustand.timer) {
            clearTimeout(zustand.timer);
        }
        zustand.timer = setTimeout(melde, ERWAEHNUNG_VERZOEGERUNG);
    };
    editor.on('text-change', anstossen);
    editor.on('selection-change', anstossen);

    // capture on the container runs before quill's own keyboard module
    zustand.tasten = (ereignis) => {
        if (!zustand.offen || zustand.tot || !zustand.dotnetRef || !ERWAEHNUNG_TASTEN.includes(ereignis.key)) {
            return;
        }
        ereignis.preventDefault();
        ereignis.stopPropagation();
        zustand.dotnetRef.invokeMethodAsync('OnMentionKey', ereignis.key).catch(() => { });
    };
    element.addEventListener('keydown', zustand.tasten, true);
}

// slash menu: a "/" at the start of an empty line offers the block formats. Entirely client-side —
// the commands are static, so a Blazor round trip would only add latency to a keystroke.
const BEFEHLE = [
    { id: 'text', name: 'Text', beschreibung: 'Normaler Absatz', form: null, suche: ['text', 'absatz'] },
    { id: 'h1', name: 'Überschrift 1', beschreibung: 'Kapitel', form: 'header', wert: 1, suche: ['h1', 'überschrift 1'] },
    { id: 'h2', name: 'Überschrift 2', beschreibung: 'Abschnitt', form: 'header', wert: 2, suche: ['h2', 'überschrift 2'] },
    { id: 'h3', name: 'Überschrift 3', beschreibung: 'Unterabschnitt', form: 'header', wert: 3, suche: ['h3', 'überschrift 3'] },
    { id: 'liste', name: 'Aufzählung', beschreibung: 'Punkte ohne Reihenfolge', form: 'list', wert: 'bullet', suche: ['aufzählung', 'liste', 'punkte'] },
    { id: 'nummern', name: 'Nummerierte Liste', beschreibung: 'Schritte mit Reihenfolge', form: 'list', wert: 'ordered', suche: ['nummeriert', 'liste', '1.'] },
    { id: 'check', name: 'Checkliste', beschreibung: 'Abzuhakende Punkte', form: 'list', wert: 'unchecked', suche: ['checkliste', 'aufgabe', 'todo'] },
    { id: 'zitat', name: 'Zitat', beschreibung: 'Abgesetzter Auszug', form: 'blockquote', wert: true, suche: ['zitat', '>'] },
    { id: 'code', name: 'Codeblock', beschreibung: 'Feste Zeilenumbrüche, Monospace', form: 'code-block', wert: true, suche: ['code', 'codeblock'] },
    { id: 'hinweis', name: 'Hinweis-Kasten', beschreibung: 'Hervorgehobener Hinweis', form: 'kasten', wert: 'hinweis', suche: ['hinweis', 'kasten'] },
    { id: 'warnung', name: 'Warnung-Kasten', beschreibung: 'Hervorgehobene Warnung', form: 'kasten', wert: 'warnung', suche: ['warnung', 'kasten', 'achtung'] },
    { id: 'info', name: 'Info-Kasten', beschreibung: 'Hervorgehobene Information', form: 'kasten', wert: 'info', suche: ['info', 'kasten'] },
    { id: 'trenner', name: 'Trennlinie', beschreibung: 'Waagerechter Trenner', einfuegen: 'trenner', suche: ['trenner', 'linie', '---'] },
    { id: 'toc', name: 'Inhaltsverzeichnis', beschreibung: 'Liste der Überschriften', aktion: 'toc', suche: ['inhalt', 'verzeichnis', 'toc'] },
];

function haengeBefehlsMenueAn(element, editor, dotnetRef, bausteine) {
    const huelle = element.parentElement;
    if (!huelle) {
        return null;
    }
    const zustand = { tot: false, offen: false, index: 0, treffer: [], start: 0, laenge: 0, masse: null, tasten: null, dotnetRef, laeuft: false };
    // the agent's own phrases join the static block formats: same menu, same filter, one extra group
    const alle = BEFEHLE.concat((bausteine || []).map((b) => ({
        id: 'baustein:' + b.id,
        name: b.name,
        beschreibung: b.vorschau || 'Eigener Textbaustein',
        baustein: b.id,
        suche: [(b.name || '').toLowerCase()],
    })));
    element.__nooseBefehle = zustand;

    const panel = document.createElement('div');
    panel.className = 'noose-befehle';
    panel.setAttribute('role', 'listbox');
    panel.hidden = true;
    huelle.appendChild(panel);

    const schliessen = () => {
        zustand.offen = false;
        panel.hidden = true;
    };

    const zeigen = () => {
        zustand.index = Math.min(zustand.index, Math.max(0, zustand.treffer.length - 1));
        panel.innerHTML = '';
        zustand.treffer.forEach((befehl, i) => {
            const eintrag = document.createElement('div');
            eintrag.className = 'noose-befehl' + (i === zustand.index ? ' noose-befehl-aktiv' : '');
            eintrag.setAttribute('role', 'option');
            eintrag.setAttribute('aria-selected', i === zustand.index ? 'true' : 'false');
            const titel = document.createElement('strong');
            titel.textContent = befehl.name;
            const text = document.createElement('span');
            text.textContent = befehl.beschreibung;
            eintrag.appendChild(titel);
            eintrag.appendChild(text);
            eintrag.addEventListener('pointerdown', (ereignis) => {
                ereignis.preventDefault();
                anwenden(befehl);
            });
            panel.appendChild(eintrag);
        });
        if (zustand.masse) {
            panel.style.top = Math.round(zustand.masse.bottom + element.offsetTop) + 'px';
            panel.style.left = Math.round(zustand.masse.left + element.offsetLeft) + 'px';
        }
        panel.hidden = false;
        zustand.offen = true;
    };

    const anwenden = (befehl) => {
        if (!zustand.offen || !befehl) {
            return;
        }
        const start = zustand.start;
        const laenge = zustand.laenge;
        schliessen();
        if (befehl.baustein) {
            // .NET owns the text: the placeholders resolve there, and only there. The typed "/wort" stays
            // until the answer is in - a failed round trip would otherwise eat the line and insert nothing.
            if (zustand.dotnetRef && !zustand.laeuft) {
                zustand.laeuft = true;
                zustand.dotnetRef.invokeMethodAsync('OnSnippetRequested', befehl.baustein)
                    .then((html) => {
                        if (!html) {
                            return;
                        }
                        if (laenge > 0) {
                            editor.deleteText(start, laenge, 'user');
                        }
                        editor.setSelection(start, 0, 'silent');
                        fuegeHtmlEin(element, html);
                    })
                    .catch(() => { /* ignore */ })
                    .finally(() => { zustand.laeuft = false; });
            }
            return;
        }
        if (laenge > 0) {
            editor.deleteText(start, laenge, 'user');
        }
        if (befehl.einfuegen === 'trenner') {
            editor.insertEmbed(start, 'trenner', true, 'user');
            editor.setSelection(start + 1, 0, 'silent');
            editor.focus();
            return;
        }
        if (befehl.aktion === 'toc') {
            fordereInhaltsverzeichnisAn(element);
            return;
        }
        if (befehl.form === null) {
            // "plain paragraph" has to clear EVERY block format. Parchment only swaps a blot out when the
            // name matches it, so asking a blockquote or a code block to unset 'header' did nothing at all -
            // the entry ate the typed slash and left the line exactly as it was.
            for (const form of ['header', 'blockquote', 'code-block', 'list']) {
                editor.formatLine(start, 1, form, false);
            }
        } else {
            // a block choice replaces the list format, it never nests inside one
            if (befehl.form !== 'list') {
                editor.formatLine(start, 1, 'list', false);
            }
            editor.formatLine(start, 1, befehl.form, befehl.wert);
        }
        editor.setSelection(start, 0, 'silent');
        editor.focus();
    };

    const pruefen = () => {
        if (zustand.tot) {
            return;
        }
        const bereich = editor.getSelection();
        if (!bereich || bereich.length > 0 || bereich.index < 1) {
            schliessen();
            return;
        }
        const [zeile] = editor.getLine(bereich.index - 1);
        if (!zeile || istCodeblock(zeile)) {
            // a slash is literal inside a code block, and an open menu would swallow the Enter that is
            // supposed to start the next code line
            schliessen();
            return;
        }
        const start = editor.getIndex(zeile);
        const text = editor.getText(start, bereich.index - start);
        const treffer = /^\/([a-zA-ZäöüÄÖÜß]*)$/.exec(text);
        if (!treffer) {
            schliessen();
            return;
        }
        const suche = treffer[1].toLowerCase();
        const gefiltert = alle.filter((befehl) => befehl.suche.some((wort) => wort.startsWith(suche)));
        if (gefiltert.length === 0) {
            schliessen();
            return;
        }
        zustand.treffer = gefiltert;
        zustand.start = start;
        zustand.laenge = text.length;
        zustand.masse = editor.getBounds(bereich.index);
        zustand.index = 0;
        zeigen();
    };

    zustand.tasten = (ereignis) => {
        if (!zustand.offen || ['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].indexOf(ereignis.key) < 0) {
            return;
        }
        ereignis.preventDefault();
        ereignis.stopPropagation();
        if (ereignis.key === 'Escape') {
            schliessen();
            return;
        }
        if (ereignis.key === 'Enter' || ereignis.key === 'Tab') {
            anwenden(zustand.treffer[zustand.index]);
            return;
        }
        const schritt = ereignis.key === 'ArrowDown' ? 1 : -1;
        zustand.index = (zustand.index + schritt + zustand.treffer.length) % zustand.treffer.length;
        zeigen();
    };
    element.addEventListener('keydown', zustand.tasten, true);

    editor.on('text-change', pruefen);
    editor.on('selection-change', pruefen);
    return zustand;
}

/// tells the editor whether the picker is open, so it may claim the arrow keys
export function setzeErwaehnungOffen(element, offen) {
    const zustand = element && element.__nooseErwaehnung;
    if (zustand) {
        zustand.offen = !!offen;
    }
}

/// replaces the active @query with a mention chip; returns the fresh html
export function erwaehnungEinfuegen(element, start, laenge, token, beschriftung) {
    const editor = element && element.__nooseQuill;
    const zustand = element && element.__nooseErwaehnung;
    if (!editor || !zustand || zustand.tot) {
        return null;
    }
    zustand.offen = false;
    zustand.beschriftungen[token] = beschriftung;
    if (laenge > 0) {
        editor.deleteText(start, laenge, 'user');
    }
    editor.insertEmbed(start, ERWAEHNUNG_BLOT, { token, beschriftung }, 'user');
    editor.insertText(start + 1, ' ', 'user');
    editor.setSelection(start + 2, 0, 'silent');
    editor.focus();
    return leseHtml(editor);
}

let komfortSymboleRegistriert = false;

function registriereKomfortSymbole() {
    if (komfortSymboleRegistriert || !window.Quill) {
        return;
    }
    const symbole = window.Quill.import('ui/icons');
    symbole['noose-suchen'] = '<svg viewBox="0 0 18 18"><circle class="ql-stroke" cx="7.5" cy="7.5" r="4.5" fill="none"/><line class="ql-stroke" x1="11" y1="11" x2="15.5" y2="15.5"/></svg>';
    symbole['noose-vollbild'] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" d="M3 7V3h4M11 3h4v4M15 11v4h-4M7 15H3v-4"/></svg>';
    symbole['noose-kasten-hinweis'] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" d="M3 4h12v8H9l-3 3v-3H3z"/></svg>';
    symbole['noose-kasten-warnung'] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" d="M9 3l6.5 11.5h-13z"/><line class="ql-stroke" x1="9" y1="8" x2="9" y2="11"/><line class="ql-stroke" x1="9" y1="12.4" x2="9" y2="13.1"/></svg>';
    symbole['noose-kasten-info'] = '<svg viewBox="0 0 18 18"><circle class="ql-stroke" cx="9" cy="9" r="6" fill="none"/><line class="ql-stroke" x1="9" y1="8" x2="9" y2="12.4"/><line class="ql-stroke" x1="9" y1="5.4" x2="9" y2="6.1"/></svg>';
    symbole['noose-trenner'] = '<svg viewBox="0 0 18 18"><line class="ql-stroke" x1="2.5" y1="9" x2="15.5" y2="9"/><circle class="ql-fill" cx="9" cy="6" r="1"/><circle class="ql-fill" cx="9" cy="12" r="1"/></svg>';
    symbole['noose-toc'] = '<svg viewBox="0 0 18 18"><line class="ql-stroke" x1="3" y1="5" x2="15" y2="5"/><line class="ql-stroke" x1="6" y1="9" x2="15" y2="9"/><line class="ql-stroke" x1="9" y1="13" x2="15" y2="13"/></svg>';
    symbole['noose-rueckgaengig'] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" d="M7 5L3 9l4 4"/><path class="ql-stroke" fill="none" d="M3 9h7a5 5 0 0 1 0 10"/></svg>';
    symbole['noose-wiederholen'] = '<svg viewBox="0 0 18 18"><path class="ql-stroke" fill="none" d="M11 5l4 4-4 4"/><path class="ql-stroke" fill="none" d="M15 9H8a5 5 0 0 0 0 10"/></svg>';
    komfortSymboleRegistriert = true;
}

// find and replace over the editor text. Replacing works on real text only and skips matches that sit
// inside an embed (a mention chip), so a sweep can never dissolve a stored @{Typ:Id} token.
function haengeSuchenAn(element) {
    const huelle = element.parentElement;
    if (!huelle) {
        return null;
    }
    const zustand = { offen: false, treffer: [], aktuell: -1, quelle: null, feld: null, tasten: null };
    element.__nooseSuchen = zustand;

    const panel = document.createElement('div');
    panel.className = 'noose-suchen noose-suchen-versteckt';
    const oberste = document.createElement('div');
    oberste.className = 'noose-suchen-zeile';
    const feld = document.createElement('input');
    feld.type = 'text';
    feld.placeholder = 'Suchen';
    feld.className = 'noose-suchen-feld';
    const ersatzfeld = document.createElement('input');
    ersatzfeld.type = 'text';
    ersatzfeld.placeholder = 'Ersetzen';
    ersatzfeld.className = 'noose-suchen-feld';
    const zaehler = document.createElement('span');
    zaehler.className = 'noose-suchen-zaehler';
    const weitere = document.createElement('div');
    weitere.className = 'noose-suchen-zeile';
    const knopf = (titel, beschriftung, aktion) => {
        const b = document.createElement('button');
        b.type = 'button';
        b.title = titel;
        b.textContent = beschriftung;
        b.addEventListener('pointerdown', (ereignis) => {
            ereignis.preventDefault();
            aktion();
        });
        weitere.appendChild(b);
        return b;
    };
    knopf('Vorheriger Treffer', 'Zurück', () => springen(-1));
    knopf('Nächster Treffer', 'Weiter', () => springen(1));
    knopf('Diesen Treffer ersetzen', 'Ersetzen', () => ersetzeAktuell());
    knopf('Alle Treffer ersetzen', 'Alle ersetzen', () => ersetzeAlle());
    knopf('Schließen (Esc)', 'Schließen', () => schliessen());
    oberste.appendChild(feld);
    oberste.appendChild(ersatzfeld);
    oberste.appendChild(zaehler);
    panel.appendChild(oberste);
    panel.appendChild(weitere);
    huelle.appendChild(panel);

    const editor = () => element.__nooseQuill;

    // Haystack in QUILL's index space, not getText()'s. getText() drops embeds outright while each embed
    // occupies exactly one position in the document, so a hit found in the former and deleted in the latter
    // is off by the number of preceding embeds - it eats neighbouring characters and can dissolve a stored
    // @{Typ:Id} token. One placeholder per embed keeps string offset and Quill index the same number; the
    // placeholder is a NUL, which no needle from the search field can ever contain, so a hit also cannot
    // overlap an embed in the first place.
    const dokumentText = () => {
        const ops = (editor().getContents().ops) || [];
        let text = '';
        for (const op of ops) {
            text += typeof op.insert === 'string' ? op.insert : '\u0000';
        }
        return text;
    };

    // second line of defence: a match overlapping an embed is never rewritten
    const beruehrtEmbed = (bereich) => {
        return editor().getContents(bereich.index, bereich.length).ops.some((op) => typeof op.insert !== 'string');
    };

    const anzeigen = () => {
        zaehler.textContent = zustand.treffer.length === 0
            ? '0 Treffer'
            : (zustand.aktuell + 1) + '/' + zustand.treffer.length;
        if (zustand.aktuell < 0) {
            return;
        }
        const bereich = zustand.treffer[zustand.aktuell];
        editor().setSelection(bereich.index, bereich.length, 'silent');
        const [zeile] = editor().getLine(bereich.index);
        if (zeile && zeile.domNode && zeile.domNode.scrollIntoView) {
            zeile.domNode.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
    };

    // ab < 0 keeps the current hit, otherwise searches forward from that index
    const neuSuchen = (ab) => {
        if (!editor()) {
            return;
        }
        const quelle = dokumentText();
        zustand.quelle = quelle;
        const nadel = feld.value;
        const vorher = ab < 0 && zustand.aktuell >= 0 && zustand.treffer[zustand.aktuell]
            ? zustand.treffer[zustand.aktuell].index
            : ab;
        zustand.treffer = [];
        if (nadel) {
            const klein = quelle.toLowerCase();
            const gesucht = nadel.toLowerCase();
            let pos = 0;
            while ((pos = klein.indexOf(gesucht, pos)) >= 0) {
                zustand.treffer.push({ index: pos, length: nadel.length });
                pos += Math.max(1, nadel.length);
            }
        }
        zustand.aktuell = -1;
        for (let i = 0; i < zustand.treffer.length; i++) {
            if (zustand.treffer[i].index >= Math.max(0, vorher)) {
                zustand.aktuell = i;
                break;
            }
        }
        if (zustand.aktuell < 0 && zustand.treffer.length > 0) {
            zustand.aktuell = 0;
        }
        anzeigen();
    };

    // a match set only stays valid while the document is untouched - same index space as neuSuchen
    const veraltet = () => zustand.quelle !== null && zustand.quelle !== dokumentText();

    const springen = (richtung) => {
        if (zustand.treffer.length === 0 || !editor()) {
            return;
        }
        zustand.aktuell = (zustand.aktuell + richtung + zustand.treffer.length) % zustand.treffer.length;
        anzeigen();
    };

    const ersetzeAktuell = () => {
        if (zustand.aktuell < 0 || !editor()) {
            return;
        }
        if (veraltet()) {
            // someone kept typing: rebuild the hit list, the next click replaces
            neuSuchen(0);
            return;
        }
        const bereich = zustand.treffer[zustand.aktuell];
        if (beruehrtEmbed(bereich)) {
            springen(1);
            return;
        }
        editor().deleteText(bereich.index, bereich.length, 'user');
        if (ersatzfeld.value) {
            editor().insertText(bereich.index, ersatzfeld.value, 'user');
        }
        neuSuchen(bereich.index);
    };

    const ersetzeAlle = () => {
        if (!editor()) {
            return;
        }
        if (veraltet()) {
            neuSuchen(0);
        }
        const bereiche = zustand.treffer.slice().reverse();
        for (const bereich of bereiche) {
            if (beruehrtEmbed(bereich)) {
                continue;
            }
            editor().deleteText(bereich.index, bereich.length, 'user');
            if (ersatzfeld.value) {
                editor().insertText(bereich.index, ersatzfeld.value, 'user');
            }
        }
        neuSuchen(0);
    };

    const schliessen = () => {
        zustand.offen = false;
        panel.classList.add('noose-suchen-versteckt');
        if (editor()) {
            editor().focus();
        }
    };

    const oeffnen = () => {
        zustand.offen = true;
        panel.classList.remove('noose-suchen-versteckt');
        feld.focus();
        feld.select();
        neuSuchen(0);
    };
    zustand.oeffnen = oeffnen;

    feld.addEventListener('input', () => neuSuchen(0));
    feld.addEventListener('keydown', (ereignis) => {
        if (ereignis.key === 'Enter') {
            ereignis.preventDefault();
            springen(ereignis.shiftKey ? -1 : 1);
        } else if (ereignis.key === 'Escape') {
            ereignis.preventDefault();
            schliessen();
        }
    });

    zustand.tasten = (ereignis) => {
        if ((ereignis.ctrlKey || ereignis.metaKey) && (ereignis.key === 'f' || ereignis.key === 'F')) {
            ereignis.preventDefault();
            ereignis.stopPropagation();
            oeffnen();
        }
    };
    element.addEventListener('keydown', zustand.tasten, true);
    return zustand;
}

// ---- draft recovery (IndexedDB) ----
// Unsaved text is kept in the browser so a broken circuit or an accidental navigation cannot swallow a long
// report. The store, the sweep and the logout wipe live in entwurf.js, shared with the plain text fields.

// key = agent + scope, supplied by the component; the plain text never lands in the key
function haengeEntwurfAn(element, editor, dotnetRef, schluessel) {
    if (!schluessel) {
        return null;
    }
    // angeboten: the html on offer while the banner is up; the first keystroke answers it
    const zustand = { schluessel, basis: leseHtml(editor), schreiben: null, tot: false, angeboten: null };
    element.__nooseEntwurf = zustand;

    zustand.geprueft = ladeEntwurfsDb().then(async (db) => {
        if (!db || zustand.tot) {
            return;
        }
        entwurfAufraeumen(db);
        const eintrag = await entwurfLesen(db, schluessel);
        if (!eintrag || zustand.tot) {
            return;
        }
        if (istAbgelaufen(eintrag)) {
            entwurfLoeschen(db, schluessel);
            return;
        }
        const aktuell = leseHtml(editor);
        // the text on screen or the saved state itself (a drop that never arrived): nothing to recover
        if (eintrag.html === aktuell || eintrag.html === zustand.basis) {
            entwurfLoeschen(db, schluessel);
            return;
        }
        // Offered even when the first keystrokes came before this read: holding it back let the pending write
        // overwrite a draft nobody had seen. That write waits for the answer instead.
        if (zustand.schreiben) {
            clearTimeout(zustand.schreiben);
            zustand.schreiben = 0;
        }
        abmelden(zustand);
        zustand.angeboten = eintrag.html;
        dotnetRef.invokeMethodAsync('OnDraftFound', eintrag.zeit).catch(() => { });
    }).catch(() => { });

    // Named, and hung on the state, so teardown can still flush a write that is only waiting out its debounce.
    // Cancelling it outright lost exactly the last sentence somebody typed before clicking away - the one case
    // the draft exists for. Deliberately without a "tot" check: by then the editor is already gone.
    zustand.jetztSchreiben = async () => {
        // after the check: a write that ran first would overwrite the stored draft before anyone saw it
        await zustand.geprueft;
        if (zustand.angeboten !== null) {
            // the offer pauses writing; the answer to it writes again
            return;
        }
        const db = await ladeEntwurfsDb();
        if (!db) {
            return;
        }
        const html = leseHtml(editor);
        if (html === zustand.basis || html.length === 0) {
            // back to the saved state (or still empty): nothing worth recovering
            await entwurfLoeschen(db, schluessel);
            dotnetRef.invokeMethodAsync('OnDraftSaved', 0).catch(() => { });
            return;
        }
        const eintrag = { schluessel, html, zeit: Date.now() };
        await entwurfSchreiben(db, eintrag);
        dotnetRef.invokeMethodAsync('OnDraftSaved', eintrag.zeit).catch(() => { });
    };

    // closing the tab or reloading must not cost the keystrokes still waiting out the debounce
    zustand.sofort = () => {
        abmelden(zustand);
        if (!zustand.schreiben) {
            return;
        }
        clearTimeout(zustand.schreiben);
        zustand.schreiben = 0;
        zustand.jetztSchreiben().catch(() => { });
    };

    editor.on('text-change', (delta, vorher, quelle) => {
        if (zustand.tot) {
            return;
        }
        // Typing past the offer answers it: the stored text is about to be overwritten, and the buttons would
        // then restore what was just typed while the offered draft is already gone. A change from the page
        // itself - a template, a reset - answers nothing, so it must not overwrite the draft still on offer.
        if (zustand.angeboten !== null) {
            if (quelle !== 'user') {
                return;
            }
            zustand.angeboten = null;
            dotnetRef.invokeMethodAsync('OnDraftFound', 0).catch(() => { });
        }
        if (zustand.schreiben) {
            clearTimeout(zustand.schreiben);
        }
        vormerken(zustand);
        zustand.schreiben = setTimeout(() => {
            zustand.schreiben = 0;
            abmelden(zustand);
            if (zustand.tot) {
                return;
            }
            zustand.jetztSchreiben().catch(() => { });
        }, ENTWURF_VERZOEGERUNG);
    });

    return zustand;
}

/// applies the offered draft; returns the fresh html or null when there is no offer (any more)
export async function entwurfAnwenden(element) {
    const editor = element && element.__nooseQuill;
    const zustand = element && element.__nooseEntwurf;
    if (!editor || !zustand) {
        return null;
    }
    // exactly what was offered: the store may hold something newer by now - another tab, or the keystrokes
    // that answered the offer a moment before this click arrived
    const html = zustand.angeboten;
    zustand.angeboten = null;
    if (html === null) {
        return null;
    }
    const beschriftungen = element.__nooseErwaehnung ? element.__nooseErwaehnung.beschriftungen : null;
    editor.setText('');
    editor.clipboard.dangerouslyPasteHTML(tokenZuChip(html, beschriftungen));
    // the baseline stays the server content: until the next save the restored text is unsaved and
    // must keep autosaving, otherwise a second disconnect would swallow it after all
    editor.focus();
    return leseHtml(editor);
}

export async function entwurfVerwerfen(element) {
    const zustand = element && element.__nooseEntwurf;
    if (!zustand) {
        return;
    }
    zustand.angeboten = null;
    const db = await ladeEntwurfsDb();
    if (db) {
        await entwurfLoeschen(db, zustand.schluessel);
    }
    // what was typed before the offer came is unsaved too, and the offer held its write back
    const editor = element.__nooseQuill;
    if (editor && !zustand.tot && leseHtml(editor).length > 0 && leseHtml(editor) !== zustand.basis) {
        zustand.jetztSchreiben().catch(() => { });
    }
}

// Drops one draft by its key, without the editor that wrote it. A dialog closes before its caller stores the
// row, so the editor is already gone by the time the save is known to have worked - and marking saved inside
// the dialog would throw the draft away on a failed save, exactly when it is the only copy left.
export async function entwurfVerwerfenNachSchluessel(schluessel) {
    if (!schluessel) {
        return;
    }
    const db = await ladeEntwurfsDb();
    if (db) {
        await entwurfLoeschen(db, schluessel);
    }
}

// called by the page after a successful save or send: the draft may go, the current html is the baseline
export async function entwurfAlsGespeichertMarkieren(element) {
    const editor = element && element.__nooseQuill;
    const zustand = element && element.__nooseEntwurf;
    if (!zustand) {
        return;
    }
    // The saved state is what the page read for its save, not what the editor shows once the answer is back:
    // whatever was typed during the round trip is still unsaved and stays a draft.
    const gespeichert = zustand.gelesen !== null && zustand.gelesen !== undefined
        ? zustand.gelesen
        : (editor ? leseHtml(editor) : zustand.basis);
    zustand.gelesen = null;
    zustand.angeboten = null;
    zustand.basis = gespeichert;
    if (editor && leseHtml(editor) !== gespeichert) {
        // typed on meanwhile: the pending write (or a fresh one) keeps the newer text
        if (!zustand.schreiben) {
            zustand.jetztSchreiben().catch(() => { });
        }
        return;
    }
    // baseline first and the pending write gone: a write landing during the delete would bring the draft back
    if (zustand.schreiben) {
        clearTimeout(zustand.schreiben);
        zustand.schreiben = 0;
    }
    abmelden(zustand);
    const db = await ladeEntwurfsDb();
    if (db) {
        await entwurfLoeschen(db, zustand.schluessel);
    }
}

// fullscreen writing mode; native fullscreen first (a dialog ancestor carries transform:scale(1), which
// would anchor a fixed overlay to the dialog instead of the viewport), CSS overlay as fallback
function haengeVollbildAn(element) {
    const huelle = element.parentElement;
    if (!huelle) {
        return null;
    }
    const zustand = { aktiv: false, nativ: false, tasten: null, wechsel: null };
    element.__nooseVollbild = zustand;

    const knopfMarkieren = (aktiv) => {
        const toolbar = element.__nooseQuill ? element.__nooseQuill.getModule('toolbar') : null;
        const knopf = toolbar ? toolbar.container.querySelector('.ql-noose-vollbild') : null;
        if (knopf) {
            knopf.classList.toggle('ql-active', aktiv);
        }
    };

    const setzen = (aktiv) => {
        zustand.aktiv = aktiv;
        huelle.classList.toggle('noose-rte-vollbild', aktiv);
        knopfMarkieren(aktiv);
        try {
            if (aktiv && typeof huelle.requestFullscreen === 'function') {
                const versprechen = huelle.requestFullscreen();
                if (versprechen && versprechen.catch) {
                    versprechen.catch(() => { /* the CSS overlay carries on */ });
                }
            } else if (!aktiv && document.fullscreenElement === huelle) {
                document.exitFullscreen().catch(() => { });
            }
        } catch (e) {
            /* CSS overlay only */
        }
    };
    zustand.umschalten = () => setzen(!zustand.aktiv);

    // native Escape leaves fullscreen without our key handler; keep class and button in step
    zustand.wechsel = () => {
        if (document.fullscreenElement === huelle) {
            zustand.nativ = true;
            return;
        }
        if (zustand.nativ && zustand.aktiv) {
            zustand.nativ = false;
            setzen(false);
        }
    };
    document.addEventListener('fullscreenchange', zustand.wechsel);

    zustand.tasten = (ereignis) => {
        if (zustand.aktiv && ereignis.key === 'Escape') {
            ereignis.preventDefault();
            ereignis.stopPropagation();
            setzen(false);
        }
    };
    element.addEventListener('keydown', zustand.tasten, true);
    return zustand;
}

// True for the one blot whose content is meant to stay literal. Quill keeps a whole code block in a single
// blot, so its first line looks exactly like the start of an empty paragraph to every caret-based check.
function istCodeblock(zeile) {
    return !!zeile && !!zeile.statics && zeile.statics.blotName === 'code-block';
}

// markdown-style block markers: typed "## " or "- " at the start of a line turn into the block format.
// text-change fires after the marker is in the document, so the marker is removed again right away —
// a keyboard-binding return value differs between quill versions, this path is stable.
function haengeBlockKuerzelAn(editor) {
    const kuerzel = [
        { muster: /^(#{1,3}) $/, form: 'header', wert: (treffer) => treffer[1].length },
        { muster: /^(-|\*) $/, form: 'list', wert: () => 'bullet' },
        { muster: /^1\. $/, form: 'list', wert: () => 'ordered' },
        { muster: /^\[\] $/, form: 'list', wert: () => 'unchecked' },
        { muster: /^> $/, form: 'blockquote', wert: () => true },
    ];
    let umwandelt = false;
    editor.on('text-change', (delta, alt, quelle) => {
        if (umwandelt || quelle !== 'user') {
            return;
        }
        const bereich = editor.getSelection();
        if (!bereich || bereich.length > 0 || bereich.index < 2) {
            return;
        }
        const [zeile] = editor.getLine(bereich.index - 1);
        if (!zeile || istCodeblock(zeile)) {
            // inside a code block "#", "-" and ">" are literal content; converting them eats the typed
            // characters and lifts the line out of the block the author just opened
            return;
        }
        const start = editor.getIndex(zeile);
        const text = editor.getText(start, bereich.index - start);
        for (const regel of kuerzel) {
            const treffer = regel.muster.exec(text);
            if (!treffer) {
                continue;
            }
            umwandelt = true;
            editor.deleteText(start, treffer[0].length, 'user');
            // a block marker replaces the list format instead of nesting inside it
            if (regel.form !== 'list') {
                editor.formatLine(start, 1, 'list', false);
            }
            editor.formatLine(start, 1, regel.form, regel.wert(treffer));
            editor.setSelection(start, 0, 'silent');
            umwandelt = false;
            break;
        }
    });
}

function setzeKiBeschaeftigt(element, beschaeftigt) {
    if (element) {
        element.classList.toggle('noose-rte-ki-laeuft', !!beschaeftigt);
    }
}

function meldeKi(element, zustand, modus) {
    const editor = element.__nooseQuill;
    if (!editor || zustand.tot || zustand.laeuft) {
        return;
    }

    // toolbar attach() already focused the editor, so the range is valid here; once the dialog opens
    // getSelection() returns null, which is why index and length are captured now and handed back on apply
    const bereich = editor.getSelection();
    const index = bereich && bereich.length > 0 ? bereich.index : -1;
    const laenge = bereich && bereich.length > 0 ? bereich.length : 0;

    const nutzlast = index < 0 ? leseHtml(editor) : htmlAusBereich(editor, index, laenge);
    const leicht = bilderAuslagern(zustand, nutzlast || '');
    if (leicht.length > MAX_KI_ZEICHEN) {
        zustand.dotnetRef.invokeMethodAsync('OnAiTooLarge').catch(() => { });
        return;
    }

    zustand.laeuft = true;
    setzeKiBeschaeftigt(element, true);
    zustand.dotnetRef
        .invokeMethodAsync('OnAiRequested', modus, leicht, index, laenge, editor.getLength())
        .catch(() => {
            zustand.laeuft = false;
            setzeKiBeschaeftigt(element, false);
        });
}

// caption line of a picture: a normal paragraph with a marker class, folded into figcaption on save
function registriereBildtext() {
    if (bildtextRegistriert || !window.Quill) {
        return;
    }
    const Block = window.Quill.import('blots/block');
    class BildtextBlot extends Block {
        static formats(knoten) {
            return knoten.classList.contains('noose-bildtext') ? true : undefined;
        }
        format(name, wert) {
            if (name === 'bildtext') {
                this.domNode.classList.toggle('noose-bildtext', !!wert);
            } else {
                super.format(name, wert);
            }
        }
    }
    BildtextBlot.blotName = 'bildtext';
    BildtextBlot.className = 'noose-bildtext';
    window.Quill.register(BildtextBlot, true);
    bildtextRegistriert = true;
}

const KASTEN_ARTEN = ['hinweis', 'warnung', 'info'];

// callout line: a class on the paragraph, no new tag, so the sanitizer needs no entry
function registriereKaesten() {
    if (kastenRegistriert || !window.Quill) {
        return;
    }
    const Block = window.Quill.import('blots/block');
    class KastenBlot extends Block {
        static formats(knoten) {
            const treffer = new RegExp('noose-kasten-(' + KASTEN_ARTEN.join('|') + ')').exec(knoten.className);
            return treffer ? treffer[1] : undefined;
        }
        format(name, wert) {
            if (name !== 'kasten') {
                super.format(name, wert);
                return;
            }
            for (const art of KASTEN_ARTEN) {
                this.domNode.classList.remove('noose-kasten-' + art);
            }
            this.domNode.classList.toggle('noose-kasten', !!wert);
            if (wert) {
                this.domNode.classList.add('noose-kasten-' + wert);
            }
        }
    }
    KastenBlot.blotName = 'kasten';
    KastenBlot.className = 'noose-kasten';
    window.Quill.register(KastenBlot, true);
    kastenRegistriert = true;
}

// Depth of a table-of-contents entry, as a class Quill is willing to carry.
// clipboard.convert() keeps only formats Parchment knows: the level classes of the inserted list were
// dropped on the way in, so every entry came out flat and the CSS written for them never matched.
// A class attributor reads the trailing segment of "noose-toc-2" as its value, which is exactly the level.
let tocKlasseRegistriert = false;

function registriereTocKlasse() {
    if (tocKlasseRegistriert || !window.Quill) {
        return;
    }
    const Parchment = window.Quill.import('parchment');
    const TocEbene = new Parchment.Attributor.Class('tocebene', 'noose-toc', {
        scope: Parchment.Scope.BLOCK,
        whitelist: ['1', '2', '3'],
    });
    window.Quill.register(TocEbene, true);
    tocKlasseRegistriert = true;
}

// divider as an atomic block embed
function registriereTrenner() {
    if (trennerRegistriert || !window.Quill) {
        return;
    }
    const BlockEmbed = window.Quill.import('blots/block/embed');
    class TrennerBlot extends BlockEmbed {
        static value() {
            return true;
        }
    }
    TrennerBlot.blotName = 'trenner';
    TrennerBlot.tagName = 'HR';
    window.Quill.register(TrennerBlot, true);
    trennerRegistriert = true;
}

function schalteKasten(element, art) {
    const editor = element && element.__nooseQuill;
    if (!editor) {
        return;
    }
    const bereich = editor.getSelection(true);
    const aktuell = editor.getFormat(bereich.index).kasten;
    editor.formatLine(bereich.index, Math.max(1, bereich.length), 'kasten', aktuell === art ? false : art, 'user');
    editor.focus();
}

function einfuegenTrenner(element) {
    const editor = element && element.__nooseQuill;
    if (!editor) {
        return;
    }
    const bereich = editor.getSelection(true);
    editor.insertEmbed(bereich.index, 'trenner', true, 'user');
    editor.setSelection(bereich.index + 1, 0, 'silent');
    editor.focus();
}

// headings. The ids are assigned on save; the returned markup links to those slugs.
function fordereInhaltsverzeichnisAn(element) {
    const editor = element && element.__nooseQuill;
    const zustand = element && element.__nooseInhalt;
    if (!editor || !zustand || zustand.tot || !zustand.dotnetRef || zustand.laeuft) {
        return;
    }
    const eintraege = Array.from(editor.root.querySelectorAll('h1, h2, h3'))
        .map((knoten) => ({ level: Number(knoten.tagName.substring(1)), text: (knoten.textContent || '').trim() }))
        .filter((eintrag) => eintrag.text.length > 0);
    if (eintraege.length === 0) {
        return;
    }
    zustand.laeuft = true;
    zustand.dotnetRef.invokeMethodAsync('OnTocRequested', eintraege)
        .then((html) => {
            if (html) {
                setInhaltsverzeichnis(element, html);
            }
        })
        .catch(() => { /* ignore */ })
        .finally(() => {
            zustand.laeuft = false;
        });
}

// inserts markup built by .NET at the caret; the table of contents and a text snippet both land here
function fuegeHtmlEin(element, html) {
    const editor = element && element.__nooseQuill;
    if (!editor || !html) {
        return;
    }
    const Delta = window.Quill.import('delta');
    const bereich = editor.getSelection(true);
    const eingefuegt = editor.clipboard.convert(html);
    editor.updateContents(new Delta().retain(bereich.index).concat(eingefuegt), 'user');
    editor.setSelection(bereich.index + eingefuegt.length(), 0, 'silent');
    editor.focus();
}

// inserts the list built by .NET at the caret
export function setInhaltsverzeichnis(element, html) {
    fuegeHtmlEin(element, html);
}

// the line after the given one, or null at the document end
function naechsteZeile(editor, zeile) {
    if (!zeile) {
        return null;
    }
    const ende = editor.getIndex(zeile) + zeile.length();
    if (ende >= editor.getLength() - 1) {
        return null;
    }
    const [naechste] = editor.getLine(ende);
    return naechste || null;
}

function zeilenText(editor, zeile) {
    return zeile ? editor.getText(editor.getIndex(zeile), Math.max(0, zeile.length() - 1)).trim() : '';
}

function istBildtext(zeile) {
    return !!(zeile && zeile.domNode && zeile.domNode.classList.contains('noose-bildtext'));
}

function liesBildtext(editor, index) {
    const [zeile] = editor.getLine(index);
    const naechste = naechsteZeile(editor, zeile);
    return istBildtext(naechste) ? zeilenText(editor, naechste) : '';
}

// writes or removes the caption line below an image line
function setzeBildtext(editor, index, text, ausrichtung) {
    const [zeile] = editor.getLine(index);
    const naechste = naechsteZeile(editor, zeile);
    const vorhanden = istBildtext(naechste);
    const neu = (text || '').trim();
    if (!vorhanden && !neu) {
        return;
    }
    if (vorhanden) {
        const start = editor.getIndex(naechste);
        if (!neu) {
            const laenge = naechste.length();
            editor.deleteText(start, laenge, 'user');
            // the empty line stays behind: drop its newline too when something follows it
            if (editor.getLength() > start + 1 && editor.getText(start, 1) === '\n') {
                editor.deleteText(start, 1, 'user');
            }
            return;
        }
        editor.deleteText(start, Math.max(0, naechste.length() - 1), 'user');
        editor.insertText(start, neu, 'user');
        editor.formatLine(start, neu.length, 'bildtext', true, 'user');
        editor.formatLine(start, neu.length, 'align', ausrichtung || false, 'user');
        return;
    }
    const ende = zeile ? editor.getIndex(zeile) + zeile.length() : index + 1;
    editor.insertText(ende, neu + '\n', 'user');
    editor.formatLine(ende, neu.length, 'bildtext', true, 'user');
    editor.formatLine(ende, neu.length, 'align', ausrichtung || false, 'user');
}

// click on a picture reports its current format to .NET, which opens the format dialog
function haengeBildBearbeitungAn(element, editor) {
    element.addEventListener('click', (ereignis) => {
        if (!(ereignis.target instanceof HTMLImageElement)) {
            return;
        }
        const zustand = element.__nooseBildOptionen;
        if (!zustand || zustand.tot || !zustand.dotnetRef) {
            return;
        }
        const blot = window.Quill.find(ereignis.target);
        if (!blot) {
            return;
        }
        const index = editor.getIndex(blot);
        const [zeile] = editor.getLine(index);
        const klasse = zeile && zeile.domNode ? zeile.domNode.className : '';
        const ausrichtung = /ql-align-(center|right|justify)/.exec(klasse);
        zustand.dotnetRef
            .invokeMethodAsync('OnImageClicked', index,
                ereignis.target.getAttribute('alt') || '',
                ereignis.target.getAttribute('width') || '',
                ausrichtung ? ausrichtung[1] : '',
                liesBildtext(editor, index))
            .catch(() => { /* ignore */ });
    });
}

// applies the dialog result to the picture at that index
export function setBildOptionen(element, index, optionen) {
    const editor = element && element.__nooseQuill;
    if (!editor || index < 0 || index >= editor.getLength()) {
        return;
    }
    const breite = optionen.width || false;
    const ausrichtung = optionen.alignment || false;
    if (optionen.remove) {
        setzeBildtext(editor, index, '', null);
        editor.deleteText(index, 1, 'user');
        return;
    }
    editor.formatText(index, 1, 'width', breite, 'user');
    editor.formatText(index, 1, 'alt', optionen.alt || false, 'user');
    editor.formatLine(index, 1, 'align', ausrichtung, 'user');
    setzeBildtext(editor, index, optionen.caption || '', optionen.alignment || null);
    editor.focus();
}

// Ctrl+K opens the link tooltip, Ctrl+S asks the page to save
function haengeTastenkuerzelAn(editor, dotnetRef) {
    editor.keyboard.addBinding({ key: 'k', shortKey: true }, () => {
        const bereich = editor.getSelection(true);
        const text = bereich.length > 0 ? editor.getText(bereich.index, bereich.length).trim() : '';
        try {
            if (editor.theme && editor.theme.tooltip) {
                editor.theme.tooltip.edit('link', /^https?:/i.test(text) ? text : '');
                editor.theme.tooltip.show();
            }
        } catch (e) {
            /* ignore */
        }
        return false;
    });
    editor.keyboard.addBinding({ key: 's', shortKey: true }, () => {
        dotnetRef.invokeMethodAsync('OnSaveShortcut').catch(() => { /* ignore */ });
        return false;
    });
}

// floating mini bar over a selection: the formats one reaches for without leaving the text
function haengeAuswahlBlaseAn(element, editor) {
    const huelle = element.parentElement;
    if (!huelle) {
        return null;
    }
    const zustand = { tot: false, index: 0, laenge: 0 };
    element.__nooseAuswahl = zustand;

    const blase = document.createElement('div');
    blase.className = 'noose-auswahl';
    blase.hidden = true;
    huelle.appendChild(blase);

    const verstecken = () => {
        blase.hidden = true;
    };

    const knoepfe = [];
    const knopf = (format, titel, inhalt) => {
        const knopfElement = document.createElement('button');
        knopfElement.type = 'button';
        knopfElement.title = titel;
        knopfElement.setAttribute('aria-label', titel);
        knopfElement.textContent = inhalt;
        knopfElement.addEventListener('pointerdown', (ereignis) => {
            ereignis.preventDefault();
            // over the whole selection, exactly as the button's own active state is read below. Asking only at
            // the start inverted the visible state whenever the selection was mixed: "Hello World" with the
            // first word bold showed the button off, and a click then removed the bold instead of adding it.
            const aktiv = !!editor.getFormat(zustand.index, zustand.laenge)[format];
            editor.format(format, !aktiv, 'user');
            aktualisieren();
        });
        blase.appendChild(knopfElement);
        knoepfe.push([knopfElement, format]);
    };
    knopf('bold', 'Fett', 'B');
    knopf('italic', 'Kursiv', 'I');
    knopf('underline', 'Unterstrichen', 'U');
    knopf('strike', 'Durchgestrichen', 'S');

    const link = document.createElement('button');
    link.type = 'button';
    link.title = 'Link';
    link.setAttribute('aria-label', 'Link einfügen');
    link.textContent = 'Link';
    link.addEventListener('pointerdown', (ereignis) => {
        ereignis.preventDefault();
        try {
            if (editor.theme && editor.theme.tooltip) {
                editor.theme.tooltip.edit('link', '');
                editor.theme.tooltip.show();
            }
        } catch (e) {
            /* ignore */
        }
    });
    blase.appendChild(link);

    const klar = document.createElement('button');
    klar.type = 'button';
    klar.title = 'Formatierung entfernen';
    klar.setAttribute('aria-label', 'Formatierung entfernen');
    klar.textContent = 'Klar';
    klar.addEventListener('pointerdown', (ereignis) => {
        ereignis.preventDefault();
        editor.removeFormat(zustand.index, zustand.laenge, 'user');
        aktualisieren();
    });
    blase.appendChild(klar);

    const aktualisieren = () => {
        if (zustand.tot) {
            return;
        }
        const bereich = editor.getSelection();
        if (!bereich || bereich.length === 0) {
            verstecken();
            return;
        }
        zustand.index = bereich.index;
        zustand.laenge = bereich.length;
        const masse = editor.getBounds(bereich.index, bereich.length);
        blase.hidden = false;
        const breite = blase.offsetWidth || 180;
        const hoehe = blase.offsetHeight || 30;
        blase.style.top = Math.max(0, Math.round(masse.top + element.offsetTop - hoehe - 8)) + 'px';
        blase.style.left = Math.max(0, Math.round(masse.left + element.offsetLeft + masse.width / 2 - breite / 2)) + 'px';
        const formate = editor.getFormat(bereich.index, bereich.length);
        for (const [knopfElement, format] of knoepfe) {
            knopfElement.classList.toggle('noose-auswahl-aktiv', !!formate[format]);
        }
    };

    editor.on('selection-change', aktualisieren);
    editor.on('text-change', aktualisieren);
    return zustand;
}

export async function initRichText(element, dotnetRef, initialHtml, minHeight, kiAktiv, erwaehnungAktiv, beschriftungen, kompakt, entwurfSchluessel, profil, bausteine) {
    await ladeQuill();
    if (!element) {
        return;
    }
    registriereGroessen();
    registriereErwaehnung();
    registriereBildtext();
    registriereKaesten();
    registriereTrenner();
    registriereTocKlasse();
    const tableHandler = await ladeTabellenModul();

    const toolbarGruppen = kompakt ? [
        ['bold', 'italic', 'underline', 'strike'],
        [{ list: 'ordered' }, { list: 'bullet' }, { list: 'check' }],
        ['blockquote', 'link', 'image', 'clean'],
    ] : [
        [{ header: [1, 2, 3, false] }],
        [{ size: ['0.75em', false, '1.5em', '2.5em'] }],
        ['bold', 'italic', 'underline', 'strike'],
        [{ list: 'ordered' }, { list: 'bullet' }, { list: 'check' }],
        [{ indent: '-1' }, { indent: '+1' }],
        [{ align: [] }],
        ['blockquote', 'code-block'],
        [{ color: [] }, { background: [] }],
        ['link', 'image', 'clean'],
    ];
    const module = {};
    if (tableHandler && !kompakt) {
        // table toolbar
        toolbarGruppen.push([{ [tableHandler.toolName]: [] }]);
        module[tableHandler.moduleName] = {
            fullWidth: false,
            customButton: 'Eigene Größe',
        };
    }

    const zustand = { dotnetRef, laeuft: false, tot: false, bilder: [] };
    element.__nooseKi = zustand;
    element.__nooseErwaehnung = {
        dotnetRef, tot: false, offen: false, timer: null, tasten: null,
        beschriftungen: Object.assign({}, beschriftungen),
    };

    if (!kompakt) {
        registriereKomfortSymbole();
        toolbarGruppen.push(['noose-rueckgaengig', 'noose-wiederholen']);
        toolbarGruppen.push(['noose-kasten-hinweis', 'noose-kasten-warnung', 'noose-kasten-info', 'noose-trenner', 'noose-toc']);
        toolbarGruppen.push(['noose-suchen', 'noose-vollbild']);
    }

    if (kiAktiv) {
        registriereKiSymbole();
        toolbarGruppen.push([KI_KORREKTUR, KI_SCHREIBEN]);
    }

    const handlers = {};
    if (kiAktiv) {
        handlers[KI_KORREKTUR] = () => meldeKi(element, zustand, 'korrigieren');
        handlers[KI_SCHREIBEN] = () => meldeKi(element, zustand, 'schreiben');
    }
    if (!kompakt) {
        handlers['noose-rueckgaengig'] = () => {
            const verlauf = editor.getModule('history');
            if (verlauf) {
                verlauf.undo();
                editor.focus();
            }
        };
        handlers['noose-wiederholen'] = () => {
            const verlauf = editor.getModule('history');
            if (verlauf) {
                verlauf.redo();
                editor.focus();
            }
        };
        for (const art of KASTEN_ARTEN) {
            handlers['noose-kasten-' + art] = () => schalteKasten(element, art);
        }
        handlers['noose-trenner'] = () => einfuegenTrenner(element);
        handlers['noose-toc'] = () => fordereInhaltsverzeichnisAn(element);
        handlers['noose-suchen'] = () => {
            const suchen = element.__nooseSuchen;
            if (suchen) {
                suchen.oeffnen();
            }
        };
        handlers['noose-vollbild'] = () => {
            const vollbild = element.__nooseVollbild;
            if (vollbild) {
                vollbild.umschalten();
            }
        };
    }

    // container/handlers form, not the plain array: the table tool must stay inside container, and the
    // table module swaps toolbar.handlers per instance — writing to Quill's shared DEFAULTS would fight that
    module.toolbar = { container: toolbarGruppen, handlers };

    const editor = new window.Quill(element, {
        theme: 'snow',
        placeholder: 'Dokument verfassen…',
        modules: module,
    });

    // must run before any content injection
    entschaerfeFokus(editor.root);
    entschaerfeFokus(editor.clipboard && editor.clipboard.container);
    haengeScrollWaechterAn(element);
    haengeBildEinfuegungAn(element);
    registriereBildMatcher(editor, element);
    haengeEinfuegeSauberungAn(element, editor, profil);
    element.__nooseBildOptionen = { dotnetRef, tot: false };
    haengeBildBearbeitungAn(element, editor);
    haengeTastenkuerzelAn(editor, dotnetRef);
    if (!kompakt) {
        haengeAuswahlBlaseAn(element, editor);
    }

    if (minHeight) {
        editor.root.style.minHeight = minHeight;
    }

    if (initialHtml) {
        editor.clipboard.dangerouslyPasteHTML(tokenZuChip(initialHtml, element.__nooseErwaehnung.beschriftungen));
    }

    if (erwaehnungAktiv) {
        haengeErwaehnungAn(element, editor, element.__nooseErwaehnung);
    }

    let timer = null;
    editor.on('text-change', () => {
        if (timer) {
            clearTimeout(timer);
        }
        // debounce
        timer = setTimeout(() => {
            // a disposed component or a lost circuit rejects; that is no error worth a console line
            dotnetRef.invokeMethodAsync('OnHtmlChanged', leseHtml(editor)).catch(() => { });
        }, 300);
    });

    element.__nooseQuill = editor;
    if (!kompakt) {
        haengeBlockKuerzelAn(editor);
        haengeBefehlsMenueAn(element, editor, dotnetRef, bausteine);
        haengeSuchenAn(element);
        haengeVollbildAn(element);
        haengeEntwurfAn(element, editor, dotnetRef, entwurfSchluessel);
        element.__nooseInhalt = { dotnetRef, tot: false };
    }

    if (kiAktiv) {
        // Quill puts no title on toolbar buttons
        const leiste = editor.getModule('toolbar').container;
        for (const [klasse, titel] of [
            [KI_KORREKTUR, 'FIBI: Rechtschreibung und Grammatik korrigieren'],
            [KI_SCHREIBEN, 'FIBI: Text schreiben lassen'],
        ]) {
            const knopf = leiste.querySelector('.ql-' + klasse);
            if (knopf) {
                knopf.setAttribute('title', titel);
            }
        }
    }
    const leiste = editor.getModule('toolbar').container;
    beschrifteToolbar(leiste, tableHandler);
    loeseDropdownsAusScrollStrip(leiste);
}

/// applies a NOOSEI result; returns the fresh html, or null when the editor moved on meanwhile
export function applyAiResult(element, index, laenge, html, erwarteteLaenge) {
    const editor = element && element.__nooseQuill;
    const zustand = element && element.__nooseKi;
    if (!editor || !zustand || zustand.tot) {
        return null;
    }
    // the dialog is modal, but a debounced paste could still have landed
    if (erwarteteLaenge > 0 && editor.getLength() !== erwarteteLaenge) {
        return null;
    }

    // the token set is unchanged (TextAssistService rejects a correction that moved a mention), so the
    // labels captured when the document was loaded still describe every token coming back
    const erwaehnungen = element.__nooseErwaehnung;
    const fertig = tokenZuChip(bilderZurueck(zustand, html || ''), erwaehnungen ? erwaehnungen.beschriftungen : null);
    const verlauf = editor.getModule('history');
    // own undo entry on both sides: neither swallow the last keystroke nor merge with the next
    if (verlauf && verlauf.cutoff) {
        verlauf.cutoff();
    }

    if (index < 0) {
        editor.clipboard.dangerouslyPasteHTML(fertig, 'user');
    } else {
        const Delta = window.Quill.import('delta');
        const eingefuegt = editor.clipboard.convert(fertig);
        editor.updateContents(new Delta().retain(index).delete(laenge).concat(eingefuegt), 'user');
        editor.setSelection(index + eingefuegt.length(), 0, 'silent');
    }

    if (verlauf && verlauf.cutoff) {
        verlauf.cutoff();
    }
    editor.focus();
    return leseHtml(editor);
}

export function setAiBusy(element, beschaeftigt) {
    const zustand = element && element.__nooseKi;
    if (zustand) {
        zustand.laeuft = !!beschaeftigt;
    }
    setzeKiBeschaeftigt(element, beschaeftigt);
}

// empty check
function leseHtml(editor) {
    const ohneText = editor.getText().trim().length === 0;
    const ohneTabelle = editor.root.querySelector('table') === null;
    const ohneBild = editor.root.querySelector('img') === null;
    // a mention carries text, but a document consisting only of one is not empty either
    const ohneErwaehnung = editor.root.querySelector('[data-erwaehnung]') === null;
    // same for a divider: it is a block embed and therefore carries no text at all, so a document that is
    // nothing but one divider used to be saved as the empty string and the line was gone
    const ohneTrenner = editor.root.querySelector('hr') === null;
    return ohneText && ohneTabelle && ohneBild && ohneErwaehnung && ohneTrenner ? '' : chipZuToken(editor.root);
}

export function setHtml(element, html, beschriftungen) {
    const editor = element && element.__nooseQuill;
    if (!editor) {
        return;
    }
    const zustand = element.__nooseErwaehnung;
    if (zustand && beschriftungen) {
        Object.assign(zustand.beschriftungen, beschriftungen);
    }
    editor.setText('');
    if (html) {
        editor.clipboard.dangerouslyPasteHTML(tokenZuChip(html, zustand ? zustand.beschriftungen : null));
    }
}

export function getHtml(element) {
    const editor = element && element.__nooseQuill;
    if (!editor) {
        return '';
    }
    const html = leseHtml(editor);
    // a save reads the text here first; marking it saved later compares against exactly this
    if (element.__nooseEntwurf) {
        element.__nooseEntwurf.gelesen = html;
    }
    return html;
}

/// moves the caret to the nth heading (h1-h3, document order) and scrolls it into view
export function springeZuUeberschrift(element, nummer) {
    const editor = element && element.__nooseQuill;
    if (!editor) {
        return;
    }
    // empty headings are skipped, the same way the C# outline builds its list
    const ziel = Array.from(editor.root.querySelectorAll('h1, h2, h3'))
        .filter((element) => (element.textContent || '').trim().length > 0)[nummer];
    if (!ziel) {
        return;
    }
    const blot = window.Quill.find(ziel);
    if (blot) {
        editor.setSelection(editor.getIndex(blot), 0, 'silent');
    }
    ziel.scrollIntoView({ block: 'center', behavior: 'smooth' });
    editor.focus();
}

export function destroyRichText(element) {
    if (!element) {
        return;
    }
    // a click queued after disposal must be inert, not an unhandled rejection
    if (element.__nooseKi) {
        element.__nooseKi.tot = true;
        element.__nooseKi.dotnetRef = null;
        element.__nooseKi.bilder = [];
    }
    const erwaehnungen = element.__nooseErwaehnung;
    if (erwaehnungen) {
        erwaehnungen.tot = true;
        erwaehnungen.dotnetRef = null;
        erwaehnungen.offen = false;
        if (erwaehnungen.timer) {
            clearTimeout(erwaehnungen.timer);
        }
        if (erwaehnungen.tasten) {
            element.removeEventListener('keydown', erwaehnungen.tasten, true);
        }
    }
    const befehle = element.__nooseBefehle;
    if (befehle) {
        befehle.tot = true;
        befehle.offen = false;
        befehle.dotnetRef = null;
        if (befehle.tasten) {
            element.removeEventListener('keydown', befehle.tasten, true);
        }
    }
    const suchen = element.__nooseSuchen;
    if (suchen && suchen.tasten) {
        element.removeEventListener('keydown', suchen.tasten, true);
    }
    const vollbild = element.__nooseVollbild;
    if (vollbild) {
        vollbild.aktiv = false;
        if (vollbild.tasten) {
            element.removeEventListener('keydown', vollbild.tasten, true);
        }
        if (vollbild.wechsel) {
            document.removeEventListener('fullscreenchange', vollbild.wechsel);
        }
        if (element.parentElement) {
            element.parentElement.classList.remove('noose-rte-vollbild');
        }
    }
    const entwurf = element.__nooseEntwurf;
    if (entwurf) {
        entwurf.tot = true;
        abmelden(entwurf);
        if (entwurf.schreiben) {
            // flush before cancelling: the pending write holds the newest keystrokes
            clearTimeout(entwurf.schreiben);
            entwurf.schreiben = 0;
            if (entwurf.jetztSchreiben) {
                entwurf.jetztSchreiben().catch(() => { });
            }
        }
    }
    const bildOptionen = element.__nooseBildOptionen;
    if (bildOptionen) {
        bildOptionen.tot = true;
        bildOptionen.dotnetRef = null;
    }
    const inhalt = element.__nooseInhalt;
    if (inhalt) {
        inhalt.tot = true;
        inhalt.dotnetRef = null;
    }
    const auswahl = element.__nooseAuswahl;
    if (auswahl) {
        auswahl.tot = true;
    }
    element.__nooseErwaehnung = null;
    element.__nooseBefehle = null;
    element.__nooseSuchen = null;
    element.__nooseVollbild = null;
    element.__nooseEntwurf = null;
    element.__nooseBildOptionen = null;
    element.__nooseInhalt = null;
    element.__nooseAuswahl = null;
    element.__nooseKi = null;
    element.__nooseQuill = null;
}
