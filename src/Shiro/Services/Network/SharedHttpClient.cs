#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static class SharedHttpClient
    {
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> Clients =
            new ConcurrentDictionary<string, Lazy<HttpClient>>(StringComparer.OrdinalIgnoreCase);

        public static HttpClient Get(string name = "default")
        {
            return Clients.GetOrAdd(name ?? "default", key => new Lazy<HttpClient>(() => CreateClient(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        public static HttpClient GetNoRedirect() => Get("no-redirect");

        internal static void SetTestClient(string name, HttpClient client) =>
            Clients[name ?? "default"] = new Lazy<HttpClient>(() => client);

        internal static void ResetTestClient(string name) =>
            Clients.TryRemove(name ?? "default", out _);

        /// <summary>
        /// Creates a short-lived client for an explicitly selected proxy.
        /// Proxy candidates are intentionally not pooled together: a proxy
        /// handler owns connection state for one candidate and must be
        /// disposed before the next candidate is tested.  Keeping creation in
        /// this factory still gives every probe the same decompression,
        /// timeout and User-Agent defaults as the shared clients.
        /// </summary>
        public static HttpClient CreateProxyClient(IWebProxy proxy, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                timeout = TimeSpan.FromSeconds(5);

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = proxy != null,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli,
                UseCookies = false,
                ConnectTimeout = timeout
            };

            HttpClient client = new HttpClient(handler, true)
            {
                Timeout = timeout
            };
            ConfigureHeaders(client);

            return client;
        }

        /// <summary>
        /// Creates a disposable client for a long-running operation that needs
        /// a timeout different from the pooled clients.  The updater is the
        /// main caller: its archive download may legitimately take minutes,
        /// while normal API calls keep the 30-second shared timeout.
        /// </summary>
        public static HttpClient CreateDedicatedClient(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                timeout = TimeSpan.FromSeconds(30);

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseCookies = false,
                UseProxy = true
            };

            HttpClient client = new HttpClient(handler, true)
            {
                Timeout = timeout,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            ConfigureHeaders(client);

            return client;
        }

        public static void DisposeAll()
        {
            foreach (KeyValuePair<string, Lazy<HttpClient>> entry in Clients.ToArray())
            {
                if (!entry.Value.IsValueCreated)
                    continue;

                try { entry.Value.Value.Dispose(); }
                catch (Exception ex) { _logger.Debug($"Disposing SharedHttpClient threw: {ex.Message}"); }
            }

            Clients.Clear();
        }

        public static async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            bool retryTransient = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            bool shouldRetry = retryTransient && IsSafeToRetry(request.Method);

            // A non-retried request can be sent as-is. Cloning and buffering
            // request content is only needed when a second attempt may be
            // created, so this fast path avoids an allocation for the common
            // avatar/metadata calls.
            if (!shouldRetry)
                return await Get().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // HttpRequestMessage instances are single-use. Buffer optional
            // content once and clone the request for each safe retry so a
            // transient 429/5xx does not turn the second attempt into a local
            // InvalidOperationException.
            byte[]? requestContent = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            int attempts = 3;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                HttpResponseMessage? response = null;
                try
                {
                    using HttpRequestMessage attemptRequest = CloneRequest(request, requestContent);
                    response = await Get().SendAsync(attemptRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!IsTransient(response.StatusCode) || attempt == attempts)
                        return response;

                    TimeSpan delay = GetRetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < attempts && (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("HTTP request failed without a response.");
        }

        public static async Task<string> GetStringAsync(
            string url,
            bool retryTransient = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await SendAsync(request, retryTransient, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        public static async Task<byte[]> GetByteArrayAsync(
            string url,
            bool retryTransient = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await SendAsync(request, retryTransient, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage source, byte[]? content)
        {
            HttpRequestMessage clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version,
                VersionPolicy = source.VersionPolicy
            };

            foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (content != null)
            {
                ByteArrayContent cloneContent = new ByteArrayContent(content);
                if (source.Content != null)
                {
                    foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content.Headers)
                        cloneContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                clone.Content = cloneContent;
            }

            return clone;
        }

        private static HttpClient CreateClient(string name)
        {
            // Local DevTools probes must never be sent through a machine-wide
            // proxy.  A proxy can add seconds of latency or even expose the
            // loopback request to a VPN gateway while Chrome is still
            // starting.  Keep these clients in the same lifetime-managed pool
            // as the public API clients, but give them a short timeout and a
            // direct loopback route.
            bool isCdpProbe = name.StartsWith("cdp", StringComparison.OrdinalIgnoreCase);
            bool isNoRedirect = string.Equals(name, "no-redirect", StringComparison.OrdinalIgnoreCase);
            TimeSpan timeout = isCdpProbe
                ? (name.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromMilliseconds(750))
                : (isNoRedirect ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = !isNoRedirect,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = isNoRedirect ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(10),
                UseCookies = false,
                UseProxy = !isCdpProbe
            };

            if (isCdpProbe)
                handler.ConnectTimeout = TimeSpan.FromSeconds(1);

            HttpClient client = new HttpClient(handler, true)
            {
                Timeout = timeout
            };

            if (!isCdpProbe)
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }
            ConfigureHeaders(client);

            if (name.StartsWith("ua_", StringComparison.OrdinalIgnoreCase))
            {
                string customUa = name.Substring(3);
                if (!string.IsNullOrWhiteSpace(customUa))
                {
                    client.DefaultRequestHeaders.Remove("User-Agent");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", customUa);
                }
            }

            return client;
        }

        private static void ConfigureHeaders(HttpClient client)
        {
            if (client == null)
                return;

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        }

        private static bool IsTransient(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout ||
                statusCode == HttpStatusCode.TooManyRequests ||
                (int)statusCode >= 500;
        }

        private static bool IsSafeToRetry(HttpMethod? method)
        {
            string? name = method?.Method;
            return string.Equals(name, HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, HttpMethod.Head.Method, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, HttpMethod.Options.Method, StringComparison.OrdinalIgnoreCase);
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromSeconds(30))
                return delta;

            if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
            {
                TimeSpan dateDelay = retryAt - DateTimeOffset.UtcNow;
                if (dateDelay >= TimeSpan.Zero && dateDelay <= TimeSpan.FromSeconds(30))
                    return dateDelay;
            }

            // Some Roblox gateways expose only the Unix reset timestamp. Use
            // it for safe GET retries while keeping the same short upper
            // bound so a stale header cannot hold the UI for minutes.
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? resetValues))
            {
                string? reset = resetValues.FirstOrDefault();
                if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long resetUnix))
                {
                    try
                    {
                        TimeSpan resetDelay = DateTimeOffset.FromUnixTimeSeconds(resetUnix) - DateTimeOffset.UtcNow;
                        if (resetDelay >= TimeSpan.Zero && resetDelay <= TimeSpan.FromSeconds(30))
                            return resetDelay;
                    }
                    catch (ArgumentOutOfRangeException) { /* invalid unix timestamp */ }
                }
            }

            return TimeSpan.FromMilliseconds(Math.Min(4000, 300 * attempt * attempt));
        }
    }
}
