#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        /// <summary>
        /// Observes short-lived post-launch tasks without allowing an exception
        /// from process enumeration or a Roblox log read to become an
        /// unobserved task exception.  The launch itself has already completed
        /// when these monitors start, so their failures must not change the
        /// result reported by the launch queue.
        /// </summary>
        private static void ObserveBackgroundTask(Task task, string operation)
        {
            if (task == null)
                return;

            _ = ObserveBackgroundTaskAsync(task, operation);
        }

        private static async Task ObserveBackgroundTaskAsync(Task task, string operation)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected when the launch queue or the
                // application is closed.
            }
            catch (Exception exception)
            {
                try { Program.Logger?.Warn($"Background {operation} failed: {exception.Message}"); }
                catch (Exception) { }
            }
        }

        private static async Task MonitorNativeAuthRateLimitAsync(
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
                            catch (Exception) { continue; }

                            if (modifiedUtc < launchStartedUtc.AddSeconds(-2))
                                continue;

                            List<string> logLines = new List<string>(256);
                            try
                            {
                                using FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                const int tailBytes = 64 * 1024; // 64 KB tail is plenty for recent log redeem requests
                                if (stream.Length > tailBytes)
                                {
                                    stream.Seek(-tailBytes, SeekOrigin.End);
                                }
                                using StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                                if (stream.Position > 0)
                                {
                                    await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); // skip partial line
                                }

                                string? l;
                                while ((l = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                                {
                                    logLines.Add(l);
                                }
                            }
                            catch (Exception) { continue; }

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

                                        MarkNativeAuthRateLimited(
                                            NativeAuthRecoverySeconds,
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

        public async Task AdjustWindowPosition(int targetPid = 0, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RobloxWatcher.RememberWindowPositions)
                return;

            if (!(int.TryParse(GetField("Window_Position_X"), out int PosX) &&
                  int.TryParse(GetField("Window_Position_Y"), out int PosY) &&
                  int.TryParse(GetField("Window_Width"), out int Width) &&
                  int.TryParse(GetField("Window_Height"), out int Height)))
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

            bool Found = false;
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
                        string? CommandLine = ProcessExtensions.GetCommandLineByPid(pid);
                        if (string.IsNullOrEmpty(CommandLine)) continue;

                        var TrackerMatch = TrackerRegex().Match(CommandLine);
                        string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                        if (TrackerID != BrowserTrackerID)
                        {
                            inspectedNonMatchingPids.Add(pid);
                            continue;
                        }

                        Found = true;
                        MoveWindow(hWnd, PosX, PosY, Width, Height, true);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"MoveWindow inspection failed for pid {pid}: {ex.Message}");
                    }
                }

                if (Found || Stopwatch.GetElapsedTime(fallbackStartTick) > fallbackTimeout) break;
            }
        }

        private Task ObserveWindowPositionAsync(CancellationToken cancellationToken) =>
            ObserveWindowPositionAsync(0, cancellationToken);

        private async Task ObserveWindowPositionAsync(int targetPid = 0, CancellationToken cancellationToken = default)
        {
            try
            {
                await AdjustWindowPosition(targetPid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Program.Logger?.Debug($"Unable to restore the Roblox window position for {Username}: {exception.Message}");
            }
        }
    }
}
