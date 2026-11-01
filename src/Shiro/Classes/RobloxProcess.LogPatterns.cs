#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Shiro.Classes
{
    internal partial class RobloxProcess
    {
        // Supports both legacy 4-column FLog headers and modern 0.745+ 5-column headers (",Info", ",Warning", ",Error")
        [GeneratedRegex(@"\[FLog::Output\] ! Joining game '[\w+\-]{36}' place \d+ at [\d+\.]+")]
        internal static partial Regex JoiningGameRegex();

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

        internal static bool IsJoiningGameLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return JoiningGameRegex().IsMatch(line) || LaunchingExperienceRegex().IsMatch(line);
        }

        internal static bool IsGameJoinChallengedLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("challengedByGcs", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengeDisplayed\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("\"feature\":\"challengeInitialized\"", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[FLog::NewWebView2Browser] WebView2 browser is successfully launched", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsChallengeCompletedLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            return line.Contains("\"feature\":\"challengeCompleted\"", StringComparison.OrdinalIgnoreCase);
        }

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
    }
}
