#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.Services
{
    public enum LogHeaderMatchResult { Inconclusive = 0, Matched = 1, Mismatched = 2 }

    internal enum RobloxLogLineOutcome
    {
        None, Connected, Disconnected, DataModelInitialized, DataModelPaused,
        DataModelReset, KillChallengedOrHijackedClient, KillBetaHomeMenu
    }

    internal struct RobloxLogSessionState
    {
        public long CurrentDataModel;
        public bool IsDMPaused;
        public bool IsConnected;
        public bool HasGameJoinChallenge;
        public bool HasSecurityCookieChanged;
        public string? LastLine;
        public int LastDisconnectReason;
    }

    internal partial class RobloxProcess
    {
        [GeneratedRegex(@"(?:\-b\s+|browserTrackerId[=:]\s*)(\d{6,20})", RegexOptions.IgnoreCase)]
        private static partial Regex LogTrackerIdRegex();

        internal static LogHeaderMatchResult EvaluateLogHeaderForTracker(string? logHeaderSample, string? expectedTrackerId)
        {
            if (string.IsNullOrWhiteSpace(logHeaderSample) || string.IsNullOrWhiteSpace(expectedTrackerId))
                return LogHeaderMatchResult.Inconclusive;

            Match match = LogTrackerIdRegex().Match(logHeaderSample);
            if (!match.Success || match.Groups.Count < 2 || string.IsNullOrEmpty(match.Groups[1].Value))
                return LogHeaderMatchResult.Inconclusive;

            return string.Equals(match.Groups[1].Value, expectedTrackerId.Trim(), StringComparison.Ordinal)
                ? LogHeaderMatchResult.Matched
                : LogHeaderMatchResult.Mismatched;
        }

        internal static string? ResolveExpectedTrackerIdForPid(int pid)
        {
            if (pid <= 0) return null;
            string? cmd = ProcessExtensions.GetCommandLineByPid(pid);
            if (string.IsNullOrEmpty(cmd)) return null;
            Match match = LogTrackerIdRegex().Match(cmd);
            return match.Success && match.Groups.Count >= 2 ? match.Groups[1].Value : null;
        }

        internal static LogHeaderMatchResult InspectStreamHeaderForTracker(FileStream stream, string? expectedTrackerId)
        {
            if (stream == null || !stream.CanRead || string.IsNullOrWhiteSpace(expectedTrackerId))
                return LogHeaderMatchResult.Inconclusive;

            try
            {
                int toRead = (int)Math.Min(8192L, stream.Length);
                if (toRead <= 0)
                    return LogHeaderMatchResult.Inconclusive;

                Span<byte> buf = stackalloc byte[toRead];
                stream.Seek(0, SeekOrigin.Begin);
                int read = stream.Read(buf);
                stream.Seek(0, SeekOrigin.Begin);
                if (read <= 0)
                    return LogHeaderMatchResult.Inconclusive;

                string sample = Encoding.UTF8.GetString(buf.Slice(0, read));
                return EvaluateLogHeaderForTracker(sample, expectedTrackerId);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[RobloxProcess] InspectStreamHeaderForTracker failed: {ex.Message}");
                return LogHeaderMatchResult.Inconclusive;
            }
        }
        // Supports both legacy 4-column FLog headers and modern 0.745+ 5-column headers (",Info", ",Warning", ",Error")
        [GeneratedRegex(@"\[FLog::Output\] ! Joining game '[\w\-]{36}' place \d+ at [\d\.]+")]
        internal static partial Regex JoiningGameRegex();

        [GeneratedRegex(@"Connected to server at [\d\.]+:\d+")]
        internal static partial Regex ConnectedToServerRegex();

        [GeneratedRegex(@"\[FLog::DataModelStarterModule\] Launching experience at \d+")]
        internal static partial Regex LaunchingExperienceRegex();

        [GeneratedRegex(@"\[FLog::Network\] Sending disconnect with reason:\s*(\d+)")]
        internal static partial Regex DisconnectRegex();

        // Legacy UGCGameController + modern 0.745+ UgcExperienceController ("initialized dm 0x27bce3f5100")
        [GeneratedRegex(@"\[FLog::UGCGameController\] UGCGameController, initialized DataModel\((?:0x)?([0-9A-Fa-f]+)\)")]
        internal static partial Regex LegacyDataModelInitRegex();

        [GeneratedRegex(@"\[FLog::UgcExperienceController\] UgcExperienceController:\s*join:\s*initialized dm\s+(?:0x)?([0-9A-Fa-f]+)")]
        internal static partial Regex ModernDataModelInitRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:\d+\]::start dataModel\((?:0x)?([0-9A-Fa-f]+)\)")]
        internal static partial Regex SurfaceDataModelInitRegex();

        [GeneratedRegex(@"\[FLog::UGCGameController\] UGCGameController::leave \(blocking:\d+\) dataModel\((?:0x)?([0-9A-Fa-f]+)\)")]
        internal static partial Regex LegacyDataModelStopRegex();

        [GeneratedRegex(@"\[FLog::UgcExperienceController\] UgcExperienceController:\s*(?:leave|finalize|stop:\s*begin|sendAnalyticsBeforeLeave)")]
        internal static partial Regex ModernDataModelStopRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:\d+\]::pause dataModel\((?:0x)?([0-9A-Fa-f]+)\), view\(\w+\), destroyView:\w+\.")]
        internal static partial Regex LegacyDataModelPauseRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] SurfaceController\[_:\d+\]::pause destroyView:(?:true|false|1|0)\.")]
        internal static partial Regex ModernDataModelPauseRegex();

        [GeneratedRegex(@"\[FLog::SurfaceController\] (?:SurfaceController\[_:\d+\]::)?stop\b")]
        internal static partial Regex DataModelStop2Regex();

        // Matches both legacy "returnToLuaApp:" and modern 0.745+ "returnToLuaAppInternal:"
        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] returnToLuaApp(?:Internal)?: \.\.\. App not yet initialized, returning from game\.")]
        internal static partial Regex ReturnToApp1Regex();

        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] returnToLuaApp(?:Internal)?: \.\.\. App has been initialized, returning from game\.")]
        internal static partial Regex ReturnToApp2Regex();

        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] returnToLuaApp:\s*\(stage:UGCGame\)")]
        internal static partial Regex ReturnToLuaAppStageRegex();

        [GeneratedRegex(@"\[FLog::SingleSurfaceApp\] setStage:\s*\(stage:LuaApp\)")]
        internal static partial Regex StageLuaAppRegex();

        [GeneratedRegex(@"\[FLog::NewWebView2Browser\] Security cookie value changed, updating HTTP layer cookie")]
        internal static partial Regex SecurityCookieChangedRegex();

        [GeneratedRegex(@"cookie Engine => Native set RBXEventTrackerV2 as [^\r\n]*?rbx(?:u)?id=(\d+)(?:&rbxuid=(\d*))?&browserid=(\d+)")]
        internal static partial Regex CookieEngineTrackerRegex();

        internal static bool IsJoiningGameLogLine(string line) =>
            !string.IsNullOrEmpty(line) && (JoiningGameRegex().IsMatch(line) || ConnectedToServerRegex().IsMatch(line));

        internal static bool IsLaunchingExperienceLogLine(string line) =>
            !string.IsNullOrEmpty(line) && LaunchingExperienceRegex().IsMatch(line);

        internal static bool IsGameJoinChallengedLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("challengedByGcs", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengeDisplayed\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengeInitialized\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengePageLoaded\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[FLog::NewWebView2Browser] WebView2 browser is successfully launched", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsChallengeCompletedLogLine(string line) =>
            !string.IsNullOrEmpty(line) && line.Contains("\"feature\":\"challengeCompleted\"", StringComparison.OrdinalIgnoreCase);

        internal static bool IsChallengeInvalidatedOrAbandonedLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("\"feature\":\"challengeInvalidated\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengeAbandoned\"", StringComparison.OrdinalIgnoreCase) ||
                   ReturnToLuaAppStageRegex().IsMatch(line);
        }

        internal static bool IsSecurityCookieChangedLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return SecurityCookieChangedRegex().IsMatch(line);
        }

        internal static bool TryParseCookieTrackerLine(string line, out long userId, out string browserTrackerId)
        {
            userId = 0;
            browserTrackerId = string.Empty;
            if (string.IsNullOrEmpty(line)) return false;

            Match match = CookieEngineTrackerRegex().Match(line);
            if (!match.Success || match.Groups.Count < 4)
                return false;

            string uidStr = !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value
                : match.Groups[1].Value;
            if (!string.IsNullOrEmpty(uidStr))
                long.TryParse(uidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);

            browserTrackerId = match.Groups[3].Value;
            return !string.IsNullOrEmpty(browserTrackerId) || userId > 0;
        }

        internal static bool TryParseDisconnectReason(string line, out int reason)
        {
            reason = 0;
            if (string.IsNullOrEmpty(line)) return false;

            Match match = DisconnectRegex().Match(line);
            if (!match.Success) return false;

            if (match.Groups.Count >= 2 && int.TryParse(match.Groups[1].Value, out int parsed))
            {
                reason = parsed;
            }
            return true;
        }

        internal static bool TryParseDataModelInit(string line, out long dataModelId)
        {
            dataModelId = 0;
            if (string.IsNullOrEmpty(line)) return false;

            Match match = ModernDataModelInitRegex().Match(line);
            if (!match.Success)
                match = SurfaceDataModelInitRegex().Match(line);
            if (!match.Success)
                match = LegacyDataModelInitRegex().Match(line);

            if (match.Success &&
                match.Groups.Count >= 2 &&
                long.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out dataModelId) &&
                dataModelId > 0)
            {
                return true;
            }

            return false;
        }

        internal static bool IsReturnToLuaAppLine(string line, bool hasActiveGameDataModel)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (ReturnToApp1Regex().IsMatch(line) || ReturnToApp2Regex().IsMatch(line) || ReturnToLuaAppStageRegex().IsMatch(line))
                return true;

            // In modern 0.745+ clients, transitioning out of an active UGC experience back to the desktop app
            // emits "[FLog::SingleSurfaceApp] setStage: (stage:LuaApp)".
            return hasActiveGameDataModel && StageLuaAppRegex().IsMatch(line);
        }

        internal static bool TryOpenCandidateLog(
            FileInfo file,
            string? expectedTrackerId,
            ref FileInfo? inconclusiveFile,
            ref FileStream? inconclusiveStream,
            out FileStream? matchedStream)
        {
            matchedStream = null;
            try
            {
                FileStream stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var matchResult = InspectStreamHeaderForTracker(stream, expectedTrackerId);
                if (matchResult == LogHeaderMatchResult.Mismatched)
                {
                    stream.Dispose();
                    return false;
                }

                if (matchResult == LogHeaderMatchResult.Matched || string.IsNullOrEmpty(expectedTrackerId))
                {
                    inconclusiveStream?.Dispose();
                    inconclusiveStream = null;
                    inconclusiveFile = null;
                    matchedStream = stream;
                    return true;
                }

                if (inconclusiveStream == null)
                {
                    inconclusiveFile = file;
                    inconclusiveStream = stream;
                }
                else
                {
                    stream.Dispose();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use exclusively or not ready yet, check next candidate
            }

            return false;
        }

        internal static async Task<bool> ContainsRecentNativeAuthRateLimitInLogAsync(
            string logPath,
            DateTime minimumTimestampUtc,
            CancellationToken cancellationToken)
        {
            DateTime modifiedUtc;
            try { modifiedUtc = File.GetLastWriteTimeUtc(logPath); }
            catch (Exception ex) { AppServices.Logger.Debug($"GetLastWriteTimeUtc failed: {ex.Message}"); return false; }

            if (modifiedUtc < minimumTimestampUtc)
                return false;

            var logLines = new List<string>(256);
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int tailBytes = 64 * 1024;
                if (stream.Length > tailBytes)
                {
                    stream.Seek(-tailBytes, SeekOrigin.End);
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
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
            catch (Exception ex) { AppServices.Logger.Debug($"Read logLines failed: {ex.Message}"); return false; }

            DateTime recentTimestampUtc = DateTime.MinValue;
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
                        return true;
                }
            }

            return false;
        }

        internal static bool TryGetRobloxLogTimestampUtc(string line, out DateTime timestampUtc)
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

        internal static bool IsRobloxRateLimitedLogLine(string line)
        {
            return !string.IsNullOrWhiteSpace(line) &&
                (line.Contains("status:429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("status: 429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("statusCode: 429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("statusCode:429", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase));
        }

        internal static RobloxLogLineOutcome AdvanceLogSessionState(
            string line, ref RobloxLogSessionState state, bool verifyDataModel, bool exitOnBeta)
        {
            if (string.IsNullOrEmpty(line))
            {
                state.LastLine = line;
                return RobloxLogLineOutcome.None;
            }

            if (IsJoiningGameLogLine(line))
            {
                state.IsConnected = true;
                state.HasGameJoinChallenge = false;
                state.HasSecurityCookieChanged = false;
                return RobloxLogLineOutcome.Connected;
            }

            if (IsChallengeCompletedLogLine(line)) state.HasGameJoinChallenge = false;
            if (!state.IsConnected && IsGameJoinChallengedLogLine(line)) state.HasGameJoinChallenge = true;
            if (IsSecurityCookieChangedLogLine(line)) state.HasSecurityCookieChanged = true;

            if (TryParseDisconnectReason(line, out int reason))
            {
                state.IsConnected = false;
                state.LastDisconnectReason = reason;
                return RobloxLogLineOutcome.Disconnected;
            }

            if (state.CurrentDataModel <= 0 && TryParseDataModelInit(line, out long parsedDataModel))
            {
                state.CurrentDataModel = parsedDataModel;
                state.IsDMPaused = false;
                return RobloxLogLineOutcome.DataModelInitialized;
            }

            Match legacyPause = LegacyDataModelPauseRegex().Match(line);
            if ((legacyPause.Success &&
                 long.TryParse(legacyPause.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long pausedDm) &&
                 pausedDm == state.CurrentDataModel) ||
                (state.CurrentDataModel > 0 && ModernDataModelPauseRegex().IsMatch(line)))
            {
                state.IsDMPaused = true;
                return RobloxLogLineOutcome.DataModelPaused;
            }

            if (IsReturnToLuaAppLine(line, hasActiveGameDataModel: state.CurrentDataModel > 0))
            {
                if ((state.HasGameJoinChallenge && !state.IsConnected) ||
                    ((state.HasGameJoinChallenge || state.CurrentDataModel > 0) && state.HasSecurityCookieChanged))
                {
                    state.CurrentDataModel = -1;
                    state.LastLine = line;
                    return RobloxLogLineOutcome.KillChallengedOrHijackedClient;
                }

                bool shouldReset = !verifyDataModel || string.IsNullOrEmpty(state.LastLine) || state.IsDMPaused;
                if (!shouldReset && state.LastLine is string lastLine)
                {
                    Match legacyStop = LegacyDataModelStopRegex().Match(lastLine);
                    bool legacyStoppedCurrent = legacyStop.Success &&
                        long.TryParse(legacyStop.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long stoppedDm) &&
                        stoppedDm == state.CurrentDataModel;
                    bool modernStoppedCurrent = state.CurrentDataModel > 0 &&
                        (ModernDataModelStopRegex().IsMatch(lastLine) || StageLuaAppRegex().IsMatch(line) || ReturnToLuaAppStageRegex().IsMatch(line));
                    shouldReset = legacyStoppedCurrent || modernStoppedCurrent;
                }

                if (shouldReset)
                {
                    state.CurrentDataModel = -1;
                    state.LastLine = line;
                    return exitOnBeta ? RobloxLogLineOutcome.KillBetaHomeMenu : RobloxLogLineOutcome.DataModelReset;
                }
            }

            if (verifyDataModel && state.IsDMPaused && DataModelStop2Regex().IsMatch(line))
            {
                state.CurrentDataModel = -1;
                state.LastLine = line;
                return RobloxLogLineOutcome.DataModelReset;
            }

            state.LastLine = line;
            return RobloxLogLineOutcome.None;
        }
    }
}
