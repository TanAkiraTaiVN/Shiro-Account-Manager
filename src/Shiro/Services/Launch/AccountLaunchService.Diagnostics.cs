#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
        [GeneratedRegex("\"([^\"]+RobloxPlayerBeta\\.exe)\"", RegexOptions.IgnoreCase)]
        private static partial Regex RobloxExeRegex();

        private static string? _cachedRobloxExecutablePath;
        private static long _cachedRobloxExecutableTick = -60000;
        private const long ExecutableCacheTtlMs = 30000;
        private static readonly AsyncLocal<string[]?> _testVersionRootsOverride = new();
        private static readonly AsyncLocal<string?> _testProtocolCommandOverride = new();
        private static readonly AsyncLocal<string?> _testDefaultIconOverride = new();
        private static readonly AsyncLocal<ExecutableCacheEntry?> _testExecutableCache = new();

        private sealed record ExecutableCacheEntry(string Path, long Tick);

        internal static string[]? TestVersionRootsOverride
        {
            get => _testVersionRootsOverride.Value;
            set => _testVersionRootsOverride.Value = value;
        }

        internal static string? TestProtocolCommandOverride
        {
            get => _testProtocolCommandOverride.Value;
            set => _testProtocolCommandOverride.Value = value;
        }

        internal static string? TestDefaultIconOverride
        {
            get => _testDefaultIconOverride.Value;
            set => _testDefaultIconOverride.Value = value;
        }

        public static void InvalidateExecutableCache()
        {
            _testExecutableCache.Value = null;
            _cachedRobloxExecutablePath = null;
            _cachedRobloxExecutableTick = -60000;
        }

        public static string? FindRobloxPlayerExecutable()
        {
            long now = Environment.TickCount64;
            bool usingTestOverrides = TestVersionRootsOverride != null
                || TestProtocolCommandOverride != null
                || TestDefaultIconOverride != null;

            if (usingTestOverrides)
            {
                ExecutableCacheEntry? testCached = _testExecutableCache.Value;
                if (testCached != null && (now - testCached.Tick) < ExecutableCacheTtlMs && File.Exists(testCached.Path))
                    return testCached.Path;

                string? testResolved = FindRobloxPlayerExecutableCore();
                if (!string.IsNullOrEmpty(testResolved))
                    _testExecutableCache.Value = new ExecutableCacheEntry(testResolved, now);
                return testResolved;
            }

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

        internal static string? ReadProtocolOpenCommand()
        {
            try
            {
                string? command = TestProtocolCommandOverride;
                if (command == null)
                {
                    using RegistryKey? commandKey = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
                    command = commandKey?.GetValue("") as string;
                }
                return command;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[AccountLaunchService] Registry protocol read failed: {ex.Message}");
                return null;
            }
        }

        internal static string? FindRobloxPlayerExecutableCore()
        {
            string[] roots = TestVersionRootsOverride ?? ClientSettingsPatcher.GetDefaultRobloxVersionRoots();

            string? command = ReadProtocolOpenCommand();
            if (!string.IsNullOrWhiteSpace(command))
            {
                var match = RobloxExeRegex().Match(command);
                if (match.Success && File.Exists(match.Groups[1].Value))
                    return match.Groups[1].Value;
            }

            if (!string.IsNullOrWhiteSpace(AppServices.ServerBrowserState.CurrentClientVersion))
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    string target = Path.Combine(root, AppServices.ServerBrowserState.CurrentClientVersion, "RobloxPlayerBeta.exe");
                    if (File.Exists(target))
                        return target;
                }
            }

            var candidates = new List<string>();

            try
            {
                string? registered = TestDefaultIconOverride;
                if (registered == null)
                {
                    using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon");
                    registered = iconKey?.GetValue("") as string;
                }
                string path = ClientSettingsPatcher.NormalizeRegistryExecutablePath(registered);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    candidates.Add(path);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FindRobloxPlayerExecutable] Registry DefaultIcon read failed: {ex.Message}");
            }

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
                    AppServices.Logger?.Debug($"[FindRobloxPlayerExecutable] Version directory scan failed for '{root}': {ex.Message}");
                }
            }

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
                    AppServices.Logger?.Debug($"FileVersionInfo failed for '{path}': {ex.Message}");
                    ver = new Version(0, 0, 0, 0);
                }

                DateTime writeUtc;
                try { writeUtc = File.GetLastWriteTimeUtc(path); }
                catch (Exception ex) { AppServices.Logger?.Debug($"GetLastWriteTimeUtc failed for '{path}': {ex.Message}"); writeUtc = DateTime.MinValue; }

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

        public static bool HasRobloxPlayerProtocolHandler()
        {
            string? command = ReadProtocolOpenCommand();
            if (string.IsNullOrWhiteSpace(command))
                return false;

            string normalized = command.ToLowerInvariant();
            if (normalized.Contains("chrome.exe") ||
                normalized.Contains("msedge.exe") ||
                normalized.Contains("firefox.exe"))
                return false;

            return normalized.Contains("roblox");
        }

        public static async Task WaitForInstallerAsync(CancellationToken cancellationToken)
        {
            if (!RobloxProcessService.IsRobloxInstallerRunning())
                return;

            long startTick = Stopwatch.GetTimestamp();
            TimeSpan maxWait = TimeSpan.FromMinutes(3);
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
                    AppServices.Logger?.Debug($"GetProcessesByName installers failed: {ex.Message}");
                    break;
                }

                if (installers.Length == 0)
                    break;

                wasUpdating = true;
                AppServices.Logger.Info("Roblox is currently updating (RobloxPlayerInstaller is running). Waiting for update to finish before proceeding...");

                try
                {
                    await WaitForExitAsync(installers[0], 2500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"WaitForExitAsync installer failed: {ex.Message}");
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    foreach (Process proc in installers)
                    {
                        SafeExecuteHelper.Try(() => proc.Dispose(), "AccountLaunchService.WaitForInstallerAsync.Dispose");
                    }
                }
            }

            if (wasUpdating)
            {
                InvalidateExecutableCache();
                AppServices.Logger?.Info("Roblox update finished. Refreshing client version...");
                try { await AppServices.ServerBrowserState.LoadClientVersionAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { AppServices.Logger?.Warn($"Failed to refresh client version after update: {ex.Message}"); }
            }
        }

        public static async Task<int> ResolveNewClientPidAsync(
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

        public static async Task<bool> WaitForExitAsync(
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
    }
}
