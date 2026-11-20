#nullable enable
using System;

namespace Shiro.Services
{
    /// <summary>
    /// Browser JavaScript hooks injected into WebView2 containers to capture HBA ECDSA keys
    /// during sign-in and restore persisted HBA keys during moderation review sessions.
    /// </summary>
    internal static class HbaBrowserScripts
    {
        internal static string BuildCollectionScript() => @"
(() => {
    try {
        const h = (window.location && window.location.hostname ? window.location.hostname : '').toLowerCase();
        if (!window.location || window.location.protocol !== 'https:' || (h !== 'roblox.com' && !h.endsWith('.roblox.com'))) return;

        if (window.__shiroHbaHookInstalled) return;
        window.__shiroHbaHookInstalled = true;

        const state = window.__shiroHbaState = window.__shiroHbaState || {
            privateKeyPkcs8: '',
            publicKeySpki: '',
            tokenTemplate: '',
            userAgent: navigator.userAgent || '',
            authoritativeKey: false,
            saiBound: false,
            recentSigns: []
        };

        const toBase64 = (buf) => {
            const bytes = new Uint8Array(buf);
            let bin = '';
            for (let i = 0; i < bytes.byteLength; i++) bin += String.fromCharCode(bytes[i]);
            return btoa(bin);
        };

        const notifyHost = () => {
            try {
                if (window.chrome && window.chrome.webview && (state.privateKeyPkcs8 || state.tokenTemplate)) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'shiro_hba_captured',
                        privateKeyPkcs8: state.privateKeyPkcs8,
                        publicKeySpki: state.publicKeySpki,
                        tokenTemplate: state.tokenTemplate,
                        userAgent: state.userAgent,
                        authoritativeKey: !!state.authoritativeKey,
                        saiBound: !!state.saiBound
                    }));
                }
            } catch (_) { /* ignore */ }
        };

        const getHbaDbConfig = () => {
            let dbName = 'hbaDB';
            let storeName = 'hbaObjectStore';
            let keyName = 'hba_keys';
            try {
                const meta = document.querySelector && document.querySelector('meta[name=""hardware-backed-authentication-data""]');
                if (meta) {
                    const ds = meta.dataset || {};
                    const attrDb = (meta.getAttribute && meta.getAttribute('data-hba-indexed-db-name')) || ds.hbaIndexedDbName || ds.indexedDbName;
                    const attrStore = (meta.getAttribute && meta.getAttribute('data-hba-indexed-db-obj-store-name')) || ds.hbaIndexedDbObjStoreName || ds.indexedDbObjectStoreName;
                    const attrKey = (meta.getAttribute && meta.getAttribute('data-hba-indexed-db-key-name')) || ds.hbaIndexedDbKeyName || ds.indexedDbKeyName;
                    if (attrDb) dbName = attrDb;
                    if (attrStore) storeName = attrStore;
                    if (attrKey) keyName = attrKey;
                }
            } catch (_) { /* ignore */ }
            return { dbName, storeName, keyName };
        };

        const origExport = (window.SubtleCrypto && SubtleCrypto.prototype) ? SubtleCrypto.prototype.exportKey : null;
        const getSubtle = () => (window.crypto && window.crypto.subtle) ? window.crypto.subtle : null;

        const tryExportPair = async (subtle, privKey, pubKey, authoritative = false, saiBound = false) => {
            try {
                const sc = subtle || getSubtle();
                if (!sc || !origExport) return false;
                if (!saiBound && state.saiBound && state.privateKeyPkcs8) return true;
                if (!authoritative && state.authoritativeKey && state.privateKeyPkcs8) return true;

                let exportedPriv = false;
                if (privKey && privKey.extractable) {
                    const pkcs8 = await origExport.call(sc, 'pkcs8', privKey);
                    state.privateKeyPkcs8 = toBase64(pkcs8);
                    exportedPriv = true;
                    if (authoritative || saiBound) state.authoritativeKey = true;
                    if (saiBound) state.saiBound = true;
                }
                if (pubKey && pubKey.extractable && (exportedPriv || !state.publicKeySpki)) {
                    const spki = await origExport.call(sc, 'spki', pubKey);
                    state.publicKeySpki = toBase64(spki);
                }
                if (state.privateKeyPkcs8) notifyHost();
                return exportedPriv;
            } catch (_) {
                return false;
            }
        };

        const syncFromHbaIndexedDb = async (clearNonExtractableOnLogin = false) => {
            try {
                if (!window.indexedDB) return state;
                const { dbName, storeName, keyName } = getHbaDbConfig();
                const dbNames = dbName === 'hbaDB' ? ['hbaDB'] : [dbName, 'hbaDB'];
                let existingDbNames = null;
                if (typeof indexedDB.databases === 'function') {
                    try {
                        const dbs = await indexedDB.databases();
                        if (Array.isArray(dbs)) existingDbNames = new Set(dbs.map(d => d && d.name).filter(Boolean));
                    } catch (_) { /* ignore */ }
                }
                for (const name of dbNames) {
                    if (existingDbNames && !existingDbNames.has(name)) continue;
                    await new Promise((resolve) => {
                        let settled = false;
                        const done = () => { if (!settled) { settled = true; resolve(); } };
                        const timer = setTimeout(done, 800);
                        try {
                            const openReq = indexedDB.open(name);
                            openReq.onupgradeneeded = (ev) => {
                                // Never implicitly create an empty Version-1 database without Roblox's object stores!
                                try {
                                    if (ev && ev.target && ev.target.transaction) ev.target.transaction.abort();
                                } catch (_) { /* ignore */ }
                                clearTimeout(timer);
                                done();
                            };
                            openReq.onerror = () => { clearTimeout(timer); done(); };
                            openReq.onsuccess = () => {
                                try {
                                    const db = openReq.result;
                                    if (!db.objectStoreNames.contains(storeName)) {
                                        db.close();
                                        clearTimeout(timer);
                                        done();
                                        return;
                                    }
                                    const mode = clearNonExtractableOnLogin ? 'readwrite' : 'readonly';
                                    const tx = db.transaction(storeName, mode);
                                    const store = tx.objectStore(storeName);
                                    const getReq = store.get(keyName);
                                    getReq.onerror = () => { try { db.close(); } catch (_) { /* ignore */ } clearTimeout(timer); done(); };
                                    getReq.onsuccess = async () => {
                                        try {
                                            const pair = getReq.result;
                                            const priv = pair && (pair.privateKey || pair);
                                            const pub = pair && pair.publicKey ? pair.publicKey : null;
                                            if (priv && priv.extractable) {
                                                await tryExportPair(getSubtle(), priv, pub, true);
                                            } else if (priv && clearNonExtractableOnLogin) {
                                                try { store.delete(keyName); } catch (_) { /* ignore */ }
                                            }
                                        } catch (_) { /* ignore */ }
                                        try { db.close(); } catch (_) { /* ignore */ }
                                        clearTimeout(timer);
                                        done();
                                    };
                                } catch (_) {
                                    clearTimeout(timer);
                                    done();
                                }
                            };
                        } catch (_) {
                            clearTimeout(timer);
                            done();
                        }
                    });
                }
            } catch (_) { /* ignore */ }
            return state;
        };

        window.__shiroSyncHbaFromIndexedDb = async () => {
            await syncFromHbaIndexedDb(false);
            return JSON.stringify({
                privateKeyPkcs8: state.privateKeyPkcs8 || '',
                publicKeySpki: state.publicKeySpki || '',
                tokenTemplate: state.tokenTemplate || '',
                userAgent: state.userAgent || navigator.userAgent || '',
                authoritativeKey: !!state.authoritativeKey,
                saiBound: !!state.saiBound
            });
        };

        if (window.IDBObjectStore && IDBObjectStore.prototype) {
            const wrapIdbWrite = (origFn) => function(value, key) {
                try {
                    const { storeName, keyName } = getHbaDbConfig();
                    if (this && (this.name === storeName || key === keyName || key === 'hba_keys')) {
                        const priv = value && (value.privateKey || value);
                        const pub = value && value.publicKey ? value.publicKey : null;
                        if (priv && priv.extractable) {
                            tryExportPair(getSubtle(), priv, pub, true);
                        }
                    }
                } catch (_) { /* ignore */ }
                return origFn.apply(this, arguments);
            };
            if (IDBObjectStore.prototype.put) IDBObjectStore.prototype.put = wrapIdbWrite(IDBObjectStore.prototype.put);
            if (IDBObjectStore.prototype.add) IDBObjectStore.prototype.add = wrapIdbWrite(IDBObjectStore.prototype.add);
        }

        if (window.SubtleCrypto && SubtleCrypto.prototype) {
            const origGen = SubtleCrypto.prototype.generateKey;
            const origSign = SubtleCrypto.prototype.sign;

            SubtleCrypto.prototype.generateKey = async function(algorithm, extractable, keyUsages) {
                let forceExtract = extractable;
                try {
                    const algName = typeof algorithm === 'string' ? algorithm : (algorithm && algorithm.name ? algorithm.name : '');
                    if (String(algName).toUpperCase() === 'ECDSA') forceExtract = true;
                } catch (_) { /* ignore */ }
                const result = await origGen.call(this, algorithm, forceExtract, keyUsages);
                try {
                    if (result && result.privateKey) {
                        await tryExportPair(this, result.privateKey, result.publicKey, false);
                    }
                } catch (_) { /* ignore */ }
                return result;
            };

            SubtleCrypto.prototype.sign = async function(algorithm, key, data) {
                try {
                    const algName = typeof algorithm === 'string' ? algorithm : (algorithm && algorithm.name ? algorithm.name : '');
                    if (String(algName).toUpperCase() === 'ECDSA') {
                        const view = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
                        const decoded = new TextDecoder().decode(view);
                        const pipeParts = decoded ? decoded.split('|') : [];
                        const isSaiPayload = pipeParts.length === 3 && pipeParts[0].length >= 60 && /^\d{9,12}$/.test(pipeParts[1]);
                        const isBatPayload = pipeParts.length === 4;
                        if (key && key.extractable && (isSaiPayload || isBatPayload || !state.privateKeyPkcs8)) {
                            await tryExportPair(this, key, null, isSaiPayload || isBatPayload, isSaiPayload);
                        }
                        if (isBatPayload) {
                            state.recentSigns.push(decoded);
                            if (state.recentSigns.length > 8) state.recentSigns.shift();
                        }
                    }
                } catch (_) { /* ignore */ }
                return origSign.call(this, algorithm, key, data);
            };
        }

        const inspectBoundHeader = (headerVal, reqUrl, reqMethod) => {
            try {
                if (!headerVal || typeof headerVal !== 'string' || !headerVal.includes('|')) return;
                const parts = headerVal.split('|');
                if (parts.length < 4) return;
                const ver = parts[0];
                const hb = parts[1];
                const ts = parts[2];
                const sigCount = parts.length - 3;
                const enc = (hb.length === 64 && /^[0-9a-f]+$/i.test(hb)) ? 'hex' : 'b64';
                let fullUrl = reqUrl || '';
                if (fullUrl) {
                    try { fullUrl = new URL(fullUrl, window.location.origin).href; } catch (_) { /* ignore */ }
                }
                let pathOnly = '';
                if (fullUrl) {
                    try { const u = new URL(fullUrl); pathOnly = u.pathname; } catch (_) { /* ignore */ }
                }
                const methodUp = reqMethod ? String(reqMethod).toUpperCase() : '';

                const matched = state.recentSigns.filter(s => s.includes(ts)).slice(-sigCount);
                if (matched.length === sigCount) {
                    const specs = matched.map(s => s.split('|').map(seg => {
                        if (seg === '') return '';
                        if (seg === hb) return 'HB';
                        if (seg === ts) return 'TS';
                        if (fullUrl && seg === fullUrl) return 'URL';
                        if (pathOnly && seg === pathOnly) return 'PATH';
                        if (seg.startsWith('http://') || seg.startsWith('https://')) return 'URL';
                        if (seg.startsWith('/')) return 'PATH';
                        if ((methodUp && seg.toUpperCase() === methodUp) || /^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)$/i.test(seg)) return 'M';
                        return '';
                    }).join(','));
                    if (specs.every(Boolean)) {
                        state.tokenTemplate = [ver, enc, ...specs].join('|');
                        notifyHost();
                    }
                }
            } catch (_) { /* ignore */ }
        };

        if (window.Headers && Headers.prototype) {
            const wrapHeadersMutator = (origFn) => function(name, value) {
                try {
                    if (name && String(name).toLowerCase() === 'x-bound-auth-token' && value) {
                        this.__shiroBatHeader = String(value);
                    }
                } catch (_) { /* ignore */ }
                return origFn.apply(this, arguments);
            };
            if (Headers.prototype.set) Headers.prototype.set = wrapHeadersMutator(Headers.prototype.set);
            if (Headers.prototype.append) Headers.prototype.append = wrapHeadersMutator(Headers.prototype.append);
        }

        if (window.fetch) {
            const origFetch = window.fetch;
            window.fetch = function(input, init) {
                try {
                    const url = typeof input === 'string' ? input : (input && input.url ? input.url : '');
                    const method = (init && init.method) || (input && input.method) || 'GET';
                    const hdrs = (init && init.headers) || (input && input.headers);
                    if (hdrs) {
                        let bat = hdrs.__shiroBatHeader || '';
                        if (!bat && typeof hdrs.get === 'function') bat = hdrs.get('x-bound-auth-token') || '';
                        else if (!bat && Array.isArray(hdrs)) {
                            const found = hdrs.find(x => x && String(x[0]).toLowerCase() === 'x-bound-auth-token');
                            if (found) bat = found[1];
                        } else if (!bat && typeof hdrs === 'object') {
                            for (const k of Object.keys(hdrs)) {
                                if (k.toLowerCase() === 'x-bound-auth-token') { bat = hdrs[k]; break; }
                            }
                        }
                        if (bat) inspectBoundHeader(bat, url, method);
                    }
                } catch (_) { /* ignore */ }
                return origFetch.apply(this, arguments);
            };
        }

        if (window.XMLHttpRequest && XMLHttpRequest.prototype) {
            const origOpen = XMLHttpRequest.prototype.open;
            const origSetHeader = XMLHttpRequest.prototype.setRequestHeader;
            XMLHttpRequest.prototype.open = function(method, url) {
                this.__shiroMethod = method;
                this.__shiroUrl = url;
                return origOpen.apply(this, arguments);
            };
            XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
                if (name && String(name).toLowerCase() === 'x-bound-auth-token') {
                    inspectBoundHeader(value, this.__shiroUrl || '', this.__shiroMethod || 'GET');
                }
                return origSetHeader.apply(this, arguments);
            };
        }
    } catch (_) { /* ignore */ }
})();";

        internal static string BuildRestoreModerationScript(
            string? privateKeyPkcs8Base64,
            string? publicKeySpkiBase64 = null,
            string? tokenTemplate = null)
        {
            if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) &&
                RobloxHbaService.TryDeriveSpkiPublicKeyBase64(privateKeyPkcs8Base64, out string derivedSpki))
            {
                publicKeySpkiBase64 = derivedSpki;
            }

            static string EscapeJsSingleQuoted(string value) =>
                value.Replace("\\", "\\\\")
                     .Replace("'", "\\'")
                     .Replace("\r", "\\r")
                     .Replace("\n", "\\n")
                     .Replace("\u2028", "\\u2028")
                     .Replace("\u2029", "\\u2029");

            string privEscaped = EscapeJsSingleQuoted(privateKeyPkcs8Base64.Trim());
            string pubEscaped = EscapeJsSingleQuoted((publicKeySpkiBase64 ?? string.Empty).Trim());
            string tplEscaped = EscapeJsSingleQuoted(RobloxHbaService.NormalizeTemplate(tokenTemplate));

            return $@"
(() => {{
    try {{
        const h = (window.location && window.location.hostname ? window.location.hostname : '').toLowerCase();
        if (!window.location || window.location.protocol !== 'https:' || (h !== 'roblox.com' && !h.endsWith('.roblox.com'))) return;

        const privB64 = '{privEscaped}';
        const pubB64 = '{pubEscaped}';
        const tpl = '{tplEscaped}';
        if (!privB64 || !window.SubtleCrypto || !SubtleCrypto.prototype) return;

        window.__shiroHbaState = window.__shiroHbaState || {{}};
        window.__shiroHbaState.authoritativeKey = true;
        if (pubB64) window.__shiroHbaState.publicKeySpki = pubB64;
        if (tpl) window.__shiroHbaState.tokenTemplate = tpl;

        const fromB64 = (b64) => {{
            const bin = atob(b64);
            const buf = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
            return buf.buffer;
        }};

        let cachedPairPromise = null;
        const getImportedPair = (subtle) => {{
            if (!cachedPairPromise) {{
                cachedPairPromise = (async () => {{
                    const alg = {{ name: 'ECDSA', namedCurve: 'P-256' }};
                    const privKey = await subtle.importKey('pkcs8', fromB64(privB64), alg, false, ['sign']);
                    let pubKey = null;
                    if (pubB64) {{
                        try {{ pubKey = await subtle.importKey('spki', fromB64(pubB64), alg, true, ['verify']); }} catch (_) {{ /* ignore */ }}
                    }}
                    return {{ privateKey: privKey, publicKey: pubKey }};
                }})();
            }}
            return cachedPairPromise;
        }};

        try {{
            if (window.indexedDB) {{
                getImportedPair(window.crypto && window.crypto.subtle).then((pair) => {{
                    if (!pair || !pair.privateKey) return;
                    try {{
                        const openReq = indexedDB.open('hbaDB', 1);
                        openReq.onupgradeneeded = () => {{
                            const db = openReq.result;
                            if (!db.objectStoreNames.contains('hbaObjectStore')) db.createObjectStore('hbaObjectStore');
                        }};
                        openReq.onsuccess = () => {{
                            try {{
                                const db = openReq.result;
                                if (db.objectStoreNames.contains('hbaObjectStore')) {{
                                    const tx = db.transaction('hbaObjectStore', 'readwrite');
                                    tx.objectStore('hbaObjectStore').put(pair, 'hba_keys');
                                }}
                                db.close();
                            }} catch (_) {{ /* ignore */ }}
                        }};
                    }} catch (_) {{ /* ignore */ }}
                }}).catch(() => {{}});
            }}
        }} catch (_) {{ /* ignore */ }}

        const origGen = SubtleCrypto.prototype.generateKey;
        SubtleCrypto.prototype.generateKey = async function(algorithm, extractable, keyUsages) {{
            try {{
                const algName = typeof algorithm === 'string' ? algorithm : (algorithm && algorithm.name ? algorithm.name : '');
                if (String(algName).toUpperCase() === 'ECDSA') {{
                    const imported = await getImportedPair(this);
                    if (imported && imported.privateKey) {{
                        return imported;
                    }}
                }}
            }} catch (_) {{ /* ignore */ }}
            return origGen.call(this, algorithm, extractable, keyUsages);
        }};
    }} catch (_) {{ /* ignore */ }}
}})();";
        }
    }
}
