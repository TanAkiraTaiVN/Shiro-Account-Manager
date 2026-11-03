#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Shiro.Services
{
    /// <summary>
    /// Cryptographic Hardware-Backed Authentication (HBA / BAT) engine for Roblox sessions.
    /// Captures ECDSA P-256 session-binding keys during browser sign-in and signs outgoing C#
    /// API/ticket requests with <c>x-bound-auth-token</c> so sessions survive VPN/IP switches.
    /// </summary>
    public static partial class RobloxHbaService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        public const string BoundAuthHeaderName = "x-bound-auth-token";
        public const string DefaultTemplate = "v1|b64|HB,TS,URL,M|,TS,URL,M";

        private static readonly ConcurrentDictionary<string, byte[]> CachedPkcs8Keys = new(StringComparer.Ordinal);
        private static int _activeInteractiveLoginCount;

        public static bool IsInteractiveLoginActive => Volatile.Read(ref _activeInteractiveLoginCount) > 0;

        public static IDisposable BeginInteractiveLoginScope()
        {
            Interlocked.Increment(ref _activeInteractiveLoginCount);
            return new InteractiveLoginScope();
        }

        private sealed class InteractiveLoginScope : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    int val = Interlocked.Decrement(ref _activeInteractiveLoginCount);
                    if (val < 0)
                        Interlocked.Exchange(ref _activeInteractiveLoginCount, 0);
                }
            }
        }

        public sealed class CaptureSnapshot
        {
            public string? PrivateKeyPkcs8Base64 { get; set; }
            public string? PublicKeySpkiBase64 { get; set; }
            public string? TokenTemplate { get; set; }
            public string? UserAgent { get; set; }
            public string? BrowserTrackerId { get; set; }

            public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKeyPkcs8Base64);
        }

        [GeneratedRegex(@"(?:^|[&\s;])browserid=(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex BrowserIdRegex();

        public static string? ExtractBrowserTrackerIdFromCookie(string? cookieValue)
        {
            if (string.IsNullOrWhiteSpace(cookieValue))
                return null;

            Match match = BrowserIdRegex().Match(cookieValue);
            return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
                ? match.Groups[1].Value
                : null;
        }

        /// <summary>
        /// Computes a fast, offline SHA-256 fingerprint of active non-loopback network interfaces and IPv4 addresses.
        /// Changes immediately when a system VPN/TUN adapter connects or disconnects.
        /// </summary>
        public static string GetLocalNetworkSignature()
        {
            try
            {
                var parts = new List<string>(8);
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties props = nic.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g => g?.Address != null && !g.Address.ToString().Equals("0.0.0.0", StringComparison.Ordinal));
                    if (!hasGateway && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel && nic.NetworkInterfaceType != NetworkInterfaceType.Ppp)
                        continue;

                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            parts.Add($"{nic.Id}:{uni.Address}");
                        }
                    }
                }

                if (parts.Count == 0)
                    return "offline";

                parts.Sort(StringComparer.Ordinal);
                string joined = string.Join("|", parts);
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
                return Convert.ToHexStringLower(hash.AsSpan(0, 12));
            }
            catch
            {
                return "unknown";
            }
        }

        public static bool IsValidPkcs8EcdsaKey(string? base64Pkcs8)
        {
            if (string.IsNullOrWhiteSpace(base64Pkcs8))
                return false;

            try
            {
                byte[] keyBytes = Convert.FromBase64String(base64Pkcs8.Trim());
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Signs an HTTP request using the account's captured ECDSA P-256 HBA private key,
        /// producing a valid <c>x-bound-auth-token</c> header matching Roblox's Account Session Protection spec.
        /// </summary>
        public static bool TryBuildBoundAuthToken(
            string? privateKeyPkcs8Base64,
            string? requestUrl,
            string? httpMethod,
            string? bodyContent,
            string? tokenTemplate,
            long? unixTimestampOverride,
            out string boundAuthToken)
        {
            boundAuthToken = string.Empty;
            if (string.IsNullOrWhiteSpace(privateKeyPkcs8Base64) || string.IsNullOrWhiteSpace(requestUrl))
                return false;

            try
            {
                string trimmedKey = privateKeyPkcs8Base64.Trim();
                if (!CachedPkcs8Keys.TryGetValue(trimmedKey, out byte[]? keyBytes))
                {
                    keyBytes = Convert.FromBase64String(trimmedKey);
                    if (CachedPkcs8Keys.Count > 512)
                        CachedPkcs8Keys.Clear();
                    CachedPkcs8Keys[trimmedKey] = keyBytes;
                }

                string template = string.IsNullOrWhiteSpace(tokenTemplate) ? DefaultTemplate : tokenTemplate.Trim();
                string[] tplParts = template.Split('|');
                if (tplParts.Length < 3)
                {
                    tplParts = DefaultTemplate.Split('|');
                }

                string version = string.IsNullOrWhiteSpace(tplParts[0]) ? "v1" : tplParts[0];
                bool useHexHash = string.Equals(tplParts[1], "hex", StringComparison.OrdinalIgnoreCase);

                byte[] bodyBytes = string.IsNullOrEmpty(bodyContent)
                    ? Array.Empty<byte>()
                    : Encoding.UTF8.GetBytes(bodyContent);
                byte[] sha256Bytes = SHA256.HashData(bodyBytes);
                string hashedBody = useHexHash
                    ? Convert.ToHexStringLower(sha256Bytes)
                    : Convert.ToBase64String(sha256Bytes);

                long ts = unixTimestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string timestampStr = ts.ToString(CultureInfo.InvariantCulture);
                string methodUpper = string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod.Trim().ToUpperInvariant();
                string fullUrl = requestUrl.Trim();
                string pathAndQuery = Uri.TryCreate(fullUrl, UriKind.Absolute, out Uri? parsedUri) && parsedUri != null
                    ? parsedUri.PathAndQuery
                    : fullUrl;

                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);

                var outputSegments = new List<string>(tplParts.Length + 1)
                {
                    version,
                    hashedBody,
                    timestampStr
                };

                for (int i = 2; i < tplParts.Length; i++)
                {
                    string sigSpec = tplParts[i];
                    string payloadToSign = BuildPayloadFromSpec(sigSpec, hashedBody, timestampStr, fullUrl, pathAndQuery, methodUpper);
                    byte[] sigBytes = ecdsa.SignData(
                        Encoding.UTF8.GetBytes(payloadToSign),
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    outputSegments.Add(Convert.ToBase64String(sigBytes));
                }

                boundAuthToken = string.Join("|", outputSegments);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[RobloxHbaService] Failed to sign x-bound-auth-token: {ex.Message}");
                return false;
            }
        }

        private static string BuildPayloadFromSpec(
            string sigSpec,
            string hashedBody,
            string timestampStr,
            string fullUrl,
            string pathAndQuery,
            string methodUpper)
        {
            string[] tokens = sigSpec.Split(',');
            var values = new string[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                values[i] = tokens[i].Trim() switch
                {
                    "HB" => hashedBody,
                    "TS" => timestampStr,
                    "URL" => fullUrl,
                    "PATH" => pathAndQuery,
                    "M" => methodUpper,
                    _ => string.Empty
                };
            }
            return string.Join("|", values);
        }

        /// <summary>
        /// JavaScript injected at <c>document_start</c> in WebView2 and Chrome sign-in containers.
        /// Hooks <c>SubtleCrypto.prototype.generateKey</c> to force <c>extractable = true</c> on Roblox's
        /// HBA ECDSA P-256 keypair, exports PKCS#8/SPKI keys, and self-calibrates the live BAT signing template.
        /// </summary>
        public static string GetBrowserCaptureScript() => @"
(() => {
    try {
        if (window.__shiroHbaHookInstalled) return;
        window.__shiroHbaHookInstalled = true;

        const state = window.__shiroHbaState = window.__shiroHbaState || {
            privateKeyPkcs8: '',
            publicKeySpki: '',
            tokenTemplate: '',
            userAgent: navigator.userAgent || '',
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
                        userAgent: state.userAgent
                    }));
                }
            } catch (_) { /* ignore */ }
        };

        if (window.SubtleCrypto && SubtleCrypto.prototype) {
            const origGen = SubtleCrypto.prototype.generateKey;
            const origExport = SubtleCrypto.prototype.exportKey;
            const origSign = SubtleCrypto.prototype.sign;

            const tryExportPair = async (subtle, privKey, pubKey) => {
                try {
                    if (privKey && privKey.extractable) {
                        const pkcs8 = await origExport.call(subtle, 'pkcs8', privKey);
                        state.privateKeyPkcs8 = toBase64(pkcs8);
                    }
                    if (pubKey && pubKey.extractable) {
                        const spki = await origExport.call(subtle, 'spki', pubKey);
                        state.publicKeySpki = toBase64(spki);
                    }
                    if (state.privateKeyPkcs8) notifyHost();
                } catch (_) { /* ignore */ }
            };

            SubtleCrypto.prototype.generateKey = async function(algorithm, extractable, keyUsages) {
                let forceExtract = extractable;
                try {
                    const algName = typeof algorithm === 'string' ? algorithm : (algorithm && algorithm.name ? algorithm.name : '');
                    if (String(algName).toUpperCase() === 'ECDSA') forceExtract = true;
                } catch (_) { /* ignore */ }
                const result = await origGen.call(this, algorithm, forceExtract, keyUsages);
                try {
                    if (result && result.privateKey) {
                        await tryExportPair(this, result.privateKey, result.publicKey);
                    }
                } catch (_) { /* ignore */ }
                return result;
            };

            SubtleCrypto.prototype.sign = async function(algorithm, key, data) {
                try {
                    const algName = typeof algorithm === 'string' ? algorithm : (algorithm && algorithm.name ? algorithm.name : '');
                    if (String(algName).toUpperCase() === 'ECDSA') {
                        if (!state.privateKeyPkcs8 && key && key.extractable) {
                            await tryExportPair(this, key, null);
                        }
                        const view = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
                        const decoded = new TextDecoder().decode(view);
                        if (decoded && decoded.includes('|')) {
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
                try { fullUrl = new URL(fullUrl, window.location.origin).href; } catch (_) { /* ignore */ }
                let pathOnly = '';
                try { const u = new URL(fullUrl); pathOnly = u.pathname + u.search; } catch (_) { /* ignore */ }
                const methodUp = String(reqMethod || 'GET').toUpperCase();

                const matched = state.recentSigns.filter(s => s.includes(ts)).slice(-sigCount);
                if (matched.length > 0) {
                    const specs = matched.map(s => s.split('|').map(seg => {
                        if (seg === '') return '';
                        if (seg === hb) return 'HB';
                        if (seg === ts) return 'TS';
                        if (fullUrl && seg === fullUrl) return 'URL';
                        if (pathOnly && seg === pathOnly) return 'PATH';
                        if (seg.toUpperCase() === methodUp) return 'M';
                        return '';
                    }).join(','));
                    state.tokenTemplate = [ver, enc, ...specs].join('|');
                    notifyHost();
                }
            } catch (_) { /* ignore */ }
        };

        if (window.fetch) {
            const origFetch = window.fetch;
            window.fetch = function(input, init) {
                try {
                    const url = typeof input === 'string' ? input : (input && input.url ? input.url : '');
                    const method = (init && init.method) || (input && input.method) || 'GET';
                    const hdrs = (init && init.headers) || (input && input.headers);
                    if (hdrs) {
                        let bat = '';
                        if (typeof hdrs.get === 'function') bat = hdrs.get('x-bound-auth-token') || '';
                        else if (Array.isArray(hdrs)) {
                            const found = hdrs.find(x => x && String(x[0]).toLowerCase() === 'x-bound-auth-token');
                            if (found) bat = found[1];
                        } else if (typeof hdrs === 'object') {
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
    }
}
