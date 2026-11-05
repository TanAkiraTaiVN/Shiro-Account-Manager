#nullable enable
using System;
using System.IO;
using System.Threading;
using Shiro.Services;

namespace Shiro.Classes
{
    internal partial class RobloxWatcher
    {
        private static FileSystemWatcher? _logDirWatcher;
        private static long _lastFsNotifyTick;

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
