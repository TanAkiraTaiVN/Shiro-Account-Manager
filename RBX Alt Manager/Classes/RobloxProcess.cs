using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    internal partial class RobloxProcess
    {
        [GeneratedRegex(@"^[\d+\-]+T[\d+:]+\.\w+Z,[\d.+]+,\w+,\d+[\s+]?\[FLog::Output\] ! Joining game '[\w+\-]{36}' place \d+ at [\d+\.]+")]
        private static partial Regex JoiningGameRegex();

        [GeneratedRegex(@"^[\d+\-]+T[\d+:]+\.\w+Z,[\d.+]+,\w+,\d+[\s+]?\[FLog::Network\] Sending disconnect with reason: (\d+)")]
        private static partial Regex DisconnectRegex();

        [GeneratedRegex(@"\[FLog::UGCGameController\] UGCGameController, initialized DataModel\((\w+)\)")]
        private static partial Regex DataModelInitRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:1\]::start dataModel\((\w+)\)")]
        private static partial Regex DataModelInit2Regex();

        [GeneratedRegex(@"\[FLog::UGCGameController\] UGCGameController::leave \(blocking:\d+\) dataModel\((\w+)\)")]
        private static partial Regex DataModelStopRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:1\]::pause dataModel\((\w+)\), view\(\w+\), destroyView:\d+\.")]
        private static partial Regex DataModelPauseRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:1\]::stop")]
        private static partial Regex DataModelStop2Regex();

        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] returnToLuaApp: \.\.\. App not yet initialized, returning from game\.")]
        private static partial Regex ReturnToApp1Regex();

        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] returnToLuaApp: \.\.\. App has been initialized, returning from game\.")]
        private static partial Regex ReturnToApp2Regex();

        private Process RbxProcess;
        private int LogFileRetries = 0;
        public FileInfo LogFile;
        private FileStream LogStream;
        private long LastPosition = 0;
        private long CurrentDataModel;
        private bool IsDMPaused;
        private bool StreamDisposed;
        private bool IsConnected;
        private DateTime DisconnectedTime;
        private string LastLine;
        private int _cleanedUp;
        private readonly CancellationTokenSource exitMonitoringCancellation = new CancellationTokenSource();
        private readonly CancellationTokenSource logLookupCancellation = new CancellationTokenSource();

        public static event Action<int, int> Disconnected;
        public static event Action<int> Connected;

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

            _ = MonitorProcessExitLoopAsync(exitMonitoringCancellation.Token);
        }

        private async Task MonitorProcessExitLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (AccountManager.Watcher.Get<bool>("ExitIfNoConnection") && AccountManager.Watcher.Get<double>("NoConnectionTimeout") is double Timeout && Timeout > 0 && !IsConnected && (DateTime.UtcNow - DisconnectedTime).TotalSeconds is double Seconds && Seconds > Timeout)
                        KillProcess($"Lost connection for more than {Seconds} second(s)");

                    bool hasExited = false;
                    try { hasExited = RbxProcess.HasExited; }
                    catch (Exception) { hasExited = true; }

                    if (!hasExited && !Program.Closed)
                        continue;

                    CleanupProcessExit();
                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception x)
            {
                Program.Logger.Error($"WaitForExit Error: {x}");
                CleanupProcessExit();
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

        private void ReadLogFile(object s, EventArgs e)
        {
            if (StreamDisposed) return;
            FileStream stream = LogStream;
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

                        if (JoiningGameRegex().IsMatch(Line))
                        {
                            IsConnected = true;
                            try { if (RbxProcess != null) Connected?.Invoke(RbxProcess.Id); }
                            catch (Exception ex) { Program.Logger?.Error($"Connected event handler failed: {ex.Message}"); }

                            continue;
                        }

                        Match disconnectMatch = DisconnectRegex().Match(Line);
                        if (disconnectMatch.Success)
                        {
                            IsConnected = false;
                            DisconnectedTime = DateTime.UtcNow;
                            int reason = disconnectMatch.Groups.Count >= 2 && int.TryParse(disconnectMatch.Groups[1].Value, out int parsedReason)
                                ? parsedReason
                                : 0;
                            try { if (RbxProcess != null) Disconnected?.Invoke(RbxProcess.Id, reason); }
                            catch (Exception ex) { Program.Logger?.Error($"Disconnected event handler failed: {ex.Message}"); }

                            continue;
                        }

                        Match DMI = DataModelInitRegex().Match(Line);

                        if (!DMI.Success || DMI.Groups.Count != 2)
                            DMI = DataModelInit2Regex().Match(Line);

                        if (CurrentDataModel <= 0 && DMI.Success && DMI.Groups.Count == 2 && long.TryParse(DMI.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out CurrentDataModel))
                        {
                            IsDMPaused = false;

                            Program.Logger.Info($"CurrentDataModel set to {CurrentDataModel} ({DMI})");

                            // Automatically trim bloated launch working set 15 seconds after map assets finish loading
                            if (AccountManager.General?.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, true) ?? false)
                            {
                                _ = SchedulePostLaunchRamTrimAsync();
                            }

                            continue;
                        }

                        long MatchedDataModel = -2;

                        Match PDM = DataModelPauseRegex().Match(Line);

                        if (PDM.Success && PDM.Groups.Count == 2 && long.TryParse(PDM.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out MatchedDataModel) && MatchedDataModel == CurrentDataModel)
                        {
                            IsDMPaused = true;

                            continue;
                        }

                        Match RTA1 = ReturnToApp1Regex().Match(Line);
                        Match RTA2 = ReturnToApp2Regex().Match(Line);

                        if (RTA1.Success || RTA2.Success)
                        {
                            Program.Logger.Info($"RTA1: {RTA1}");
                            Program.Logger.Info($"RTA2: {RTA2}");
                            Program.Logger.Info($"Was Paused: {IsDMPaused}");
                            Program.Logger.Info($"Current Line: {i}");

                            if (RobloxWatcher.VerifyDataModel && !string.IsNullOrEmpty(LastLine))
                            {
                                Match DMS = DataModelStopRegex().Match(LastLine);

                                if (IsDMPaused || (DMS.Success && DMS.Groups.Count == 2 && long.TryParse(DMS.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out MatchedDataModel) && MatchedDataModel == CurrentDataModel))
                                {
                                    CurrentDataModel = -1;
                                    Program.Logger.Info($"CurrentDataModel set to {CurrentDataModel} ({DMS}) | Position: {LastPosition}");

                                    if (AccountManager.Watcher.Get<bool>("ExitOnBeta") && KillProcess("Beta home menu detected"))
                                        return;
                                }
                            }
                            else
                            {
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

            exitMonitoringCancellation.Cancel();
            logLookupCancellation.Cancel();

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

            return true;
        }

        private async Task WaitForLogPathAsync(CancellationToken cancellationToken)
        {
            while (LogFileRetries <= 30 && !RbxProcess.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Program.Logger.Info($"Attempting to find LogFile for {RbxProcess.Id}, retries: {LogFileRetries}");
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
                                Program.Logger.Info($"Found LogFile Path via direct logs folder: {LogFile.FullName}");
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
                        Program.Logger.Debug($"Direct logs scan error: {ex.Message}");
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
