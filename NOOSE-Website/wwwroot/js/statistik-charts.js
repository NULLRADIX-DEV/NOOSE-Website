// ECharts wrapper for the four statistics charts MudBlazor cannot draw (or cannot draw predictably):
// a calendar heatmap, a treemap, the score race and the classification-flow sankey. Everything else on
// /statistik is MudChart - this boundary is deliberate, so we do not maintain two full chart systems.
// Bump the ?v= in EChartsCanvas.razor whenever this file changes; dynamic imports skip Blazor's
// asset fingerprinting.

let loadPromise = null;
const instances = new Map();

function loadLib() {
    if (window.echarts) { return Promise.resolve(); }
    if (loadPromise) { return loadPromise; }
    loadPromise = new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = 'lib/echarts/echarts.min.js';
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('ECharts konnte nicht geladen werden.'));
        document.head.appendChild(script);
    });
    return loadPromise;
}

// ECharts draws on canvas and cannot read CSS variables, so every colour arrives as hex from C#.
function baseTextStyle(theme) {
    return { color: theme.textSecondary, fontFamily: 'system-ui, -apple-system, "Segoe UI", sans-serif', fontSize: 11 };
}

function calendarOption(data, theme) {
    const values = data.days.map(d => [d.day, d.count]);
    const max = data.days.reduce((m, d) => Math.max(m, d.count), 0);
    return {
        backgroundColor: 'transparent',
        tooltip: {
            formatter: (p) => `${p.value[0]}: ${p.value[1]} ${data.unit || ''}`.trim(),
            backgroundColor: theme.surface,
            borderColor: theme.gridline,
            textStyle: { color: theme.textPrimary },
        },
        visualMap: {
            min: 0,
            max: max || 1,
            calculable: false,
            orient: 'horizontal',
            left: 'right',
            bottom: 0,
            itemWidth: 12,
            itemHeight: 90,
            // single-hue ramp; zero keeps the surface showing through
            inRange: { color: data.ramp },
            textStyle: baseTextStyle(theme),
        },
        calendar: {
            top: 30,
            left: 45,
            right: 20,
            cellSize: ['auto', 14],
            range: data.range,
            itemStyle: { color: 'transparent', borderWidth: 1, borderColor: theme.gridline },
            splitLine: { lineStyle: { color: theme.gridline, width: 1, type: 'solid' } },
            yearLabel: { show: false },
            monthLabel: { nameMap: data.months, textStyle: baseTextStyle(theme) },
            dayLabel: { nameMap: data.weekdays, firstDay: 1, textStyle: baseTextStyle(theme) },
        },
        series: [{ type: 'heatmap', coordinateSystem: 'calendar', data: values }],
    };
}

function treemapOption(data, theme) {
    return {
        backgroundColor: 'transparent',
        tooltip: {
            formatter: (p) => `${p.name}: ${p.value} ${data.unit || ''}`.trim(),
            backgroundColor: theme.surface,
            borderColor: theme.gridline,
            textStyle: { color: theme.textPrimary },
        },
        series: [{
            type: 'treemap',
            roam: false,
            nodeClick: false,
            breadcrumb: { show: false },
            // 2px gap in the surface colour separates tiles instead of a drawn border
            itemStyle: { borderColor: theme.surface, borderWidth: 2, gapWidth: 2 },
            label: { show: true, formatter: '{b}', color: '#151515', fontSize: 11 },
            data: data.tiles.map(t => ({
                name: t.name,
                value: t.weight,
                itemStyle: { color: t.colour },
            })),
        }],
    };
}

function raceOption(data, theme, frameIndex) {
    const frame = data.frames[frameIndex] || { entries: [] };
    const entries = [...frame.entries].sort((a, b) => a.score - b.score);
    return {
        backgroundColor: 'transparent',
        grid: { top: 30, left: 8, right: 60, bottom: 20, containLabel: true },
        xAxis: {
            type: 'value', max: 100,
            axisLine: { lineStyle: { color: theme.gridline } },
            splitLine: { lineStyle: { color: theme.gridline, type: 'solid' } },
            axisLabel: baseTextStyle(theme),
        },
        yAxis: {
            type: 'category',
            data: entries.map(e => e.name),
            axisLine: { lineStyle: { color: theme.gridline } },
            axisTick: { show: false },
            axisLabel: baseTextStyle(theme),
        },
        title: {
            text: frame.label || '',
            right: 10, top: 0,
            textStyle: { color: theme.textSecondary, fontSize: 12, fontWeight: 'normal' },
        },
        series: [{
            type: 'bar',
            data: entries.map(e => ({ value: e.score, itemStyle: { color: e.colour } })),
            barMaxWidth: 18,
            itemStyle: { borderRadius: [0, 4, 4, 0] },
            label: { show: true, position: 'right', color: theme.textSecondary, fontSize: 11 },
        }],
        animationDuration: 400,
        animationDurationUpdate: 400,
        animationEasing: 'linear',
        animationEasingUpdate: 'linear',
    };
}

function sankeyOption(data, theme) {
    return {
        backgroundColor: 'transparent',
        tooltip: {
            trigger: 'item',
            backgroundColor: theme.surface,
            borderColor: theme.gridline,
            textStyle: { color: theme.textPrimary },
        },
        series: [{
            type: 'sankey',
            left: 10, right: 110, top: 10, bottom: 10,
            nodeWidth: 12,
            nodeGap: 10,
            emphasis: { focus: 'adjacency' },
            data: data.nodes.map((n, i) => ({
                name: n,
                itemStyle: { color: data.palette[i % data.palette.length] },
            })),
            links: data.links,
            // a wash rather than a saturated block, so the labels stay readable over it
            lineStyle: { color: 'gradient', opacity: 0.28, curveness: 0.5 },
            label: { color: theme.textSecondary, fontSize: 11 },
        }],
    };
}

function build(kind, data, theme, frameIndex) {
    if (kind === 'calendar') { return calendarOption(data, theme); }
    if (kind === 'treemap') { return treemapOption(data, theme); }
    if (kind === 'sankey') { return sankeyOption(data, theme); }
    return raceOption(data, theme, frameIndex);
}

export async function render(containerId, kind, payload) {
    await loadLib();
    const container = document.getElementById(containerId);
    if (!container) { return; }
    destroy(containerId);

    const data = typeof payload === 'string' ? JSON.parse(payload) : payload;
    const chart = window.echarts.init(container, null, { renderer: 'canvas' });
    const state = { chart, kind, data, frame: 0, timer: null };
    chart.setOption(build(kind, data, data.theme, 0));

    const onResize = () => { try { chart.resize(); } catch (e) { /* ignore */ } };
    window.addEventListener('resize', onResize);
    state.onResize = onResize;
    instances.set(containerId, state);
}

export function setData(containerId, payload) {
    const state = instances.get(containerId);
    if (!state) { return; }
    state.data = typeof payload === 'string' ? JSON.parse(payload) : payload;
    state.frame = 0;
    try { state.chart.setOption(build(state.kind, state.data, state.data.theme, 0), true); }
    catch (e) { /* best effort */ }
}

// The race animates client-side; the old version stepped it from a server timer,
// which cost one SignalR round trip per frame.
export function play(containerId, intervalMs) {
    const state = instances.get(containerId);
    if (!state || state.kind !== 'race' || state.timer) { return; }
    state.timer = setInterval(() => {
        const frames = state.data.frames || [];
        if (frames.length === 0) { return; }
        state.frame = (state.frame + 1) % frames.length;
        try { state.chart.setOption(build('race', state.data, state.data.theme, state.frame)); }
        catch (e) { /* ignore */ }
    }, intervalMs || 1200);
}

export function pause(containerId) {
    const state = instances.get(containerId);
    if (state && state.timer) { clearInterval(state.timer); state.timer = null; }
}

export function showFrame(containerId, index) {
    const state = instances.get(containerId);
    if (!state || state.kind !== 'race') { return; }
    state.frame = index;
    try { state.chart.setOption(build('race', state.data, state.data.theme, index)); }
    catch (e) { /* ignore */ }
}

export function destroy(containerId) {
    const state = instances.get(containerId);
    if (!state) { return; }
    if (state.timer) { clearInterval(state.timer); }
    if (state.onResize) { window.removeEventListener('resize', state.onResize); }
    try { state.chart.dispose(); } catch (e) { /* ignore */ }
    instances.delete(containerId);
}
