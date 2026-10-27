using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        [GeneratedRegex("\"([^\"]+RobloxPlayerBeta\\.exe)\"", RegexOptions.IgnoreCase)]
        private static partial Regex RobloxExeRegex();

        private static string? _cachedRobloxExecutablePath;
        private static long _cachedRobloxExecutableTick = -60000;
        private const long ExecutableCacheTtlMs = 30000;

        public static void InvalidateExecutableCache()
        {
            _cachedRobloxExecutablePath = null;
            _cachedRobloxExecutableTick = -60000;
        }

        public static string? FindRobloxPlayerExecutablePublic() => FindRobloxPlayerExecutable();
        public static bool HasRobloxPlayerProtocolHandlerPublic() => HasRobloxPlayerProtocolHandler();

        /// <summary>
        /// Finds the installed Roblox player without relying on the
        /// roblox-player protocol association. On machines where that
        /// association was taken over by Chrome, Process.Start("roblox-player:")
        /// opens a browser instead of the native client. Launching the player
        /// executable directly keeps the same ticket and join URL while
        /// avoiding that Windows association.
        /// </summary>
        private static string? FindRobloxPlayerExecutable()
        {
            long now = Environment.TickCount64;
            string? cached = _cachedRobloxExecutablePath;
            if (!string.IsNullOrEmpty(cached) && (now - _cachedRobloxExecutableTick) < ExecutableCacheTtlMs && File.Exists(cached))
            {
                return cached;
            }

            string? resolved = FindRobloxPlayerExecutableCore();
            if (!string.IsNullOrEmpty(resolved))
            {
                _cachedRobloxExecutablePath = resolved;
                _cachedRobloxExecutableTick = now;
            }
            return resolved;
        }

        private static string? FindRobloxPlayerExecutableCore()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] roots =
            {
                Path.Combine(localAppData, "Roblox", "Versions"),
                string.IsNullOrWhiteSpace(programFilesX86) ? string.Empty : Path.Combine(programFilesX86, "Roblox", "Versions"),
                string.IsNullOrWhiteSpace(programFiles) ? string.Empty : Path.Combine(programFiles, "Roblox", "Versions")
            };

            // 1. Check registry command first - if Registry points to an existing executable,
            // that is the authoritative active version registered with Windows, preventing mismatch loops.
            try
            {
                using RegistryKey? commandKey = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
                string? command = commandKey?.GetValue("") as string;
                if (!string.IsNullOrWhiteSpace(command))
                {
                    var match = RobloxExeRegex().Match(command);
                    if (match.Success && File.Exists(match.Groups[1].Value))
                        return match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FindRobloxPlayerExecutable] Registry protocol read failed: {ex.Message}");
            }

            // 2. If CurrentVersion from CDN exists on disk, use it directly
            if (!string.IsNullOrWhiteSpace(AccountManager.CurrentVersion))
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    string target = Path.Combine(root, AccountManager.CurrentVersion, "RobloxPlayerBeta.exe");
                    if (File.Exists(target))
                        return target;
                }
            }

            var candidates = new List<string>();

            try
            {
                using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon");
                string? registered = iconKey?.GetValue("") as string;
                if (!string.IsNullOrWhiteSpace(registered))
                {
                    string path = registered.Trim().Trim('"');
                    int comma = path.IndexOf(',');
                    if (comma > 0) path = path.Substring(0, comma).Trim().Trim('"');
                    if (File.Exists(path)) candidates.Add(path);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[FindRobloxPlayerExecutable] Registry DefaultIcon read failed: {ex.Message}");
            }

            // 3. Scan version folders
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                try
                {
                    foreach (string folder in Directory.EnumerateDirectories(root, "version-*"))
                    {
                        string exe = Path.Combine(folder, "RobloxPlayerBeta.exe");
                        if (File.Exists(exe))
                            candidates.Add(exe);
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[FindRobloxPlayerExecutable] Version directory scan failed for '{root}': {ex.Message}");
                }
            }

            // Select the highest FileVersion (newest version first), breaking ties by LastWriteTimeUtc
            string? bestPath = null;
            Version bestVersion = new Version(0, 0, 0, 0);
            DateTime bestWriteUtc = DateTime.MinValue;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in candidates)
            {
                if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path) || !File.Exists(path))
                    continue;

                Version ver;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    ver = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"FileVersionInfo failed for '{path}': {ex.Message}");
                    ver = new Version(0, 0, 0, 0);
                }

                DateTime writeUtc;
                try { writeUtc = File.GetLastWriteTimeUtc(path); }
                catch (Exception) { writeUtc = DateTime.MinValue; }

                int cmp = ver.CompareTo(bestVersion);
                if (bestPath == null || cmp > 0 || (cmp == 0 && writeUtc > bestWriteUtc))
                {
                    bestPath = path;
                    bestVersion = ver;
                    bestWriteUtc = writeUtc;
                }
            }

            return bestPath;
        }

        private static bool HasRobloxPlayerProtocolHandler()
        {
            try
            {
                using RegistryKey? commandKey = Registry.ClassesRoot.OpenSubKey(
                    @"roblox-player\shell\open\command");
                string? command = commandKey?.GetValue("") as string;

                if (string.IsNullOrWhiteSpace(command))
                    return false;

                // A previous version could leave Chrome/Edge as the protocol
                // handler.  Do not send the URI there, but accept Roblox's
                // launcher name as well as RobloxPlayerBeta.exe because the
                // registered command changed between client generations.
                string normalized = command.ToLowerInvariant();
                if (normalized.Contains("chrome.exe") ||
                    normalized.Contains("msedge.exe") ||
                    normalized.Contains("firefox.exe"))
                    return false;

                return normalized.Contains("roblox");
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ResolveRobloxProtocolExecutable failed: {ex.Message}");
                return false;
            }
        }

        private static async Task WaitForInstallerAsync(CancellationToken cancellationToken)
        {
            if (!RobloxProcessService.IsRobloxInstallerRunning())
                return;

            long startTick = Stopwatch.GetTimestamp();
            TimeSpan maxWait = TimeSpan.FromMinutes(3); // up to 3 minutes for downloading and installing update
            bool wasUpdating = false;

            while (!cancellationToken.IsCancellationRequested && Stopwatch.GetElapsedTime(startTick) < maxWait)
            {
                if (!RobloxProcessService.IsRobloxInstallerRunning())
                    break;

                Process[] installers;
                try
                {
                    installers = Process.GetProcessesByName("RobloxPlayerInstaller")
                        .Concat(Process.GetProcessesByName("RobloxPlayerLauncher"))
                        .ToArray();
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"GetProcessesByName installers failed: {ex.Message}");
                    break;
                }

                if (installers.Length == 0)
                    break;

                wasUpdating = true;
                Program.Logger.Info("Roblox is currently updating (RobloxPlayerInstaller is running). Waiting for update to finish before proceeding...");

                try
                {
                    await WaitForExitAsync(installers[0], 2500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"WaitForExitAsync installer failed: {ex.Message}");
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    foreach (Process proc in installers)
                    {
                        try { proc.Dispose(); } catch (Exception) { }
                    }
                }
            }

            if (wasUpdating)
            {
                InvalidateExecutableCache();
                Program.Logger?.Info("Roblox update finished. Refreshing client version...");
                try { await AccountManager.LoadClientVersionAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { Program.Logger?.Warn($"Failed to refresh client version after update: {ex.Message}"); }
            }
        }

        private static async Task<int> ResolveNewClientPidAsync(
            HashSet<int> existingClientPids,
            int launcherPid,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<int> currentPids = RobloxProcessService.GetRunningRobloxPids();
                if (launcherPid > 0 && currentPids.Contains(launcherPid))
                    return launcherPid;

                foreach (int pid in currentPids)
                {
                    if (existingClientPids == null || !existingClientPids.Contains(pid))
                        return pid;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        private static async Task<bool> WaitForExitAsync(
            Process process,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (process == null)
                return true;

            using CancellationTokenSource timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(Math.Max(1, timeoutMilliseconds));

            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        // Post-launch background tasks and window positioning extracted to Account.Launcher.Diagnostics.PostLaunch.cs
    }
}
