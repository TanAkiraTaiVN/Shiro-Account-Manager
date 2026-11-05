#nullable enable
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
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Manages per-account dedicated proxy assignment, parsing, sticky/round-robin distribution,
    /// and HTTP routing to ensure individual account isolation and prevent IP-based rate limiting.
    /// </summary>
    public static class ProxyBindingService
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static ISettingsService _settings = AppServices.GeneralSettings;
        public static ISettingsService Settings
        {
            get => _settings;
            set => _settings = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> _proxyClients =
            new ConcurrentDictionary<string, Lazy<HttpClient>>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Threading.Lock _distributionLock = new();
        private const string NoRedirectKeyPrefix = "no-redirect_";

        /// <summary>
        /// Parses a proxy string into an <see cref="IWebProxy"/> instance.
        /// Supports standard URI formats (http://, socks5://, user:pass@host:port)
        /// and provider colon formats (host:port, host:port:user:pass).
        /// </summary>
        public static IWebProxy? ParseWebProxy(string? proxyString)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return null;

            string raw = proxyString.Trim();

            try
            {
                ProxyItem? parsed = ProxyManagerService.ParseProxy(raw);
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
                _logger.Warn($"[ProxyBindingService] Failed to parse proxy '{proxyString}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Determines whether proxy routing is currently allowed by global settings.
        /// When <c>GeneralSettings</c> has an explicit <c>UseProxies</c> key set to <c>false</c>,
        /// proxy routing is bypassed so disabling proxies in Settings takes effect immediately.
        /// </summary>
        internal static bool IsProxyRoutingAllowed()
        {
            if (_settings.Exists(AppSettingsKeys.General.UseProxies))
                return _settings.Get<bool>(AppSettingsKeys.General.UseProxies, false);

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
                _settings.Exists(AppSettingsKeys.General.UseProxies) &&
                _settings.Get<bool>(AppSettingsKeys.General.UseProxies, false))
            {
                try
                {
                    lock (_distributionLock)
                    {
                        if (string.IsNullOrWhiteSpace(account.Proxy))
                        {
                            var configuredProxies = ProxyManagerService.GetCachedProxyStrings();
                            if (configuredProxies.Count > 0)
                            {
                                var snapshot = AppServices.AccountRepository.GetAll();
                                var targets = snapshot.Count > 0 && snapshot.Contains(account)
                                    ? snapshot
                                    : new[] { account };
                                DistributeProxiesSticky(targets, configuredProxies);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to distribute sticky proxies for account {account.Username}: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(account.Proxy))
                return SharedHttpClient.Get();

            return GetClient(account.Proxy);
        }

        /// <summary>
        /// Retrieves an HttpClient configured with AllowAutoRedirect = false for fast header/redirect probes.
        /// </summary>
        public static HttpClient GetNoRedirectClient(Account? account)
        {
            if (account == null || !IsProxyRoutingAllowed() || string.IsNullOrWhiteSpace(account.Proxy))
                return SharedHttpClient.GetNoRedirect();

            string normalized = account.Proxy.Trim();
            string key = $"{NoRedirectKeyPrefix}{normalized}";
            return _proxyClients.GetOrAdd(key, k => new Lazy<HttpClient>(() =>
            {
                IWebProxy? webProxy = ParseWebProxy(normalized);
                if (webProxy == null)
                {
                    _logger.Error($"[ProxyBindingService] Proxy '{normalized}' is invalid. Fail-Closed enforced to prevent IP leak.");
                    throw new InvalidOperationException($"Proxy '{normalized}' failed to parse. Fallback to direct IP blocked to prevent IP leak.");
                }

                var handler = new SocketsHttpHandler
                {
                    Proxy = webProxy,
                    UseProxy = true,
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true
                };

                var client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                return client;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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
                IWebProxy? webProxy = ParseWebProxy(key);
                if (webProxy == null)
                {
                    _logger.Error($"[ProxyBindingService] Proxy '{key}' is invalid. Fail-Closed enforced to prevent IP leak.");
                    throw new InvalidOperationException($"Proxy '{key}' failed to parse. Fallback to direct IP blocked to prevent IP leak.");
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

            var proxyUsage = new Dictionary<string, (int Usage, int Order)>(proxyList.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < proxyList.Count; i++)
            {
                proxyUsage[proxyList[i]] = (0, i);
            }

            // First pass: tally accounts that already use one of the supplied proxies
            var needsAssignment = new List<Account>();
            foreach (var acc in accounts)
            {
                if (acc == null) continue;
                if (!string.IsNullOrWhiteSpace(acc.Proxy) && proxyUsage.TryGetValue(acc.Proxy, out var current))
                {
                    proxyUsage[acc.Proxy] = (current.Usage + 1, current.Order);
                }
                else
                {
                    needsAssignment.Add(acc);
                }
            }

            if (needsAssignment.Count == 0)
                return;

            // Second pass: O((M + N) log M) min-heap assignment with deterministic tie-breaking by original proxy order
            var minHeap = new PriorityQueue<string, (int Usage, int Order)>(proxyList.Count);
            foreach (string proxy in proxyList)
            {
                minHeap.Enqueue(proxy, proxyUsage[proxy]);
            }

            foreach (var acc in needsAssignment)
            {
                if (minHeap.TryDequeue(out string? leastUsedProxy, out var priority))
                {
                    acc.Proxy = leastUsedProxy;
                    minHeap.Enqueue(leastUsedProxy, (priority.Usage + 1, priority.Order));
                }
            }
        }

        /// <summary>
        /// Tests connectivity and latency of a proxy against Roblox services.
        /// </summary>
        public static async Task<(bool Success, TimeSpan Latency, string? Error)> TestProxyAsync(string proxyString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return (false, TimeSpan.Zero, "Proxy string is empty.");

            IWebProxy? webProxy = ParseWebProxy(proxyString);
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
            var sharedNoRedirect = SharedHttpClient.GetNoRedirect();
            foreach (var entry in _proxyClients)
            {
                string rawProxyKey = entry.Key.StartsWith(NoRedirectKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? entry.Key.Substring(NoRedirectKeyPrefix.Length)
                    : entry.Key;

                if (!activeSet.Contains(rawProxyKey) && _proxyClients.TryRemove(entry.Key, out var removed))
                {
                    if (removed.IsValueCreated)
                    {
                        try
                        {
                            var client = removed.Value;
                            if (client != null && !ReferenceEquals(client, sharedClient) && !ReferenceEquals(client, sharedNoRedirect))
                                client.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"Error disposing proxy client: {ex.Message}");
                        }
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
            var sharedNoRedirect = SharedHttpClient.GetNoRedirect();
            foreach (var entry in _proxyClients)
            {
                if (_proxyClients.TryRemove(entry.Key, out var removed) && removed.IsValueCreated)
                {
                    try
                    {
                        var client = removed.Value;
                        if (client != null && !ReferenceEquals(client, sharedClient) && !ReferenceEquals(client, sharedNoRedirect))
                            client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Error disposing proxy client: {ex.Message}");
                    }
                }
            }
            _proxyClients.Clear();
        }
    }
}
