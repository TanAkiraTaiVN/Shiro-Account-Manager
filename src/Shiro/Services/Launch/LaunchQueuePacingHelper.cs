#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Shared pacing, auth-failure state normalization, client-unlock waiting,
    /// and completion telemetry helpers used by <see cref="LaunchCoordinator"/>.
    /// </summary>
    internal static class LaunchQueuePacingHelper
    {
        public static void ApplyAuthFailureStatus(
            LaunchRequest request,
            LaunchProgress state,
            bool isSingleAccountBatch,
            bool sessionConfirmedValidOnTicketError)
        {
            bool wasAlreadyBanned = request.Account.IsBannedOrLocked;
            bool isTicketAuthError = !request.Account.Valid ||
                (state.Status == LaunchOutcome.TicketError && !sessionConfirmedValidOnTicketError);

            if (isTicketAuthError)
            {
                if (!wasAlreadyBanned)
                    request.Account.AccountStatusIssue = string.Empty;
                request.Account.Valid = false;
                AppServices.HostUiBridge.UpdateAccountValidityOnUi(request.Account, false);

                if (wasAlreadyBanned || request.Account.IsBannedOrLocked)
                {
                    AppServices.Logger?.Warn(LocalizationService.Format("Launch.AccountBannedLog", request.Account.Username, request.Account.AccountStatusIssue));
                    if (isSingleAccountBatch)
                    {
                        AppDispatcher.Post(() =>
                            ToastService.ShowWarning(LocalizationService.Get("Launch.AccountBannedStatus"), LocalizationService.Format("Launch.CheckBanButtonTip", request.Account.Username, request.Account.AccountStatusIssue)));
                    }
                }
                else
                {
                    AppServices.Logger?.Warn(LocalizationService.Format("Launch.SessionExpiredLog", request.Account.Username));
                    if (isSingleAccountBatch)
                    {
                        AppDispatcher.Post(() =>
                            ToastService.ShowWarning(LocalizationService.Get("Status_Expired"), LocalizationService.Format("Launch.SessionExpiredCardTip", request.Account.Username)));
                    }
                }
            }

            state.Message = state.Status == LaunchOutcome.Success
                ? $"[{state.Index}/{state.Total}] {request.Account.Username} launched."
                : (isTicketAuthError
                    ? (request.Account.IsBannedOrLocked
                        ? LocalizationService.Format("Launch.CheckBanButtonAlert", request.Account.Username, request.Account.AccountStatusIssue)
                        : LocalizationService.Format("Launch.SessionExpiredAlert", request.Account.Username))
                    : $"Warning ({request.Account.Username}): {state.Result}");
        }

        public static async Task WaitSpacingBetweenLaunchesAsync(
            LaunchRequest request,
            int displayIndex,
            int displayTotal,
            string? nextUsername,
            int targetPid,
            LaunchProgress state,
            int safeDelay,
            bool asyncJoin,
            bool smartLaunch,
            bool hasNextLaunch,
            TaskCompletionSource<bool> signal,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (smartLaunch && state.Status == LaunchOutcome.Success)
            {
                if (hasNextLaunch)
                {
                    LaunchCoordinator.Report(progress, new LaunchProgress
                    {
                        Index = displayIndex,
                        Total = displayTotal,
                        Account = request.Account,
                        Message = $"[{displayIndex}/{displayTotal}] {request.Account.Username} launched. Smart Pipeline waiting for instance unlock..."
                    });
                }

                bool unlocked = await WaitForClientUnlockWithTestSeamAsync(targetPid, cancellationToken).ConfigureAwait(false);
                int minDelayMs = unlocked
                    ? Math.Min(LaunchCoordinator.GetSmartLaunchMinDelayMs(null, includeJitter: false), safeDelay * 1000)
                    : (safeDelay * 1000);

                if (hasNextLaunch && !string.IsNullOrEmpty(nextUsername))
                {
                    string statusPrefix = unlocked ? "" : "[Fallback] ";
                    LaunchCoordinator.Report(progress, new LaunchProgress
                    {
                        Index = displayIndex,
                        Total = displayTotal,
                        Account = request.Account,
                        Status = LaunchOutcome.Success,
                        Completed = true,
                        Message = LocalizationService.Format("Launch.QueueProgressWaiting", statusPrefix, displayIndex, displayTotal, request.Account.Username, minDelayMs / 1000.0, nextUsername)
                    });
                }

                await Task.Delay(minDelayMs, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!hasNextLaunch)
                return;

            if (asyncJoin)
            {
                using (CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task timeout = Task.Delay(TimeSpan.FromSeconds(20), timeoutCancellation.Token);
                    try
                    {
                        await Task.WhenAny(signal.Task, timeout).ConfigureAwait(false);
                    }
                    finally
                    {
                        timeoutCancellation.Cancel();
                    }
                }

                if (!signal.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
                {
                    state.Status = LaunchOutcome.Timeout;
                    state.Message = $"Timeout ({request.Account.Username}): Roblox handoff did not respond; continuing with the next account.";
                    LaunchCoordinator.Report(progress, new LaunchProgress
                    {
                        Index = displayIndex,
                        Total = displayTotal,
                        Account = request.Account,
                        Status = LaunchOutcome.Timeout,
                        Result = state.Result,
                        Completed = true,
                        Message = $"Roblox handoff for {request.Account.Username} timed out; continuing with the next account."
                    });
                }
                return;
            }

            for (int remaining = safeDelay; remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LaunchCoordinator.Report(progress, new LaunchProgress
                {
                    Index = displayIndex,
                    Total = displayTotal,
                    Account = request.Account,
                    Status = state.Status,
                    Completed = true,
                    Message = $"[{displayIndex}/{displayTotal}] {request.Account.Username} processed. Waiting {remaining}s before {nextUsername}..."
                });
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        public static Task<bool> WaitForClientUnlockWithTestSeamAsync(int targetPid, CancellationToken cancellationToken)
        {
            if (LaunchCoordinator.TestWaitForClientUnlockedOverride != null)
                return LaunchCoordinator.TestWaitForClientUnlockedOverride(targetPid, TimeSpan.FromSeconds(15), cancellationToken);

            if (LaunchCoordinator.TestInspectProcessGameJoinStateOverride != null && targetPid == Environment.ProcessId)
                return Task.FromResult(true);

            return RobloxSingletonService.WaitForClientUnlockedAsync(targetPid, TimeSpan.FromSeconds(15), cancellationToken);
        }

        public static void RecordAttemptTiming(
            LaunchProgress state,
            LaunchAttemptContext metrics,
            long gapMs,
            int recent10mCount,
            LaunchRequest request,
            int pass,
            int targetPid,
            ClientGameJoinState observedState,
            CancellationToken cancellationToken)
        {
            long joinMs = observedState == ClientGameJoinState.JoinedGame && metrics.AttemptStartTicks > 0
                ? (long)Stopwatch.GetElapsedTime(metrics.AttemptStartTicks).TotalMilliseconds
                : -1;

            var entry = new LaunchTimingEntry(
                Index: state.Index,
                Total: state.Total,
                Username: request.Account.Username,
                Pass: pass,
                OpenHome: request.OpenHome,
                Outcome: state.Status,
                CaptchaState: observedState,
                RebindPerformed: metrics.RebindPerformed,
                RebindMs: metrics.RebindMs,
                TicketMs: metrics.TicketMs,
                SpawnMs: metrics.SpawnMs,
                WindowMs: -1,
                JoinMs: joinMs,
                GapMs: gapMs,
                Recent10mCount: recent10mCount);

            LaunchTimingService.Default.ScheduleCompletionLog(
                entry,
                metrics,
                targetPid,
                request.Account.BrowserTrackerID,
                cancellationToken);
        }
    }
}
