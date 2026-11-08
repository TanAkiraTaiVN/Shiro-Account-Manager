#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        private string? _expectedTrackerId;
        private bool _trackerIdResolved;
        private readonly CancellationTokenSource exitMonitoringCancellation = new CancellationTokenSource();
        private readonly CancellationTokenSource logLookupCancellation = new CancellationTokenSource();
        private readonly System.Threading.Lock _logSync = new();

        public static event Action<int, int>? Disconnected;
        public static event Action<int>? Connected;

        public RobloxProcess(Process process, string? expectedTrackerId = null)
        {
            Program.Logger.Info($"New RobloxProcess created for {process.Id}");

            RbxProcess = process;
            if (!string.IsNullOrEmpty(expectedTrackerId))
            {
                _expectedTrackerId = expectedTrackerId;
                _trackerIdResolved = true;
            }

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
                    await RbxProcess.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Intentional: cancellation requested */ }
            catch (InvalidOperationException) { /* Intentional: process exited before wait */ }
            catch (Exception x) { Program.Logger.Error($"WaitForExit Error: {x}"); }
            finally
            {
                if (Program.Closed || SafeHasExited())
                    CleanupProcessExit();
            }
        }

        private bool SafeHasExited() =>
            SafeExecuteHelper.Try(() => RbxProcess == null || RbxProcess.HasExited, fallback: true);

        private void ScheduleNoConnectionWatchdogIfEnabled(double initialGraceSeconds = 0)
        {
            try
            {
                if (AppSettings.Watcher == null || !AppSettings.Watcher.Get<bool>("ExitIfNoConnection")) return;
                double timeout = AppSettings.Watcher.Get<double>("NoConnectionTimeout");
                if (timeout <= 0) return;

                var token = exitMonitoringCancellation.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, initialGraceSeconds + timeout + 0.5)), token).ConfigureAwait(false);
                        if (token.IsCancellationRequested || IsConnected || Program.Closed || SafeHasExited()) return;
                        if (AppSettings.Watcher == null || !AppSettings.Watcher.Get<bool>("ExitIfNoConnection")) return;

                        double configuredTimeout = AppSettings.Watcher.Get<double>("NoConnectionTimeout");
                        double elapsed = (DateTime.UtcNow - DisconnectedTime).TotalSeconds;
                        if (configuredTimeout > 0 && !IsConnected && elapsed > configuredTimeout)
                            KillProcess($"Lost connection for more than {elapsed:0.0} second(s)");
                    }
                    catch (OperationCanceledException) { /* Intentional: watchdog cancelled */ }
                    catch (Exception ex) { Program.Logger?.Debug($"NoConnection watchdog error: {ex.Message}"); }
                }, token);
            }
            catch (Exception ex) { Program.Logger?.Debug($"ScheduleNoConnectionWatchdogIfEnabled failed: {ex.Message}"); }
        }

        private void CleanupProcessExit()
        {
            if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return;
            try
            {
                SafeExecuteHelper.Try(exitMonitoringCancellation.Cancel);
                SafeExecuteHelper.Try(logLookupCancellation.Cancel);

                int processId = SafeExecuteHelper.Try(() => RbxProcess?.Id ?? 0, fallback: 0);
                if (processId > 0) Program.Logger.Info($"{processId} has exited");

                RobloxWatcher.LogFileRead -= ReadLogFile;
                lock (RobloxWatcher.InstanceRegistryLock) { RobloxWatcher.Instances.Remove(this); }
                if (processId > 0) RobloxWatcher.RemoveProcessMetadata(processId);

                lock (_logSync)
                {
                    StreamDisposed = true;
                    LogStream?.Dispose();
                    LogStream = null;
                }
                SafeExecuteHelper.Try(() => RbxProcess?.Dispose());
                SafeExecuteHelper.Try(logLookupCancellation.Dispose);
                SafeExecuteHelper.Try(exitMonitoringCancellation.Dispose);
                Utilities.QueueRefreshNotificationArea();
            }
            catch (Exception ex) { Program.Logger?.Debug($"CleanupProcessExit error: {ex.Message}"); }
        }

        internal static string[] ReadAppendedLines(Stream stream, ref long lastPosition)
        {
            if (stream == null || !stream.CanRead) return Array.Empty<string>();
            long currentLength = stream.Length;
            if (currentLength <= lastPosition) return Array.Empty<string>();
            long delta = currentLength - lastPosition;
            if (delta <= 0 || delta > int.MaxValue) return Array.Empty<string>();

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
                    if (lastNewline < 0) return Array.Empty<string>();
                    consumeLength = lastNewline + 1;
                }
                lastPosition += consumeLength;
                return Encoding.UTF8.GetString(rented.AsSpan(0, consumeLength)).Split('\n');
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        private void ReadLogFile(object? s, EventArgs e)
        {
            if (StreamDisposed) return;

            string[] Lines;
            lock (_logSync)
            {
                if (StreamDisposed) return;
                FileStream? stream = LogStream;
                if (stream == null) return;
                try
                {
                    if (!stream.CanRead) return;
                    Lines = ReadAppendedLines(stream, ref LastPosition);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception x)
                {
                    Program.Logger.Error($"An error occured while trying to read LogFile of {RbxProcess.Id}: {x}");
                    return;
                }
            }

            try
            {
                if (Lines.Length > 0)
                {
                    for (int i = 0; i < Lines.Length; i++)
                    {
                        string Line = Lines[i];

                        if (IsJoiningGameLogLine(Line))
                        {
                            IsConnected = true;
                            HasGameJoinChallenge = false;
                            HasSecurityCookieChanged = false;
                            try { if (RbxProcess != null) Connected?.Invoke(RbxProcess.Id); }
                            catch (Exception ex) { Program.Logger?.Error($"Connected event handler failed: {ex.Message}"); }
                            continue;
                        }

                        if (IsChallengeCompletedLogLine(Line))
                            HasGameJoinChallenge = false;

                        if (!IsConnected && IsGameJoinChallengedLogLine(Line))
                            HasGameJoinChallenge = true;

                        if (IsSecurityCookieChangedLogLine(Line))
                            HasSecurityCookieChanged = true;

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
                            if (AppSettings.General?.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false) ?? false)
                                _ = SchedulePostLaunchRamTrimAsync();
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
                            // Prevent duplicate LuaApp Home windows when a verification challenge (challengedByGcs)
                            // fails before entering the game, or when the session cookie was hijacked mid-session.
                            if ((HasGameJoinChallenge && !IsConnected) ||
                                ((HasGameJoinChallenge || CurrentDataModel > 0) && HasSecurityCookieChanged))
                            {
                                CurrentDataModel = -1;
                                int pid = SafeExecuteHelper.Try(() => RbxProcess?.Id ?? 0, fallback: 0);
                                if (pid > 0)
                                    AutoReconnectService.Instance.SuppressReconnectByPid(pid);

                                if (KillProcess("Game client challenged or session modified during fallback to LuaApp"))
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

                                    if (AppSettings.Watcher?.Get<bool>("ExitOnBeta") == true && KillProcess("Beta home menu detected"))
                                        return;
                                }
                            }
                            else
                            {
                                CurrentDataModel = -1;
                                if (AppSettings.Watcher?.Get<bool>("ExitOnBeta") == true && KillProcess("Beta home menu detected"))
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

            SafeExecuteHelper.Try(exitMonitoringCancellation.Cancel);
            SafeExecuteHelper.Try(logLookupCancellation.Cancel);

            LogStream?.Dispose();
            try
            {
                if (!RbxProcess.HasExited)
                    RbxProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { Program.Logger?.Debug($"Kill process failed: {ex.Message}"); }

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
                            if (file.Name.Contains("_CrashHandler_", StringComparison.OrdinalIgnoreCase) || claimedPaths.Contains(file.FullName))
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

                        if (!_trackerIdResolved)
                        {
                            _expectedTrackerId = ResolveExpectedTrackerIdForPid(SafeExecuteHelper.Try(() => RbxProcess?.Id ?? 0, fallback: 0));
                            _trackerIdResolved = true;
                        }
                        string? expectedTrackerId = _expectedTrackerId;
                        FileInfo? inconclusiveFile = null;
                        FileStream? inconclusiveStream = null;

                        foreach (var candidate in candidateLogs)
                        {
                            var file = candidate.File;
                            if (TryOpenCandidateLog(file, expectedTrackerId, ref inconclusiveFile, ref inconclusiveStream, out FileStream? matchedStream) && matchedStream != null)
                            {
                                lock (_logSync)
                                {
                                    if (Volatile.Read(ref _cleanedUp) != 0 || StreamDisposed)
                                    {
                                        matchedStream.Dispose();
                                        inconclusiveStream?.Dispose();
                                        return;
                                    }
                                    LogFile = file;
                                    LogStream = matchedStream;
                                }
                                Program.Logger?.Info($"Found LogFile Path via direct logs folder: {LogFile.FullName}");
                                return;
                            }
                        }

                        if (inconclusiveStream != null && inconclusiveFile != null)
                        {
                            lock (_logSync)
                            {
                                if (Volatile.Read(ref _cleanedUp) != 0 || StreamDisposed)
                                {
                                    inconclusiveStream.Dispose();
                                    return;
                                }
                                LogFile = inconclusiveFile;
                                LogStream = inconclusiveStream;
                            }
                            AppServices.Logger.Info($"Found LogFile Path via direct logs folder: {LogFile.FullName}");
                            return;
                        }
                    }
                    catch (Exception ex) { Program.Logger?.Debug($"Direct logs scan error: {ex.Message}"); }
                }

                await Task.Delay(LogFileRetries <= 4 ? 400 : 1500, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SchedulePostLaunchRamTrimAsync()
        {
            try
            {
                await Task.Delay(15000, exitMonitoringCancellation.Token).ConfigureAwait(false);
                MemoryOptimizer.TrimRobloxClients();
            }
            catch (Exception ex) { Program.Logger?.Debug($"SchedulePostLaunchRamTrimAsync stopped: {ex.Message}"); }
        }

        private async Task ObserveLogPathLookupAsync()
        {
            try
            {
                await WaitForLogPathAsync(logLookupCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (logLookupCancellation.IsCancellationRequested) { /* Intentional: cancelled */ }
            catch (Exception exception) { Program.Logger.Error($"WaitForLogPath Error for Roblox process {RbxProcess?.Id}: {exception}"); }
        }
    }
}
