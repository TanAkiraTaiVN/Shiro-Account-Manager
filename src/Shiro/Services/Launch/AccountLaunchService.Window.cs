#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

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
                Program.Logger?.Debug($"Background {operation} cancelled: {ex.Message}");
            }
            catch (Exception exception)
            {
                Program.Logger?.Warn($"Background {operation} failed: {exception.Message}");
            }
        }

        public static async Task AdjustWindowPositionAsync(Account account, int targetPid = 0, CancellationToken cancellationToken = default)
        {
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
                        MoveWindow(hWnd, PosX, PosY, Width, Height, true);
                        Program.Logger?.Info($"[AdjustWindowPosition] Fast-repositioned window for PID {targetPid} to ({PosX},{PosY} {Width}x{Height}).");
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

                        var match = TrackerRegex().Match(commandLine);
                        string trackerId = match.Success ? match.Groups[1].Value : string.Empty;

                        if (trackerId != account.BrowserTrackerID)
                        {
                            inspectedNonMatchingPids.Add(pid);
                            continue;
                        }

                        found = true;
                        MoveWindow(hWnd, PosX, PosY, Width, Height, true);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"MoveWindow inspection failed for pid {pid}: {ex.Message}");
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
            string username = account?.Username ?? "account";
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "logs");

            long startTick = Stopwatch.GetTimestamp();
            TimeSpan monitorDuration = TimeSpan.FromSeconds(25);

            while (Stopwatch.GetElapsedTime(startTick) < monitorDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(logDirectory))
                    {
                        foreach (string logPath in Directory.EnumerateFiles(logDirectory, "*_Player_*_last.log"))
                        {
                            DateTime modifiedUtc;
                            try { modifiedUtc = File.GetLastWriteTimeUtc(logPath); }
                            catch (Exception ex) { Program.Logger?.Debug($"GetLastWriteTimeUtc failed: {ex.Message}"); continue; }

                            if (modifiedUtc < launchStartedUtc.AddSeconds(-2))
                                continue;

                            List<string> logLines = new List<string>(256);
                            try
                            {
                                using FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                const int tailBytes = 64 * 1024;
                                if (stream.Length > tailBytes)
                                {
                                    stream.Seek(-tailBytes, SeekOrigin.End);
                                }
                                using StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                                if (stream.Position > 0)
                                {
                                    await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                                }

                                string? l;
                                while ((l = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                                {
                                    logLines.Add(l);
                                }
                            }
                            catch (Exception ex) { Program.Logger?.Debug($"Read logLines failed: {ex.Message}"); continue; }

                            DateTime recentTimestampUtc = DateTime.MinValue;
                            DateTime minimumTimestampUtc = launchStartedUtc.AddSeconds(-2);
                            for (int lineIndex = 0; lineIndex < logLines.Count; lineIndex++)
                            {
                                string line = logLines[lineIndex];
                                if (TryGetRobloxLogTimestampUtc(line, out DateTime parsedTimestampUtc))
                                    recentTimestampUtc = parsedTimestampUtc;

                                if (recentTimestampUtc < minimumTimestampUtc ||
                                    !line.Contains("authentication-ticket/redeem", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                int lookAheadEnd = Math.Min(logLines.Count - 1, lineIndex + 3);
                                for (int lookAhead = lineIndex; lookAhead <= lookAheadEnd; lookAhead++)
                                {
                                    if (IsRobloxRateLimitedLogLine(logLines[lookAhead]))
                                    {
                                        if (account != null)
                                            account.LastNativeLaunchRateLimited = true;

                                        Account.MarkNativeAuthRateLimited(
                                            Account.NativeAuthRecoverySeconds,
                                            username,
                                            "native authentication redeem");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    Program.Logger?.Debug($"Unable to inspect Roblox launch logs for HTTP 429: {exception.Message}");
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool TryGetRobloxLogTimestampUtc(string line, out DateTime timestampUtc)
        {
            timestampUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(line) || line.Length < 24)
                return false;

            if (!DateTimeOffset.TryParse(
                    line.AsSpan(0, 24),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
                return false;

            timestampUtc = parsed.UtcDateTime;
            return true;
        }

        private static bool IsRobloxRateLimitedLogLine(string line)
        {
            return !string.IsNullOrWhiteSpace(line) &&
                (line.Contains("status:429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("status: 429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("statusCode: 429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("statusCode:429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase));
        }
    }
}
