using System.Text.Json;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Shiro.Services;

namespace Shiro.Classes
{
    internal partial class AccountBrowser
    {
        // PuppeteerSharp defaults to a `.local-chromium` directory beside the
        // executable. Shiro no longer downloads Chromium, but older versions
        // did so into this cache folder; the fetcher is kept only to locate
        // such an existing copy (see FindCachedChromiumExecutable).
        public static BrowserFetcher Fetcher = new BrowserFetcher(new BrowserFetcherOptions
        {
            Browser = SupportedBrowser.Chrome,
            Path = Path.Combine(StoragePaths.CacheDirectory, "Chromium")
        });

        private static Dictionary<int, Vector2>? ScreenGrid;
        // Home's proxy fallback returns an interactive browser while the
        // caller only needs a success string. Keep the managed wrapper alive
        // until Chromium disconnects; otherwise a GC cycle could collect the
        // Puppeteer object while the external window is still starting.
        private static readonly ConcurrentDictionary<string, AccountBrowser> ActiveSessions =
            new ConcurrentDictionary<string, AccountBrowser>(StringComparer.Ordinal);

        private IWebProxy? Proxy = null;
        private int proxyDisposed;
        private ICDPSession? ProxyAuthSession;
        private string? ProxyUsername;
        private string? ProxyPassword;
        private bool AuthenticateProxyInBrowser;


        public Browser? browser;
        public Page? page;
        public Vector2 Size = new Vector2(880, 740), Position;
        public int Index = -1;

        public AccountBrowser() { }

        /// <summary>
        /// Starts an asynchronous browser operation from a UI/event
        /// boundary while ensuring failures are observed. Puppeteer events do
        /// not accept Task-returning delegates, so this is the single safe
        /// bridge used by constructors and event callbacks.
        /// </summary>
        public static void Observe(Task? task)
        {
            if (task == null)
                return;

            _ = ObserveAsync(task);
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Program.Logger.Error("Browser operation failed.", exception);
            }
        }

        public AccountBrowser(Account account, string? Url = null, string? Script = null, Func<Page, Task>? PostNavigation = null, bool UseStealth = true)
        {
            Observe(OpenAsync(account, Url, Script, PostNavigation, UseStealth));
        }

        /// <summary>
        /// Opens an account browser and completes only after the browser has
        /// launched, the session cookie has been installed and the first
        /// navigation has been accepted.  The constructor above is kept for
        /// legacy menu/event handlers that intentionally use fire-and-forget;
        /// flows that need a truthful result (Home/fallback launch) use this
        /// awaitable boundary instead.
        /// </summary>
        public static async Task<AccountBrowser> OpenAsync(
            Account account,
            string? Url = null,
            string? Script = null,
            Func<Page, Task>? PostNavigation = null,
            bool UseStealth = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            cancellationToken.ThrowIfCancellationRequested();
            AccountBrowser session = new AccountBrowser();
            try
            {
                await session.LaunchBrowser(
                    Url ?? string.Empty,
                    Script: Script,
                    PostNavigation: PostNavigation,
                    UseStealth: UseStealth,
                    PostPageCreation: () => session.page?.SetCookieAsync(new CookieParam
                    {
                        Name = ".ROBLOSECURITY",
                        Domain = ".roblox.com",
                        Expires = (double)DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds(),
                        HttpOnly = true,
                        Secure = true,
                        Url = "https://roblox.com",
                        Value = account.SecurityToken
                    }) ?? Task.CompletedTask,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (session.page == null || session.page.IsClosed)
                    throw new InvalidOperationException("The Chromium page closed before the Roblox Home session was ready.");

                CookieParam[] cookies = await session.page
                    .GetCookiesAsync("https://www.roblox.com/")
                    .ConfigureAwait(false);
                if (!(cookies ?? Array.Empty<CookieParam>()).Any(cookie =>
                    string.Equals(cookie.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(cookie.Value)))
                    throw new InvalidOperationException("The isolated Chromium profile did not accept the Roblox session cookie.");

                session.KeepAliveUntilDisconnected();

                return session;
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"Failed to launch or initialize isolated browser session: {ex.Message}");
                try
                {
                    if (session.browser != null && session.browser.IsConnected)
                        await session.browser.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception closeEx)
                {
                    Program.Logger?.Debug($"Closing failed browser session threw: {closeEx.Message}");
                }

                // A browser can fail before the Disconnected handler is
                // attached. Dispose the selected proxy from the failure path
                // as well, while the idempotent helper keeps this safe when
                // Chromium did emit Disconnected during CloseAsync.
                session.DisposeProxy();

                throw;
            }
        }

        private void KeepAliveUntilDisconnected()
        {
            if (browser == null)
                return;

            string key = Guid.NewGuid().ToString("N");
            ActiveSessions[key] = this;
            browser.Disconnected += (_, __) =>
            {
                // Use an explicitly typed value here.  The discard overload
                // is inferred as `object` by the compiler for this event
                // callback on some target framework combinations, which
                // prevents the project from compiling even though the
                // dictionary itself is strongly typed.
                AccountBrowser? removedSession;
                ActiveSessions.TryRemove(key, out removedSession);
                DisposeProxy();
            };
        }

        private void DisposeProxy()
        {
            if (Interlocked.Exchange(ref proxyDisposed, 1) != 0)
                return;

            IWebProxy? selectedProxy = Proxy;
            Proxy = null;
            try { (selectedProxy as IDisposable)?.Dispose(); }
            catch (Exception ex) { Program.Logger?.Debug($"Disposing web proxy threw: {ex.Message}"); }
        }

        public async Task LaunchBrowser(
            string Url = "https://roblox.com/",
            Func<Task>? PostPageCreation = null,
            Func<Page, Task>? PostNavigation = null,
            string? Script = null,
            string[]? Arguments = null,
            bool UseStealth = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(Url)) Url = "https://roblox.com/";

            if (Index >= 0 && ScreenGrid != null && ScreenGrid.Count > 0)
                Position = ScreenGrid[Index % ScreenGrid.Count];
            else if (Position == default(Vector2))
            {
                var (workW, workH) = GetWorkArea();
                Position = new Vector2((float)(workW / 2 - (Size.X / 2)), (float)(workH / 2 - (Size.Y / 2)));
            }

            List<string> Args = new List<string>(Arguments ?? Array.Empty<string>());
            Args.AddRange(Services.BrowserService.NoPasswordManagerSwitches);

            string ExtensionPath = StoragePaths.ResolveAsset("extension");
            string ConfigPath = StoragePaths.GetUserFile("BrowserConfig.json");
            BrowserConfig? Config = null;

            if (Directory.Exists(ExtensionPath))
                Args.AddRange(new string[] { $@"--disable-extensions-except=""{ExtensionPath}""", $@"--load-extension=""{ExtensionPath}""" });
            if (File.Exists(ConfigPath))
                File.ReadAllText(ConfigPath).TryParseJson(out Config);

            if (Config?.CustomArguments != null) Args.AddRange(Config.CustomArguments);

            if (Arguments == null)
                Args.AddRange(new string[] { $"--window-size=\"{(int)Size.X},{(int)Size.Y}\"", $"--window-position=\"{(int)Position.X},{(int)Position.Y}\"" });

            VerifiedBrowserProxy? verifiedProxy = null;
            try
            {
                verifiedProxy = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Program.Logger?.Error($"[AccountBrowser] {ex.Message}");
                ToastService.ShowError("Lỗi Proxy", ex.Message);
                return;
            }
            if (verifiedProxy != null)
            {
                Proxy = verifiedProxy.WebProxy;
                AuthenticateProxyInBrowser = verifiedProxy.AuthenticateInBrowser;
                ProxyUsername = verifiedProxy.Username;
                ProxyPassword = verifiedProxy.Password;
                if (!string.IsNullOrEmpty(verifiedProxy.RawAuthority))
                    Args.Add($"--proxy-server={verifiedProxy.RawAuthority}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var Options = new LaunchOptions
            {
                Headless = false,
                DefaultViewport = null,
                Args = Args.ToArray(),
                AcceptInsecureCerts = UseStealth
            };

            foreach (string SystemBrowser in FindSystemBrowserExecutables())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Options.ExecutablePath = SystemBrowser;

                try
                {
                    Program.Logger.Info($"Launching installed browser: {SystemBrowser}");
                    browser = await LaunchBrowserInstance(Options, UseStealth);
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                }
                catch (Exception ex)
                {
                    Program.Logger.Warn($"Unable to launch the installed browser at {SystemBrowser}; trying the next browser candidate.", ex);
                }
            }

            if (browser == null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? cachedChromium = FindCachedChromiumExecutable();
                if (string.IsNullOrEmpty(cachedChromium))
                    throw new FileNotFoundException(BrowserNotFoundMessage);

                Program.Logger.Warn($"No installed browser launched successfully. Using previously cached Chromium at {cachedChromium}.");
                Options.ExecutablePath = cachedChromium;
                browser = await LaunchBrowserInstance(Options, UseStealth);
                cancellationToken.ThrowIfCancellationRequested();
            }

            page = (Page)(await browser.PagesAsync())[0];

            if (UseStealth)
            {
                try
                {
                    await page.EvaluateExpressionOnNewDocumentAsync(
                        "Object.defineProperty(navigator, 'webdriver', {get: () => undefined});" +
                        "if (!window.chrome) { window.chrome = { runtime: {} }; }"
                    );
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"EvaluateExpressionOnNewDocumentAsync stealth failed: {ex.Message}");
                }
            }

            if (AuthenticateProxyInBrowser)
            {
                ProxyAuthSession = await page.Target.CreateCDPSessionAsync();
                ProxyAuthSession.MessageReceived += ProxyAuthSession_MessageReceived;
                await ProxyAuthSession.SendAsync("Fetch.enable", new { handleAuthRequests = true });
            }

            if (Proxy != null) browser.Disconnected += (s, e) => DisposeProxy();

            if (Config?.PreNavigateActions != null)
                foreach (var action in Config.PreNavigateActions)
                {
                    if (!Uri.IsWellFormedUriString(action.Key, UriKind.Absolute)) continue;

                    await page.GoToAsync(action.Key);
                    await page.EvaluateExpressionAsync(action.Value);
                }

            if (Config?.PreNavigateScripts != null)
                foreach (var script in Config.PreNavigateScripts)
                    await page.EvaluateExpressionAsync(script);

            if (PostPageCreation != null)
                try { await PostPageCreation(); }
                catch (Exception ex) { Program.Logger?.Error($"PostPageCreation hook failed: {ex.Message}"); }

            try { await page.GoToAsync(Url, new NavigationOptions { Timeout = 300000 }); }
            catch (Exception ex) { Program.Logger?.Warn($"Navigation to '{Url}' failed or timed out: {ex.Message}"); }

            if (!string.IsNullOrEmpty(Script)) await page.EvaluateExpressionAsync(Script);

            // CAPTCHA and human verification stay in the user's hands. No
            // solver hook is attached to Roblox pages.

            if (PostNavigation != null)
                try { await PostNavigation(page); }
                catch (Exception ex) { Program.Logger?.Error($"PostNavigation hook failed: {ex.Message}"); }

            if (Config?.PostNavigateScripts != null)
                foreach (var script in Config.PostNavigateScripts)
                    await page.EvaluateExpressionAsync(script);
        }

        private static async Task<Browser> LaunchBrowserInstance(LaunchOptions options, bool UseStealth)
        {
            if (UseStealth)
            {
                var args = options.Args != null ? new List<string>(options.Args) : new List<string>();
                if (!args.Contains("--disable-blink-features=AutomationControlled"))
                    args.Add("--disable-blink-features=AutomationControlled");
                options.Args = args.ToArray();
            }

            return (Browser)await Puppeteer.LaunchAsync(options);
        }

        private void ProxyAuthSession_MessageReceived(object? sender, PuppeteerSharp.MessageEventArgs e)
        {
            if (ProxyAuthSession != null)
            {
                Observe(ChromeLaunchHelper.HandleCdpProxyAuthChallengeAsync(
                    ProxyAuthSession,
                    e,
                    ProxyUsername ?? string.Empty,
                    ProxyPassword ?? string.Empty,
                    AuthenticateProxyInBrowser));
            }
        }
 }
}
