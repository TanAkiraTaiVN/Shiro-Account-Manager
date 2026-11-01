#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Classes
{
    internal partial class RobloxProcess
    {
        private Process RbxProcess;
        private int LogFileRetries = 0;
        public FileInfo? LogFile;
        private FileStream? LogStream;
        private long LastPosition = 0;
        private long CurrentDataModel;
        private bool IsDMPaused;
        private bool HasGameJoinChallenge;
        private bool HasSecurityCookieChanged;
        private bool StreamDisposed;
        private volatile bool IsConnected;
        private DateTime DisconnectedTime;
        private string? LastLine;
        private int _cleanedUp;
        private readonly CancellationTokenSource exitMonitoringCancellation = new CancellationTokenSource();
        private readonly CancellationTokenSource logLookupCancellation = new CancellationTokenSource();

        public static event Action<int, int>? Disconnected;
        public static event Action<int>? Connected;

        public RobloxProcess(Process process)
        {
            Program.Logger.Info($"New RobloxProcess created for {process.Id}");

            RbxProcess = process;

            RobloxWatcher.LogFileRead += ReadLogFile;

            // The helper process performs blocking OS I/O, so keep the first
            // lookup off the caller's thread. Observe failures inside the
            // wrapper instead of leaving a continuation or faulted task
            // behind after a Roblox process exits.
            _ = ObserveLogPathLookupAsync();

            DisconnectedTime = DateTime.UtcNow.AddSeconds(90);

            _ = MonitorProcessExitAsync(exitMonitoringCancellation.Token);
            ScheduleNoConnectionWatchdogIfEnabled(initialGraceSeconds: 90);
        }

        private async Task MonitorProcessExitAsync(CancellationToken token)
        {
            try
            {
                if (!RbxProcess.HasExited && !Program.Closed)
                {
                    await RbxProcess.WaitForExitAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            catch (Exception x)
            {
                Program.Logger.Error($"WaitForExit Error: {x}");
            }
            finally
            {
                if (Program.Closed || SafeHasExited())
                {
                    CleanupProcessExit();
                }
            }
        }

        private bool SafeHasExited()
        {
            try { return RbxProcess == null || RbxProcess.HasExited; }
            catch { return true; }
        }

        private void ScheduleNoConnectionWatchdogIfEnabled(double initialGraceSeconds = 0)
        {
            try
            {
                if (AccountManager.Watcher == null || !AccountManager.Watcher.Get<bool>("ExitIfNoConnection"))
                    return;

                double timeout = AccountManager.Watcher.Get<double>("NoConnectionTimeout");
                if (timeout <= 0)
                    return;

                var token = exitMonitoringCancellation.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        double waitSeconds = Math.Max(1.0, initialGraceSeconds + timeout + 0.5);
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token).ConfigureAwait(false);

                        if (token.IsCancellationRequested || IsConnected || Program.Closed || SafeHasExited())
                            return;

                        if (!AccountManager.Watcher.Get<bool>("ExitIfNoConnection"))
                            return;

                        double configuredTimeout = AccountManager.Watcher.Get<double>("NoConnectionTimeout");
                        double elapsed = (DateTime.UtcNow - DisconnectedTime).TotalSeconds;
                        if (configuredTimeout > 0 && !IsConnected && elapsed > configuredTimeout)
                        {
                            KillProcess($"Lost connection for more than {elapsed:0.0} second(s)");
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"NoConnection watchdog error: {ex.Message}");
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"ScheduleNoConnectionWatchdogIfEnabled failed: {ex.Message}");
            }
        }

        private void CleanupProcessExit()
        {
            if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
                return;

            try
            {
                try { exitMonitoringCancellation.Cancel(); } catch (Exception) { }
                try { logLookupCancellation.Cancel(); } catch (Exception) { }

                int processId = 0;
                try { processId = RbxProcess?.Id ?? 0; } catch (Exception) { }
                if (processId > 0)
                    Program.Logger.Info($"{processId} has exited");

                RobloxWatcher.LogFileRead -= ReadLogFile;

                lock (RobloxWatcher.InstanceRegistryLock)
                {
                    RobloxWatcher.Instances.Remove(this);
                }
                if (processId > 0)
                    RobloxWatcher.RemoveProcessMetadata(processId);

                LogStream?.Dispose();
                try { RbxProcess?.Dispose(); } catch (Exception) { }
                try { logLookupCancellation.Dispose(); } catch (Exception) { }
                try { exitMonitoringCancellation.Dispose(); } catch (Exception) { }

                Utilities.QueueRefreshNotificationArea();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"CleanupProcessExit error: {ex.Message}");
            }
        }

        internal static string[] ReadAppendedLines(Stream stream, ref long lastPosition)
        {
            if (stream == null || !stream.CanRead)
                return Array.Empty<string>();

            long currentLength = stream.Length;
            if (currentLength <= lastPosition)
                return Array.Empty<string>();

            long delta = currentLength - lastPosition;
            if (delta <= 0 || delta > int.MaxValue)
                return Array.Empty<string>();

            int length = (int)delta;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.Seek(lastPosition, SeekOrigin.Begin);
                stream.ReadExactly(rented, 0, length);

                // If the stream is a live FileStream and the last byte isn't a newline,
                // only consume up to the last complete newline so partial lines being written by Roblox are never split mid-line.
                int consumeLength = length;
                if (stream is FileStream && rented[length - 1] != (byte)'\n')
                {
                    int lastNewline = Array.LastIndexOf(rented, (byte)'\n', length - 1, length);
                    if (lastNewline >= 0)
                    {
                        consumeLength = lastNewline + 1;
                    }
                }

                lastPosition += consumeLength;
                string content = Encoding.UTF8.GetString(rented.AsSpan(0, consumeLength));
                return content.Split('\n');
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void ReadLogFile(object? s, EventArgs e)
        {
            if (StreamDisposed) return;
            FileStream? stream = LogStream;
            if (stream == null) return;
            try { if (!stream.CanRead) return; }
            catch (ObjectDisposedException) { return; }

            try
            {
                string[] Lines = ReadAppendedLines(stream, ref LastPosition);
                if (Lines.Length > 0)
                {
                    for (int i = 0; i < Lines.Length; i++)
                    {
                        string Line = Lines[i];

                        if (IsJoiningGameLogLine(Line))
                        {
                            IsConnected = true;
                            HasGameJoinChallenge = false;
                            try { if (RbxProcess != null) Connected?.Invoke(RbxProcess.Id); }
                            catch (Exception ex) { Program.Logger?.Error($"Connected event handler failed: {ex.Message}"); }

                            continue;
                        }

                        if (IsGameJoinChallengedLogLine(Line))
                        {
                            HasGameJoinChallenge = true;
                        }

                        if (IsSecurityCookieChangedLogLine(Line))
                        {
                            HasSecurityCookieChanged = true;
                        }

                        if (TryParseDisconnectReason(Line, out int reason))
                        {
                            IsConnected = false;
                            DisconnectedTime = DateTime.UtcNow;
                            ScheduleNoConnectionWatchdogIfEnabled(initialGraceSeconds: 0);
                            try { if (RbxProcess != null) Disconnected?.Invoke(RbxProcess.Id, reason); }
                            catch (Exception ex) { Program.Logger?.Error($"Disconnected event handler failed: {ex.Message}"); }

                            continue;
                        }

                        if (CurrentDataModel <= 0 && TryParseDataModelInit(Line, out long parsedDataModel))
                        {
                            CurrentDataModel = parsedDataModel;
                            IsDMPaused = false;

                            Program.Logger?.Info($"CurrentDataModel set to {CurrentDataModel:X}");

                            // Automatically trim launch working set 15 seconds after map assets finish loading if enabled
                            if (AccountManager.General?.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false) ?? false)
                            {
                                _ = SchedulePostLaunchRamTrimAsync();
                            }

                            continue;
                        }

                        Match legacyPause = LegacyDataModelPauseRegex().Match(Line);
                        if ((legacyPause.Success &&
                             legacyPause.Groups.Count == 2 &&
                             long.TryParse(legacyPause.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long pausedDm) &&
                             pausedDm == CurrentDataModel) ||
                            (CurrentDataModel > 0 && ModernDataModelPauseRegex().IsMatch(Line)))
                        {
                            IsDMPaused = true;
                            continue;
                        }

                        if (IsReturnToLuaAppLine(Line, hasActiveGameDataModel: CurrentDataModel > 0))
                        {
                            // Prevent duplicate LuaApp Home windows logged into the wrong account when a verification
                            // challenge (challengedByGcs) had its WebView2 session cookie overwritten mid-challenge.
                            if ((HasGameJoinChallenge || CurrentDataModel > 0) && HasSecurityCookieChanged)
                            {
                                CurrentDataModel = -1;
                                int pid = 0;
                                try { pid = RbxProcess?.Id ?? 0; } catch { }
                                if (pid > 0)
                                    AutoReconnectService.Instance.SuppressReconnectByPid(pid);

                                if (KillProcess("Session cookie hijacked during verification challenge fallback to LuaApp"))
                                    return;
                            }

                            if (RobloxWatcher.VerifyDataModel && !string.IsNullOrEmpty(LastLine))
                            {
                                Match legacyStop = LegacyDataModelStopRegex().Match(LastLine);
                                bool legacyStoppedCurrent = legacyStop.Success &&
                                    legacyStop.Groups.Count == 2 &&
                                    long.TryParse(legacyStop.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long stoppedDm) &&
                                    stoppedDm == CurrentDataModel;
                                bool modernStoppedCurrent = CurrentDataModel > 0 &&
                                    (ModernDataModelStopRegex().IsMatch(LastLine) || StageLuaAppRegex().IsMatch(Line) || ReturnToLuaAppStageRegex().IsMatch(Line));

                                if (IsDMPaused || legacyStoppedCurrent || modernStoppedCurrent)
                                {
                                    CurrentDataModel = -1;
                                    Program.Logger?.Info($"CurrentDataModel reset to {CurrentDataModel} | Position: {LastPosition}");

                                    if (AccountManager.Watcher.Get<bool>("ExitOnBeta") && KillProcess("Beta home menu detected"))
                                        return;
                                }
                            }
                            else
                            {
                                CurrentDataModel = -1;
                                if (AccountManager.Watcher.Get<bool>("ExitOnBeta") && KillProcess("Beta home menu detected"))
                                    return;
                            }
                        }

                        if (RobloxWatcher.VerifyDataModel && IsDMPaused && DataModelStop2Regex().IsMatch(Line))
                            CurrentDataModel = -1;

                        LastLine = Line;
                    }
                }
            }
            catch (ObjectDisposedException) { /* Stream was closed concurrently */ }
            catch (Exception x) { Program.Logger.Error($"An error occured while trying to read LogFile of {RbxProcess.Id}: {x}"); }
        }

        private bool KillProcess(string Reason)
        {
            if (!(RobloxWatcher.IgnoreExistingProcesses || (!RobloxWatcher.IgnoreExistingProcesses && LastPosition > 0))) return false;

            Program.Logger.Info($"Attempting to kill process {RbxProcess.Id}, reason: {Reason}");
            StreamDisposed = true;

            try { exitMonitoringCancellation.Cancel(); } catch { }
            try { logLookupCancellation.Cancel(); } catch { }

            LogStream?.Dispose();
            try
            {
                if (!RbxProcess.HasExited)
                    RbxProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Kill process failed: {ex.Message}");
            }

            CleanupProcessExit();
            return true;
        }

        private async Task WaitForLogPathAsync(CancellationToken cancellationToken)
        {
            while (LogFileRetries <= 30 && !SafeHasExited())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Program.Logger?.Info($"Attempting to find LogFile for {RbxProcess.Id}, retries: {LogFileRetries}");
                LogFileRetries += 1;

                // Scan standard Roblox logs folder directly (pure C#, high performance, zero external executable)
                string defaultLogsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
                if (Directory.Exists(defaultLogsDir))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(defaultLogsDir);
                        DateTime processStartTimeUtc = DateTime.MinValue;
                        try { processStartTimeUtc = RbxProcess.StartTime.ToUniversalTime(); } catch (Exception ex) { Program.Logger?.Debug($"Get process start time failed: {ex.Message}"); }
                        DateTime minWriteUtc = processStartTimeUtc != DateTime.MinValue ? processStartTimeUtc.AddMinutes(-1) : DateTime.MinValue;

                        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        lock (RobloxWatcher.InstanceRegistryLock)
                        {
                            foreach (var inst in RobloxWatcher.Instances)
                            {
                                if (inst != this && inst?.LogFile != null)
                                    claimedPaths.Add(inst.LogFile.FullName);
                            }
                        }

                        var candidateLogs = new List<(FileInfo File, double StartDeltaSeconds, DateTime LastWriteUtc)>(8);
                        foreach (var file in dirInfo.EnumerateFiles("*_Player_*_last.log"))
                        {
                            // Skip empty CrashHandler companion logs created at the exact same timestamp in 0.745+
                            if (file.Name.Contains("_CrashHandler_", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (claimedPaths.Contains(file.FullName))
                                continue;

                            DateTime lastWriteUtc = file.LastWriteTimeUtc;
                            if (minWriteUtc != DateTime.MinValue && lastWriteUtc < minWriteUtc)
                                continue;

                            double delta = processStartTimeUtc != DateTime.MinValue
                                ? Math.Abs((file.CreationTimeUtc - processStartTimeUtc).TotalSeconds)
                                : 0.0;

                            candidateLogs.Add((file, delta, lastWriteUtc));
                        }

                        candidateLogs.Sort((a, b) =>
                        {
                            int cmp = a.StartDeltaSeconds.CompareTo(b.StartDeltaSeconds);
                            return cmp != 0 ? cmp : b.LastWriteUtc.CompareTo(a.LastWriteUtc);
                        });

                        foreach (var candidate in candidateLogs)
                        {
                            var file = candidate.File;
                            lock (RobloxWatcher.InstanceRegistryLock)
                            {
                                if (RobloxWatcher.Instances.Any(inst => inst != this && inst.LogFile != null && string.Equals(inst.LogFile.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                            }

                            try
                            {
                                FileStream stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                LogFile = file;
                                LogStream = stream;
                                Program.Logger?.Info($"Found LogFile Path via direct logs folder: {LogFile.FullName}");
                                return;
                            }
                            catch
                            {
                                // In use exclusively or not ready yet, check next candidate
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Direct logs scan error: {ex.Message}");
                    }
                }

                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SchedulePostLaunchRamTrimAsync()
        {
            try
            {
                await Task.Delay(15000, exitMonitoringCancellation.Token).ConfigureAwait(false);
                MemoryOptimizer.TrimRobloxClients();
            }
            catch
            {
            }
        }

        private async Task ObserveLogPathLookupAsync()
        {
            try
            {
                await WaitForLogPathAsync(logLookupCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (logLookupCancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Program.Logger.Error($"WaitForLogPath Error for Roblox process {RbxProcess?.Id}: {exception}");
            }
        }
    }
}
