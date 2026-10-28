#nullable enable
using System;
using System.Threading;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    internal sealed partial class LaunchCoordinator
    {
        public static event Action<LaunchProgress>? LaunchProgressChanged;

        internal static int GetSmartLaunchMinDelayMs(int? configuredSecondsOverride = null, bool includeJitter = true)
        {
            int seconds = DefaultSmartLaunchMinDelaySeconds;
            if (configuredSecondsOverride.HasValue)
            {
                seconds = configuredSecondsOverride.Value;
            }
            else if (AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.AccountJoinDelay))
            {
                try
                {
                    seconds = AccountManager.General.Get<int>(AppSettingsKeys.General.AccountJoinDelay, DefaultSmartLaunchMinDelaySeconds);
                }
                catch
                {
                    seconds = DefaultSmartLaunchMinDelaySeconds;
                }
            }
            else if (AccountManager.General != null && AccountManager.General.Exists(AppSettingsKeys.General.SmartLaunchMinDelaySeconds))
            {
                try
                {
                    seconds = AccountManager.General.Get<int>(AppSettingsKeys.General.SmartLaunchMinDelaySeconds, DefaultSmartLaunchMinDelaySeconds);
                }
                catch
                {
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

        private static bool IsSuccessfulResult(string result)
        {
            return !string.IsNullOrWhiteSpace(result) &&
                (result.Contains("Success", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result, Account.BrowserHomeResult, StringComparison.Ordinal));
        }

        private static LaunchOutcome ClassifyResult(string result)
        {
            if (IsSuccessfulResult(result))
                return LaunchOutcome.Success;

            if (string.IsNullOrWhiteSpace(result))
                return LaunchOutcome.Failed;

            if (result.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("giới hạn", StringComparison.OrdinalIgnoreCase))
                return LaunchOutcome.RateLimited;

            if (result.Contains("Invalid Authentication Ticket", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("signed you out", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                return LaunchOutcome.TicketError;

            if (result.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return LaunchOutcome.Timeout;

            return LaunchOutcome.Failed;
        }
    }
}
