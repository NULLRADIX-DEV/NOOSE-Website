// vis-network interop

let visLadenPromise = null;

// node colors
const TYP_FARBE = {
    Person: '#22D3EE',
    Faction: '#7C8CF8',
    PersonGroup: '#D29922',
    Party: '#3FB950',
    Operation: '#F0883E',
    Case: '#A371F7',
    Taskforce: '#2DD4BF',
    Job: '#8B98A8',
    Agent: '#E6EDF3',
    Law: '#C9A227',
    PersonDoc: '#6E7681',
    Observation: '#58A6FF',
};

// border by classification
const EINSTUFUNG_RAND = { 1: '#9BA8B8', 2: '#D29922', 3: '#F85149' };

// edge colors
const ART_FARBE = { 0: 'rgba(139,152,168,0.55)', 1: '#F85149', 2: '#3FB950' };

const instanzen = new Map(); // id → instance

function ladeVisNetwork() {
    if (window.vis && window.vis.Network) {
        return Promise.resolve();
    }
    if (visLadenPromise) {
        return visLadenPromise;
    }
    visLadenPromise = new Promise((resolve, reject) => {
        if (!document.querySelector('link[data-vis-css]')) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = 'lib/vis-network/vis-network.min.css';
            link.setAttribute('data-vis-css', '');
            document.head.appendChild(link);
        }
        const script = document.createElement('script');
        script.src = 'lib/vis-network/vis-network.min.js';
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('vis-network konnte nicht geladen werden.'));
        document.head.appendChild(script);
    });
    return visLadenPromise;
}

// key figures (centrality workbench): gold fill + glow. The border stays free for type/classification.
const SCHLUESSEL_GOLD = '#FFC857';
const SCHLUESSEL_FUELLUNG = '#4A3708';
const SCHLUESSEL_HELL = '#5E4609';
const SCHLUESSEL_GROESSE = 1.8; // value factor over the busiest ordinary node

// the record the viewer picked: magenta is the only hue no type/level/edge kind uses
const FOKUS_MAGENTA = '#F778BA';
const FOKUS_GLUEHEN = 'rgba(247,120,186,0.85)';

function fuellung(schluessel) {
    return schluessel ? SCHLUESSEL_FUELLUNG : '#161B22';
}

function knotenfarbe(k, schluessel) {
    const basis = TYP_FARBE[k.type] || '#8B98A8';
    const rand = k.isFocus ? FOKUS_MAGENTA : (EINSTUFUNG_RAND[k.classificationLevel] || basis);
    const hell = schluessel ? SCHLUESSEL_HELL : '#1F2630';
    return {
        background: fuellung(schluessel),
        border: rand,
        // selecting the focus node must not swap its ring for the cyan one
        highlight: { background: hell, border: k.isFocus ? FOKUS_MAGENTA : '#22D3EE' },
        hover: { background: hell, border: rand },
    };
}

function baueTooltip(k) {
    const div = document.createElement('div');
    div.className = 'noose-graph-tip';
    const typ = document.createElement('div');
    typ.style.cssText = 'font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:#9BA8B8;';
    typ.textContent = k.type + (k.isClassified ? ' · Verschlusssache' : '') + (k.isArchived ? ' · Archiviert' : '');
    const name = document.createElement('div');
    name.style.cssText = 'font-weight:600;color:#E6EDF3;margin-top:2px;';
    name.textContent = k.designation;
    div.appendChild(typ);
    div.appendChild(name);
    if (k.subtitle) {
        const unter = document.createElement('div');
        unter.style.cssText = 'font-size:12px;color:#9BA8B8;';
        unter.textContent = k.subtitle;
        div.appendChild(unter);
    }
    if (k.isKeyFigure) {
        const schluessel = document.createElement('div');
        schluessel.style.cssText = 'font-size:12px;font-weight:600;color:' + SCHLUESSEL_GOLD + ';margin-top:4px;';
        const wert = typeof k.betweenness === 'number' ? ' · Vermittlerwert ' + k.betweenness.toFixed(2) : '';
        schluessel.textContent = '★ Schlüsselfigur' + wert;
        div.appendChild(schluessel);
    }
    if (k.isFocus) {
        const fokus = document.createElement('div');
        fokus.style.cssText = 'font-size:12px;font-weight:600;color:' + FOKUS_MAGENTA + ';margin-top:4px;';
        fokus.textContent = '◎ Fokus-Akte';
        div.appendChild(fokus);
    }
    return div;
}

// community palette (workbench cluster colouring)
const CLUSTER_FARBE = ['#22D3EE', '#F0883E', '#3FB950', '#A371F7', '#D29922', '#58A6FF', '#F85149', '#2DD4BF', '#7C8CF8', '#C9A227'];

function mapKnoten(k, maxWert) {
    const schluessel = !!k.isKeyFigure;
    const fokus = !!k.isFocus;
    const wert = 1 + (k.degree || 0);
    const node = {
        id: k.id,
        label: (fokus ? '◎ ' : '') + (schluessel ? '★ ' : '') + k.designation,
        title: baueTooltip(k),
        color: knotenfarbe(k, schluessel),
        // outgrow every ordinary node — a bridge node can be key with only two links
        value: schluessel
            ? Math.max(wert, Math.round(maxWert * SCHLUESSEL_GROESSE))
            : fokus ? Math.max(wert, maxWert) : wert,
        borderWidth: fokus ? 6 : (schluessel ? 5 : (k.classificationLevel >= 3 ? 3 : 2)),
        borderWidthSelected: fokus ? 7 : (schluessel ? 6 : 5),
        // the searched-for record wins every other glow
        shadow: fokus
            ? { enabled: true, color: FOKUS_GLUEHEN, size: 30, x: 0, y: 0 }
            : schluessel
                ? { enabled: true, color: 'rgba(255,200,87,0.9)', size: 34, x: 0, y: 0 }
                : k.classificationLevel >= 3
                    ? { enabled: true, color: 'rgba(248,81,73,0.55)', size: 18, x: 0, y: 0 }
                    : { enabled: true, color: 'rgba(0,0,0,0.5)', size: 8, x: 0, y: 2 },
    };
    if (schluessel) {
        node.font = { color: SCHLUESSEL_GOLD, strokeWidth: 4, strokeColor: '#0B0E13', face: 'Inter, Segoe UI, sans-serif' };
    } else if (fokus) {
        node.font = { color: FOKUS_MAGENTA, strokeWidth: 4, strokeColor: '#0B0E13', face: 'Inter, Segoe UI, sans-serif' };
    }
    if (k.photoUrl) {
        node.shape = 'circularImage';
        node.image = k.photoUrl;
        node.brokenImage = undefined;
    } else {
        node.shape = 'dot';
    }
    // an archived record stays in the picture, just quiet
    if (k.isArchived) {
        node.opacity = 0.45;
        node.shapeProperties = Object.assign({}, node.shapeProperties, { borderDashes: [4, 4] });
        node.font = Object.assign({ size: 14, face: 'Inter, Segoe UI, sans-serif' }, node.font, { color: '#7D8590' });
    }
    return node;
}

function mapKante(e) {
    const farbe = ART_FARBE[e.kind] != null ? ART_FARBE[e.kind] : ART_FARBE[0];
    return {
        from: e.source,
        to: e.target,
        label: e.label || undefined,
        dashes: !!e.automatic,
        width: e.kind === 1 || e.kind === 2 ? 2 : 1,
        color: { color: farbe, highlight: '#22D3EE', hover: '#22D3EE', opacity: 1 },
        font: { color: '#9BA8B8', size: 11, strokeWidth: 0, background: 'rgba(14,17,22,0.85)' },
    };
}

// free mode: share of the drag delta a neighbour follows, per hop distance
const ZUG_ANTEIL = [0.5, 0.18];

function baueNachbarn(kanten) {
    const map = new Map();
    (kanten || []).forEach((e) => {
        if (!e || e.source == null || e.target == null || e.source === e.target) {
            return;
        }
        if (!map.has(e.source)) { map.set(e.source, new Set()); }
        if (!map.has(e.target)) { map.set(e.target, new Set()); }
        map.get(e.source).add(e.target);
        map.get(e.target).add(e.source);
    });
    return map;
}

// neighbourhood of the dragged node(s), weighted by hop distance
function zugKreis(nachbarn, startIds) {
    const gewicht = new Map();
    const gesehen = new Set(startIds);
    let rand = startIds.slice();
    for (let hop = 0; hop < ZUG_ANTEIL.length && rand.length > 0; hop++) {
        const naechste = [];
        rand.forEach((id) => {
            const nb = nachbarn.get(id);
            if (!nb) {
                return;
            }
            nb.forEach((n) => {
                if (gesehen.has(n)) {
                    return;
                }
                gesehen.add(n);
                gewicht.set(n, ZUG_ANTEIL[hop]);
                naechste.push(n);
            });
        });
        rand = naechste;
    }
    return gewicht;
}

function optionen(knotenAnzahl, kantenAnzahl) {
    // skip layout for large graphs
    const gross = knotenAnzahl > 120 || kantenAnzahl > 200;
    return {
        autoResize: true,
        layout: { improvedLayout: !gross },
        nodes: {
            shape: 'dot',
            scaling: { min: 10, max: 40, label: { enabled: true, min: 12, max: 22 } },
            font: { color: '#E6EDF3', size: 14, face: 'Inter, Segoe UI, sans-serif' },
        },
        edges: {
            selectionWidth: 2,
            hoverWidth: 1,
            smooth: gross ? false : { enabled: true, type: 'continuous' },
        },
        physics: {
            solver: 'forceAtlas2Based',
            forceAtlas2Based: { gravitationalConstant: -75, centralGravity: 0.006, springLength: 150, springConstant: 0.12, damping: 0.5, avoidOverlap: 0.4 },
            stabilization: { enabled: true, iterations: gross ? 120 : 200, updateInterval: 25, fit: true },
        },
        interaction: { hover: true, tooltipDelay: 120, navigationButtons: true, keyboard: false, multiselect: false },
    };
}

export async function render(containerId, datenJson, dotnetRef) {
    await ladeVisNetwork();
    const container = document.getElementById(containerId);
    if (!container) {
        return;
    }
    zerstoere(containerId);

    const daten = typeof datenJson === 'string' ? JSON.parse(datenJson) : datenJson;
    const maxWert = (daten.node || []).reduce((m, k) => Math.max(m, 1 + (k.degree || 0)), 1);
    const knotenListe = (daten.node || []).map((k) => mapKnoten(k, maxWert));
    const kantenListe = (daten.edges || []).map(mapKante);
    const nodes = new window.vis.DataSet(knotenListe);
    const edges = new window.vis.DataSet(kantenListe);
    const opts = optionen(knotenListe.length, kantenListe.length);
    const network = new window.vis.Network(container, { nodes, edges }, opts);
    const fokusKnoten = (daten.node || []).find((k) => k.isFocus);

    // physics stays on

    // click handlers
    network.on('doubleClick', (params) => {
        let id = (params.nodes && params.nodes.length > 0) ? params.nodes[0] : null;
        if (!id && params.pointer && params.pointer.DOM) {
            id = network.getNodeAt(params.pointer.DOM);
        }
        if (!id) {
            const sel = network.getSelectedNodes();
            if (sel && sel.length > 0) { id = sel[0]; }
        }
        if (id) {
            try { dotnetRef.invokeMethodAsync('OnNodeClick', id); } catch (e) { /* ignore */ }
        }
    });
    network.on('selectNode', (params) => {
        if (params.nodes && params.nodes.length > 0) {
            const id = params.nodes[0];
            network.focus(id, { scale: 1.15, animation: { duration: 400, easingFunction: 'easeInOutQuad' } });
            try { dotnetRef.invokeMethodAsync('OnNodeSelect', id); } catch (e) { /* ignore */ }
        }
    });

    // free mode has physics off, so springs cannot pull neighbours — do it by hand
    network.on('dragStart', (params) => {
        const inst = instanzen.get(containerId);
        if (!inst) {
            return;
        }
        inst.zug = null;
        const gezogen = (params.nodes || []).filter((id) => id != null);
        if (!inst.frei || gezogen.length === 0) {
            return;
        }
        const gewicht = zugKreis(inst.nachbarn, gezogen);
        if (gewicht.size === 0) {
            return;
        }
        const pos = network.getPositions([...gezogen, ...gewicht.keys()]);
        const anker = pos[gezogen[0]];
        if (!anker) {
            return;
        }
        const ziele = [];
        gewicht.forEach((w, id) => {
            if (pos[id]) {
                ziele.push({ id, w, x: pos[id].x, y: pos[id].y });
            }
        });
        const start = params.pointer && params.pointer.canvas ? params.pointer.canvas : null;
        inst.zug = { anker: gezogen[0], startX: anker.x, startY: anker.y, ziele, zeiger: start ? { x: start.x, y: start.y } : null };
    });
    network.on('dragging', (params) => {
        const inst = instanzen.get(containerId);
        const zug = inst && inst.zug;
        if (!zug || zug.ziele.length === 0) {
            return;
        }
        // 'dragging' fires before vis moves the node — take the delta from the pointer, not the node
        const zeiger = zug.zeiger && params && params.pointer ? params.pointer.canvas : null;
        let dx;
        let dy;
        if (zeiger) {
            dx = zeiger.x - zug.zeiger.x;
            dy = zeiger.y - zug.zeiger.y;
        } else {
            const jetzt = network.getPositions([zug.anker])[zug.anker];
            if (!jetzt) {
                return;
            }
            dx = jetzt.x - zug.startX;
            dy = jetzt.y - zug.startY;
        }
        // write positions straight to the body: moveNode() per node would redraw per node
        const koerper = network.body && network.body.nodes ? network.body.nodes : null;
        zug.ziele.forEach((z) => {
            const kn = koerper ? koerper[z.id] : null;
            if (kn) {
                kn.x = z.x + dx * z.w;
                kn.y = z.y + dy * z.w;
            } else {
                try { network.moveNode(z.id, z.x + dx * z.w, z.y + dy * z.w); } catch (e) { /* ignore */ }
            }
        });
        if (koerper) {
            try { network.redraw(); } catch (e) { /* ignore */ }
        }
    });
    network.on('dragEnd', () => {
        const inst = instanzen.get(containerId);
        if (inst) {
            inst.zug = null;
        }
    });

    // save defaults
    instanzen.set(containerId, {
        network, nodes, edges, dotnetRef,
        physikStandard: opts.physics,
        frei: false,
        zug: null,
        nachbarn: baueNachbarn(daten.edges),
        rohKnoten: daten.node || [],
        fokusId: fokusKnoten ? fokusKnoten.id : null,
        autoZentrieren: true,
    });

    if (fokusKnoten) {
        // vis fits the whole graph when stabilising ends — centre only after that
        network.once('stabilizationIterationsDone', () => {
            const inst = instanzen.get(containerId);
            if (!inst || inst.autoZentrieren === false) {
                return;
            }
            requestAnimationFrame(() => fokussiere(containerId, null, 0));
        });
    }
}

export function setzeFreierModus(containerId, frei) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    inst.frei = !!frei;
    inst.zug = null;
    if (inst.frei) {
        // frozen layout: nothing drifts, nothing collapses; dragging pulls neighbours instead
        inst.network.setOptions({ physics: false });
        return;
    }
    inst.network.setOptions({ physics: { ...inst.physikStandard, enabled: true, stabilization: false } });
    // setOptions alone only re-enables physics, it does not restart the loop
    try { inst.network.startSimulation(); } catch (e) { /* ignore */ }
}

// highlight path
export function markierePfad(containerId, knotenIds, kantenSchluessel) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    const aktiv = new Set(knotenIds || []);
    const kantenAktiv = new Set(kantenSchluessel || []);
    inst.nodes.forEach((n) => {
        const an = aktiv.size === 0 || aktiv.has(n.id);
        inst.nodes.update({ id: n.id, opacity: an ? 1 : 0.18 });
    });
    inst.edges.forEach((e) => {
        const an = kantenAktiv.size === 0 || kantenAktiv.has(e.from + '|' + e.to) || kantenAktiv.has(e.to + '|' + e.from);
        inst.edges.update({ id: e.id, hidden: !an && kantenAktiv.size > 0 });
    });
    if (aktiv.size > 0) {
        try { inst.network.fit({ nodes: [...aktiv], animation: { duration: 500, easingFunction: 'easeInOutQuad' } }); } catch (e) { /* ignore */ }
    }
}

// reset highlights
export function zuruecksetzen(containerId) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    inst.nodes.forEach((n) => inst.nodes.update({ id: n.id, opacity: 1 }));
    inst.edges.forEach((e) => inst.edges.update({ id: e.id, hidden: false }));
}

export function passeAn(containerId) {
    const inst = instanzen.get(containerId);
    if (inst) {
        try { inst.network.fit({ animation: { duration: 500, easingFunction: 'easeInOutQuad' } }); } catch (e) { /* ignore */ }
    }
}

// centre on a node; without an id the remembered focus node
export function fokussiere(containerId, nodeId, mindestZoom) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    const ziel = nodeId || inst.fokusId;
    if (!ziel) {
        return;
    }
    try {
        // no minimum means pan only — zooming in would throw away the overview
        const jetzt = inst.network.getScale();
        const skala = mindestZoom ? Math.max(jetzt, mindestZoom) : jetzt;
        inst.network.focus(ziel, { scale: skala, animation: { duration: 500, easingFunction: 'easeInOutQuad' } });
    } catch (e) { /* ignore */ }
}

export function vollbild(containerId) {
    const container = document.getElementById(containerId);
    if (!container) {
        return;
    }
    try {
        if (document.fullscreenElement) {
            document.exitFullscreen();
        } else {
            container.requestFullscreen();
        }
    } catch (e) { /* ignore */ }
}

export function alsBildExportieren(containerId) {
    const container = document.getElementById(containerId);
    const canvas = container ? container.querySelector('canvas') : null;
    if (!canvas) {
        return;
    }
    try {
        const a = document.createElement('a');
        a.href = canvas.toDataURL('image/png');
        a.download = 'fib-beziehungsgraph.png';
        a.click();
    } catch (e) { /* ignore */ }
}

// colour nodes by detected community, or restore base colours
export function faerbeNachCommunity(containerId, an) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    const roh = new Map((inst.rohKnoten || []).map((k) => [k.id, k]));
    inst.nodes.forEach((n) => {
        const k = roh.get(n.id);
        if (!k) {
            return;
        }
        const schluessel = !!k.isKeyFigure;
        if (an) {
            // cluster colour takes over the border; key-figure fill and focus ring must survive it
            const farbe = k.isFocus ? FOKUS_MAGENTA : CLUSTER_FARBE[(k.communityId || 0) % CLUSTER_FARBE.length];
            const grund = fuellung(schluessel);
            const hell = schluessel ? SCHLUESSEL_HELL : '#1F2630';
            inst.nodes.update({ id: n.id, color: { background: grund, border: farbe, highlight: { background: hell, border: k.isFocus ? FOKUS_MAGENTA : '#22D3EE' }, hover: { background: hell, border: farbe } } });
        } else {
            inst.nodes.update({ id: n.id, color: knotenfarbe(k, schluessel) });
        }
    });
}

// id → creation timestamp, parsed once per render instead of once per scrub step
function zeitIndex(inst) {
    if (!inst.zeiten) {
        inst.zeiten = new Map((inst.rohKnoten || []).map((k) => [k.id, k.createdAt ? new Date(k.createdAt).getTime() : null]));
    }
    return inst.zeiten;
}

// hide nodes created after the cutoff (network-growth scrubber); null shows all
export function setzeZeitfenster(containerId, grenzeIso) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    const grenze = grenzeIso ? new Date(grenzeIso).getTime() : null;
    const zeiten = zeitIndex(inst);
    // every update() call rebuilds vis' whole graph state — collect, then write once
    const aenderungen = [];
    inst.nodes.forEach((n) => {
        const erstellt = zeiten.get(n.id);
        const versteckt = grenze != null && erstellt != null && erstellt > grenze;
        if (!!n.hidden !== versteckt) {
            aenderungen.push({ id: n.id, hidden: versteckt });
        }
    });
    if (aenderungen.length > 0) {
        inst.nodes.update(aenderungen);
    }
}

// edge-drawing mode: user drags between two nodes → OnEdgeDrawn(from,to); we never add it visually
export function starteVerknuepfungsModus(containerId) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    inst.network.setOptions({
        manipulation: {
            enabled: false,
            addEdge: (data, callback) => {
                if (data.from !== data.to) {
                    try { inst.dotnetRef.invokeMethodAsync('OnEdgeDrawn', data.from, data.to); } catch (e) { /* ignore */ }
                }
                callback(null); // C# persists + reloads
            },
        },
    });
    try { inst.network.addEdgeMode(); } catch (e) { /* ignore */ }
}

export function beendeVerknuepfungsModus(containerId) {
    const inst = instanzen.get(containerId);
    if (inst) {
        try { inst.network.disableEditMode(); } catch (e) { /* ignore */ }
    }
}

export function holePositionen(containerId) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return '{}';
    }
    try {
        inst.network.storePositions();
        return JSON.stringify(inst.network.getPositions());
    } catch (e) {
        return '{}';
    }
}

export function setzePositionen(containerId, json) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    let pos;
    try { pos = typeof json === 'string' ? JSON.parse(json) : json; } catch (e) { return; }
    // freeze physics so the loaded layout sticks
    inst.frei = true;
    inst.zug = null;
    // a saved view carries its own framing — do not centre over it
    inst.autoZentrieren = false;
    inst.network.setOptions({ physics: false });
    const koerper = inst.network.body && inst.network.body.nodes ? inst.network.body.nodes : null;
    Object.keys(pos || {}).forEach((id) => {
        const p = pos[id];
        if (!p || typeof p.x !== 'number' || typeof p.y !== 'number') {
            return;
        }
        // records deleted since the view was saved are simply absent — moveNode would log for each
        if (koerper && !koerper[id]) {
            return;
        }
        try { inst.network.moveNode(id, p.x, p.y); } catch (e) { /* ignore */ }
    });
    try { inst.network.fit({ animation: { duration: 400 } }); } catch (e) { /* ignore */ }
}

export function zerstoere(containerId) {
    const inst = instanzen.get(containerId);
    if (inst) {
        try { inst.network.destroy(); } catch (e) { /* ignore */ }
        instanzen.delete(containerId);
    }
}
