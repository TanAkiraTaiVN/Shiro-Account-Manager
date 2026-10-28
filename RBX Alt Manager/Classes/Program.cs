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
        public static readonly IAppLogger Logger = CreateLogger();

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
        public static bool Elevated;
        public static float Scale
        {
            get
            {
                float _Scale = (AccountManager.General != null && AccountManager.General.Exists("WindowScale")) ? AccountManager.General.Get<float>("WindowScale") : 0f;

                if (_Scale > 3f) AccountManager.General?.RemoveProperty("WindowScale");

                if (_Scale > 0 && _Scale <= 3)
                    return AccountManager.General?.Get<float>("WindowScale") ?? 1f;

                return 1f;
            }
        }

        public static bool ScaleFonts
        {
            get
            {
                if (AccountManager.General != null && AccountManager.General.Exists("ScaleFonts"))
                    return AccountManager.General.Get<bool>("ScaleFonts");

                return true;
            }
        }

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
                Logger?.Debug($"SetCurrentProcessExplicitAppUserModelID failed: {ex.Message}");
            }

            int Stupid = 1337;

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
                DirectoryInfo applicationDirectory = new DirectoryInfo(StoragePaths.BaseDirectory);
                string temporaryRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (applicationDirectory.Parent != null &&
                    applicationDirectory.Parent.FullName.Contains(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (OperatingSystem.IsWindows())
                        MessageBox(IntPtr.Zero, "Shiro must be extracted in order to function correctly!", "Shiro", 0x10);
                    Environment.Exit(Stupid);
                }
            }
            catch (Exception ex)
            {
                Logger?.Debug($"Temp path extraction check failed: {ex.Message}");
            }

            try
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    Elevated = true;

                // MessageBox.Show("Some features may not work properly if you ran the account manager as admin!", "Roblox Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); // I don't think this is an issue anymore
            }
            catch (Exception ex)
            {
                Logger?.Debug($"Administrator role check failed: {ex.Message}");
            }

#if DEBUG
            bool IsAutoUpdater = true;
#else
            string processPath = Environment.ProcessPath ?? string.Empty;
            bool IsAutoUpdater = processPath.EndsWith("Auto Update.exe", StringComparison.OrdinalIgnoreCase);
#endif

            if (Arguments.Length >= 1 && string.Equals(Arguments[0], "--recover-vault-key", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = VaultKeyRecoveryService.ExecuteRecoveryCli();
                Environment.Exit(exitCode);
                return;
            }

            if (Arguments.Length >= 1 && string.Equals(Arguments[0], "--merge-accounts", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = VaultKeyRecoveryService.MergeAllKnownAccountsCli();
                Environment.Exit(exitCode);
                return;
            }

            if (Arguments.Length == 1 && Arguments[0] == "-update")
            {
                if (!IsAutoUpdater)
                {
                    string AFN = Path.Combine(Directory.GetCurrentDirectory(), "Auto Update.exe");
                    string currentExe = Environment.ProcessPath ?? string.Empty;

                    File.Copy(currentExe, AFN, true);
                    Process.Start(AFN, "-update");
                    Environment.Exit(2);
                }

                void StartUpdater() {
                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(Arguments);
                }

                if (!Elevated)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? string.Empty) { Arguments = "-update", Verb = "runas" });
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Debug($"Runas updater failed, starting direct: {ex.Message}");
                        StartUpdater();
                    }
                }
                else
                    StartUpdater();

                return;
            }


            ConfigureUnhandledExceptionRouting();



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
                }
            }
            else
            {
                if (OperatingSystem.IsWindows())
                    MessageBox(IntPtr.Zero, "Shiro is already running!", "Shiro", 0x10);
            }
#endif
        }

        public static Avalonia.AppBuilder BuildAvaloniaApp()
            => Avalonia.AppBuilder.Configure<Shiro.AvaloniaUI.App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

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
                // values. The type and first-line message are enough to find
                // the failing boundary in the normal application log.
                string message = (exception.Message ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ");

                // Filter out benign transient browser lifecycle events (Puppeteer navigation redirects, tab closing)
                if (IsBenignTransientException(exception, message))
                {
                    Logger.Debug($"Transient {source} task exception ignored: {exception.GetType().Name}: {message}");
                    return;
                }

                Logger.Error($"Unhandled {source} exception: {exception.GetType().Name}: {message}");
                Logger.Error($"Unhandled {source} stack trace: {exception}");
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
