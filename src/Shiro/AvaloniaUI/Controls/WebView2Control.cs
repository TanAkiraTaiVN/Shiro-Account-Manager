#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Controls
{
    public partial class WebView2Control : NativeControlHost
    {
        private const string BaseBrowserArguments = "--disable-features=DeviceBoundSessionCredentials,Translate,OptimizationHints,MediaRouter";
        private static readonly System.Threading.Lock EnvironmentLock = new();
        private static readonly ConcurrentDictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks = new(StringComparer.Ordinal);

        private IntPtr _childHwnd;
        private CoreWebView2Controller? _controller;
        private CoreWebView2Environment? _environment;
        private string? _currentProfileName;
        private string? _currentDataFolder;
        private string? _proxyServerArg;
        private byte[]? _protectedProxyUser;
        private byte[]? _protectedProxyPass;
        private int _proxyAuthAttemptCount;
        private bool _isInitialized;
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;
        public CoreWebView2Controller? Controller => _controller;

        public event EventHandler<WebView2InitializedEventArgs>? CoreWebView2InitializationCompleted;

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(IntPtr hwnd);

        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (!OperatingSystem.IsWindows())
                return base.CreateNativeControlCore(parent);

            double scale = VisualRoot?.RenderScaling ?? 1.0;
            int width = Math.Max(100, (int)Math.Round(Bounds.Width * scale));
            int height = Math.Max(100, (int)Math.Round(Bounds.Height * scale));

            _childHwnd = CreateWindowEx(
                0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, width, height, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_childHwnd == IntPtr.Zero)
            {
                var error = new InvalidOperationException($"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
                AppServices.Logger?.Error($"[WebView2Control] {error.Message}");
                CoreWebView2InitializationCompleted?.Invoke(this, new WebView2InitializedEventArgs(error));
                return base.CreateNativeControlCore(parent);
            }

            _ = InitializeWebView2Async();
            return new PlatformHandle(_childHwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (OperatingSystem.IsWindows())
            {
                string? oldProfile = _currentProfileName;
                string? oldDataFolder = _currentDataFolder;
                _currentProfileName = null;
                _currentDataFolder = null;

                var oldController = _controller;
                _controller = null;
                CloseController(oldController);

                InMemorySecretProtector.ZeroAndClear(ref _protectedProxyUser);
                InMemorySecretProtector.ZeroAndClear(ref _protectedProxyPass);
                Volatile.Write(ref _proxyAuthAttemptCount, 0);

                if (_childHwnd != IntPtr.Zero)
                {
                    SafeExecuteHelper.Try(() => DestroyWindow(_childHwnd), "WebView2Control.DestroyWindow");
                    _childHwnd = IntPtr.Zero;
                }

                ScheduleOwnedProfileCleanup(oldProfile, oldDataFolder);
            }

            base.DestroyNativeControlCore(control);
        }

        /// <summary>Configures per-instance proxy routing and protects proxy credentials in memory.</summary>
        public void ConfigureProxy(string? rawProxy)
        {
            InMemorySecretProtector.ZeroAndClear(ref _protectedProxyUser);
            InMemorySecretProtector.ZeroAndClear(ref _protectedProxyPass);
            Volatile.Write(ref _proxyAuthAttemptCount, 0);

            if (!TryParseProxyForWebView(rawProxy, out string? proxyServerArg, out string? username, out string? password, out bool isSocksWithAuth))
            {
                _proxyServerArg = null;
                if (!string.IsNullOrWhiteSpace(rawProxy))
                    AppServices.Logger?.Warn($"[WebView2Control] Invalid proxy '{ProxyManagerService.RedactProxy(rawProxy)}' ignored.");
                return;
            }

            _proxyServerArg = proxyServerArg;
            if (isSocksWithAuth)
                AppServices.Logger?.Warn($"[WebView2Control] SOCKS proxy '{proxyServerArg}' has credentials; Chromium WebView2 does not natively authenticate SOCKS5.");

            _protectedProxyUser = InMemorySecretProtector.Protect(username);
            _protectedProxyPass = InMemorySecretProtector.Protect(password);
        }

        /// <summary>Parses a proxy string into a credential-stripped Chromium server argument and optional credentials.</summary>
        internal static bool TryParseProxyForWebView(
            string? rawProxy, out string? proxyServerArg, out string? username, out string? password, out bool isSocksWithAuth)
        {
            proxyServerArg = null;
            username = null;
            password = null;
            isSocksWithAuth = false;

            if (string.IsNullOrWhiteSpace(rawProxy))
                return false;

            ProxyItem? parsed = ProxyManagerService.ParseProxy(rawProxy);
            if (parsed == null)
                return false;

            string protocol = string.IsNullOrWhiteSpace(parsed.Protocol) ? "http" : parsed.Protocol.Trim().ToLowerInvariant();
            proxyServerArg = $"{protocol}://{ProxyManagerService.FormatHostForUri(parsed.Host)}:{parsed.Port}";

            username = string.IsNullOrEmpty(parsed.Username)
                ? null
                : (parsed.IsUriEncoded ? TrySafeUnescape(parsed.Username) : parsed.Username);
            password = string.IsNullOrEmpty(parsed.Password)
                ? null
                : (parsed.IsUriEncoded ? TrySafeUnescape(parsed.Password) : parsed.Password);

            isSocksWithAuth = protocol.StartsWith("socks", StringComparison.OrdinalIgnoreCase) &&
                              (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password));
            return true;
        }

        private static string TrySafeUnescape(string value)
        {
            try { return Uri.UnescapeDataString(value); }
            catch (UriFormatException) { return value; }
        }

        /// <summary>Builds WebView2 browser arguments, appending a credential-free proxy flag when provided.</summary>
        internal static string BuildAdditionalBrowserArguments(string? proxyServerArg)
        {
            if (string.IsNullOrWhiteSpace(proxyServerArg))
                return BaseBrowserArguments;

            string sanitized = proxyServerArg.Trim();
            if (sanitized.Contains('@') || sanitized.Any(char.IsWhiteSpace) || sanitized.Contains('"'))
            {
                if (TryParseProxyForWebView(sanitized, out string? stripped, out _, out _, out _) && !string.IsNullOrWhiteSpace(stripped))
                    sanitized = stripped;
                else
                    return BaseBrowserArguments;
            }

            if (sanitized.Contains('@') || sanitized.Any(char.IsWhiteSpace) || sanitized.Contains('"'))
                return BaseBrowserArguments;

            return $"{BaseBrowserArguments} --proxy-server={sanitized}";
        }

        private static string GetEnvironmentKey(string? proxyServerArg)
        {
            if (string.IsNullOrWhiteSpace(proxyServerArg))
                return "direct";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(proxyServerArg.Trim()));
            return Convert.ToHexStringLower(hash);
        }

        /// <summary>Resolves the WebView2 user data folder for direct browsing or an isolated SHA-256 proxy folder.</summary>
        internal static string ResolveEnvironmentDataFolder(string? proxyServerArg)
        {
            if (string.IsNullOrWhiteSpace(proxyServerArg))
                return GetEmbeddedBrowserDataFolder();

            string dataFolder = Path.Combine(
                StoragePaths.BrowserSessionsDirectory,
                $"EmbeddedBrowser_Proxy_{GetEnvironmentKey(proxyServerArg)}");
            Directory.CreateDirectory(dataFolder);
            return dataFolder;
        }

        internal static string GetEmbeddedBrowserDataFolder()
        {
            string dataFolder = Path.Combine(StoragePaths.BrowserSessionsDirectory, "EmbeddedBrowser");
            Directory.CreateDirectory(dataFolder);
            return dataFolder;
        }

        internal static bool IsOwnedProfileDirectoryName(string? profileName)
        {
            const string Prefix = "Shiro_";
            if (string.IsNullOrEmpty(profileName) || !profileName.StartsWith(Prefix, StringComparison.Ordinal) || profileName.Length != Prefix.Length + 32)
                return false;

            return Guid.TryParseExact(profileName.AsSpan(Prefix.Length), "N", out _);
        }

        internal static bool TryDeleteOwnedProfileDirectory(string dataFolder, string? profileName)
        {
            if (string.IsNullOrWhiteSpace(dataFolder) || !IsOwnedProfileDirectoryName(profileName))
                return false;

            try
            {
                string profilesRoot = Path.Combine(dataFolder, "EBWebView");
                string targetDir = Path.Combine(profilesRoot, profileName!);
                if (!StoragePaths.IsWithinDirectory(targetDir, profilesRoot) || !Directory.Exists(targetDir))
                    return false;

                Directory.Delete(targetDir, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppServices.Logger?.Debug($"[WebView2Control] Profile directory delete skipped: {ex.Message}");
                return false;
            }
        }

        private static void ScheduleOwnedProfileCleanup(string? profileName, string? dataFolder = null)
        {
            if (!IsOwnedProfileDirectoryName(profileName)) return;

            string targetDataFolder = string.IsNullOrWhiteSpace(dataFolder) ? GetEmbeddedBrowserDataFolder() : dataFolder;
            _ = Task.Run(async () =>
            {
                if (TryDeleteOwnedProfileDirectory(targetDataFolder, profileName)) return;
                await Task.Delay(350).ConfigureAwait(false);
                if (TryDeleteOwnedProfileDirectory(targetDataFolder, profileName)) return;
                await Task.Delay(1000).ConfigureAwait(false);
                TryDeleteOwnedProfileDirectory(targetDataFolder, profileName);
            });
        }

        // Removes isolated per-account profiles (EBWebView\Shiro_<guid>) left behind by a crash.
        internal static void CleanupOrphanedProfiles(string dataFolder)
        {
            try
            {
                string profilesRoot = Path.Combine(dataFolder, "EBWebView");
                if (!Directory.Exists(profilesRoot)) return;

                DateTime cutoffUtc = DateTime.UtcNow.AddHours(-1);
                foreach (string dir in Directory.EnumerateDirectories(profilesRoot, "Shiro_*"))
                {
                    if (!StoragePaths.IsWithinDirectory(dir, profilesRoot) || !IsOwnedProfileDirectoryName(Path.GetFileName(dir))) continue;
                    try { if (StoragePaths.IsDirectoryStale(dir, cutoffUtc)) Directory.Delete(dir, true); }
                    catch (Exception ex) { AppServices.Logger?.Debug($"[WebView2Control] Could not remove orphaned profile: {ex.Message}"); }
                }
            }
            catch (Exception ex) { AppServices.Logger?.Debug($"[WebView2Control] Orphaned profile cleanup failed: {ex.Message}"); }
        }

        private static Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync(string? proxyServerArg)
        {
            string envKey = GetEnvironmentKey(proxyServerArg);
            lock (EnvironmentLock)
            {
                if (EnvironmentTasks.TryGetValue(envKey, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
                    return existing;

                string dataFolder = ResolveEnvironmentDataFolder(proxyServerArg);
                CleanupOrphanedProfiles(dataFolder);

                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BuildAdditionalBrowserArguments(proxyServerArg) };
                var created = CoreWebView2Environment.CreateAsync(null, dataFolder, options);
                EnvironmentTasks[envKey] = created;
                return created;
            }
        }

        private void SetControllerEvents(CoreWebView2? core, bool attach)
        {
            if (core == null) return;
            if (attach) { core.NavigationStarting += OnCoreNavigationStarting; core.BasicAuthenticationRequested += OnBasicAuthenticationRequested; }
            else { core.NavigationStarting -= OnCoreNavigationStarting; core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested; }
        }

        private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
            => Volatile.Write(ref _proxyAuthAttemptCount, 0);

        private void OnBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
        {
            byte[]? userBlob = _protectedProxyUser;
            byte[]? passBlob = _protectedProxyPass;
            if (userBlob == null && passBlob == null) return;
            if (Interlocked.Increment(ref _proxyAuthAttemptCount) > 2) { e.Cancel = true; return; }

            try
            {
                e.Response.UserName = userBlob != null ? InMemorySecretProtector.Unprotect(userBlob) : string.Empty;
                e.Response.Password = passBlob != null ? InMemorySecretProtector.Unprotect(passBlob) : string.Empty;
            }
            catch (CryptographicException ex)
            {
                AppServices.Logger?.Warn($"[WebView2Control] Failed to unprotect proxy credentials: {ex.Message}");
                e.Cancel = true;
            }
        }

        private void CloseController(CoreWebView2Controller? controller)
        {
            if (controller == null) return;
            try
            {
                CoreWebView2? core = controller.CoreWebView2;
                if (core != null) { SetControllerEvents(core, attach: false); SafeExecuteHelper.Try(() => core.Stop(), "WebView2Control.StopOldCore"); }
            }
            catch (Exception ex) { AppServices.Logger?.Debug($"Stopping old CoreWebView2 threw: {ex.Message}"); }
            SafeExecuteHelper.Try(() => controller.Close(), "WebView2Control.CloseOldController");
        }

        private async Task<(CoreWebView2Controller Controller, string ProfileName, string DataFolder)> CreateIsolatedControllerCoreAsync()
        {
            string? proxyServerArg = _proxyServerArg;
            string dataFolder = ResolveEnvironmentDataFolder(proxyServerArg);
            _environment = await GetOrCreateEnvironmentAsync(proxyServerArg);
            var controllerOptions = _environment.CreateCoreWebView2ControllerOptions();
            string profileName = $"Shiro_{Guid.NewGuid():N}";
            controllerOptions.ProfileName = profileName;
            controllerOptions.IsInPrivateModeEnabled = false;

            var controller = await _environment.CreateCoreWebView2ControllerAsync(_childHwnd, controllerOptions);
            try { controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 13, 17); }
            catch (Exception ex) { AppServices.Logger?.Debug($"Set DefaultBackgroundColor threw: {ex.Message}"); }
            return (controller, profileName, dataFolder);
        }

        private void ActivateController(CoreWebView2Controller controller, string profileName, string dataFolder, bool resetBounds)
        {
            _controller = controller;
            _currentProfileName = profileName;
            _currentDataFolder = dataFolder;
            SetControllerEvents(_controller.CoreWebView2, attach: true);
            if (resetBounds) { _lastWidth = -1; _lastHeight = -1; }
            UpdateBounds();
            _controller.IsVisible = true;
            _isInitialized = true;
        }

        public async Task<CoreWebView2?> ResetToFreshProfileAsync()
        {
            if (!OperatingSystem.IsWindows() || _childHwnd == IntPtr.Zero) return CoreWebView2;

            var oldController = _controller;
            string? oldProfile = _currentProfileName, oldDataFolder = _currentDataFolder;
            _controller = null; _currentProfileName = null; _currentDataFolder = null;
            Volatile.Write(ref _proxyAuthAttemptCount, 0);

            CloseController(oldController);
            ScheduleOwnedProfileCleanup(oldProfile, oldDataFolder);

            var (controller, newProfileName, newDataFolder) = await CreateIsolatedControllerCoreAsync();
            if (_childHwnd == IntPtr.Zero) { CloseController(controller); ScheduleOwnedProfileCleanup(newProfileName, newDataFolder); return null; }

            ActivateController(controller, newProfileName, newDataFolder, resetBounds: true);
            return _controller!.CoreWebView2;
        }

        private async Task InitializeWebView2Async()
        {
            if (_isInitialized || _childHwnd == IntPtr.Zero) return;

            try
            {
                var (controller, profileName, dataFolder) = await CreateIsolatedControllerCoreAsync();
                if (_childHwnd == IntPtr.Zero) { CloseController(controller); ScheduleOwnedProfileCleanup(profileName, dataFolder); return; }

                ActivateController(controller, profileName, dataFolder, resetBounds: false);
                CoreWebView2InitializationCompleted?.Invoke(this, new WebView2InitializedEventArgs(null));
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[WebView2Control] Failed to initialize: {ex}");
                CoreWebView2InitializationCompleted?.Invoke(this, new WebView2InitializedEventArgs(ex));
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);
            UpdateBounds();
            return result;
        }

        public void UpdateBounds()
        {
            if (_controller == null || _childHwnd == IntPtr.Zero)
                return;

            try
            {
                double scale = VisualRoot?.RenderScaling ?? 1.0;
                int w = (int)Math.Round(Bounds.Width * scale);
                int h = (int)Math.Round(Bounds.Height * scale);

                if (w > 0 && h > 0 && (w != _lastWidth || h != _lastHeight))
                {
                    _lastWidth = w;
                    _lastHeight = h;
                    Win32NativeHelper.MoveWindow(_childHwnd, 0, 0, w, h, true);
                    _controller.Bounds = new System.Drawing.Rectangle(0, 0, w, h);
                }
            }
            catch (Exception ex) { AppServices.Logger?.Debug($"UpdateBounds threw: {ex.Message}"); }
        }
    }

    public class WebView2InitializedEventArgs(Exception? error) : EventArgs
    {
        public Exception? Error { get; } = error;
        public bool IsSuccess => Error == null;
    }
}
