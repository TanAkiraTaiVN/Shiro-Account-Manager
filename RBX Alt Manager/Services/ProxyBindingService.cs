using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Manages per-account dedicated proxy assignment, parsing, sticky/round-robin distribution,
    /// and HTTP routing to ensure individual account isolation and prevent IP-based rate limiting.
    /// </summary>
    public static class ProxyBindingService
    {
        private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> _proxyClients =
            new ConcurrentDictionary<string, Lazy<HttpClient>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses a proxy string into an <see cref="IWebProxy"/> instance.
        /// Supports standard URI formats (http://, socks5://, user:pass@host:port)
        /// and provider colon formats (host:port, host:port:user:pass).
        /// </summary>
        public static IWebProxy ParseWebProxy(string proxyString)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return null;

            string raw = proxyString.Trim();

            try
            {
                ProxyItem parsed = ProxyManagerService.ParseProxy(raw);
                if (parsed == null)
                    return null;

                var proxyUri = new Uri($"{parsed.Protocol}://{parsed.Host}:{parsed.Port}");
                var proxy = new WebProxy(proxyUri, false);

                if (!string.IsNullOrEmpty(parsed.Username))
                {
                    string user = Uri.UnescapeDataString(parsed.Username);
                    string pass = !string.IsNullOrEmpty(parsed.Password)
                        ? Uri.UnescapeDataString(parsed.Password)
                        : string.Empty;
                    proxy.Credentials = new NetworkCredential(user, pass);
                }

                return proxy;
            }
            catch (Exception ex)
            {
                Program.Logger.Warn($"[ProxyBindingService] Failed to parse proxy '{proxyString}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Determines whether proxy routing is currently allowed by global settings.
        /// When <c>AccountManager.General</c> has an explicit <c>UseProxies</c> key set to <c>false</c>,
        /// proxy routing is bypassed so disabling proxies in Settings takes effect immediately.
        /// </summary>
        internal static bool IsProxyRoutingAllowed()
        {
            if (AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.UseProxies))
                return AccountManager.General.Get<bool>(AppSettingsKeys.General.UseProxies, false);

            return true;
        }

        /// <summary>
        /// Retrieves the configured HttpClient for an account, routing through its dedicated proxy if present.
        /// If no proxy is assigned (or global proxy support is disabled), falls back cleanly to the global shared client.
        /// </summary>
        public static HttpClient GetClient(Account account)
        {
            if (account == null || !IsProxyRoutingAllowed())
                return SharedHttpClient.Get();

            if (string.IsNullOrWhiteSpace(account.Proxy) &&
                AccountManager.General != null &&
                AccountManager.General.Exists(AppSettingsKeys.General.UseProxies) &&
                AccountManager.General.Get<bool>(AppSettingsKeys.General.UseProxies, false))
            {
                try
                {
                    var configuredProxies = ProxyManagerService.GetCachedProxyStrings();
                    if (configuredProxies.Count > 0)
                    {
                        var snapshot = AccountManager.GetAccountsSnapshot();
                        var targets = snapshot.Count > 0 && snapshot.Contains(account)
                            ? snapshot
                            : new[] { account };
                        DistributeProxiesSticky(targets, configuredProxies);
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(account.Proxy))
                return SharedHttpClient.Get();

            return GetClient(account.Proxy);
        }

        /// <summary>
        /// Retrieves a pooled HttpClient bound to the specified proxy string.
        /// </summary>
        public static HttpClient GetClient(string proxyString)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return SharedHttpClient.Get();

            string normalized = proxyString.Trim();
            return _proxyClients.GetOrAdd(normalized, key => new Lazy<HttpClient>(() =>
            {
                IWebProxy webProxy = ParseWebProxy(key);
                if (webProxy == null)
                {
                    Program.Logger.Warn($"[ProxyBindingService] Proxy '{key}' is invalid. Routing via default gateway.");
                    return SharedHttpClient.Get();
                }

                var handler = new SocketsHttpHandler
                {
                    Proxy = webProxy,
                    UseProxy = true,
                    UseCookies = false,
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true
                };

                var client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

                return client;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>
        /// Assigns or unassigns a dedicated proxy for a specific account.
        /// </summary>
        public static void AssignProxy(Account account, string proxyString)
        {
            if (account == null) return;
            account.Proxy = string.IsNullOrWhiteSpace(proxyString) ? null : proxyString.Trim();
        }

        /// <summary>
        /// Distributes a list of proxies across accounts in a round-robin cycle.
        /// </summary>
        public static void DistributeProxiesRoundRobin(IEnumerable<Account> accounts, IEnumerable<string> proxies)
        {
            if (accounts == null || proxies == null) return;

            var proxyList = proxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            if (proxyList.Count == 0) return;

            int index = 0;
            foreach (var account in accounts)
            {
                if (account == null) continue;
                account.Proxy = proxyList[index % proxyList.Count];
                index++;
            }
        }

        /// <summary>
        /// Distributes proxies stickily: keeps valid current assignments, assigns least-used proxies to unassigned accounts.
        /// </summary>
        public static void DistributeProxiesSticky(IEnumerable<Account> accounts, IEnumerable<string> proxies)
        {
            if (accounts == null || proxies == null) return;

            var proxyList = proxies
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (proxyList.Count == 0) return;

            var proxyUsage = proxyList.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);

            var accountList = accounts.Where(a => a != null).ToList();

            // First pass: tally accounts that already use one of the supplied proxies
            var needsAssignment = new List<Account>();
            foreach (var acc in accountList)
            {
                if (!string.IsNullOrWhiteSpace(acc.Proxy) && proxyUsage.ContainsKey(acc.Proxy))
                {
                    proxyUsage[acc.Proxy]++;
                }
                else
                {
                    needsAssignment.Add(acc);
                }
            }

            // Second pass: assign remaining accounts to the proxy with lowest current usage
            foreach (var acc in needsAssignment)
            {
                var leastUsed = proxyUsage.MinBy(kvp => kvp.Value);
                acc.Proxy = leastUsed.Key;
                proxyUsage[leastUsed.Key]++;
            }
        }

        /// <summary>
        /// Tests connectivity and latency of a proxy against Roblox services.
        /// </summary>
        public static async Task<(bool Success, TimeSpan Latency, string Error)> TestProxyAsync(string proxyString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return (false, TimeSpan.Zero, "Proxy string is empty.");

            IWebProxy webProxy = ParseWebProxy(proxyString);
            if (webProxy == null)
                return (false, TimeSpan.Zero, "Invalid proxy format.");

            using var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)");

            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync("https://users.roblox.com/v1/users/authenticated", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                // 401 Unauthorized is expected because we haven't sent a cookie, which proves proxy connection and TLS reached Roblox successfully!
                bool reachesRoblox = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden;
                if (reachesRoblox)
                    return (true, sw.Elapsed, null);

                return (false, sw.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (false, sw.Elapsed, ex.Message);
            }
        }

        /// <summary>
        /// Disposes cached proxy HttpClients that are no longer in the active proxy list.
        /// </summary>
        public static void PruneClients(IEnumerable<string> activeProxies)
        {
            var activeSet = activeProxies != null
                ? new HashSet<string>(
                    activeProxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()),
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var sharedClient = SharedHttpClient.Get();
            foreach (var entry in _proxyClients)
            {
                if (!activeSet.Contains(entry.Key) && _proxyClients.TryRemove(entry.Key, out var removed))
                {
                    if (removed.IsValueCreated)
                    {
                        try
                        {
                            var client = removed.Value;
                            if (client != null && !ReferenceEquals(client, sharedClient))
                                client.Dispose();
                        }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Disposes all cached proxy HttpClients.
        /// </summary>
        public static void ClearClients()
        {
            var sharedClient = SharedHttpClient.Get();
            foreach (var entry in _proxyClients)
            {
                if (_proxyClients.TryRemove(entry.Key, out var removed) && removed.IsValueCreated)
                {
                    try
                    {
                        var client = removed.Value;
                        if (client != null && !ReferenceEquals(client, sharedClient))
                            client.Dispose();
                    }
                    catch { }
                }
            }
            _proxyClients.Clear();
        }
    }
}
