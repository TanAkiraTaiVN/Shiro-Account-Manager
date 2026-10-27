using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    internal partial class RobloxWatcher
    {
        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex TrackerRegex();
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static HashSet<int> Seen = [];
        public static List<RobloxProcess> Instances = [];
        private static readonly Dictionary<int, (string TrackerId, bool IsGameClient, RECT LastSavedRect)> _pidMetadataCache = new();
        internal static readonly System.Threading.Lock InstanceRegistryLock = new();
        public static bool VerifyDataModel = true;
        public static bool IgnoreExistingProcesses = true;
        public static bool CloseIfMemoryLow = false;
        public static bool CloseIfWindowTitle = false;
        public static bool RememberWindowPositions = false;
        public static int MemoryLowValue = 200;
        public static string ExpectedWindowTitle = "Roblox";

        private static ModernPeriodicTask _watcherLoop;
        private static ModernPeriodicTask _logReadLoop;

        public static event EventHandler<EventArgs> LogFileRead;

        public static void Start()
        {
            if (_watcherLoop == null)
            {
                _watcherLoop = new ModernPeriodicTask(
                    TimeSpan.FromSeconds(2),
                    async token => await Task.Run(CheckProcesses, token).ConfigureAwait(false),
                    enabled: true);
            }

            if (_logReadLoop == null)
            {
                _logReadLoop = new ModernPeriodicTask(
                    TimeSpan.FromSeconds(1),
                    _ => { LogFileRead?.Invoke(null, EventArgs.Empty); return Task.CompletedTask; },
                    enabled: true);
            }
        }

        public static void Stop()
        {
            _watcherLoop?.Stop();
            _watcherLoop?.Dispose();
            _watcherLoop = null;

            _logReadLoop?.Stop();
            _logReadLoop?.Dispose();
            _logReadLoop = null;
        }

        public static void CheckProcesses()
        {
            if (!RobloxProcessService.IsAnyRobloxRunning())
            {
                lock (InstanceRegistryLock)
                {
                    if (Seen.Count > 0) Seen.Clear();
                    if (Instances.Count > 0) Instances.Clear();
                    if (_pidMetadataCache.Count > 0) _pidMetadataCache.Clear();
                }
                return;
            }

            if (!CloseIfMemoryLow && !CloseIfWindowTitle && !RememberWindowPositions)
            {
                var runningPids = RobloxProcessService.GetRunningRobloxPids();
                bool allKnown = true;
                lock (InstanceRegistryLock)
                {
                    for (int i = 0; i < runningPids.Count; i++)
                    {
                        if (!_pidMetadataCache.ContainsKey(runningPids[i]))
                        {
                            allKnown = false;
                            break;
                        }
                    }

                    if (allKnown)
                    {
                        if (_pidMetadataCache.Count > runningPids.Count)
                        {
                            List<int> exitedPids = null;
                            foreach (int cachedPid in _pidMetadataCache.Keys)
                            {
                                if (!runningPids.Contains(cachedPid))
                                {
                                    exitedPids ??= new List<int>(4);
                                    exitedPids.Add(cachedPid);
                                }
                            }

                            if (exitedPids != null)
                            {
                                foreach (int exitedPid in exitedPids)
                                {
                                    _pidMetadataCache.Remove(exitedPid);
                                    Seen.Remove(exitedPid);
                                }
                            }
                        }
                        return;
                    }
                }
            }

            Process[] processes;
            try { processes = Process.GetProcessesByName("RobloxPlayerBeta"); }
            catch { return; }

            if (processes.Length == 0)
            {
                lock (InstanceRegistryLock)
                {
                    if (Seen.Count > 0) Seen.Clear();
                    if (Instances.Count > 0) Instances.Clear();
                    if (_pidMetadataCache.Count > 0) _pidMetadataCache.Clear();
                }
                return;
            }

            IntPtr Focused = GetForegroundWindow();
            Dictionary<string, Account> accountByTracker = null;
            if (RememberWindowPositions)
            {
                accountByTracker = new Dictionary<string, Account>(StringComparer.Ordinal);
                foreach (Account acc in AccountManager.GetAccountsSnapshot())
                {
                    if (acc != null && !string.IsNullOrEmpty(acc.BrowserTrackerID))
                        accountByTracker[acc.BrowserTrackerID] = acc;
                }
            }

            var activePids = new HashSet<int>(processes.Length);

            foreach (var process in processes)
            {
                bool retained = false;
                try
                {
                    int pid = process.Id;
                    activePids.Add(pid);
                    bool isFocusedWindow = Focused != IntPtr.Zero && process.MainWindowHandle == Focused;

                    void Kill(string Reason) { Program.Logger.Info($"Attempting to kill process {pid}, reason: {Reason}"); try { process.Kill(); } catch { } }

                    string trackerId = string.Empty;
                    bool isGameClient = false;
                    RECT lastRect = default;
                    bool hasCachedMeta;

                    lock (InstanceRegistryLock)
                    {
                        hasCachedMeta = _pidMetadataCache.TryGetValue(pid, out var cachedMeta);
                        if (hasCachedMeta)
                        {
                            trackerId = cachedMeta.TrackerId;
                            isGameClient = cachedMeta.IsGameClient;
                            lastRect = cachedMeta.LastSavedRect;
                        }
                    }

                    if (!hasCachedMeta)
                    {
                        string commandLine = process.GetCommandLine();
                        if (!string.IsNullOrEmpty(commandLine) &&
                            !commandLine.StartsWith("\\??\\") &&
                            (commandLine.Contains("-t ") || commandLine.Contains("-j ")))
                        {
                            isGameClient = true;
                            var trackerMatch = TrackerRegex().Match(commandLine);
                            trackerId = trackerMatch.Success ? trackerMatch.Groups[1].Value : string.Empty;
                        }

                        lock (InstanceRegistryLock)
                        {
                            _pidMetadataCache[pid] = (trackerId, isGameClient, default);
                        }
                    }

                    if (!isGameClient)
                        continue;

                    bool isOlderThan30s = false;
                    if (!isFocusedWindow || (RememberWindowPositions && !string.IsNullOrEmpty(trackerId)))
                    {
                        try { isOlderThan30s = (DateTime.Now - process.StartTime).TotalSeconds > 30; }
                        catch { }
                    }

                    if (!isFocusedWindow && isOlderThan30s)
                    {
                        try
                        {
                            if (CloseIfMemoryLow && process.WorkingSet64 / 1024 / 1024 < MemoryLowValue)
                                Kill($"Low Memory ({process.WorkingSet64 / 1024 / 1024} < {MemoryLowValue})");

                            if (CloseIfWindowTitle && process.MainWindowTitle != ExpectedWindowTitle)
                                Kill($"Window Title isn't {ExpectedWindowTitle}, got {process.MainWindowTitle}");
                        }
                        catch (Exception x) { Program.Logger.Error($"Error with checking for Memory & Window Title: {x.Message}\n{x.StackTrace}"); }
                    }

                    if (RememberWindowPositions && !string.IsNullOrEmpty(trackerId) && isOlderThan30s)
                    {
                        if (accountByTracker != null && accountByTracker.TryGetValue(trackerId, out Account account))
                        {
                            try
                            {
                                IntPtr hWnd = process.MainWindowHandle;
                                if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect))
                                {
                                    int width = rect.Right - rect.Left;
                                    int height = rect.Bottom - rect.Top;
                                    // Ignore minimized (-32000) or collapsed windows
                                    if (width >= 200 && height >= 200 && rect.Left > -10000 && rect.Top > -10000 &&
                                        (rect.Left != lastRect.Left || rect.Top != lastRect.Top || rect.Right != lastRect.Right || rect.Bottom != lastRect.Bottom))
                                    {
                                        account.SetWindowBounds(rect.Left, rect.Top, width, height);

                                        lock (InstanceRegistryLock)
                                        {
                                            _pidMetadataCache[pid] = (trackerId, isGameClient, rect);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    lock (InstanceRegistryLock)
                        if (Seen.Contains(pid)) continue;

                    try
                    {
                        if (process.HasExited) continue;

                        lock (InstanceRegistryLock)
                        {
                            if (Seen.Contains(pid))
                                continue;

                            Instances.Add(new RobloxProcess(process));
                            Seen.Add(pid);
                            retained = true;
                        }
                    }
                    catch (Exception x) { Program.Logger.Error($"Access to Process {pid} denied! This may be due to roblox being ran as admin or roblox's second process(This message can be ignored): {x.Message}"); }
                }
                finally
                {
                    if (!retained)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }

            lock (InstanceRegistryLock)
            {
                if (_pidMetadataCache.Count > activePids.Count)
                {
                    List<int> exitedPids = null;
                    foreach (int cachedPid in _pidMetadataCache.Keys)
                    {
                        if (!activePids.Contains(cachedPid))
                        {
                            exitedPids ??= new List<int>(4);
                            exitedPids.Add(cachedPid);
                        }
                    }

                    if (exitedPids != null)
                    {
                        foreach (int exitedPid in exitedPids)
                        {
                            _pidMetadataCache.Remove(exitedPid);
                            Seen.Remove(exitedPid);
                        }
                    }
                }
            }
        }
        internal static void RemoveProcessMetadata(int pid)
        {
            lock (InstanceRegistryLock)
            {
                Seen.Remove(pid);
                _pidMetadataCache.Remove(pid);
            }
        }
    }
}
