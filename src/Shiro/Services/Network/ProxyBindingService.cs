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

                var proxyUri = new Uri($"{parsed.Protocol}://{ProxyManagerService.FormatHostForUri(parsed.Host)}:{parsed.Port}");
                var proxy = new WebProxy(proxyUri, false);

                if (!string.IsNullOrEmpty(parsed.Username))
                {
                    string user = parsed.IsUriEncoded ? TrySafeUnescape(parsed.Username) : parsed.Username;
                    string pass = !string.IsNullOrEmpty(parsed.Password)
                        ? (parsed.IsUriEncoded ? TrySafeUnescape(parsed.Password) : parsed.Password)
                        : string.Empty;
                    proxy.Credentials = new NetworkCredential(user, pass);
                }

                return proxy;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ProxyBindingService] Failed to parse proxy '{ProxyManagerService.RedactProxy(proxyString)}': {LogRedactor.Redact(ex.Message)}");
                return null;
            }
        }

        private static string TrySafeUnescape(string value)
        {
            try { return Uri.UnescapeDataString(value); }
            catch (UriFormatException) { return value; }
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

        private static string? ResolveEffectiveProxy(Account? account)
        {
            if (account == null || !IsProxyRoutingAllowed())
                return null;

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
                    _logger.Warn($"Failed to distribute sticky proxies for account {account.Username}: {LogRedactor.Redact(ex.Message)}");
                }
            }

            return string.IsNullOrWhiteSpace(account.Proxy) ? null : account.Proxy.Trim();
        }

        internal static string? GetEffectiveProxyForAccount(Account? account) => ResolveEffectiveProxy(account);

        /// <summary>
        /// Retrieves the configured HttpClient for an account, routing through its dedicated proxy if present.
        /// If no proxy is assigned (or global proxy support is disabled), falls back cleanly to the global shared client.
        /// </summary>
        public static HttpClient GetClient(Account account)
        {
            string? effectiveProxy = ResolveEffectiveProxy(account);
            if (string.IsNullOrWhiteSpace(effectiveProxy))
                return SharedHttpClient.Get();

            return GetClient(effectiveProxy);
        }

        /// <summary>
        /// Retrieves an HttpClient configured with AllowAutoRedirect = false for fast header/redirect probes.
        /// </summary>
        public static HttpClient GetNoRedirectClient(Account? account)
        {
            string? effectiveProxy = ResolveEffectiveProxy(account);
            if (string.IsNullOrWhiteSpace(effectiveProxy))
                return SharedHttpClient.GetNoRedirect();

            return GetOrCreatePooledProxyClient(
                $"{NoRedirectKeyPrefix}{effectiveProxy}",
                effectiveProxy,
                allowAutoRedirect: false);
        }

        /// <summary>
        /// Retrieves a pooled HttpClient bound to the specified proxy string.
        /// </summary>
        public static HttpClient GetClient(string proxyString)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return SharedHttpClient.Get();

            string normalized = proxyString.Trim();
            return GetOrCreatePooledProxyClient(normalized, normalized, allowAutoRedirect: true);
        }

        private static HttpClient GetOrCreatePooledProxyClient(string cacheKey, string proxyString, bool allowAutoRedirect)
        {
            if (_proxyClients.TryGetValue(cacheKey, out var cached))
                return cached.Value;

            // Validate before inserting so an invalid proxy string is never cached as a faulted Lazy<HttpClient>.
            IWebProxy? webProxy = ParseWebProxy(proxyString);
            if (webProxy == null)
            {
                string safeProxy = ProxyManagerService.RedactProxy(proxyString);
                _logger.Error($"[ProxyBindingService] Proxy '{safeProxy}' is invalid. Fail-Closed enforced to prevent IP leak.");
                throw new InvalidOperationException($"Proxy '{safeProxy}' failed to parse. Fallback to direct IP blocked to prevent IP leak.");
            }

            return _proxyClients.GetOrAdd(cacheKey, _ => new Lazy<HttpClient>(
                () => CreateProxyHttpClient(webProxy, allowAutoRedirect),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static HttpClient CreateProxyHttpClient(IWebProxy webProxy, bool allowAutoRedirect)
        {
            var handler = RobloxHttpHandlerHelper.CreateDefaultHandler(
                allowAutoRedirect: allowAutoRedirect,
                proxy: webProxy,
                useProxy: true,
                connectTimeout: allowAutoRedirect ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5),
                useConnectionPoolSettings: true);

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = allowAutoRedirect ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RobloxHttpHandlerHelper.ChromeUserAgent);
            if (allowAutoRedirect)
            {
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            }

            return client;
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

        private static List<string> NormalizeDistinctProxies(IEnumerable<string>? proxies) =>
            proxies == null
                ? new List<string>()
                : proxies
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

        private static Dictionary<string, (int Usage, int Order)> CreateUsageMap(List<string> proxyList)
        {
            var usage = new Dictionary<string, (int Usage, int Order)>(proxyList.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < proxyList.Count; i++)
                usage[proxyList[i]] = (0, i);
            return usage;
        }

        /// <summary>
        /// Distributes proxies stickily: keeps valid current assignments, assigns least-used proxies to unassigned accounts.
        /// </summary>
        public static void DistributeProxiesSticky(IEnumerable<Account> accounts, IEnumerable<string> proxies)
        {
            if (accounts == null || proxies == null) return;

            var proxyList = NormalizeDistinctProxies(proxies);
            if (proxyList.Count == 0) return;

            var proxyUsage = CreateUsageMap(proxyList);

            // First pass: tally accounts that already use one of the supplied proxies
            var needsAssignment = new List<Account>();
            foreach (var acc in accounts)
            {
                if (acc == null) continue;
                if (!string.IsNullOrWhiteSpace(acc.Proxy))
                {
                    if (proxyUsage.TryGetValue(acc.Proxy.Trim(), out var current))
                        proxyUsage[acc.Proxy.Trim()] = (current.Usage + 1, current.Order);
                }
                else
                {
                    needsAssignment.Add(acc);
                }
            }

            if (needsAssignment.Count > 0)
                AssignLeastUsedFromHeap(needsAssignment, proxyList, proxyUsage);
        }

        /// <summary>
        /// Rebalances accounts that were assigned to proxies removed from the active proxy pool,
        /// leaving unassigned, manual, and surviving pool accounts untouched.
        /// </summary>
        public static int RebalanceRemovedPoolProxies(
            IEnumerable<Account> accounts,
            IReadOnlyCollection<string> removedProxies,
            IEnumerable<string> activeProxies)
        {
            if (accounts == null || removedProxies == null || removedProxies.Count == 0)
                return 0;

            var activeList = NormalizeDistinctProxies(activeProxies);
            var activeSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
            var removedSet = new HashSet<string>(
                NormalizeDistinctProxies(removedProxies).Where(p => !activeSet.Contains(p)),
                StringComparer.OrdinalIgnoreCase);
            if (removedSet.Count == 0)
                return 0;

            var proxyUsage = CreateUsageMap(activeList);
            var toRebalance = new List<Account>();
            foreach (var acc in accounts)
            {
                if (acc == null || string.IsNullOrWhiteSpace(acc.Proxy))
                    continue;

                string current = acc.Proxy.Trim();
                if (removedSet.Contains(current))
                    toRebalance.Add(acc);
                else if (proxyUsage.TryGetValue(current, out var usage))
                    proxyUsage[current] = (usage.Usage + 1, usage.Order);
            }

            if (toRebalance.Count == 0)
                return 0;

            if (activeList.Count == 0)
            {
                foreach (var acc in toRebalance)
                    acc.Proxy = string.Empty;
                return toRebalance.Count;
            }

            AssignLeastUsedFromHeap(toRebalance, activeList, proxyUsage);
            return toRebalance.Count;
        }

        private static void AssignLeastUsedFromHeap(
            List<Account> targets,
            List<string> proxyList,
            Dictionary<string, (int Usage, int Order)> proxyUsage)
        {
            // O((M + N) log M) min-heap assignment with deterministic tie-breaking by original proxy order
            var minHeap = new PriorityQueue<string, (int Usage, int Order)>(proxyList.Count);
            foreach (string proxy in proxyList)
                minHeap.Enqueue(proxy, proxyUsage[proxy]);

            foreach (var acc in targets)
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
        internal static bool IsProbeHttpSuccess(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return (code >= 200 && code <= 299) || statusCode == HttpStatusCode.Unauthorized;
        }

        public static async Task<(bool Success, TimeSpan Latency, string? Error)> TestProxyAsync(string proxyString, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(proxyString))
                return (false, TimeSpan.Zero, "Proxy string is empty.");

            IWebProxy? webProxy = ParseWebProxy(proxyString);
            if (webProxy == null)
                return (false, TimeSpan.Zero, "Invalid proxy format.");

            using var handler = RobloxHttpHandlerHelper.CreateDefaultHandler(
                allowAutoRedirect: true,
                proxy: webProxy,
                useProxy: true,
                connectTimeout: TimeSpan.FromSeconds(10),
                useConnectionPoolSettings: false);

            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RobloxHttpHandlerHelper.ChromeUserAgent);

            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync("https://users.roblox.com/v1/users/authenticated", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                // 401 Unauthorized is expected without a cookie; 403 indicates blocked/WAF traffic rather than a usable proxy.
                if (IsProbeHttpSuccess(response.StatusCode))
                    return (true, sw.Elapsed, null);

                return (false, sw.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (false, sw.Elapsed, LogRedactor.Redact(ex.Message));
            }
        }

        /// <summary>
        /// Disposes cached proxy HttpClients that are no longer in the active proxy list.
        /// </summary>
        public static void PruneClients(IEnumerable<string> activeProxies)
        {
            var activeSet = new HashSet<string>(NormalizeDistinctProxies(activeProxies), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _proxyClients)
            {
                string rawProxyKey = entry.Key.StartsWith(NoRedirectKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? entry.Key.Substring(NoRedirectKeyPrefix.Length)
                    : entry.Key;

                if (!activeSet.Contains(rawProxyKey) && _proxyClients.TryRemove(entry.Key, out var removed))
                    RetireClient(removed);
            }
        }

        private static void RetireClient(Lazy<HttpClient> removed)
        {
            if (!removed.IsValueCreated)
                return;

            try
            {
                var client = removed.Value;
                if (client != null && !SharedHttpClient.IsSharedInstance(client))
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
                    {
                        SafeExecuteHelper.Try(client.Dispose);
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error retiring proxy client: {LogRedactor.Redact(ex.Message)}");
            }
        }

        /// <summary>
        /// Disposes all cached proxy HttpClients.
        /// </summary>
        internal static void ClearClients()
        {
            foreach (var entry in _proxyClients)
            {
                if (_proxyClients.TryRemove(entry.Key, out var removed))
                {
                    RetireClient(removed);
                }
            }
            _proxyClients.Clear();
        }
    }
}
