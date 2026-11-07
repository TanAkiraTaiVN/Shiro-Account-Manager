#nullable enable
using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        /// <summary>
        /// Generates a signed <c>x-bound-auth-token</c> for the given account and request parameters.
        /// Returns null if the account does not possess a valid HBA private key or if signing fails.
        /// </summary>
        public static string? GenerateBoundAuthToken(
            Account account,
            string httpMethod,
            string requestUrl,
            string? timestamp = null)
        {
            if (account == null || !account.HasHbaBinding || string.IsNullOrWhiteSpace(requestUrl))
                return null;

            long? tsOverride = null;
            if (!string.IsNullOrWhiteSpace(timestamp) &&
                long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs))
            {
                tsOverride = parsedTs;
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                requestUrl,
                httpMethod,
                bodyContent: null,
                account.HbaTemplate,
                tsOverride,
                out string token))
            {
                return token;
            }

            return null;
        }

        /// <summary>
        /// Cryptographically signs the outgoing HTTP request using the account's HBA key
        /// and attaches the <c>x-bound-auth-token</c> header synchronously.
        /// When request body is present, callers should supply <paramref name="bodyContent"/> directly
        /// or use <see cref="TryAttachHbaHeaderAsync"/> to prevent sync-over-async blocking.
        /// </summary>
        public static bool TryAttachHbaHeader(HttpRequestMessage request, Account account, string? bodyContent = null)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            if (bodyContent == null && request.Content != null)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    request.Content.CopyTo(ms, context: null, CancellationToken.None);
                    bodyContent = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[RobloxHbaService] Sync body read failed: {ex.Message}");
                }
            }

            if (TryBuildBoundAuthToken(
                account.HbaPrivateKeyPkcs8,
                request.RequestUri.AbsoluteUri,
                request.Method.Method,
                bodyContent,
                account.HbaTemplate,
                unixTimestampOverride: null,
                out string token))
            {
                request.Headers.Remove(BoundAuthHeaderName);
                request.Headers.TryAddWithoutValidation(BoundAuthHeaderName, token);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Asynchronously reads request body content if needed, cryptographically signs the request
        /// using the account's HBA key, and attaches the <c>x-bound-auth-token</c> header without thread-pool starvation.
        /// </summary>
        public static async Task<bool> TryAttachHbaHeaderAsync(
            HttpRequestMessage request,
            Account account,
            CancellationToken cancellationToken = default)
        {
            if (request == null || account == null || !account.HasHbaBinding || request.RequestUri == null)
                return false;

            string? bodyContent = null;
            if (request.Content != null)
            {
                try
                {
                    bodyContent = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bodyContent = null;
                }
            }

            return TryAttachHbaHeader(request, account, bodyContent);
        }

        public static async Task<string?> RestoreWebViewModerationSessionAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2 core,
            Account account,
            CaptureSnapshot targetSnapshot)
        {
            if (core == null || account == null || string.IsNullOrWhiteSpace(account.SecurityToken))
                return null;

            if (!string.IsNullOrWhiteSpace(account.SessionUserAgent))
            {
                try { core.Settings.UserAgent = account.SessionUserAgent; }
                catch (Exception ex) { _logger.Debug($"RestoreWebViewModerationSessionAsync set UserAgent threw: {ex.Message}"); }
            }

            string[] cookieDomains = { ".roblox.com", "www.roblox.com" };
            foreach (string domain in cookieDomains)
            {
                var banCookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", account.SecurityToken, domain, "/");
                banCookie.IsHttpOnly = true;
                banCookie.IsSecure = true;
                core.CookieManager.AddOrUpdateCookie(banCookie);
            }

            if (!string.IsNullOrWhiteSpace(account.BrowserTrackerID))
            {
                string uidPart = account.UserID > 0 ? account.UserID.ToString(CultureInfo.InvariantCulture) : string.Empty;
                var trackerCookie = core.CookieManager.CreateCookie(
                    "RBXEventTrackerV2",
                    $"CreateDate={DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}&rbxid={uidPart}&browserid={account.BrowserTrackerID}",
                    ".roblox.com",
                    "/");
                core.CookieManager.AddOrUpdateCookie(trackerCookie);
            }

            if (!string.IsNullOrWhiteSpace(account.HbaPrivateKey))
            {
                if (targetSnapshot != null)
                {
                    targetSnapshot.PrivateKeyPkcs8Base64 = account.HbaPrivateKey;
                    if (!string.IsNullOrWhiteSpace(account.HbaPublicKey))
                        targetSnapshot.PublicKeySpkiBase64 = account.HbaPublicKey;
                    if (!string.IsNullOrWhiteSpace(account.HbaTemplate))
                        targetSnapshot.TokenTemplate = account.HbaTemplate;
                }

                string hbaScript = GetPersistedKeyInjectionScript(
                    account.HbaPrivateKey,
                    account.HbaPublicKey,
                    account.HbaTemplate);
                if (!string.IsNullOrEmpty(hbaScript))
                {
                    return await core.AddScriptToExecuteOnDocumentCreatedAsync(hbaScript);
                }
            }

            return null;
        }

        internal static bool TryDeriveSpkiPublicKeyBase64(string? pkcs8PrivateKeyBase64, out string spkiPublicKeyBase64)
        {
            spkiPublicKeyBase64 = string.Empty;
            if (string.IsNullOrWhiteSpace(pkcs8PrivateKeyBase64))
                return false;

            try
            {
                byte[] pkcs8 = Convert.FromBase64String(pkcs8PrivateKeyBase64.Trim());
                try
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
                    spkiPublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
                    return !string.IsNullOrWhiteSpace(spkiPublicKeyBase64);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string GetPersistedKeyInjectionScript(
            string? privateKeyPkcs8Base64,
            string? publicKeySpkiBase64,
            string? tokenTemplate)
        {
            if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) &&
                TryDeriveSpkiPublicKeyBase64(privateKeyPkcs8Base64, out string derivedSpki))
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
            string tplEscaped = EscapeJsSingleQuoted(NormalizeTemplate(tokenTemplate));

            return $@"
(() => {{
    try {{
        const privB64 = '{privEscaped}';
        const pubB64 = '{pubEscaped}';
        const tpl = '{tplEscaped}';
        if (!privB64 || !window.SubtleCrypto || !SubtleCrypto.prototype) return;

        window.__shiroHbaState = window.__shiroHbaState || {{}};
        window.__shiroHbaState.privateKeyPkcs8 = privB64;
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
                    const privKey = await subtle.importKey('pkcs8', fromB64(privB64), alg, true, ['sign']);
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
