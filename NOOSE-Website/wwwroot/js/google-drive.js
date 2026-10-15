// Google sign-in for the import. Google Identity Services hands out a short-lived token for the drive.file scope, the
// Google Picker lets the agent choose one file, and the file's id and the token go to the server once, which reads the
// file with it and keeps neither.
//
// Google's two scripts run in this page's origin once loaded, and stay until the tab reloads. So they load only when an
// agent reaches for the button (pointer over it, focus or touch), never just because a page shows it.
//
// The sign-in opens in a window of its own, and a browser lets a page open one only straight from a click. So the click
// is handled here and not on the server: a round trip over the circuit first could outlast the click's grace period.

const GSI = 'https://accounts.google.com/gsi/client';
const GAPI = 'https://apis.google.com/js/api.js';
const SCOPE = 'https://www.googleapis.com/auth/drive.file';
const TYPES = {
    document: 'application/vnd.google-apps.document',
    presentation: 'application/vnd.google-apps.presentation',
};
const INTENT = ['pointerenter', 'focusin', 'touchstart'];

const scripts = new Map();      // url -> load promise
let loading = null;
const clients = new Map();      // client id -> token client
let pending = null;             // { host, state, answer } of the token request in flight
let shown = null;               // the Picker on screen
const buttons = new WeakMap();  // host element -> { dotnet, config, onClick, onIntent }

function script(src, present) {
    if (present()) {
        return Promise.resolve();
    }
    let promise = scripts.get(src);
    if (!promise) {
        promise = new Promise((resolve, reject) => {
            const tag = document.createElement('script');
            tag.src = src;
            tag.async = true;
            tag.onload = resolve;
            tag.onerror = () => {
                tag.remove();
                reject(new Error(`${src} did not load`));
            };
            document.head.appendChild(tag);
        });
        // a failed script is fetched again on the next try, one that loaded never twice
        promise.catch(() => scripts.delete(src));
        scripts.set(src, promise);
    }
    return promise;
}

function ready() {
    if (!loading) {
        loading = Promise.all([
            script(GSI, () => !!window.google?.accounts?.oauth2),
            script(GAPI, () => !!window.gapi?.load),
        ]).then(() => window.google?.picker?.PickerBuilder ? undefined : new Promise((resolve, reject) => {
            window.gapi.load('picker', { callback: resolve, onerror: reject, timeout: 15000, ontimeout: reject });
        }));
        loading.catch(() => { loading = null; });
    }
    return loading;
}

function loaded() {
    return !!(window.google?.accounts?.oauth2 && window.google?.picker?.PickerBuilder);
}

function tokenClient(clientId) {
    let client = clients.get(clientId);
    if (!client) {
        // one client per id, its answer handed to whichever click asked last
        client = window.google.accounts.oauth2.initTokenClient({
            client_id: clientId,
            scope: SCOPE,
            callback: response => settle(response, null),
            error_callback: error => settle(null, error),
        });
        clients.set(clientId, client);
    }
    return client;
}

function settle(response, error) {
    const request = pending;
    pending = null;
    // the dialog may have closed or switched tabs while the Google window was open: then nobody waits for a file
    if (!request || !request.host.isConnected || buttons.get(request.host) !== request.state) {
        return;
    }
    request.answer(response, error);
}

function tell(state, message) {
    state.dotnet.invokeMethodAsync('Meldung', message).catch(() => { });
}

function signIn(host, state) {
    const button = host.querySelector('button');
    if (button?.disabled) {
        return;
    }
    if (!loaded()) {
        tell(state, 'Google lädt noch. Klicke gleich noch einmal.');
        ready().catch(() => tell(state, 'Google ließ sich nicht laden. Prüfe Werbeblocker oder Netz und versuche es erneut.'));
        return;
    }
    const answer = (response, error) => {
        if (error) {
            // closing the window is a choice, not a fault
            if (error.type === 'popup_failed_to_open') {
                tell(state, 'Der Browser hat das Google-Fenster blockiert. Erlaube Pop-ups für diese Seite.');
            } else if (error.type !== 'popup_closed') {
                tell(state, 'Die Anmeldung bei Google ist fehlgeschlagen. Versuche es erneut.');
            }
            return;
        }
        if (response.error) {
            tell(state, response.error === 'access_denied'
                ? 'Ohne deine Zustimmung bei Google geht es nicht.'
                : 'Google hat die Anmeldung abgelehnt. Versuche es erneut.');
            return;
        }
        if (!window.google.accounts.oauth2.hasGrantedAllScopes(response, SCOPE)) {
            tell(state, 'Ohne Zugriff auf die gewählte Datei geht es nicht. Setze im Google-Fenster den Haken dafür.');
            return;
        }
        try {
            pick(state, response.access_token);
        } catch {
            tell(state, 'Die Dateiauswahl von Google ließ sich nicht öffnen. Versuche es erneut.');
        }
    };
    pending = { host, state, answer };
    try {
        // no prompt once the agent agreed: the window closes by itself
        tokenClient(state.config.clientId).requestAccessToken({ prompt: '' });
    } catch {
        pending = null;
        tell(state, 'Die Anmeldung bei Google ließ sich nicht starten. Versuche es erneut.');
    }
}

function pick(state, token) {
    const { config } = state;
    const picker = window.google.picker;
    const view = new picker.DocsView(config.kind === 'presentation' ? picker.ViewId.PRESENTATIONS : picker.ViewId.DOCUMENTS)
        .setMimeTypes(TYPES[config.kind])
        .setMode(picker.DocsViewMode.LIST);
    if (config.fileId) {
        // a refresh reads the file the import came from
        view.setFileIds(config.fileId);
    } else {
        view.setIncludeFolders(true);
    }
    shown?.dispose();
    shown = new picker.PickerBuilder()
        // the project number: the Picker grants the chosen file to it, which is all drive.file can then read
        .setAppId(config.appId)
        .setDeveloperKey(config.apiKey)
        .setOAuthToken(token)
        .setLocale('de')
        .setTitle(config.title)
        .addView(view)
        .setCallback(data => {
            const action = data[picker.Response.ACTION];
            if (action !== picker.Action.PICKED && action !== picker.Action.CANCEL) {
                return;
            }
            shown?.dispose();
            shown = null;
            const doc = action === picker.Action.PICKED ? data[picker.Response.DOCUMENTS]?.[0] : null;
            if (doc) {
                state.dotnet.invokeMethodAsync('Gewaehlt', doc[picker.Document.ID], doc[picker.Document.NAME] ?? '', token)
                    .catch(() => { });
            }
        })
        .build();
    shown.setVisible(true);
}

// Makes the button inside `host` open the Google sign-in and then the Picker. Called again on every change of the
// settings (`fileId` of a refresh); `dotnet` receives `Gewaehlt(id, name, token)` and `Meldung(text)`.
export function verbinden(host, dotnet, config) {
    if (!host) {
        return;
    }
    const known = buttons.get(host);
    if (known) {
        known.dotnet = dotnet;
        known.config = config;
        return;
    }
    const state = { dotnet, config, onClick: null, onIntent: null };
    state.onClick = () => signIn(host, state);
    // the agent reaches for the button: load now, so the click can open the sign-in window at once
    state.onIntent = () => ready().catch(() => { });
    host.addEventListener('click', state.onClick);
    for (const type of INTENT) {
        host.addEventListener(type, state.onIntent, { passive: true });
    }
    buttons.set(host, state);
}

export function trennen(host) {
    const state = host && buttons.get(host);
    if (!state) {
        return;
    }
    host.removeEventListener('click', state.onClick);
    for (const type of INTENT) {
        host.removeEventListener(type, state.onIntent);
    }
    buttons.delete(host);
    if (pending?.host === host) {
        pending = null;
    }
}
