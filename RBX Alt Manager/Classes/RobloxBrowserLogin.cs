using System.Text.Json;
using PuppeteerSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.AvaloniaUI.Views;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Runs Roblox sign-in in a normal, isolated Chrome/Edge profile so Roblox's
    /// human verification is handled by the browser and the user.
    /// </summary>
    internal static class RobloxBrowserLogin
    {
        // The browser window should be visible almost immediately. DevTools is
        // only used to capture the session after Chrome starts, so it should
        // have a short startup budget without treating a slow page as a
        // closed browser.
        private const int DevToolsStartupTimeoutSeconds = 12;
        private const int DevToolsPollMilliseconds = 100;
        private const int LoginNavigationTimeoutMilliseconds = 15000;
        private const int LoginSelectorTimeoutMilliseconds = 10000;
        // Keep the interactive Add Account browser compact and portrait
        // oriented, matching the size used in the Shiro UI reference.
        // These are outer Chrome window dimensions in CSS pixels.
        private const int InteractiveBrowserWidth = 630;
        private const int InteractiveBrowserHeight = 840;
        private static readonly string[] VerificationProbeUrls =
        {
            // Roblox's current network allowlist names these Arkose endpoints.
            // Probing the bare funcaptcha.com apex can fail DNS even when the
            // actual challenge hosts are reachable, which produced a false warning.
            "https://roblox-api.arkoselabs.com/",
            "https://cdn.arkoselabs.com/"
        };
        public static async Task<Account?> LoginAsync(
            string? username = null,
            string? password = null,
            int windowSlot = 0,
            bool submitCredentials = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Account? createdAccount = null;
            string browserPath;
            try
            {
                // Chrome is preferred, Edge is fallback, and the bundled
                // Chromium cache is the last isolated option. Never fall
                // back to the user's default browser profile.
                browserPath = await BrowserService.ResolveBrowserExecutableAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Program.Logger.Warn("No isolated Chrome/Edge/Chromium browser could be prepared for sign-in. " + exception.Message);
                OpenManualSignIn(null);
                return null;
            }

            FileVersionInfo browserVersion = FileVersionInfo.GetVersionInfo(browserPath);
            Program.Logger.Info($"Using {Path.GetFileName(browserPath)} {browserVersion.ProductVersion} for interactive Roblox sign-in.");

            string sessionId = CreateNonce();
            string temporaryRoot = Path.Combine(StoragePaths.BrowserSessionsDirectory, "login-" + sessionId);
            string profilePath = Path.Combine(temporaryRoot, "profile");

            ProxyConfiguration? proxy = await SelectProxyAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (proxy != null)
            {
                MessageBoxResult routeChoice = MessageBoxDialog.Show(
                    "A proxy is configured for sign-in. Roblox may reject human verification when it detects a proxy connection.\n\n" +
                    "Yes: try a direct connection (recommended for verification)\n" +
                    "No: keep using the configured proxy\n" +
                    "Cancel: stop this sign-in attempt",
                    "Roblox sign-in connection", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);

                if (routeChoice == MessageBoxResult.Cancel)
                    return null;
                if (routeChoice == MessageBoxResult.Yes)
                {
                    proxy = null;
                    Program.Logger.Info("Interactive Roblox sign-in will use a direct connection.");
                }
                else
                    Program.Logger.Info("Interactive Roblox sign-in will use a configured proxy.");
            }
            else
                Program.Logger.Info("No sign-in proxy was selected; interactive Roblox sign-in will use a direct connection.");

            Process? browser = null;
            IBrowser? connectedBrowser = null;
            IPage? signInPage = null;
            ICDPSession? proxyAuthSession = null;
            bool handedOff = false;

            try
            {
                Directory.CreateDirectory(profilePath);
                BrowserService.SuppressPasswordManager(profilePath);

                // Chrome sets navigator.webdriver when remote debugging uses
                // port 0. A free, loopback-only port keeps the CDP connection
                // while allowing Roblox's human-verification page to see a
                // normal browser session.
                int devToolsPort = GetFreeLoopbackPort();

                string[] browserArguments = new[]
                {
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-save-password-bubble",
                    "--disable-single-click-autofill",
                    "--disable-autofill-keyboard-accessory-view",
                    "--disable-search-engine-choice-screen",
                    "--disable-sync",
                    "--password-store=basic",
                    "--disable-features=DeviceBoundSessionCredentials,AutofillServerCommunication,PasswordManager,SavePasswordBubble,PasswordLeakDetection,msImplicitSignin,msEdgeSidebarV2",
                    // Add Account is a focused sign-in window rather than a
                    // full-screen browser. Keep it centered so it does not
                    // cover the whole Shiro dashboard.
                    $"--window-size={InteractiveBrowserWidth},{InteractiveBrowserHeight}",
                    $"--window-position={GetBrowserPosition(windowSlot)}",
                    "--user-data-dir=" + QuoteArgument(profilePath),
                    // Chrome 136+ only honors remote debugging with a non-default
                    // profile. This profile is temporary and isolated from Chrome data.
                    "--remote-debugging-address=127.0.0.1",
                    "--remote-debugging-port=" + devToolsPort
                };
                if (proxy != null)
                    browserArguments = browserArguments.Concat(new[]
                    {
                        "--proxy-server=" + QuoteArgument(proxy.Server),
                        "--proxy-bypass-list=" + QuoteArgument("127.0.0.1;localhost")
                    }).ToArray();
                // Give Chrome the login URL at process start. This makes the
                // window useful while DevTools is connecting and removes the
                // extra blank-tab -> Navigate round trip that used to delay
                // the first paint on slow or VPN-routed networks.
                browserArguments = browserArguments.Concat(new[] { QuoteArgument("https://www.roblox.com/login") }).ToArray();

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = string.Join(" ", browserArguments),
                    UseShellExecute = false,
                    WorkingDirectory = temporaryRoot
                };

                browser = Process.Start(startInfo);
                if (browser == null)
                    throw new InvalidOperationException("The browser did not start.");
                Program.Logger.Info("Started the interactive Roblox sign-in browser.");

                try
                {
                    // Do not use browser.HasExited here. Chromium can hand the
                    // visible window to a child process and exit this launcher
                    // process while the browser is still open.
                    await WaitForDevToolsPortAsync(devToolsPort, cancellationToken).ConfigureAwait(false);
                    connectedBrowser = await Puppeteer.ConnectAsync(new ConnectOptions
                    {
                        BrowserURL = "http://127.0.0.1:" + devToolsPort,
                        // Do not let PuppeteerSharp apply its default 800x600
                        // device metrics to the real Chrome window. That leaves
                        // a large gray area and can make Roblox's challenge
                        // iframe fail because it sees an emulated viewport.
                        DefaultViewport = null
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Program.Logger.Warn("Chrome did not expose its local sign-in session to the account manager; falling back to manual cookie import. " + ex.Message);
                    // Keep the already-open isolated profile. The previous
                    // fallback started a second browser without
                    // --user-data-dir, which touched the user's personal
                    // Chrome profile and also leaked the first session.
                    handedOff = true;
                    HandOffManualSignIn(browser, temporaryRoot);
                    return null;
                }

                IPage[] pages = await connectedBrowser.PagesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                signInPage = pages.FirstOrDefault();
                if (signInPage == null)
                    signInPage = await connectedBrowser.NewPageAsync();

                await ApplyAntiDetectionAsync(signInPage);

                connectedBrowser.TargetCreated += async (s, e) =>
                {
                    try
                    {
                        if (e.Target.Type == TargetType.Page)
                        {
                            var p = await e.Target.PageAsync();
                            if (p != null)
                                await ApplyAntiDetectionAsync(p);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"ApplyAntiDetectionAsync on targetcreated threw: {ex.Message}");
                    }
                };

                if (proxy != null)
                    proxyAuthSession = await AttachProxyAuthenticationAsync(signInPage, proxy);

                await EnsureLoginPageAsync(signInPage);

                await PrepareLoginAsync(signInPage, username, password, submitCredentials);

                Program.Logger.Info(submitCredentials
                    ? "Chrome is ready for interactive Roblox sign-in. Waiting for the user to finish sign-in and verification."
                    : "Chrome is ready for manual sign-in. Credentials were prefilled; click Log In and finish verification.");

                string? lastAttemptedCookie = null;
                DateTime lastCookieAttemptUtc = DateTime.MinValue;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The DevTools connection is the source of truth for the
                    // visible browser. The Process returned by Start() may be
                    // only a short-lived launcher process.
                    if (connectedBrowser == null || connectedBrowser.IsClosed || !connectedBrowser.IsConnected)
                        throw new OperationCanceledException("The sign-in browser was closed before an account was added.");

                    try
                    {
                        if (signInPage == null || signInPage.IsClosed)
                        {
                            signInPage = await FindActiveSignInPageAsync(connectedBrowser);

                            if (signInPage == null)
                            {
                                try
                                {
                                    signInPage = await connectedBrowser.NewPageAsync();
                                    await EnsureLoginPageAsync(signInPage);
                                }
                                catch (Exception pageEx)
                                {
                                    Program.Logger?.Warn($"Could not create new tab in sign-in browser: {pageEx.Message}");
                                    signInPage = null;
                                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                                    continue;
                                }
                            }

                            if (signInPage == null)
                            {
                                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            if (proxyAuthSession != null)
                            {
                                await DetachProxyAuthenticationAsync(proxyAuthSession);
                                proxyAuthSession = null;
                            }

                            proxyAuthSession = await AttachProxyAuthenticationAsync(signInPage, proxy);
                            Program.Logger?.Info("Reattached to the active Roblox sign-in browser tab.");
                        }

                        CookieParam? sessionCookie = await ExtractRoblosecurityCookieAsync(signInPage).ConfigureAwait(false);

                        if (sessionCookie != null &&
                            (!string.Equals(sessionCookie.Value, lastAttemptedCookie, StringComparison.Ordinal) ||
                             (DateTime.UtcNow - lastCookieAttemptUtc).TotalSeconds >= 15))
                        {
                            lastAttemptedCookie = sessionCookie.Value;
                            lastCookieAttemptUtc = DateTime.UtcNow;

                            Program.Logger?.Info("[SignInAsync] Initial .ROBLOSECURITY cookie detected. Waiting for post-login navigation to /home and cookie rotation to settle...");

                            // 1. Allow browser to settle: wait for redirect away from /login to /home or /discover
                            try
                            {
                                for (int w = 0; w < 20; w++)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (signInPage != null && !signInPage.IsClosed && !string.IsNullOrEmpty(signInPage.Url))
                                    {
                                        if (signInPage.Url.Contains("/home") || signInPage.Url.Contains("/discover") || !signInPage.Url.Contains("/login"))
                                            break;
                                    }
                                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                                }
                                // Give background API requests and initial session rotation 1.5 seconds to settle
                                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Navigation wait loop failed: {ex.Message}");
                            }

                            // 2. Read account details through the authenticated browser page
                            string? accountJson = signInPage != null ? await ReadAccountJsonAsync(signInPage).ConfigureAwait(false) : null;

                            // 3. Immediately re-read cookies from the page to capture the SETTLED / ROTATED token!
                            string settledCookieValue = sessionCookie.Value;
                            try
                            {
                                if (signInPage != null && !signInPage.IsClosed)
                                {
                                    var settledCookie = await ExtractRoblosecurityCookieAsync(signInPage).ConfigureAwait(false);
                                    if (settledCookie != null && !string.IsNullOrWhiteSpace(settledCookie.Value))
                                    {
                                        settledCookieValue = settledCookie.Value;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Re-read settled cookie failed: {ex.Message}");
                            }

                            // 4. Add or update account with the settled cookie
                            Account? account = await AccountManager.AddAccountAsync(settledCookieValue, password ?? string.Empty, accountJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (account != null)
                            {
                                createdAccount = account;
                                Program.Logger?.Info($"[SignInAsync] Account {account.Username} registered from browser. Verifying session live before closing...");

                                // 5. Verify live against Roblox API. ValidateSessionAsync also automatically captures and updates
                                // any rotated token from the response headers!
                                bool sessionValid = await account.ValidateSessionAsync(cancellationToken).ConfigureAwait(false);
                                if (!sessionValid)
                                {
                                    Program.Logger?.Warn($"[SignInAsync] Validation check for {account.Username} was not immediately OK. Waiting 1s to re-read page cookies...");
                                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                                    if (signInPage != null && !signInPage.IsClosed)
                                    {
                                        var fresh = await ExtractRoblosecurityCookieAsync(signInPage).ConfigureAwait(false);
                                        if (fresh != null && !string.Equals(account.SecurityToken, fresh.Value, StringComparison.Ordinal))
                                        {
                                            Program.Logger?.Info($"[SignInAsync] Captured post-settle rotated cookie for {account.Username}.");
                                            account.SecurityToken = fresh.Value;
                                            sessionValid = await account.ValidateSessionAsync(cancellationToken).ConfigureAwait(false);
                                        }
                                    }
                                }

                                account.Valid = sessionValid;
                                try { AccountManager.SaveAccounts(true); }
                                catch (Exception ex) { Program.Logger?.Warn($"SaveAccounts failed after browser login: {ex.Message}"); }

                                if (sessionValid)
                                {
                                    Program.Logger?.Info($"[SignInAsync] Live session verified successfully for {account.Username} (UserId: {account.UserID})! Ready to close browser.");
                                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                                    break;
                                }
                                else
                                {
                                    lastAttemptedCookie = null;
                                    Program.Logger?.Warn($"[SignInAsync] Session for {account.Username} could not be validated yet; keeping browser open for completion.");
                                }
                            }
                            else
                            {
                                Program.Logger?.Warn("Roblox did not accept the session from the temporary browser; the cookie value was not logged.");
                            }
                        }
                    }
                    catch (TargetClosedException ex)
                    {
                        signInPage = null;
                        Program.Logger?.Warn("The Roblox sign-in tab changed while reading cookies; waiting for the active tab. " + ex.Message);
                    }
                    catch (PuppeteerException ex) when (IsDetachedPageException(ex))
                    {
                        signInPage = null;
                        Program.Logger?.Warn("The Roblox sign-in tab was replaced while reading cookies; waiting for the active tab. " + ex.Message);
                    }
                    catch (MessageException ex)
                    {
                        signInPage = null;
                        Program.Logger?.Warn("DevTools protocol message error in sign-in browser; retrying tab: " + ex.Message);
                    }
                    catch (PuppeteerException ex)
                    {
                        signInPage = null;
                        Program.Logger?.Warn("Puppeteer exception in sign-in browser: " + ex.Message);
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!handedOff && proxyAuthSession != null)
                {
                    await DetachProxyAuthenticationAsync(proxyAuthSession);
                }

                if (!handedOff)
                {
                    if (connectedBrowser != null)
                    {
                        try { await connectedBrowser.CloseAsync(); }
                        catch (Exception ex) { Program.Logger?.Debug($"connectedBrowser.CloseAsync threw: {ex.Message}"); }
                    }

                    if (browser != null)
                    {
                        try { if (!browser.HasExited) CloseBrowser(browser); }
                        catch (Exception ex) { Program.Logger?.Debug($"CloseBrowser threw: {ex.Message}"); }
                        browser.Dispose();
                    }

                    await TryDeleteTemporaryDirectoryAsync(temporaryRoot).ConfigureAwait(false);
                }
            }

            return createdAccount;
        }

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
                                        try { el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: val })); } catch(_) {}
                                        el.dispatchEvent(new Event('change', { bubbles: true }));
                                    } catch (_) {}
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
                MessageBoxDialog.Show(
                    "Shiro could not prepare an isolated Chrome/Edge/Chromium window. Install or repair Google Chrome or Microsoft Edge, then try again. " +
                    "You can also use Add Account > By Cookie. Keep the .ROBLOSECURITY value private; it grants access to your account.",
                    "Roblox sign-in", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBoxDialog.Show(
                "Roblox sign-in opened in an isolated Chrome/Edge profile. Complete verification there, then import the session using Add Account > By Cookie. " +
                "In browser developer tools, open Application/Storage > Cookies > https://www.roblox.com and copy the .ROBLOSECURITY value. " +
                "Keep that value private; it grants access to your account.",
                "Roblox sign-in", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                try { browser?.Dispose(); } catch (Exception) { }
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
