#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using Shiro.Services;

namespace Shiro.Classes
{
    internal static partial class RobloxBrowserLogin
    {
        private static async Task PrepareLoginAsync(IPage page, string? username, string? password, bool submitCredentials)
        {
            if (page == null || (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password)))
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(username))
                    await FillLoginInputAsync(page, "#login-username", username);

                if (!string.IsNullOrWhiteSpace(password))
                    await FillLoginInputAsync(page, "#login-password", password);

                // Install page-level watchdog to prevent Roblox/React hydration or captcha reset from clearing credentials
                try
                {
                    _ = page.EvaluateFunctionAsync(@"(data) => {
                        window.__shiroCreds = data;
                        if (!window.__shiroLoginWatchdog) {
                            window.__shiroLoginWatchdog = true;
                            function restore() {
                                if (window.__shiroSubmittedSuccess || !window.location.href.includes('/login')) return;
                                const u = document.querySelector('#login-username');
                                const p = document.querySelector('#login-password');
                                const fill = (el, val) => {
                                    if (!el || !val || el.value === val) return;
                                    try {
                                        if (el._valueTracker) el._valueTracker.setValue('');
                                        const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
                                        if (setter) setter.call(el, val); else el.value = val;
                                        try { el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: val })); } catch(_) { /* ignore */ }
                                        el.dispatchEvent(new Event('change', { bubbles: true }));
                                    } catch (_) { /* ignore */ }
                                };
                                if (u && data.username && !u.value) fill(u, data.username);
                                if (p && data.password && !p.value) fill(p, data.password);
                            }
                            setInterval(restore, 800);
                        }
                    }", new { username = username ?? "", password = password ?? "" });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Register login watchdog threw: {ex.Message}");
                }

                // Keep the one-click behavior, but let the normally
                // launched Chrome instance own the verification step.
                if (submitCredentials && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    await Task.Delay(1000);
                    bool clicked = await ClickVisibleLoginButtonAsync(page);
                    if (!clicked)
                    {
                        // Some Roblox layouts submit from the password field
                        // when Enter is pressed even if the button has no
                        // stable id.
                        try
                        {
                            if (page != null && !page.IsClosed && page.Keyboard != null)
                                await page.Keyboard.PressAsync("Enter");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Keyboard.PressAsync Enter threw: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A changed Roblox selector should not prevent the user from
                // completing the sign-in manually in the open Chrome window.
                Program.Logger.Warn("Could not prefill the Roblox sign-in form; complete it manually in Chrome. " + ex.Message);
            }
        }

        private static async Task FillLoginInputAsync(IPage page, string selector, string value)
        {
            value ??= string.Empty;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Timeout = LoginSelectorTimeoutMilliseconds });

                    // Focus element and clear existing text
                    try
                    {
                        await page.ClickAsync(selector);
                        await page.Keyboard.DownAsync("Control");
                        await page.Keyboard.PressAsync("A");
                        await page.Keyboard.UpAsync("Control");
                        await page.Keyboard.PressAsync("Backspace");
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Clearing text for {selector} threw: {ex.Message}");
                    }

                    // Real typing with Puppeteer
                    try
                    {
                        await page.TypeAsync(selector, value);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"TypeAsync {selector} threw: {ex.Message}");
                    }

                    // Verify DOM and apply React-aware controlled input fallback
                    bool entered = await ChromeLaunchHelper.SetReactControlledInputValueAsync(page, selector, value);

                    if (entered)
                        return;
                }
                catch (Exception exception) when (exception.Message.Contains("detached", StringComparison.OrdinalIgnoreCase) ||
                                                   exception.Message.Contains("execution context", StringComparison.OrdinalIgnoreCase))
                {
                    // Roblox may rebuild the controlled input after username
                    // validation. Re-query the field before trying again.
                }

                await Task.Delay(180);
            }

            Program.Logger?.Warn($"Roblox did not confirm the prefilled login field {selector}; complete it manually in Chrome.");
        }

        private static async Task EnsureLoginPageAsync(IPage page)
        {
            if (page == null || page.IsClosed)
                return;

            try
            {
                // Wait for the main frame to be attached
                for (int i = 0; i < 20; i++)
                {
                    if (page.MainFrame != null)
                        break;
                    await Task.Delay(100);
                }

                string currentUrl = string.Empty;
                try { currentUrl = page.Url ?? string.Empty; }
                catch (Exception ex) { Program.Logger?.Debug($"Get page.Url threw: {ex.Message}"); }

                if (string.IsNullOrEmpty(currentUrl) ||
                    currentUrl.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) ||
                    currentUrl.StartsWith("chrome://newtab", StringComparison.OrdinalIgnoreCase) ||
                    !currentUrl.Contains("roblox.com"))
                {
                    await page.GoToAsync(
                        "https://www.roblox.com/login",
                        new NavigationOptions
                        {
                            Timeout = LoginNavigationTimeoutMilliseconds,
                            WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }
                        });
                }

                // Ensure the login inputs are present in the DOM before returning to prefill
                try
                {
                    await page.WaitForSelectorAsync("#login-username, input[type='text'], input[type='password']",
                        new WaitForSelectorOptions { Timeout = 6000 });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"WaitForSelectorAsync login inputs timed out or threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // A blocked or slow network should leave the real Chrome page
                // open for the user instead of making the app wait 60 seconds.
                Program.Logger.Warn("The Roblox login page is still loading in Chrome. " + ex.Message);
            }
        }

        private static async Task<bool> ClickVisibleLoginButtonAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<bool>(@"() => {
                const isVisible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = window.getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 &&
                        style.display !== 'none' && style.visibility !== 'hidden' &&
                        style.opacity !== '0';
                };

                // Priority 1: Direct #login-button ID match
                const loginBtn = document.querySelector('#login-button');
                if (loginBtn && isVisible(loginBtn)) {
                    loginBtn.click();
                    return true;
                }

                // Priority 2: Candidates matching English or Vietnamese login text
                const candidates = Array.from(document.querySelectorAll(
                    'button, input[type=""submit""], [role=""button""]'));
                const button = candidates.find(element => {
                    if (!isVisible(element)) return false;
                    const text = (element.innerText || element.value ||
                        element.getAttribute('aria-label') || '')
                        .replace(/\s+/g, ' ').trim().toLowerCase();
                    return text === 'log in' || text === 'login' || text.includes('log in') ||
                           text === 'đăng nhập' || text.includes('đăng nhập');
                });

                if (!button) return false;
                button.click();
                return true;
            }");
        }

        private static async Task ApplyAntiDetectionAsync(IPage page)
        {
            await ChromeLaunchHelper.ApplyAntiDetectionAsync(page).ConfigureAwait(false);
            await InstallHbaCaptureOnPageAsync(page).ConfigureAwait(false);
            await ChromeLaunchHelper.DismissCookieBannerAsync(page).ConfigureAwait(false);
        }

        private static Task<string?> ReadAccountJsonAsync(IPage page)
            => ChromeLaunchHelper.ReadAuthenticatedRobloxAccountJsonAsync(page, maxAttempts: 2);

        private static Task<IPage?> FindActiveSignInPageAsync(IBrowser connectedBrowser)
            => ChromeLaunchHelper.FindActiveRobloxPageAsync(connectedBrowser);

        private static async Task<ICDPSession?> AttachProxyAuthenticationAsync(IPage page, ProxyConfiguration? proxy)
        {
            if (proxy == null || string.IsNullOrEmpty(proxy.Username))
                return null;

            string user = proxy.Username;
            string pass = proxy.Password ?? string.Empty;

            ICDPSession session = await page.CreateCDPSessionAsync();
            session.MessageReceived += (sender, e) =>
            {
                if (sender is ICDPSession cdpSession)
                    AccountBrowser.Observe(ChromeLaunchHelper.HandleCdpProxyAuthChallengeAsync(cdpSession, e, user, pass));
            };
            await session.SendAsync("Fetch.enable", new { handleAuthRequests = true });
            return session;
        }

        private static async Task DetachProxyAuthenticationAsync(ICDPSession session)
        {
            if (session == null)
                return;

            try { await session.DetachAsync(); }
            catch (Exception ex) { Program.Logger?.Debug($"DetachAsync threw: {ex.Message}"); }
        }

        private static bool IsDetachedPageException(PuppeteerException exception)
            => ChromeLaunchHelper.IsDetachedOrTransientBrowserException(exception);

        private static Task WaitForDevToolsPortAsync(int devToolsPort, CancellationToken cancellationToken = default)
            => ChromeLaunchHelper.WaitForDevToolsPortAsync(devToolsPort, DevToolsStartupTimeoutSeconds, DevToolsPollMilliseconds, cancellationToken);

        private static string GetBrowserPosition(int windowSlot = 0)
            => ChromeLaunchHelper.GetInteractiveBrowserPosition(InteractiveBrowserWidth, InteractiveBrowserHeight, windowSlot, allowSideBySideSlots: true);

        private static int GetFreeLoopbackPort()
            => ChromeLaunchHelper.GetFreeLoopbackPort();

        private static void OpenManualSignIn(string? browserPath)
        {
            if (string.IsNullOrEmpty(browserPath))
            {
                AppServices.Dialogs.ShowInfo(
                    "Shiro could not prepare an isolated Chrome/Edge/Chromium window. Install or repair Google Chrome or Microsoft Edge, then try again. " +
                    "You can also use Add Account > By Cookie. Keep the .ROBLOSECURITY value private; it grants access to your account.",
                    "Roblox sign-in");
                return;
            }

            string sessionRoot = Path.Combine(StoragePaths.BrowserSessionsDirectory, "manual-login-" + CreateNonce());
            string profilePath = Path.Combine(sessionRoot, "profile");
            try
            {
                Directory.CreateDirectory(profilePath);
                BrowserService.SuppressPasswordManager(profilePath);
                Process? browser = Process.Start(new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = string.Join(" ", new[]
                    {
                        "--no-first-run",
                        "--no-default-browser-check",
                        "--disable-extensions",
                        "--disable-save-password-bubble",
                        "--disable-single-click-autofill",
                        "--disable-autofill-keyboard-accessory-view",
                        "--disable-search-engine-choice-screen",
                        "--disable-sync",
                        "--password-store=basic",
                        "--disable-features=DeviceBoundSessionCredentials,AutofillServerCommunication,PasswordManager,SavePasswordBubble,PasswordLeakDetection,msImplicitSignin,msEdgeSidebarV2",
                        "--new-window",
                        $"--window-size={InteractiveBrowserWidth},{InteractiveBrowserHeight}",
                        $"--window-position={GetBrowserPosition()}",
                        "--user-data-dir=" + QuoteArgument(profilePath),
                        QuoteArgument("https://www.roblox.com/login")
                    }),
                    UseShellExecute = false,
                    WorkingDirectory = sessionRoot
                });

                if (browser == null)
                    throw new InvalidOperationException("The browser did not start.");

                HandOffManualSignIn(browser, sessionRoot);
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"OpenManualSignIn failed: {ex.Message}");
                AccountBrowser.Observe(TryDeleteTemporaryDirectoryAsync(sessionRoot));
                throw;
            }
        }

        private static void HandOffManualSignIn(Process browser, string sessionRoot)
        {
            Program.Logger.Info("Roblox sign-in is open in an isolated browser profile; cookie capture remains manual.");
            AccountBrowser.Observe(MonitorManualBrowserAndCleanupAsync(browser, sessionRoot));
            AppServices.Dialogs.ShowInfo(
                "Roblox sign-in opened in an isolated Chrome/Edge profile. Complete verification there, then import the session using Add Account > By Cookie. " +
                "In browser developer tools, open Application/Storage > Cookies > https://www.roblox.com and copy the .ROBLOSECURITY value. " +
                "Keep that value private; it grants access to your account.",
                "Roblox sign-in");
        }

        private static async Task MonitorManualBrowserAndCleanupAsync(Process browser, string sessionRoot)
        {
            try
            {
                if (browser != null)
                    await browser.WaitForExitAsync().ConfigureAwait(false);
                await Task.Delay(750).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"WaitForExitAsync manual browser threw: {ex.Message}");
            }
            finally
            {
                await TryDeleteTemporaryDirectoryAsync(sessionRoot).ConfigureAwait(false);
                SafeExecuteHelper.Try(() => browser?.Dispose(), "RobloxBrowserLogin.PageAutomation.DisposeBrowser");
            }
        }

        private static async Task<ProxyConfiguration?> SelectProxyAsync(CancellationToken cancellationToken = default)
        {
            var verified = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync(VerificationProbeUrls, cancellationToken).ConfigureAwait(false);
            if (verified == null)
                return null;

            return new ProxyConfiguration
            {
                Server = verified.BrowserServerArgument ?? string.Empty,
                Username = verified.Username ?? string.Empty,
                Password = verified.Password ?? string.Empty
            };
        }

        private static Task<CookieParam?> ExtractRoblosecurityCookieAsync(IPage page)
            => ChromeLaunchHelper.ExtractRoblosecurityCookieAsync(page);

        private static void CloseBrowser(Process browser)
            => ChromeLaunchHelper.CloseBrowserProcess(browser, dispose: false);

        private static string CreateNonce()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexStringLower(bytes);
        }

        private static string QuoteArgument(string value) => ChromeLaunchHelper.QuoteArgument(value);

        private sealed class ProxyConfiguration
        {
            public string Server { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        private static Task TryDeleteTemporaryDirectoryAsync(string path)
            => ChromeLaunchHelper.TryDeleteSessionDirectoryAsync(path);
    }
}
