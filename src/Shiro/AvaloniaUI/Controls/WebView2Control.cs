#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Controls
{
    public partial class WebView2Control : NativeControlHost
    {
        private static readonly System.Threading.Lock EnvironmentLock = new();
        private static Task<CoreWebView2Environment>? SharedEnvironmentTask;

        private IntPtr _childHwnd;
        private CoreWebView2Controller? _controller;
        private CoreWebView2Environment? _environment;
        private bool _isInitialized;
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;
        public CoreWebView2Controller? Controller => _controller;

        public event EventHandler<WebView2InitializedEventArgs>? CoreWebView2InitializationCompleted;

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(IntPtr hwnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (!OperatingSystem.IsWindows())
                return base.CreateNativeControlCore(parent);

            double scale = VisualRoot?.RenderScaling ?? 1.0;
            int width = Math.Max(100, (int)Math.Round(Bounds.Width * scale));
            int height = Math.Max(100, (int)Math.Round(Bounds.Height * scale));

            _childHwnd = CreateWindowEx(
                0,
                "static",
                "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, width, height,
                parent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            _ = InitializeWebView2Async();

            return new PlatformHandle(_childHwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _controller?.Close();
                    _controller = null;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"WebView2 controller close threw: {ex.Message}");
                }

                if (_childHwnd != IntPtr.Zero)
                {
                    SafeExecuteHelper.Try(() => DestroyWindow(_childHwnd), "WebView2Control.DestroyWindow");
                    _childHwnd = IntPtr.Zero;
                }
            }

            base.DestroyNativeControlCore(control);
        }

        // Removes isolated per-account profiles (EBWebView\Shiro_<guid>) left behind by a crash.
        // Runs before the environment exists, so no live browser can be using them.
        private static void CleanupOrphanedProfiles(string dataFolder)
        {
            try
            {
                string profilesRoot = Path.Combine(dataFolder, "EBWebView");
                if (!Directory.Exists(profilesRoot)) return;

                DateTime cutoffUtc = DateTime.UtcNow.AddHours(-1);
                foreach (string dir in Directory.EnumerateDirectories(profilesRoot, "Shiro_*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) < cutoffUtc)
                            Directory.Delete(dir, true);
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[WebView2Control] Could not remove orphaned profile: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[WebView2Control] Orphaned profile cleanup failed: {ex.Message}");
            }
        }

        private static Task<CoreWebView2Environment> GetOrCreateSharedEnvironmentAsync()
        {
            lock (EnvironmentLock)
            {
                if (SharedEnvironmentTask != null && !SharedEnvironmentTask.IsFaulted && !SharedEnvironmentTask.IsCanceled)
                    return SharedEnvironmentTask;

                string dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Shiro", "EmbeddedBrowser");
                Directory.CreateDirectory(dataFolder);
                CleanupOrphanedProfiles(dataFolder);

                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--disable-features=DeviceBoundSessionCredentials,Translate,OptimizationHints,MediaRouter"
                };

                SharedEnvironmentTask = CoreWebView2Environment.CreateAsync(null, dataFolder, options);
                return SharedEnvironmentTask;
            }
        }

        private async Task<CoreWebView2Controller> CreateIsolatedControllerCoreAsync()
        {
            _environment ??= await GetOrCreateSharedEnvironmentAsync();
            var controllerOptions = _environment.CreateCoreWebView2ControllerOptions();
            controllerOptions.ProfileName = $"Shiro_{Guid.NewGuid():N}";
            controllerOptions.IsInPrivateModeEnabled = false;

            var controller = await _environment.CreateCoreWebView2ControllerAsync(_childHwnd, controllerOptions);
            try { controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 13, 17); }
            catch (Exception ex) { Program.Logger?.Debug($"Set DefaultBackgroundColor threw: {ex.Message}"); }
            return controller;
        }

        public async Task<CoreWebView2?> ResetToFreshProfileAsync()
        {
            if (!OperatingSystem.IsWindows() || _childHwnd == IntPtr.Zero)
                return CoreWebView2;

            var oldController = _controller;
            _controller = null;

            if (oldController != null)
            {
                try
                {
                    var oldCore = oldController.CoreWebView2;
                    if (oldCore != null)
                    {
                        SafeExecuteHelper.Try(() => oldCore.Stop(), "WebView2Control.StopOldCore");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Stopping old CoreWebView2 threw: {ex.Message}");
                }

                SafeExecuteHelper.Try(() => oldController.Close(), "WebView2Control.CloseOldController");
            }

            _controller = await CreateIsolatedControllerCoreAsync();
            _lastWidth = -1;
            _lastHeight = -1;
            UpdateBounds();
            _controller.IsVisible = true;
            _isInitialized = true;
            return _controller.CoreWebView2;
        }

        private async Task InitializeWebView2Async()
        {
            if (_isInitialized || _childHwnd == IntPtr.Zero)
                return;

            try
            {
                _controller = await CreateIsolatedControllerCoreAsync();

                UpdateBounds();
                _controller.IsVisible = true;
                _isInitialized = true;

                CoreWebView2InitializationCompleted?.Invoke(this, new WebView2InitializedEventArgs(null));
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[WebView2Control] Failed to initialize: {ex}");
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
                    MoveWindow(_childHwnd, 0, 0, w, h, true);
                    _controller.Bounds = new System.Drawing.Rectangle(0, 0, w, h);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"UpdateBounds threw: {ex.Message}");
            }
        }
    }

    public class WebView2InitializedEventArgs : EventArgs
    {
        public Exception? Error { get; }
        public bool IsSuccess => Error == null;

        public WebView2InitializedEventArgs(Exception? error)
        {
            Error = error;
        }
    }
}
