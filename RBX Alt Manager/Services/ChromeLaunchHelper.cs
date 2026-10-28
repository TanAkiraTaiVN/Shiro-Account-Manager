#nullable enable
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
    /// <summary>
    /// Shared helper providing standard launch arguments, DevTools port probing,
    /// anti-detection scripts, cookie/account extraction, CDP proxy authentication,
    /// and profile cleanup across all browser automation flows.
    /// </summary>
    public static partial class ChromeLaunchHelper
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

        public static List<string> BuildLaunchArgs(string userDataDir, string? proxy = null, bool headless = false)
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
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ApplyAntiDetectionAsync threw: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                Program.Logger?.Debug($"DismissCookieBannerAsync threw: {ex.Message}");
            }
        }

        public static async Task<IPage?> FindActiveRobloxPageAsync(IBrowser? connectedBrowser)
        {
            if (connectedBrowser == null || connectedBrowser.IsClosed)
                return null;

            IPage[] pages;
            try { pages = await connectedBrowser.PagesAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"FindActiveRobloxPageAsync PagesAsync threw: {ex.Message}");
                return null;
            }

            IPage? fallback = null;
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
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Page inspection during FindActiveRobloxPage threw: {ex.Message}");
                }
            }

            return fallback;
        }

        public static async Task<CookieParam?> ExtractRoblosecurityCookieAsync(IPage? page)
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
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ExtractRoblosecurityCookieAsync threw: {ex.Message}");
                return null;
            }
        }

        public static async Task<string?> ReadAuthenticatedRobloxAccountJsonAsync(IPage? page, int maxAttempts = 2)
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
                        try { await Task.Delay(600, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception) { }
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
            for (Exception? cur = exception; cur != null; cur = cur.InnerException)
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
            catch (Exception ex)
            {
                Program.Logger?.Debug($"CloseBrowser threw: {ex.Message}");
            }

            if (dispose)
            {
                try { browser.Dispose(); }
                catch (Exception) { }
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
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"TryDeleteSessionDirectory attempt {attempt} threw: {ex.Message}");
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"TryDeleteSessionDirectory failed: {ex.Message}");
            }
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
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"TryDeleteDirectoryAsync threw: {ex.Message}");
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
    }
}
