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
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    internal partial class AccountBrowser
    {
        [GeneratedRegex(@"/users/[0-9]+/two-step-verification/login")]
        private static partial Regex TwoStepLoginRegex();
        // PuppeteerSharp defaults to a `.local-chromium` directory beside the
        // executable. Keep the downloaded fallback browser in the same
        // user-data boundary as the other mutable caches so publishing or
        // upgrading never writes extra files into build\\Release.
        public static BrowserFetcher Fetcher = new BrowserFetcher(new BrowserFetcherOptions
        {
            Browser = SupportedBrowser.Chrome,
            Path = Path.Combine(StoragePaths.CacheDirectory, "Chromium")
        });

        private static Dictionary<int, Vector2> ScreenGrid;
        // Home's proxy fallback returns an interactive browser while the
        // caller only needs a success string. Keep the managed wrapper alive
        // until Chromium disconnects; otherwise a GC cycle could collect the
        // Puppeteer object while the external window is still starting.
        private static readonly ConcurrentDictionary<string, AccountBrowser> ActiveSessions =
            new ConcurrentDictionary<string, AccountBrowser>(StringComparer.Ordinal);

        private IWebProxy Proxy = null;
        private int proxyDisposed;
        private ICDPSession ProxyAuthSession;
        private string ProxyUsername;
        private string ProxyPassword;
        private bool AuthenticateProxyInBrowser;
        private string Password;

        public Browser browser;
        public Page page;
        public Vector2 Size = new Vector2(880, 740), Position;
        public int Index = -1;

        public AccountBrowser() { }

        /// <summary>
        /// Starts an asynchronous browser operation from a WinForms/event
        /// boundary while ensuring failures are observed. Puppeteer events do
        /// not accept Task-returning delegates, so this is the single safe
        /// bridge used by constructors and event callbacks.
        /// </summary>
        public static void Observe(Task task)
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

        private static string[] _cachedSystemBrowserExecutables;

        public static string FindSystemBrowserExecutable()
        {
            var executables = FindSystemBrowserExecutables();
            return executables.Length > 0 ? executables[0] : null;
        }

        public static string[] FindSystemBrowserExecutables()
        {
            var cached = _cachedSystemBrowserExecutables;
            if (cached != null && cached.Length > 0 && File.Exists(cached[0]))
                return cached;

            string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // A 32-bit build sees the 32-bit Program Files directory through
            // SpecialFolder.ProgramFiles. ProgramW6432 is needed to find a
            // normal 64-bit Chrome installation from that build.
            string ProgramFiles = Environment.GetEnvironmentVariable("ProgramFiles") ??
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string ProgramFiles64 = Environment.GetEnvironmentVariable("ProgramW6432");
            string ProgramFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ??
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] Candidates =
            [
                Path.Combine(LocalAppData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(LocalAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(ProgramFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(LocalAppData, "Programs", "Opera", "opera.exe"),
                Path.Combine(LocalAppData, "Programs", "Opera GX", "opera.exe"),
                Path.Combine(ProgramFiles, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(LocalAppData, "Vivaldi", "Application", "vivaldi.exe")
            ];

            string[] found = Candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (found.Length > 0)
                _cachedSystemBrowserExecutables = found;
            return found;
        }

        public AccountBrowser(Account account, string Url = null, string Script = null, Func<Page, Task> PostNavigation = null, bool UseStealth = true)
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
            string Url = null,
            string Script = null,
            Func<Page, Task> PostNavigation = null,
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
                    PostPageCreation: () => session.page.SetCookieAsync(new CookieParam
                    {
                        Name = ".ROBLOSECURITY",
                        Domain = ".roblox.com",
                        Expires = (double)DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds(),
                        HttpOnly = true,
                        Secure = true,
                        Url = "https://roblox.com",
                        Value = account.SecurityToken
                    }),
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
                AccountBrowser removedSession;
                ActiveSessions.TryRemove(key, out removedSession);
                DisposeProxy();
            };
        }

        private void DisposeProxy()
        {
            if (Interlocked.Exchange(ref proxyDisposed, 1) != 0)
                return;

            IWebProxy selectedProxy = Proxy;
            Proxy = null;
            try { (selectedProxy as IDisposable)?.Dispose(); }
            catch (Exception ex) { Program.Logger?.Debug($"Disposing web proxy threw: {ex.Message}"); }
        }

        public async Task LaunchBrowser(
            string Url = "https://roblox.com/",
            Func<Task> PostPageCreation = null,
            Func<Page, Task> PostNavigation = null,
            string Script = null,
            string[] Arguments = null,
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

            List<string> Args = new List<string>(Arguments ?? (UseStealth ? new[] { "--disable-web-security" } : Array.Empty<string>()));
            Args.AddRange(Services.BrowserService.NoPasswordManagerSwitches);

            string ExtensionPath = StoragePaths.ResolveAsset("extension");
            string ConfigPath = StoragePaths.GetUserFile("BrowserConfig.json");
            BrowserConfig Config = null;

            if (Directory.Exists(ExtensionPath))
                Args.AddRange(new string[] { $@"--disable-extensions-except=""{ExtensionPath}""", $@"--load-extension=""{ExtensionPath}""" });
            if (File.Exists(ConfigPath))
                File.ReadAllText(ConfigPath).TryParseJson(out Config);

            if (Config?.CustomArguments != null) Args.AddRange(Config.CustomArguments);

            if (Arguments == null)
                Args.AddRange(new string[] { $"--window-size=\"{(int)Size.X},{(int)Size.Y}\"", $"--window-position=\"{(int)Position.X},{(int)Position.Y}\"" });

            var verifiedProxy = await ChromeLaunchHelper.SelectVerifiedBrowserProxyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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
                Program.Logger.Warn($"No installed browser launched successfully. Falling back to PuppeteerSharp Chrome build {Chrome.DefaultBuildId}.");
                InstalledBrowser revision = await Fetcher.DownloadAsync(Chrome.DefaultBuildId);
                cancellationToken.ThrowIfCancellationRequested();
                Options.ExecutablePath = revision.GetExecutablePath();
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

        private void ProxyAuthSession_MessageReceived(object sender, PuppeteerSharp.MessageEventArgs e)
        {
            Observe(ChromeLaunchHelper.HandleCdpProxyAuthChallengeAsync(
                ProxyAuthSession,
                e,
                ProxyUsername,
                ProxyPassword,
                AuthenticateProxyInBrowser));
        }

        public async Task Login(string Username = "", string Password = "", string[] Arguments = null)
        {
            // Use a normal browser for the interactive Roblox sign-in so CAPTCHA,
            // passkeys and 2-step verification remain under the user's control.
            if (string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password))
            {
                // Keep this legacy public entry point on the same browser
                // boundary as the dashboard and bulk import flows.
                await new BrowserService().SignInAsync();
                return;
            }

            // Preserve the existing bulk username/password import flow.
            await LaunchBrowser("https://roblox.com/login", Arguments: Arguments,
                PostNavigation: async (page) => await LoginTask(page, Username, Password),
                UseStealth: false);
        }

        public async Task LoginTask(Page page, string Username = "", string Password = "")
        {
            page.RequestFinished += Page_RequestFinished;
            page.RequestFailed += Page_RequestFailed;

            await page.EvaluateExpressionAsync(@"document.body.classList.remove(""light-theme"");document.body.classList.add(""dark-theme"");");

            try
            {
                if (!string.IsNullOrEmpty(Username) && await page.WaitForSelectorAsync("#login-username", new WaitForSelectorOptions() { Timeout = 5000 }) != null)
                    await page.TypeAsync("#login-username", Username);
                if (!string.IsNullOrEmpty(Password) && await page.WaitForSelectorAsync("#login-password", new WaitForSelectorOptions() { Timeout = 5000 }) != null)
                    await page.TypeAsync("#login-password", Password);

                if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)) await page.ClickAsync("#login-button");
            }
            catch (Exception ex) { Program.Logger.Error($"An exception was caught while trying to automatically log in: {ex}"); }
        }

        private void Page_RequestFinished(object sender, RequestEventArgs e)
        {
            Observe(HandlePageRequestFinishedAsync(sender, e));
        }

        private async Task HandlePageRequestFinishedAsync(object sender, RequestEventArgs e)
        {
            try
            {
                if (e?.Request == null || !Uri.TryCreate(e.Request.Url, UriKind.Absolute, out Uri Url)) return;
                var Response = e.Request.Response;
                if (Response == null) return;

                if (IsCaptchaProviderHost(Url.Host) && (int)Response.Status >= 400)
                    Program.Logger.Warn($"Roblox verification request to {Url.Host} returned HTTP {(int)Response.Status}.");

                if (Response.Status == HttpStatusCode.OK && e.Request.Method == HttpMethod.Post && Url.Host == "auth.roblox.com")
                {
                    if ((Url.AbsolutePath == "/v2/login" || Url.AbsolutePath == "/v2/signup") && e.Request.PostData != null)
                    {
                        try
                        {
                            using var loginDoc = JsonDocument.Parse((string)e.Request.PostData);
                            var root = loginDoc.RootElement;
                            string password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;
                            string loginType = root.TryGetProperty("ctype", out var ct) ? ct.GetString() : null;
                            if (!string.IsNullOrEmpty(password) && string.Equals(loginType, "username", StringComparison.OrdinalIgnoreCase))
                                Password = password;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Parse login PostData failed: {ex.Message}");
                        }

                        if (page != null && !page.IsClosed)
                        {
                            var cookies = await page.GetCookiesAsync("https://roblox.com/");
                            if (cookies?.FirstOrDefault(Cookie => Cookie.Name == ".ROBLOSECURITY") is CookieParam Cookie)
                                await AddAccount(Cookie);
                        }
                    }
                    else if (TwoStepLoginRegex().IsMatch(Url.AbsolutePath) && page != null && !page.IsClosed)
                    {
                        var cookies = await page.GetCookiesAsync("https://roblox.com/");
                        if (cookies?.FirstOrDefault(Cookie => Cookie.Name == ".ROBLOSECURITY") is CookieParam Cookie)
                            await AddAccount(Cookie);
                    }
                }
            }
            catch (Exception ex) when (IsTransientBrowserException(ex))
            {
                // Benign transient navigation or destroyed context
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"HandlePageRequestFinishedAsync error: {ex.Message}");
            }
        }

        private static bool IsTransientBrowserException(Exception ex)
            => ChromeLaunchHelper.IsDetachedOrTransientBrowserException(ex);

        private void Page_RequestFailed(object sender, RequestEventArgs e)
        {
            if (Uri.TryCreate(e.Request.Url, UriKind.Absolute, out Uri Url) && IsCaptchaProviderHost(Url.Host))
                Program.Logger.Warn($"Roblox verification request to {Url.Host} failed: {e.Request.FailureText ?? "unknown network error"}.");
        }

        private static bool IsCaptchaProviderHost(string Host) =>
            Host.EndsWith(".arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
            Host.Equals("arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
            Host.EndsWith(".funcaptcha.com", StringComparison.OrdinalIgnoreCase) ||
            Host.Equals("funcaptcha.com", StringComparison.OrdinalIgnoreCase);

        private async Task AddAccount(CookieParam SecurityToken)
        {
            if (Proxy == null)
                await AccountManager.AddAccountAsync(SecurityToken.Value, Password).ConfigureAwait(false);
            else
            {
                await page.WaitForNavigationAsync().ConfigureAwait(false);
                string accountJson = await page.EvaluateFunctionAsync<string>("() => { return fetch('/my/account/json').then(x=>x.text()); }").ConfigureAwait(false);
                await AccountManager.AddAccountAsync(SecurityToken.Value, Password, accountJson).ConfigureAwait(false);
            }

            Password = null;

            await browser.DisposeAsync();
        }

        public static void CreateGrid(Vector2 Size)
        {
            ScreenGrid = new Dictionary<int, Vector2>();

            var (screenWidth, screenHeight) = GetVirtualScreen();

            for (int x = 0; x < screenWidth; x += (int)Size.X)
                for (int y = 0; y < screenHeight - (Size.Y / 2); y += (int)Size.Y)
                    ScreenGrid.Add(ScreenGrid.Count, new Vector2(x, y));
        }

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        private static (int width, int height) GetWorkArea()
        {
            if (OperatingSystem.IsWindows())
            {
                var (_, _, width, height) = RobloxWindowService.GetTargetWorkArea();
                if (width > 0 && height > 0)
                    return (width, height);
            }
            return (1920, 1080);
        }

        private static (int width, int height) GetVirtualScreen()
        {
            if (OperatingSystem.IsWindows())
            {
                int w = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
                int h = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
                if (w > 0 && h > 0) return (w, h);
            }
            return (1920, 1080);
        }
    }
    internal class BrowserConfig
    {
        public Dictionary<string, string> PreNavigateActions = null;

        public List<string> PreNavigateScripts = null;
        public List<string> PostNavigateScripts = null;

        public List<string> CustomArguments = null;
    }
}
