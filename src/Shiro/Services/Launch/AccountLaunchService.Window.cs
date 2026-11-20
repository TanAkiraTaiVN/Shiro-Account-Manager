#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
        public static void ObserveBackgroundTask(Task task, string operation)
        {
            if (task == null) return;
            _ = ObserveBackgroundTaskAsync(task, operation);
        }

        private static async Task ObserveBackgroundTaskAsync(Task task, string operation)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                AppServices.Logger?.Debug($"Background {operation} cancelled: {ex.Message}");
            }
            catch (Exception exception)
            {
                AppServices.Logger?.Warn($"Background {operation} failed: {exception.Message}");
            }
        }

        internal static Func<Account, int, CancellationToken, Task>? TestAdjustWindowPositionOverride;
        internal static Func<DateTime, Account, CancellationToken, Task>? TestMonitorNativeAuthRateLimitOverride;

        public static async Task AdjustWindowPositionAsync(Account account, int targetPid = 0, CancellationToken cancellationToken = default)
        {
            if (TestAdjustWindowPositionOverride != null)
            {
                await TestAdjustWindowPositionOverride(account, targetPid, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (account == null) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!RobloxWatcher.RememberWindowPositions)
                return;

            if (!(int.TryParse(account.GetField("Window_Position_X"), out int PosX) &&
                  int.TryParse(account.GetField("Window_Position_Y"), out int PosY) &&
                  int.TryParse(account.GetField("Window_Width"), out int Width) &&
                  int.TryParse(account.GetField("Window_Height"), out int Height)))
                return;

            if (Width < 200 || Height < 200 || PosX <= -10000 || PosY <= -10000)
                return;

            if (targetPid > 0)
            {
                long fastStartTick = Stopwatch.GetTimestamp();
                TimeSpan fastTimeout = TimeSpan.FromSeconds(15);
                while (Stopwatch.GetElapsedTime(fastStartTick) < fastTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr hWnd = RobloxWindowService.FindWindowForPid((uint)targetPid);
                    if (hWnd != IntPtr.Zero)
                    {
                        Win32NativeHelper.MoveWindow(hWnd, PosX, PosY, Width, Height, true);
                        AppServices.Logger?.Info($"[AdjustWindowPosition] Fast-repositioned window for PID {targetPid} to ({PosX},{PosY} {Width}x{Height}).");
                        return;
                    }
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }

            bool found = false;
            long fallbackStartTick = Stopwatch.GetTimestamp();
            TimeSpan fallbackTimeout = TimeSpan.FromSeconds(20);
            var inspectedNonMatchingPids = new HashSet<int>();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);

                List<int> runningPids = RobloxProcessService.GetRunningRobloxPids();
                for (int i = runningPids.Count - 1; i >= 0; i--)
                {
                    int pid = runningPids[i];
                    if (inspectedNonMatchingPids.Contains(pid))
                        continue;

                    IntPtr hWnd = RobloxWindowService.FindWindowForPid((uint)pid);
                    if (hWnd == IntPtr.Zero) continue;

                    try
                    {
                        string? commandLine = ProcessExtensions.GetCommandLineByPid(pid);
                        if (string.IsNullOrEmpty(commandLine)) continue;

                        var match = RobloxProcessIdentityHelper.BrowserTrackerArgRegex().Match(commandLine);
                        string trackerId = match.Success ? match.Groups[1].Value : string.Empty;

                        if (trackerId != account.BrowserTrackerID)
                        {
                            inspectedNonMatchingPids.Add(pid);
                            continue;
                        }

                        found = true;
                        Win32NativeHelper.MoveWindow(hWnd, PosX, PosY, Width, Height, true);
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"MoveWindow inspection failed for pid {pid}: {ex.Message}");
                    }
                }

                if (found || Stopwatch.GetElapsedTime(fallbackStartTick) > fallbackTimeout) break;
            }
        }

        public static async Task MonitorNativeAuthRateLimitAsync(
            DateTime launchStartedUtc,
            Account account,
            CancellationToken cancellationToken = default)
        {
            if (TestMonitorNativeAuthRateLimitOverride != null)
            {
                await TestMonitorNativeAuthRateLimitOverride(launchStartedUtc, account, cancellationToken).ConfigureAwait(false);
                return;
            }
            string username = account?.Username ?? "account";
            int targetPid = account?.LastLaunchedProcessId ?? 0;
            string? browserTrackerId = account?.BrowserTrackerID;
            string? targetedLogPath = null;
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "logs");

            long startTick = Stopwatch.GetTimestamp();
            TimeSpan monitorDuration = TimeSpan.FromSeconds(25);
            DateTime minimumTimestampUtc = launchStartedUtc.AddSeconds(-2);

            while (Stopwatch.GetElapsedTime(startTick) < monitorDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (targetPid > 0 && string.IsNullOrEmpty(targetedLogPath))
                        targetedLogPath = LaunchCoordinator.TryFindProcessPlayerLogPath(targetPid, browserTrackerId);

                    IEnumerable<string> candidateLogs = !string.IsNullOrEmpty(targetedLogPath) && File.Exists(targetedLogPath)
                        ? new[] { targetedLogPath }
                        : (targetPid <= 0 && Directory.Exists(logDirectory) ? Directory.EnumerateFiles(logDirectory, "*_Player_*_last.log") : Array.Empty<string>());
                    foreach (string logPath in candidateLogs)
                    {
                        if (await RobloxProcess.ContainsRecentNativeAuthRateLimitInLogAsync(logPath, minimumTimestampUtc, cancellationToken).ConfigureAwait(false))
                        {
                            if (account != null) account.LastNativeLaunchRateLimited = true;
                            Account.MarkNativeAuthRateLimited(Account.NativeAuthRecoverySeconds, username, "native authentication redeem");
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AppServices.Logger?.Debug($"Unable to inspect Roblox launch logs for HTTP 429: {exception.Message}");
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
