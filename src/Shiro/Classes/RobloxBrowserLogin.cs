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
using Shiro.AvaloniaUI.Views;
using Shiro.Services;

namespace Shiro.Classes
{
    /// <summary>
    /// Runs Roblox sign-in in a normal, isolated Chrome/Edge profile so Roblox's
    /// human verification is handled by the browser and the user.
    /// </summary>
    internal static partial class RobloxBrowserLogin
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
            using var interactiveLoginScope = RobloxHbaService.BeginInteractiveLoginScope();
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
                            var hbaSnapshot = await ExtractHbaCaptureFromPageAsync(signInPage).ConfigureAwait(false);
                            Account? account = await AccountManager.AddAccountAsync(settledCookieValue, password ?? string.Empty, accountJson, cancellationToken: cancellationToken, hbaCapture: hbaSnapshot).ConfigureAwait(false);
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
    }
}
