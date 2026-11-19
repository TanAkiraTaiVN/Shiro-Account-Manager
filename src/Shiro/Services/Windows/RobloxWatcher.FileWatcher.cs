#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Shiro.Services;

namespace Shiro.Services
{
    internal partial class RobloxWatcher
    {
        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex TrackerRegex();

        private static FileSystemWatcher? _logDirWatcher;
        private static long _lastFsNotifyTick;

        internal static bool TryClassifyClientCommandLine(string? commandLine, out string trackerId)
        {
            trackerId = string.Empty;
            if (string.IsNullOrWhiteSpace(commandLine) || commandLine.StartsWith("\\??\\", StringComparison.Ordinal))
                return false;

            if (!commandLine.Contains("-t ", StringComparison.Ordinal) &&
                !commandLine.Contains("-j ", StringComparison.Ordinal) &&
                !commandLine.Contains("--app", StringComparison.Ordinal))
            {
                return false;
            }

            Match trackerMatch = TrackerRegex().Match(commandLine);
            if (trackerMatch.Success)
                trackerId = trackerMatch.Groups[1].Value;

            return true;
        }

        internal static bool TryValidateCachedProcessUnderLock(
            int pid,
            long currentCreationTicks,
            out (string TrackerId, bool IsGameClient, RECT LastSavedRect, long CreationTicks) cachedMeta)
        {
            if (_pidMetadataCache.TryGetValue(pid, out cachedMeta) &&
                RobloxProcessIdentityHelper.IsCreationMatch(cachedMeta.CreationTicks, currentCreationTicks))
            {
                return true;
            }

            if (_pidMetadataCache.ContainsKey(pid) ||
                (_seenCreationTicks.TryGetValue(pid, out long seenTicks) &&
                 !RobloxProcessIdentityHelper.IsCreationMatch(seenTicks, currentCreationTicks)))
            {
                _pidMetadataCache.Remove(pid);
                Seen.Remove(pid);
                _seenCreationTicks.Remove(pid);
            }

            cachedMeta = default;
            return false;
        }

        internal static bool IsSeenWithSameCreationUnderLock(int pid, long currentCreationTicks)
        {
            if (!Seen.Contains(pid))
                return false;

            if (_seenCreationTicks.TryGetValue(pid, out long seenTicks) &&
                RobloxProcessIdentityHelper.IsCreationMatch(seenTicks, currentCreationTicks))
            {
                return true;
            }

            Seen.Remove(pid);
            _seenCreationTicks.Remove(pid);
            return false;
        }

        internal static void SeedProcessCacheForTest(int pid, string trackerId, bool isGameClient, long creationTicks)
        {
            lock (InstanceRegistryLock)
            {
                _pidMetadataCache[pid] = (trackerId, isGameClient, default, creationTicks);
                Seen.Add(pid);
                _seenCreationTicks[pid] = creationTicks;
            }
        }

        internal static bool TryGetValidatedMetadataForTest(int pid, long currentCreationTicks, out string trackerId)
        {
            lock (InstanceRegistryLock)
            {
                if (TryValidateCachedProcessUnderLock(pid, currentCreationTicks, out var meta))
                {
                    trackerId = meta.TrackerId;
                    return true;
                }
            }

            trackerId = string.Empty;
            return false;
        }

        internal static bool TryValidateWindowBoundsChange(RECT rect, RECT lastRect, out int width, out int height)
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;

            if (width < 200 || height < 200 || rect.Left <= -10000 || rect.Top <= -10000)
                return false;

            return rect.Left != lastRect.Left ||
                   rect.Top != lastRect.Top ||
                   rect.Right != lastRect.Right ||
                   rect.Bottom != lastRect.Bottom;
        }

        private static void StartLogDirectoryWatcher()
        {
            if (_logDirWatcher != null)
                return;

            try
            {
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "logs");

                if (!Directory.Exists(logsDir))
                    return;

                var watcher = new FileSystemWatcher(logsDir, "*_Player_*_last.log")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };

                FileSystemEventHandler onChanged = (_, _) =>
                {
                    long now = Environment.TickCount64;
                    long prev = Volatile.Read(ref _lastFsNotifyTick);
                    if (now - prev < 150)
                        return;

                    if (Interlocked.CompareExchange(ref _lastFsNotifyTick, now, prev) == prev)
                    {
                        try
                        {
                            LogFileRead?.Invoke(null, EventArgs.Empty);
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger.Debug($"[RobloxWatcher] FileSystemWatcher LogFileRead error: {ex.Message}");
                        }
                    }
                };

                watcher.Changed += onChanged;
                watcher.Created += onChanged;
                watcher.EnableRaisingEvents = true;
                _logDirWatcher = watcher;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxWatcher] StartLogDirectoryWatcher failed: {ex.Message}");
            }
        }

        private static void StopLogDirectoryWatcher()
        {
            var watcher = Interlocked.Exchange(ref _logDirWatcher, null);
            if (watcher == null)
                return;

            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxWatcher] StopLogDirectoryWatcher failed: {ex.Message}");
            }
        }
    }
}
