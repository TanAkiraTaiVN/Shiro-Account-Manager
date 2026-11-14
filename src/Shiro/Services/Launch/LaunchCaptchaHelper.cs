#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Encapsulates Captcha (challengedByGcs) probe, client closure, cooldown pacing,
    /// and setting lookup for queued multi-account launches.
    /// </summary>
    internal static class LaunchCaptchaHelper
    {
        public static TimeSpan DefaultCooldown => TimeSpan.FromSeconds(75);

        public static bool IsWaitForCaptchaEnabled()
        {
            return AppServices.GeneralSettings.Exists(AppSettingsKeys.General.WaitForCaptchaInQueue)
                && AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.WaitForCaptchaInQueue);
        }

        public static TimeSpan GetCooldownDuration()
        {
            return LaunchCoordinator.TestCaptchaRetryCooldownOverride ?? DefaultCooldown;
        }

        public static void CloseChallengedClient(Account account, int targetPid)
        {
            AutoReconnectService.Instance.SuppressReconnect(account.Username);
            if (targetPid > 0)
                AutoReconnectService.Instance.SuppressReconnectByPid(targetPid);

            try
            {
                if (LaunchCoordinator.TestCloseChallengedProcessOverride != null)
                {
                    LaunchCoordinator.TestCloseChallengedProcessOverride(account);
                }
                else
                {
                    RobloxProcessService.CloseAccountRobloxProcess(account);
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Close challenged fallback client failed: {ex.Message}");
            }
        }

        public static async Task<ClientGameJoinState> ProbeClientJoinStateAsync(
            Account account,
            int targetPid,
            CancellationToken cancellationToken)
        {
            if (targetPid <= 0 || !RobloxProcessService.IsProcessAlive(targetPid))
                return ClientGameJoinState.Pending;

            string? cachedLogPath = null;
            string? trackerId = account.BrowserTrackerID;

            ClientGameJoinState joinState = LaunchCoordinator.InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);
            if (joinState != ClientGameJoinState.Pending)
                return joinState;

            for (int probe = 0; probe < 8 && joinState == ClientGameJoinState.Pending; probe++)
            {
                await Task.Delay(LaunchCoordinator.ChallengeProbeDelayMs, cancellationToken).ConfigureAwait(false);
                joinState = LaunchCoordinator.InspectProcessGameJoinState(targetPid, trackerId, ref cachedLogPath);
            }

            return joinState;
        }

        public static async Task<ClientGameJoinState> InspectAutoDeferChallengeAsync(
            LaunchRequest request,
            int targetPid,
            LaunchProgress state,
            bool isRetryPass,
            int displayIndex,
            int displayTotal,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (request.OpenHome || targetPid <= 0 || !RobloxProcessService.IsProcessAlive(targetPid))
                return ClientGameJoinState.Pending;

            ClientGameJoinState joinState = await ProbeClientJoinStateAsync(request.Account, targetPid, cancellationToken).ConfigureAwait(false);
            if (joinState != ClientGameJoinState.Challenged && joinState != ClientGameJoinState.ChallengeFailedToLuaApp)
            {
                return joinState;
            }

            state.CaptchaDetected = true;
            AppServices.Logger?.Warn(
                $"[LaunchCoordinator] Account '{request.Account.Username}' (PID {targetPid}, pass={(isRetryPass ? 2 : 1)}) hit forced verification ({joinState}). Closing unverified client immediately.");

            CloseChallengedClient(request.Account, targetPid);

            if (isRetryPass)
            {
                state.CaptchaFailedAfterRetry = true;
                state.Status = LaunchOutcome.Failed;
                state.Result = LocalizationService.Get("Launch.CaptchaRetryFailedError");
                state.Message = LocalizationService.Format("Launch.CaptchaRetryFailedWarning", request.Account.Username);
            }
            else
            {
                state.Status = LaunchOutcome.Failed;
                state.Result = LocalizationService.Get("Launch_CaptchaRequiredError");
                state.Message = LocalizationService.Format(
                    "Launch.CaptchaDeferredWarning",
                    displayIndex,
                    displayTotal,
                    request.Account.Username);
            }

            LaunchCoordinator.Report(progress, state);
            return joinState;
        }

        public static async Task WaitCooldownWithProgressAsync(
            int deferredCount,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken)
        {
            TimeSpan cooldown = GetCooldownDuration();
            if (cooldown <= TimeSpan.Zero)
                return;

            AppServices.Logger?.Info(
                $"[LaunchCoordinator] Waiting {cooldown.TotalSeconds:0.#}s cooldown before retrying {deferredCount} Captcha-deferred account(s).");

            if (cooldown < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(cooldown, cancellationToken).ConfigureAwait(false);
                return;
            }

            long waitStart = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(waitStart) < cooldown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan remaining = cooldown - Stopwatch.GetElapsedTime(waitStart);
                int remainingSecs = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));

                LaunchCoordinator.Report(progress, new LaunchProgress
                {
                    Index = 0,
                    Total = deferredCount,
                    Status = LaunchOutcome.Pending,
                    Completed = false,
                    Message = LocalizationService.Format("Launch.CaptchaCooldownWaiting", remainingSecs, deferredCount)
                });

                TimeSpan step = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
                await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
