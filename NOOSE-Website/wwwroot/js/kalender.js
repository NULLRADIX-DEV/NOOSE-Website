// fullcalendar interop

let fcLadenPromise = null;

// source colors
const QUELLE_FARBE = {
    0: '#3FB950', // Termin
    1: '#F0883E', // Operation
    2: '#B27923', // Observation
    3: '#9A9488', // Aufgabe (faellig)
    4: '#D29922', // Wiedervorlage
    5: '#83A412', // Fraktions-Aktivitaet
    6: '#A371F7', // Personen-Dok
    7: '#8A7650', // Besprechung
    8: '#DB61A2', // Abmeldung
    9: '#B07C9E', // Team-Abmeldung
};

const instanzen = new Map(); // id → instance

function ladeFullCalendar() {
    if (window.FullCalendar && window.FullCalendar.Calendar) {
        return Promise.resolve();
    }
    if (fcLadenPromise) {
        return fcLadenPromise;
    }
    fcLadenPromise = new Promise((resolve, reject) => {
        const core = document.createElement('script');
        core.src = 'lib/fullcalendar/index.global.min.js';
        core.onload = () => {
            // german locale
            const loc = document.createElement('script');
            loc.src = 'lib/fullcalendar/locales/de.global.min.js';
            loc.onload = () => resolve();
            loc.onerror = () => resolve();
            document.head.appendChild(loc);
        };
        core.onerror = () => reject(new Error('FullCalendar konnte nicht geladen werden.'));
        document.head.appendChild(core);
    });
    return fcLadenPromise;
}

// theme styles
function stilSicherstellen() {
    if (document.querySelector('style[data-noose-kal]')) {
        return;
    }
    const st = document.createElement('style');
    st.setAttribute('data-noose-kal', '');
    st.textContent = `
.fc {
    --fc-border-color: var(--mud-palette-lines-default);
    --fc-page-bg-color: transparent;
    --fc-neutral-bg-color: var(--mud-palette-background-gray);
    --fc-today-bg-color: rgba(113, 97, 74, 0.12);
    --fc-now-indicator-color: var(--mud-palette-error);
    --fc-highlight-color: rgba(113, 97, 74, 0.2);
    --fc-list-event-hover-bg-color: var(--mud-palette-action-default-hover);
    font-family: inherit;
    color: var(--mud-palette-text-primary);
}
.fc .fc-toolbar-title { color: var(--mud-palette-text-primary); font-size: 1.15rem; font-weight: 600; }
.fc .fc-col-header-cell-cushion,
.fc .fc-list-day-text,
.fc .fc-list-day-side-text { color: var(--mud-palette-text-primary); text-decoration: none; padding: 6px 4px; }
.fc .fc-timegrid-slot-label-cushion,
.fc .fc-timegrid-axis-cushion,
.fc .fc-list-event-time { color: var(--mud-palette-text-secondary); font-size: 0.74rem; }
.fc .fc-timegrid-axis-cushion { white-space: normal; line-height: 1.05; }
.fc-theme-standard .fc-scrollgrid,
.fc-theme-standard td, .fc-theme-standard th { border-color: var(--mud-palette-lines-default); }
.fc .fc-day-today .fc-col-header-cell-cushion { color: var(--mud-palette-primary-lighten); font-weight: 700; }
.fc .fc-timegrid-slot-minor { border-top-style: dotted; }
/* buttons */
.fc .fc-button-primary {
    background-color: var(--mud-palette-surface);
    border-color: var(--mud-palette-lines-default);
    color: var(--mud-palette-text-primary);
    text-transform: none;
    box-shadow: none;
}
.fc .fc-button-primary:hover {
    background-color: var(--mud-palette-action-default-hover);
    border-color: var(--mud-palette-lines-default);
    color: var(--mud-palette-text-primary);
}
.fc .fc-button-primary:disabled { opacity: 0.4; }
.fc .fc-button:focus, .fc .fc-button-primary:focus { box-shadow: none; outline: none; }
.fc .fc-button-primary:not(:disabled).fc-button-active,
.fc .fc-button-primary:not(:disabled):active {
    background-color: var(--mud-palette-primary);
    border-color: var(--mud-palette-primary);
    color: var(--mud-palette-primary-text);
}
/* events */
.fc .fc-event { border-radius: 6px; border: none; font-size: 0.76rem; }
.fc .fc-timegrid-event { box-shadow: none; }
.fc .fc-timegrid-event .fc-event-main { padding: 1px 5px; }
.fc .fc-daygrid-event { padding: 1px 5px; }
.fc .fc-h-event .fc-event-title, .fc .fc-event-title { font-weight: 600; }
.fc .fc-list-event:hover td { background: var(--mud-palette-action-default-hover); }
.noose-kal-hinfaellig { opacity: .6; text-decoration: line-through; }
/* phones: the toolbars wrap instead of pushing the page wider than the screen */
@media screen and (max-width: 599.98px) {
    .fc .fc-toolbar { flex-wrap: wrap; gap: 6px; }
    .fc .fc-toolbar.fc-footer-toolbar { justify-content: center; }
    .fc .fc-toolbar-title { font-size: 1rem; }
}
/* scrollbar */
.fc .fc-scroller::-webkit-scrollbar { width: 10px; height: 10px; }
.fc .fc-scroller::-webkit-scrollbar-thumb { background: var(--mud-palette-lines-default); border-radius: 6px; }
.fc .fc-scroller::-webkit-scrollbar-track { background: transparent; }
`;
    document.head.appendChild(st);
}

function addTage(iso, n) {
    const d = new Date(iso);
    if (isNaN(d.getTime())) {
        return iso;
    }
    d.setDate(d.getDate() + n);
    const p = (x) => String(x).padStart(2, '0');
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate());
}

function mapEreignis(e) {
    const farbe = QUELLE_FARBE[e.source] != null ? QUELLE_FARBE[e.source] : '#9A9488';
    const ev = {
        id: e.id,
        title: e.title,
        // normalize date
        start: e.wholeDay ? addTage(e.startLocal, 0) : e.startLocal,
        allDay: !!e.wholeDay,
        backgroundColor: farbe,
        borderColor: farbe,
        extendedProps: { href: e.href || null },
    };
    if (e.endLocal) {
        // exclusive end
        ev.end = e.wholeDay ? addTage(e.endLocal, 1) : e.endLocal;
    }
    if (e.obsolete) {
        ev.classNames = ['noose-kal-hinfaellig'];
    }
    return ev;
}

export async function render(containerId, eintraege, dotnetRef, locale) {
    await ladeFullCalendar();
    stilSicherstellen();
    const el = document.getElementById(containerId);
    if (!el) {
        return;
    }
    zerstoere(containerId);

    const handy = window.matchMedia(HANDY_ABFRAGE);
    const cal = new window.FullCalendar.Calendar(el, {
        initialView: handy.matches ? 'listWeek' : 'timeGridWeek',
        ...ansichtOptionen(handy.matches),
        locale: locale || 'de',
        firstDay: 1,
        timeZone: 'local',
        nowIndicator: true,
        scrollTime: '07:00:00',
        slotLabelFormat: { hour: '2-digit', minute: '2-digit', hour12: false },
        eventTimeFormat: { hour: '2-digit', minute: '2-digit', hour12: false },
        // german labels
        buttonText: { today: 'Heute', month: 'Monat', week: 'Woche', day: 'Tag', list: 'Liste' },
        allDayText: 'Ganztägig',
        noEventsText: 'Keine Einträge in diesem Zeitraum',
        weekText: 'KW',
        moreLinkText: (n) => '+ ' + n + ' weitere',
        dayMaxEvents: true,
        navLinks: false,
        events: (eintraege || []).map(mapEreignis),
        // navigate on click
        eventClick: (info) => {
            info.jsEvent.preventDefault();
            const href = info.event.extendedProps && info.event.extendedProps.href;
            if (href) {
                try { dotnetRef.invokeMethodAsync('OnEventClick', href); } catch (e) { /* ignore */ }
            }
        },
        // date range changed
        datesSet: (arg) => {
            try { dotnetRef.invokeMethodAsync('OnDateRange', arg.startStr, arg.endStr); } catch (e) { /* ignore */ }
        },
    });
    cal.render();
    // turning the phone or resizing the window across 600px switches between the list and the week grid
    const wechsel = (e) => {
        const o = ansichtOptionen(e.matches);
        cal.setOption('height', o.height);
        cal.setOption('headerToolbar', o.headerToolbar);
        cal.setOption('footerToolbar', o.footerToolbar);
        cal.changeView(e.matches ? 'listWeek' : 'timeGridWeek');
    };
    handy.addEventListener('change', wechsel);
    instanzen.set(containerId, { cal, dotnetRef, handy, wechsel });
}

// the phone width of app.css ("Handy", below 600px): there the week grid has no room, the list shows the week instead
const HANDY_ABFRAGE = 'screen and (max-width: 599.98px)';

// toolbars and height per width; from 600px up exactly what the calendar always had
function ansichtOptionen(handy) {
    return handy
        ? {
            height: 'auto',
            headerToolbar: { left: 'prev,next', center: 'title', right: 'today' },
            footerToolbar: { left: '', center: 'listWeek,timeGridDay,dayGridMonth', right: '' },
        }
        : {
            height: '78vh',
            headerToolbar: { left: 'prev,next today', center: 'title', right: 'timeGridDay,timeGridWeek,dayGridMonth,listWeek' },
            footerToolbar: false,
        };
}

export function setzeEreignisse(containerId, eintraege) {
    const inst = instanzen.get(containerId);
    if (!inst) {
        return;
    }
    inst.cal.removeAllEvents();
    (eintraege || []).forEach((e) => inst.cal.addEvent(mapEreignis(e)));
}

export function zerstoere(containerId) {
    const inst = instanzen.get(containerId);
    if (inst) {
        try { inst.handy.removeEventListener('change', inst.wechsel); } catch (e) { /* ignore */ }
        try { inst.cal.destroy(); } catch (e) { /* ignore */ }
        instanzen.delete(containerId);
    }
}
