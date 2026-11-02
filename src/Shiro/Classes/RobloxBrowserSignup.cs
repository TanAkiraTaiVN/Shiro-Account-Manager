using PuppeteerSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Classes
{
    /// <summary>
    /// Opens the Roblox sign-up page in a temporary, isolated browser profile.
    /// Roblox's account-creation risk checks are sensitive to scripted form
    /// input and synthetic clicks, so the final form entry and submission stay
    /// in a normal browser window under the user's control. The temporary
    /// profile keeps the user's personal Chrome session and bookmarks out of
    /// the account-creation flow.
    /// </summary>
    internal static partial class RobloxBrowserSignup
    {
        private const int DevToolsStartupTimeoutSeconds = 12;
        private const int DevToolsPollMilliseconds = 100;
        private const int NavigationTimeoutMilliseconds = 15000;
        private const int SelectorTimeoutMilliseconds = 10000;
        private const int SignupButtonReadyTimeoutMilliseconds = 8000;
        // Roblox rebuilds parts of the sign-up form while its React bundles,
        // risk checks and verification widget load.  A CDP evaluation that
        // overlaps that rebuild loses its execution context.  Keep retries
        // short so the browser still feels immediate, while allowing a new
        // document context to settle before trying the same read again.
        private const int NavigationRetryCount = 6;
        private const int NavigationRetryDelayMilliseconds = 220;
        // Use CDP only to prefill the ordinary form controls. The final
        // Sign Up click and every CAPTCHA/human-verification step remain
        // manual, so Roblox receives a normal user-driven submission while
        // the requested username, password and generated profile are ready.
        // Keep the Add Account browser compact and centered, matching the
        // portrait-sized Chrome window used by the Shiro UI.
        private const int InteractiveBrowserWidth = 630;
        private const int InteractiveBrowserHeight = 840;
        private const string SignupUrl = "https://www.roblox.com/signup";

        private static string SignupSessionRoot => StoragePaths.BrowserSessionsDirectory;

        internal sealed class SignupProfile
        {
            public DateTime BirthDate { get; set; }
            public string Gender { get; set; } = "male";
        }

        public static SignupProfile CreateRandomProfile()
        {
            // Use an adult range so the generated form does not introduce
            // parental-control requirements or an accidentally underage account.
            DateTime oldest = DateTime.Today.AddYears(-35);
            DateTime youngest = DateTime.Today.AddYears(-18);
            int days = Math.Max(1, (youngest - oldest).Days);

            return new SignupProfile
            {
                BirthDate = oldest.AddDays(RandomNumberGenerator.GetInt32(days + 1)),
                Gender = RandomNumberGenerator.GetInt32(2) == 0 ? "Male" : "Female"
            };
        }

        public static async Task<Account?> OpenAsync(
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string browserPath = await BrowserService.ResolveBrowserExecutableAsync(cancellationToken)
                .ConfigureAwait(false);

            SignupProfile profile = CreateRandomProfile();
            string sessionRoot = Path.Combine(SignupSessionRoot, Guid.NewGuid().ToString("N"));
            string profilePath = Path.Combine(sessionRoot, "profile");
            int devToolsPort = GetFreeLoopbackPort();
            Process? browser = null;
            IBrowser? connectedBrowser = null;

            try
            {
                Directory.CreateDirectory(profilePath);
                BrowserService.SuppressPasswordManager(profilePath);

                string[] browserArguments =
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
                    "--disable-features=AutofillServerCommunication,PasswordManager,SavePasswordBubble,PasswordLeakDetection,msImplicitSignin,msEdgeSidebarV2",
                    $"--window-size={InteractiveBrowserWidth},{InteractiveBrowserHeight}",
                    $"--window-position={GetCenteredBrowserPosition()}",
                    "--user-data-dir=" + QuoteArgument(profilePath),
                    "--remote-debugging-address=127.0.0.1",
                    "--remote-debugging-port=" + devToolsPort,
                    QuoteArgument(SignupUrl)
                };

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = string.Join(" ", browserArguments),
                    UseShellExecute = false,
                    WorkingDirectory = sessionRoot
                };

                browser = Process.Start(startInfo);
                if (browser == null)
                    throw new InvalidOperationException("The browser did not start.");

                await WaitForDevToolsPortAsync(devToolsPort, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                connectedBrowser = await Puppeteer.ConnectAsync(new ConnectOptions
                {
                    BrowserURL = "http://127.0.0.1:" + devToolsPort,
                    DefaultViewport = null
                });

                IPage? page = (await connectedBrowser.PagesAsync()).FirstOrDefault();
                if (page == null)
                    page = await connectedBrowser.NewPageAsync();

                await ChromeLaunchHelper.ApplyAntiDetectionAsync(page).ConfigureAwait(false);
                await EnsureSignupPageAsync(page);
                await ChromeLaunchHelper.DismissCookieBannerAsync(page).ConfigureAwait(false);
                await FillSignupFormAsync(page, username, password, profile, submitCredentials: false);

                Program.Logger.Info("Roblox sign-up page opened in Chrome. Waiting for user to complete registration...");
                AppSettings.SetDashboardStatus(
                    "Đã mở trang đăng ký Roblox trong Chrome. Hoàn tất đăng ký trong trình duyệt; tài khoản sẽ tự động được thêm vào Shiro.");

                string? lastAttemptedCookie = null;
                DateTime lastCookieAttemptUtc = DateTime.MinValue;
                string? capturedPassword = password;
                Account? createdAccount = null;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (connectedBrowser == null || connectedBrowser.IsClosed || !connectedBrowser.IsConnected)
                        throw new OperationCanceledException("The sign-up browser was closed before registration was completed.");

                    try
                    {
                        if (page == null || page.IsClosed)
                        {
                            page = await FindActiveRobloxPageAsync(connectedBrowser);
                            if (page == null)
                            {
                                page = await connectedBrowser.NewPageAsync();
                                await EnsureSignupPageAsync(page);
                            }
                        }

                        // Try to capture password typed into #signup-password if not provided upfront
                        if (string.IsNullOrWhiteSpace(capturedPassword) && page != null && !page.IsClosed)
                        {
                            try
                            {
                                (bool hasVal, string? domPass) = await TryEvaluateFunctionAsync<string>(page,
                                    "() => document.querySelector('#signup-password')?.value || ''");
                                if (hasVal && !string.IsNullOrWhiteSpace(domPass))
                                    capturedPassword = domPass;
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Extract domPass threw: {ex.Message}");
                            }
                        }

                        // Read cookies for roblox.com
                        CookieParam? sessionCookie = null;
                        try
                        {
                            if (page != null)
                                sessionCookie = await ExtractRoblosecurityCookieAsync(page).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"ExtractRoblosecurityCookieAsync threw: {ex.Message}");
                        }

                        // Fallback: search across all pages in the browser
                        if (sessionCookie == null && connectedBrowser != null)
                        {
                            try
                            {
                                IPage[] allPages = await connectedBrowser.PagesAsync();
                                foreach (IPage candidatePage in allPages)
                                {
                                    if (candidatePage.IsClosed) continue;
                                    CookieParam? found = await ExtractRoblosecurityCookieAsync(candidatePage).ConfigureAwait(false);
                                    if (found != null)
                                    {
                                        sessionCookie = found;
                                        page = candidatePage;
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Scan allPages for cookies threw: {ex.Message}");
                            }
                        }

                        if (sessionCookie != null &&
                            (!string.Equals(sessionCookie.Value, lastAttemptedCookie, StringComparison.Ordinal) ||
                             (DateTime.UtcNow - lastCookieAttemptUtc).TotalSeconds >= 15))
                        {
                            lastAttemptedCookie = sessionCookie.Value;
                            lastCookieAttemptUtc = DateTime.UtcNow;

                            string? accountJson = page != null ? await ReadAccountJsonAsync(page) : null;
                            string finalPassword = !string.IsNullOrWhiteSpace(capturedPassword)
                                ? capturedPassword
                                : (password ?? string.Empty);

                            createdAccount = await AppServices.AccountRepository.AddOrUpdateAsync(sessionCookie.Value, finalPassword, accountJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (createdAccount != null)
                            {
                                Program.Logger?.Info($"Roblox account '{createdAccount.Username}' registered and added to manager successfully.");
                                AppSettings.SetDashboardStatus(
                                    $"Đã đăng ký và thêm tài khoản '{createdAccount.Username}' thành công!");
                                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                                break;
                            }

                            Program.Logger?.Warn("Roblox session cookie was detected but AddAccount did not complete; retrying...");
                        }
                    }
                    catch (TargetClosedException)
                    {
                        page = null;
                    }
                    catch (PuppeteerException ex) when (IsDetachedPageException(ex) || IsTransientNavigationException(ex))
                    {
                        page = null;
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }

                return createdAccount;
            }
            finally
            {
                if (connectedBrowser != null)
                {
                    try { await connectedBrowser.CloseAsync(); }
                    catch (Exception ex) { Program.Logger?.Debug($"connectedBrowser.CloseAsync threw: {ex.Message}"); }
                }
                if (browser != null)
                    CloseBrowser(browser);
                await TryDeleteTemporaryDirectoryAsync(sessionRoot).ConfigureAwait(false);
            }
        }
    }
}
