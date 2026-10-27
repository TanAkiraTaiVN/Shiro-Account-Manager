using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;

namespace RBX_Alt_Manager.Services
{
    public sealed class VerifiedBrowserProxy
    {
        public WebProxy WebProxy { get; init; }
        public string BrowserServerArgument { get; init; }
        public string RawAuthority { get; init; }
        public bool IsSocks { get; init; }
        public bool AuthenticateInBrowser { get; init; }
        public string Username { get; init; }
        public string Password { get; init; }
    }
    /// <summary>
    /// Shared helper providing standard launch arguments, DevTools port probing,
    /// anti-detection scripts, cookie/account extraction, CDP proxy authentication,
    /// and profile cleanup across all browser automation flows.
    /// </summary>
    public static class ChromeLaunchHelper
    {
        public static readonly string[] StandardArgs =
        {
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-component-update",
            "--disable-background-networking",
            "--disable-breakpad",
            "--disable-sync",
            "--disable-features=DeviceBoundSessionCredentials,Translate,OptimizationHints,MediaRouter",
            "--disable-search-engine-choice-screen",
            "--password-store=basic",
            "--use-mock-keychain",
            "--disable-blink-features=AutomationControlled"
        };

        public static List<string> BuildLaunchArgs(string userDataDir, string proxy = null, bool headless = false)
        {
            var args = new List<string>(StandardArgs);

            if (!string.IsNullOrWhiteSpace(userDataDir))
                args.Add($"--user-data-dir={userDataDir}");

            if (!string.IsNullOrWhiteSpace(proxy))
                args.Add($"--proxy-server={proxy}");

            if (headless)
                args.Add("--headless=new");

            return args;
        }

        public static int GetFreeLoopbackPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        public static string GetInteractiveBrowserPosition(int width, int height, int windowSlot = 0, bool allowSideBySideSlots = false)
        {
            var workingArea = RobloxWindowService.GetTargetWorkArea();
            if (allowSideBySideSlots)
            {
                int slot = Math.Max(0, windowSlot) % 2;
                int pairGap = 24;
                int pairWidth = width * 2 + pairGap;
                if (workingArea.Width >= pairWidth)
                {
                    int slottedLeft = (int)workingArea.Left + Math.Max(0, (int)(workingArea.Width - pairWidth) / 2) + slot * (width + pairGap);
                    int slottedTop = (int)workingArea.Top + Math.Max(0, (int)(workingArea.Height - height) / 2);
                    return $"{slottedLeft},{slottedTop}";
                }
            }

            int left = (int)workingArea.Left + Math.Max(0, (int)(workingArea.Width - width) / 2);
            int top = (int)workingArea.Top + Math.Max(0, (int)(workingArea.Height - height) / 2);
            return $"{left},{top}";
        }

        public static async Task WaitForDevToolsPortAsync(
            int devToolsPort,
            int timeoutSeconds = 12,
            int pollMilliseconds = 100,
            CancellationToken cancellationToken = default)
        {
            long startTick = Stopwatch.GetTimestamp();
            TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
            HttpClient client = SharedHttpClient.Get("cdp-probe");

            while (Stopwatch.GetElapsedTime(startTick) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using HttpResponseMessage response = await client.GetAsync(
                        $"http://127.0.0.1:{devToolsPort}/json/version",
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return;
                }
                catch (HttpRequestException)
                {
                    // Chrome may still be starting its DevTools endpoint.
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Retry if the endpoint did not answer before the short timeout.
                }

                await Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Chrome did not create its local DevTools endpoint in time.");
        }

        public static async Task ApplyAntiDetectionAsync(IPage page)
        {
            if (page == null || page.IsClosed) return;
            try
            {
                const string script = @"() => {
                    try {
                        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                        if (!window.chrome) window.chrome = { runtime: {} };
                    } catch (_) {}
                }";

                await page.EvaluateFunctionOnNewDocumentAsync(script).ConfigureAwait(false);
                await page.EvaluateFunctionAsync(script).ConfigureAwait(false);
            }
            catch { }
        }

        public static async Task DismissCookieBannerAsync(IPage page)
        {
            if (page == null || page.IsClosed) return;
            try
            {
                await page.EvaluateFunctionAsync(@"() => {
                    try {
                        const selectors = [
                            '.cookie-modal-container',
                            '#cookie-banner',
                            '#onetrust-banner-sdk',
                            '#onetrust-consent-sdk',
                            '.onetrust-pc-dark-filter',
                            '.modal-backdrop'
                        ];
                        for (const s of selectors) {
                            const el = document.querySelector(s);
                            if (el) el.remove();
                        }
                    } catch (_) {}
                }").ConfigureAwait(false);
            }
            catch { }
        }

        public static async Task<IPage> FindActiveRobloxPageAsync(IBrowser connectedBrowser)
        {
            if (connectedBrowser == null || connectedBrowser.IsClosed)
                return null;

            IPage[] pages;
            try { pages = await connectedBrowser.PagesAsync().ConfigureAwait(false); }
            catch { return null; }

            IPage fallback = null;
            foreach (IPage page in pages)
            {
                try
                {
                    if (page.IsClosed)
                        continue;

                    string url = page.Url ?? string.Empty;
                    if (url.IndexOf("roblox.com", StringComparison.OrdinalIgnoreCase) >= 0)
                        return page;

                    fallback ??= page;
                }
                catch
                {
                    // A target can disappear while the browser reports its page list.
                }
            }

            return fallback;
        }

        public static async Task<CookieParam> ExtractRoblosecurityCookieAsync(IPage page)
        {
            if (page == null || page.IsClosed) return null;
            try
            {
                var cookies = await page.GetCookiesAsync().ConfigureAwait(false);
                var found = cookies?.FirstOrDefault(c => c.Name == ".ROBLOSECURITY" &&
                                                        !string.IsNullOrWhiteSpace(c.Value) &&
                                                        c.Value.Length >= 100 &&
                                                        c.Value.Contains("_|WARNING:-DO-NOT-SHARE-THIS"));
                if (found != null) return found;

                var urlCookies = await page.GetCookiesAsync("https://www.roblox.com/", "https://roblox.com/").ConfigureAwait(false);
                return urlCookies?.FirstOrDefault(c => c.Name == ".ROBLOSECURITY" &&
                                                      !string.IsNullOrWhiteSpace(c.Value) &&
                                                      c.Value.Length >= 100 &&
                                                      c.Value.Contains("_|WARNING:-DO-NOT-SHARE-THIS"));
            }
            catch { return null; }
        }

        public static async Task<string> ReadAuthenticatedRobloxAccountJsonAsync(IPage page, int maxAttempts = 2)
        {
            if (page == null || page.IsClosed)
                return null;

            for (int attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
            {
                try
                {
                    string accountJson = await page.EvaluateFunctionAsync<string>(@"async () => {
                        try {
                            const resAuth = await fetch('https://users.roblox.com/v1/users/authenticated', { credentials: 'include' });
                            if (resAuth.ok) {
                                const data = await resAuth.json();
                                if (data && data.id) {
                                    return JSON.stringify({ UserId: data.id, Name: data.name, DisplayName: data.displayName });
                                }
                            }
                        } catch (_) {}
                        try {
                            const response = await fetch('/my/account/json', { credentials: 'include' });
                            if (response.ok) return await response.text();
                        } catch (_) {
                            return '';
                        }
                        return '';
                    }").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(accountJson))
                        return accountJson;
                }
                catch (PuppeteerException)
                {
                    if (attempt == 0 && !page.IsClosed && maxAttempts > 1)
                    {
                        try { await Task.Delay(600).ConfigureAwait(false); } catch { }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn("Could not read account details through signed-in browser session: " + ex.Message);
                    return null;
                }
            }

            return null;
        }

        public static bool IsDetachedOrTransientBrowserException(Exception exception)
        {
            for (Exception cur = exception; cur != null; cur = cur.InnerException)
            {
                string message = cur.Message ?? string.Empty;
                if (message.IndexOf("Session closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Target.detachedFromTarget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Target closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("most likely because of a navigation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Cannot find context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Navigating frame was detached", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("frame was detached", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Response body is unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static async Task HandleCdpProxyAuthChallengeAsync(
            ICDPSession session,
            MessageEventArgs e,
            string proxyUsername,
            string proxyPassword,
            bool authenticateProxy = true)
        {
            if (session == null || e == null)
                return;

            try
            {
                JsonElement message = e.MessageData;
                if (message.ValueKind != JsonValueKind.Object)
                    return;

                if (!message.TryGetProperty("method", out var methodProp) || methodProp.GetString() != "Fetch.authRequired")
                    return;

                if (!message.TryGetProperty("params", out var parameters))
                    return;

                string requestId = parameters.TryGetProperty("requestId", out var reqProp) ? reqProp.GetString() : null;
                string challengeSource = parameters.TryGetProperty("authChallenge", out var authChal) && authChal.TryGetProperty("source", out var srcProp)
                    ? srcProp.GetString()
                    : null;

                if (string.IsNullOrEmpty(requestId))
                    return;

                if (authenticateProxy && challengeSource == "Proxy" && !string.IsNullOrEmpty(proxyUsername))
                {
                    await session.SendAsync("Fetch.continueWithAuth", new
                    {
                        requestId,
                        authChallengeResponse = new
                        {
                            response = "ProvideCredentials",
                            username = proxyUsername,
                            password = proxyPassword ?? string.Empty
                        }
                    }).ConfigureAwait(false);
                }
                else
                {
                    await session.SendAsync("Fetch.continueWithAuth", new
                    {
                        requestId,
                        authChallengeResponse = new { response = "Default" }
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Unable to answer a browser authentication challenge: {ex.Message}");
            }
        }

        public static void CloseBrowserProcess(Process browser, bool dispose = false)
        {
            if (browser == null) return;
            try
            {
                if (!browser.HasExited)
                {
                    if (!browser.CloseMainWindow() && !browser.HasExited)
                        browser.Kill(true);
                    if (!browser.HasExited && !browser.WaitForExit(3500))
                        browser.Kill(true);
                }
            }
            catch { }

            if (dispose)
            {
                try { browser.Dispose(); } catch { }
            }
        }

        public static async Task TryDeleteSessionDirectoryAsync(string path, int maxAttempts = 20)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                string target = Path.GetFullPath(path);
                if (!StoragePaths.IsWithinDirectory(target, StoragePaths.BrowserSessionsDirectory) || !Directory.Exists(target))
                    return;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        if (!Directory.Exists(target))
                            return;
                        Directory.Delete(target, true);
                        return;
                    }
                    catch
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }

        public static async Task TryDeleteDirectoryAsync(string directoryPath, int maxAttempts = 5)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(directoryPath, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(attempt * 250).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(attempt * 250).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }

        public static async Task<bool> SetReactControlledInputValueAsync(IPage page, string selector, string value)
        {
            if (page == null || page.IsClosed || string.IsNullOrWhiteSpace(selector))
                return false;

            value ??= string.Empty;
            return await page.EvaluateFunctionAsync<bool>(@"data => {
                const field = document.querySelector(data.selector);
                if (!field) return false;
                if (field.value === data.value) return true;
                try {
                    field.focus();
                    if (field._valueTracker) {
                        field._valueTracker.setValue('');
                    }
                    const prototype = window.HTMLInputElement.prototype;
                    const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
                    if (setter) {
                        setter.call(field, data.value);
                    } else {
                        field.value = data.value;
                    }
                    try {
                        field.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: data.value }));
                    } catch (_) {
                        field.dispatchEvent(new Event('input', { bubbles: true }));
                    }
                    field.dispatchEvent(new Event('change', { bubbles: true }));
                    return field.value === data.value;
                } catch {
                    field.value = data.value;
                    return true;
                }
            }", new { selector, value }).ConfigureAwait(false);
        }

        public static async Task<VerifiedBrowserProxy> SelectVerifiedBrowserProxyAsync(
            string[] additionalProbeUrls = null,
            CancellationToken cancellationToken = default)
        {
            if (AccountManager.General == null || !AccountManager.General.Get<bool>("UseProxies"))
                return null;

            string proxiesPath = StoragePaths.GetUserFile("proxies.txt");
            if (!File.Exists(proxiesPath))
                return null;

            Random rng = new Random(unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode()));
            int timeout = AccountManager.General.Exists("ProxyTimeout") ? AccountManager.General.Get<int>("ProxyTimeout") : 3000;
            int limit = AccountManager.General.Exists("ProxyTestLimit") ? AccountManager.General.Get<int>("ProxyTestLimit") : 10;
            string[] proxies = File.ReadAllLines(proxiesPath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                .OrderBy(_ => rng.Next())
                .Take(Math.Max(limit, 0))
                .ToArray();

            foreach (string entry in proxies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string scheme = "http";
                string candidate = entry;
                int schemeIndex = candidate.IndexOf("://", StringComparison.Ordinal);
                if (schemeIndex >= 0)
                {
                    scheme = candidate.Substring(0, schemeIndex);
                    candidate = candidate.Substring(schemeIndex + 3);
                }

                string username = string.Empty;
                string password = string.Empty;
                int credentialsIndex = candidate.IndexOf('@');
                if (credentialsIndex >= 0)
                {
                    string credentials = candidate.Substring(0, credentialsIndex);
                    candidate = candidate.Substring(credentialsIndex + 1);
                    int passwordIndex = credentials.IndexOf(':');
                    if (passwordIndex >= 0)
                    {
                        username = credentials.Substring(0, passwordIndex);
                        password = credentials.Substring(passwordIndex + 1);
                    }
                    else
                    {
                        username = credentials;
                    }
                }

                if (!Uri.TryCreate("http://" + candidate, UriKind.Absolute, out Uri proxyUri) ||
                    string.IsNullOrWhiteSpace(proxyUri.Host) || proxyUri.Port < 1)
                {
                    Program.Logger?.Warn("Skipping a proxy entry with an invalid address.");
                    continue;
                }

                string browserScheme;
                if (scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "socks5";
                else if (scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "socks4";
                else if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    browserScheme = "http";
                else
                {
                    Program.Logger?.Warn("Skipping a proxy entry with an unsupported protocol.");
                    continue;
                }

                try
                {
                    var webProxy = new WebProxy(proxyUri);
                    if (!string.IsNullOrEmpty(username))
                        webProxy.Credentials = new NetworkCredential(username, password);

                    bool probeReachable = true;
                    using (HttpClient client = SharedHttpClient.CreateProxyClient(
                        webProxy,
                        TimeSpan.FromMilliseconds(Math.Max(250, timeout))))
                    {
                        using (HttpResponseMessage response = await client.GetAsync("https://www.roblox.com/", cancellationToken).ConfigureAwait(false))
                        {
                            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                                continue;
                        }

                        if (additionalProbeUrls != null)
                        {
                            foreach (string url in additionalProbeUrls)
                            {
                                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                                {
                                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                                    {
                                        probeReachable = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (!probeReachable)
                    {
                        Program.Logger?.Warn("A configured proxy could reach Roblox but not its verification services; trying another proxy.");
                        continue;
                    }

                    bool isSocks = browserScheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
                    bool canAuthenticate = !isSocks && !string.IsNullOrEmpty(username);
                    Program.Logger?.Info("A configured proxy passed the Roblox connectivity check.");
                    return new VerifiedBrowserProxy
                    {
                        WebProxy = webProxy,
                        BrowserServerArgument = browserScheme + "://" + proxyUri.Authority,
                        RawAuthority = (isSocks ? $"{browserScheme}://" : "") + proxyUri.Authority,
                        IsSocks = isSocks,
                        AuthenticateInBrowser = canAuthenticate,
                        Username = canAuthenticate ? username : null,
                        Password = canAuthenticate ? password : null
                    };
                }
                catch
                {
                    // Try the next configured proxy.
                }
            }

            if (proxies.Length > 0)
                Program.Logger?.Warn("No configured proxy passed the Roblox connectivity check; the browser will use the direct connection.");

            return null;
        }
    }
}
