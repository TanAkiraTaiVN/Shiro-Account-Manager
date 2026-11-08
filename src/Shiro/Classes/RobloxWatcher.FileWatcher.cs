#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Shiro.Services;

namespace Shiro.Classes
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
