#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Shiro.Services;

namespace Shiro
{
    internal static partial class Program
    {
        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int SetCurrentProcessExplicitAppUserModelID(string AppID);

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        private static IAppLogger? _logger;
        private static readonly Lock _loggerLock = new();

        public static IAppLogger Logger
        {
            get
            {
                lock (_loggerLock)
                {
                    return _logger ??= CreateLogger();
                }
            }
        }

        private static IAppLogger CreateLogger()
        {
            try
            {
                StoragePaths.EnsureInitialized();
                Environment.CurrentDirectory = StoragePaths.BaseDirectory;
                return new AppLogger(StoragePaths.LogDirectory, "Account Manager");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Fallback logger init due to: {ex.Message}");
                return new AppLogger(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shiro", "Logs"), "Account Manager");
            }
        }
        public static bool Closed = false; // RobloxProcess.cs would cause the program to chill in the background as long roblox was also running

#if !DEBUG
        private static readonly Mutex mutex = new Mutex(false, "{93b3858f-3dac-4dc0-99cb-0476efc5adce}");
#endif

        [STAThread]
        static void Main(params string[] Arguments)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SetCurrentProcessExplicitAppUserModelID("Shiro.RobloxAccountManager");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SetCurrentProcessExplicitAppUserModelID failed: {ex.Message}");
            }

            // Modernization: Encryption uses pure .NET System.Security.Cryptography (AES-256-GCM + PBKDF2-SHA256). Zero native DLL drop on startup.

#if !DEBUG
            bool hasHandle = false;
            try
            {
                hasHandle = mutex.WaitOne(TimeSpan.FromSeconds(2), false);
            }
            catch (AbandonedMutexException)
            {
                hasHandle = true;
            }

            if (hasHandle)
            {
#endif
                try
                {
                    try
                    {
                        StoragePaths.EnsureInitialized();
                        Environment.CurrentDirectory = StoragePaths.BaseDirectory;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                int staleArtifacts = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(7));
                                if (staleArtifacts > 0)
                                    Logger?.Info($"Removed {staleArtifacts} stale runtime artifact(s) from the previous session.");
                            }
                            catch (Exception ex)
                            {
                                Logger?.Warn($"CleanupStaleRuntimeArtifacts error: {ex.Message}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error($"Startup storage initialization error: {ex.Message}");
                    }

                    try
                    {
                        string baseFull = Path.GetFullPath(StoragePaths.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string tempFull = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (baseFull.StartsWith(tempFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            if (OperatingSystem.IsWindows())
                                MessageBox(IntPtr.Zero, "Shiro must be extracted in order to function correctly!", "Shiro", 0x10);
                            Environment.Exit(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.Debug($"Temp path extraction check failed: {ex.Message}");
                    }

                    // Note: Elevation state is checked dynamically per launch by RobloxDirectLauncher.DeElevation.

                    ConfigureUnhandledExceptionRouting();

                    AccountManager.EnsureBackendInitialized();

                    Logger?.Info("Launching Shiro Account Manager with Pure Avalonia UI 11 engine...");
                    try
                    {
                        BuildAvaloniaApp().StartWithClassicDesktopLifetime(Arguments);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error($"StartWithClassicDesktopLifetime crashed: {ex}");
                        throw;
                    }
                }
#if DEBUG
                finally { }
#else
                finally
                {
                    try { mutex.ReleaseMutex(); }
                    catch (Exception ex) { Logger?.Debug($"ReleaseMutex failed: {ex.Message}"); }
                    finally { SafeExecuteHelper.Try(() => mutex.Dispose(), "Program.DisposeMutex"); }
                }
            }
            else
            {
                if (OperatingSystem.IsWindows())
                    MessageBox(IntPtr.Zero, "Shiro is already running!", "Shiro", 0x10);
            }
#endif
        }

        internal static Win32PlatformOptions CreateWin32PlatformOptions() => new()
        {
            RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
            CompositionMode = new[]
            {
                Win32CompositionMode.RedirectionSurface,
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.DirectComposition
            }
        };

        public static Avalonia.AppBuilder BuildAvaloniaApp()
        {
            var builder = Avalonia.AppBuilder.Configure<Shiro.AvaloniaUI.App>()
                .UsePlatformDetect()
                .With(CreateWin32PlatformOptions())
                .WithInterFont();
#if DEBUG
            builder = builder.LogToTrace();
#endif
            return builder;
        }

        private static void ConfigureUnhandledExceptionRouting()
        {
            TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
            {
                LogUnhandled("Task", eventArgs.Exception);
                eventArgs.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                    LogUnhandled("AppDomain", exception);
            };
        }

        private static void LogUnhandled(string source, Exception exception)
        {
            if (exception == null)
                return;

            try
            {
                if (exception is AggregateException aggregate)
                    exception = aggregate.Flatten().InnerException ?? exception;

                // Do not serialize exception objects or request bodies here;
                // browser/API exceptions can include sensitive cookie or token
                // values. The type, redacted message and stack trace are enough to find
                // the failing boundary in the normal application log.
                string message = LogRedactor.Redact((exception.Message ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " "));

                // Filter out benign transient WebView2 / DevTools browser lifecycle events (navigation redirects, detached targets, tab closing)
                if (IsBenignTransientException(exception, message))
                {
                    Logger.Debug($"Transient {source} task exception ignored: {exception.GetType().Name}: {message}");
                    return;
                }

                Logger.Error($"Unhandled {source} exception: {exception.GetType().Name}: {message}");
                Logger.Error($"Unhandled {source} stack trace:\n{LogRedactor.Redact(exception.StackTrace)}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Logging unhandled exception failed: {ex.Message}");
            }
        }

        private static bool IsBenignTransientException(Exception exception, string message)
        {
            if (exception is System.Threading.Tasks.TaskCanceledException || exception is OperationCanceledException)
                return true;

            if (string.IsNullOrEmpty(message))
                return false;

            return message.IndexOf("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Response body is unavailable for redirect responses", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Target closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Session closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Target.detachedFromTarget", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
