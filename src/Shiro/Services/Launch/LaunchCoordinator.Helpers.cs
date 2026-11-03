#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    internal enum ClientGameJoinState
    {
        Pending,
        JoinedGame,
        Challenged,
        ChallengeFailedToLuaApp,
        ProcessExited
    }

    internal sealed partial class LaunchCoordinator
    {
        public static event Action<LaunchProgress>? LaunchProgressChanged;
        public static event Action? GlobalCancelRequested;

        public static void RequestGlobalCancel()
        {
            try { AppSettings.CancelLaunching(); }
            catch (Exception ex) { Program.Logger?.Debug($"CancelLaunching threw: {ex.Message}"); }

            try { GlobalCancelRequested?.Invoke(); }
            catch (Exception ex) { Program.Logger?.Debug($"GlobalCancelRequested subscriber threw: {ex.Message}"); }
        }

        internal static int GetSmartLaunchMinDelayMs(int? configuredSecondsOverride = null, bool includeJitter = true)
        {
            int seconds = DefaultSmartLaunchMinDelaySeconds;
            if (configuredSecondsOverride.HasValue)
            {
                seconds = configuredSecondsOverride.Value;
            }
            else if (AppSettings.General != null && AppSettings.General.Exists(AppSettingsKeys.General.SmartLaunchMinDelaySeconds))
            {
                try
                {
                    seconds = AppSettings.General.Get<int>(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, DefaultSmartLaunchMinDelaySeconds);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"[LaunchCoordinator.GetSmartLaunchMinDelayMs] {ex.Message}");
                    seconds = DefaultSmartLaunchMinDelaySeconds;
                }
            }

            seconds = Math.Clamp(seconds, 1, 60);
            int jitterMs = includeJitter ? Random.Shared.Next(0, 1500) : 0;
            return (seconds * 1000) + jitterMs;
        }

        private static void Report(Action<LaunchProgress>? progress, LaunchProgress state)
        {
            try { progress?.Invoke(state); }
            catch (Exception ex) { Program.Logger?.Warn($"Launch progress callback threw: {ex.Message}"); }

            try { LaunchProgressChanged?.Invoke(state); }
            catch (Exception ex) { Program.Logger?.Warn($"LaunchProgressChanged subscriber threw: {ex.Message}"); }
        }


        internal static ClientGameJoinState AnalyzeClientLogLines(IEnumerable<string> lines)
        {
            if (lines == null)
                return ClientGameJoinState.Pending;

            bool hasChallenge = false;
            bool challengeFailed = false;
            bool joinedGame = false;
            bool hasGameDataModel = false;

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;

                if (RobloxProcess.TryParseDataModelInit(line, out _))
                {
                    hasGameDataModel = true;
                }

                if (RobloxProcess.IsJoiningGameLogLine(line))
                {
                    joinedGame = true;
                    hasChallenge = false;
                    challengeFailed = false;
                }
                else if (RobloxProcess.IsChallengeCompletedLogLine(line))
                {
                    hasChallenge = false;
                    challengeFailed = false;
                }
                else if (RobloxProcess.IsGameJoinChallengedLogLine(line))
                {
                    if (!joinedGame)
                        hasChallenge = true;
                }
                else if (RobloxProcess.IsChallengeInvalidatedOrAbandonedLogLine(line) ||
                         RobloxProcess.IsReturnToLuaAppLine(line, hasGameDataModel))
                {
                    if (hasChallenge || !joinedGame)
                    {
                        hasChallenge = true;
                        challengeFailed = true;
                        joinedGame = false;
                    }
                }
            }

            if (joinedGame)
                return ClientGameJoinState.JoinedGame;

            if (challengeFailed)
                return ClientGameJoinState.ChallengeFailedToLuaApp;

            if (hasChallenge)
                return ClientGameJoinState.Challenged;

            return ClientGameJoinState.Pending;
        }

        internal static string? TryFindProcessPlayerLogPath(int pid, string? browserTrackerId)
        {
            if (pid <= 0)
                return null;

            lock (RobloxWatcher.InstanceRegistryLock)
            {
                foreach (var inst in RobloxWatcher.Instances)
                {
                    try
                    {
                        if (inst?.LogFile != null && inst.LogFile.Exists && File.Exists(inst.LogFile.FullName))
                        {
                            // Match by checking if the log file contains our browserTrackerId or is the newest log for this process
                            if (!string.IsNullOrEmpty(browserTrackerId))
                            {
                                // Fast path checked below across candidates
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"[LaunchCoordinator.TryFindProcessPlayerLogPath] Watcher lookup: {ex.Message}");
                    }
                }
            }

            string defaultLogsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox",
                "logs");
            if (!Directory.Exists(defaultLogsDir))
                return null;

            DateTime processStartUtc = DateTime.MinValue;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                    return null;
                processStartUtc = proc.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[LaunchCoordinator.TryFindProcessPlayerLogPath] Process start time: {ex.Message}");
                return null;
            }
            try
            {
                var dirInfo = new DirectoryInfo(defaultLogsDir);
                DateTime minWriteUtc = processStartUtc != DateTime.MinValue ? processStartUtc.AddSeconds(-15) : DateTime.UtcNow.AddMinutes(-2);
                string? bestMatchPath = null;
                double bestDelta = double.MaxValue;

                foreach (var file in dirInfo.EnumerateFiles("*_Player_*_last.log"))
                {
                    if (file.Name.Contains("_CrashHandler_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (file.LastWriteTimeUtc < minWriteUtc)
                        continue;

                    double delta = processStartUtc != DateTime.MinValue
                        ? Math.Abs((file.CreationTimeUtc - processStartUtc).TotalSeconds)
                        : 0.0;

                    if (delta > 30.0)
                        continue;

                    if (!string.IsNullOrWhiteSpace(browserTrackerId))
                    {
                        try
                        {
                            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            int headBytes = (int)Math.Min(16384, stream.Length);
                            byte[] buf = new byte[headBytes];
                            int read = stream.Read(buf, 0, headBytes);
                            string head = Encoding.UTF8.GetString(buf, 0, read);
                            if (head.Contains(browserTrackerId, StringComparison.Ordinal))
                                return file.FullName;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"[LaunchCoordinator.TryFindProcessPlayerLogPath] Tracker stream read: {ex.Message}");
                        }
                    }

                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMatchPath = file.FullName;
                    }
                }

                return bestDelta <= 6.0 ? bestMatchPath : null;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[LaunchCoordinator.TryFindProcessPlayerLogPath] Enumerate files: {ex.Message}");
                return null;
            }
        }

        internal static ClientGameJoinState InspectProcessGameJoinState(int pid, string? browserTrackerId, ref string? cachedLogPath)
        {
            if (pid <= 0 || !RobloxProcessService.IsProcessAlive(pid))
                return ClientGameJoinState.ProcessExited;

            if (string.IsNullOrEmpty(cachedLogPath) || !File.Exists(cachedLogPath))
            {
                cachedLogPath = TryFindProcessPlayerLogPath(pid, browserTrackerId);
                if (string.IsNullOrEmpty(cachedLogPath))
                    return ClientGameJoinState.Pending;
            }

            try
            {
                using var stream = new FileStream(cachedLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int maxBytes = 128 * 1024;
                if (stream.Length > maxBytes)
                    stream.Seek(-maxBytes, SeekOrigin.End);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (stream.Position > 0)
                    reader.ReadLine();

                var lines = new List<string>(256);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }

                return AnalyzeClientLogLines(lines);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[LaunchCoordinator.InspectProcessGameJoinState] {ex.Message}");
                return ClientGameJoinState.Pending;
            }
        }

        private static async Task WaitIfClientChallengedBeforeNextLaunchAsync(
            LaunchRequest request,
            int index,
            IReadOnlyList<LaunchRequest> launchList,
            int targetPid,
            LaunchProgress state,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (request.OpenHome || targetPid <= 0 || !RobloxProcessService.IsProcessAlive(targetPid))
                return;

            string? cachedLogPath = null;
            string? trackerId = request.Account.BrowserTrackerID;

            ClientGameJoinState joinState = InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);
            if (joinState == ClientGameJoinState.Pending)
            {
                // Give the client up to 4s (8 probes x 500ms) while joinState is Pending.
                // Breaks immediately as soon as the client logs JoinedGame, Challenged, ChallengeFailedToLuaApp, or ProcessExited.
                for (int probe = 0; probe < 8 && joinState == ClientGameJoinState.Pending; probe++)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    joinState = InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);
                }
            }

            if (joinState != ClientGameJoinState.Challenged && joinState != ClientGameJoinState.ChallengeFailedToLuaApp)
                return;

            string nextUsername = (index + 1 < launchList.Count) ? launchList[index + 1].Account.Username : string.Empty;

            if (joinState == ClientGameJoinState.Challenged)
            {
                Program.Logger?.Warn($"[LaunchCoordinator] Account '{request.Account.Username}' (PID {targetPid}) hit forced verification (challengedByGcs). Pausing batch before launching '{nextUsername}' to prevent WebView2 session hijacking.");

                AppDispatcher.Post(() =>
                {
                    ToastService.ShowWarning(
                        Shiro.Services.LocalizationService.Get("Launch.CaptchaRequiredTitle"),
                        Shiro.Services.LocalizationService.Format("Launch.CaptchaPromptMessage", request.Account.Username, nextUsername));
                });

                long waitStart = Stopwatch.GetTimestamp();
                TimeSpan maxVerificationWait = TimeSpan.FromSeconds(90);

                while (Stopwatch.GetElapsedTime(waitStart) < maxVerificationWait)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int remainingSecs = Math.Max(1, (int)(maxVerificationWait - Stopwatch.GetElapsedTime(waitStart)).TotalSeconds);
                    Report(progress, new LaunchProgress
                    {
                        Index = index + 1,
                        Total = launchList.Count,
                        Account = request.Account,
                        Status = LaunchOutcome.Pending,
                        Completed = false,
                        Message = Shiro.Services.LocalizationService.Format("Launch.CaptchaProgressWarning", index + 1, launchList.Count, request.Account.Username, remainingSecs, nextUsername)
                    });

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    joinState = InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);

                    if (joinState == ClientGameJoinState.JoinedGame)
                    {
                        Program.Logger?.Info($"[LaunchCoordinator] Account '{request.Account.Username}' (PID {targetPid}) completed verification and joined game. Resuming batch.");
                        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (joinState == ClientGameJoinState.ProcessExited)
                    {
                        Program.Logger?.Info($"[LaunchCoordinator] Challenged client for '{request.Account.Username}' (PID {targetPid}) was closed by user. Resuming batch.");
                        AutoReconnectService.Instance.SuppressReconnect(request.Account.Username);
                        state.Status = LaunchOutcome.Failed;
                        state.Result = Loc.Get("Launch_CaptchaClosedError", Shiro.Services.LocalizationService.Get("Launch_CaptchaClosedError"));
                        state.Message = Shiro.Services.LocalizationService.Format("Launch.CaptchaIncompleteWarning", request.Account.Username);
                        Report(progress, state);
                        return;
                    }

                    if (joinState == ClientGameJoinState.ChallengeFailedToLuaApp)
                        break;
                }
            }

            // If the challenge invalidated/abandoned back to LuaApp Home (or timed out without completing) while more accounts
            // remain in the batch, close this unverified fallback window so its WebView2 browser does not absorb the next account's cookie
            // and spawn duplicate Home tabs of the same account!
            if (joinState == ClientGameJoinState.ChallengeFailedToLuaApp || joinState == ClientGameJoinState.Challenged)
            {
                Program.Logger?.Warn($"[LaunchCoordinator] Closing unverified fallback client for '{request.Account.Username}' (PID {targetPid}) before launching '{nextUsername}' to prevent duplicate-account Home windows.");
                AutoReconnectService.Instance.SuppressReconnect(request.Account.Username);
                AutoReconnectService.Instance.SuppressReconnectByPid(targetPid);
                try { RobloxProcessService.CloseAccountRobloxProcess(request.Account); }
                catch (Exception ex) { Program.Logger?.Debug($"Close challenged fallback client failed: {ex.Message}"); }

                state.Status = LaunchOutcome.Failed;
                state.Result = Loc.Get("Launch_CaptchaRequiredError", Shiro.Services.LocalizationService.Get("Launch_CaptchaRequiredError"));
                state.Message = Shiro.Services.LocalizationService.Format("Launch.CaptchaTabClosedWarning", request.Account.Username);
                Report(progress, state);
            }
        }
    }
}
