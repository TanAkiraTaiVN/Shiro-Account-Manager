#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal sealed partial class LaunchCoordinator
    {
        private readonly record struct LaunchAttemptOutcome(
            LaunchProgress State,
            LaunchAttemptContext Metrics,
            long GapMs,
            int Recent10mCount);

        private async Task<LaunchAttemptOutcome> ExecuteAttemptWithRetriesAsync(
            LaunchRequest request,
            int displayIndex,
            int displayTotal,
            bool isSingleAccountBatch,
            int pass,
            string? progressPrefix,
            Action<LaunchProgress>? progress,
            TaskCompletionSource<bool> signal,
            CancellationToken cancellationToken)
        {
            if (RobloxRateLimitCoordinator.Instance.IsBackingOff)
            {
                double backoffSecs = Math.Max(1.0, RobloxRateLimitCoordinator.Instance.RemainingBackoff.TotalSeconds);
                Report(progress, new LaunchProgress
                {
                    Index = displayIndex,
                    Total = displayTotal,
                    Account = request.Account,
                    Status = LaunchOutcome.RateLimited,
                    Message = LocalizationService.Format("Launch.RateLimitedIpWaiting", displayIndex, displayTotal, backoffSecs, request.Account.Username)
                });
                await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
            }

            LaunchProgress state = new LaunchProgress
            {
                Index = displayIndex,
                Total = displayTotal,
                Account = request.Account,
                Status = LaunchOutcome.Pending,
                CaptchaDetected = pass > 1,
                CaptchaRetried = pass > 1,
                Message = progressPrefix ?? $"[{displayIndex}/{displayTotal}] Launching {request.Account.Username}..."
            };
            Report(progress, state);

            var metrics = new LaunchAttemptContext();
            var (gapMs, recent10mCount) = LaunchTimingService.Default.RecordStart();
            LaunchTimingService.CurrentAttempt = metrics;

            LaunchResult launchResult;
            using var signalScope = LaunchQueuePacingHelper.BeginCoordinatorSignalScope();
            try
            {
                try
                {
                    launchResult = await executeLaunch(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    launchResult = LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox for {request.Account.Username}\n{exception.Message}");
                    AppServices.Logger?.Error($"Unexpected launch error for {request.Account.Username}: {exception}");
                }

                state.Result = launchResult.Message;
                state.Status = launchResult.Outcome;

                for (int retry = 1;
                     retry <= MaxRateLimitRetries && state.Status == LaunchOutcome.RateLimited && !cancellationToken.IsCancellationRequested;
                     retry++)
                {
                    RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication);
                    double waitSecs = Math.Max(1.0, RobloxRateLimitCoordinator.Instance.RemainingBackoff.TotalSeconds);
                    AppServices.Logger?.Warn($"[LaunchCoordinator] Account {request.Account.Username} was rate limited during launch. Waiting {waitSecs:0.0}s before retry {retry}/{MaxRateLimitRetries}...");

                    Report(progress, new LaunchProgress
                    {
                        Index = displayIndex,
                        Total = displayTotal,
                        Account = request.Account,
                        Status = LaunchOutcome.RateLimited,
                        Message = LocalizationService.Format("Launch.RateLimitedRetryWaiting", displayIndex, displayTotal, request.Account.Username, waitSecs, retry, MaxRateLimitRetries)
                    });

                    await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        try { RobloxProcessService.CloseAccountRobloxProcess(request.Account); }
                        catch (Exception ex) { AppServices.Logger?.Debug($"Process cleanup ignored: {ex.Message}"); }

                        launchResult = await executeLaunch(request, cancellationToken).ConfigureAwait(false);
                        state.Result = launchResult.Message;
                        state.Status = launchResult.Outcome;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception retryEx)
                    {
                        AppServices.Logger?.Error($"[LaunchCoordinator] Retry #{retry} failed for {request.Account.Username}: {retryEx}");
                        state.Status = LaunchOutcome.Failed;
                        state.Result = $"ERROR: Failed to launch Roblox for {request.Account.Username}\n{retryEx.Message}";
                    }
                }

                bool sessionConfirmedValidOnTicketError = false;
                if (state.Status == LaunchOutcome.TicketError && !cancellationToken.IsCancellationRequested)
                {
                    var recovery = await TryRecoverTicketErrorAsync(request, launchResult, cancellationToken).ConfigureAwait(false);
                    launchResult = recovery.Result;
                    state.Result = launchResult.Message;
                    state.Status = launchResult.Outcome;
                    sessionConfirmedValidOnTicketError = recovery.SessionConfirmedValid;
                }

                if (state.Status == LaunchOutcome.Success)
                    RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Authentication);

                state.Completed = true;
                LaunchQueuePacingHelper.ApplyAuthFailureStatus(request, state, isSingleAccountBatch, sessionConfirmedValidOnTicketError);

                if (state.Status != LaunchOutcome.Success)
                    signal.TrySetResult(false);

                signalScope.FlushIfRequested();
                return new LaunchAttemptOutcome(state, metrics, gapMs, recent10mCount);
            }
            finally
            {
                LaunchTimingService.CurrentAttempt = null;
            }
        }

        private async Task<int> RunPass1ItemAsync(
            LaunchRequest request,
            int index,
            List<LaunchRequest> launchList,
            int totalSnapshot,
            int safeDelay,
            bool asyncJoin,
            Action<LaunchProgress>? progress,
            List<LaunchProgress> results,
            List<(LaunchRequest Request, int ResultIndex)> deferredCaptcha,
            int lastSpawnedPid,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (stateLock)
                nextSignal = signal;

            var attempt = await ExecuteAttemptWithRetriesAsync(
                request, index + 1, totalSnapshot, totalSnapshot == 1, pass: 1,
                progressPrefix: null, progress, signal, cancellationToken).ConfigureAwait(false);

            LaunchProgress state = attempt.State;
            int targetPid = state.Status == LaunchOutcome.Success ? request.Account.LastLaunchedProcessId : 0;
            if (targetPid > 0)
                lastSpawnedPid = targetPid;

            results.Add(state);
            int resultSlot = results.Count - 1;
            Report(progress, state);

            string? nextUsername = null;
            bool hasMoreInPass;
            int updatedTotal;
            lock (stateLock)
            {
                updatedTotal = launchList.Count;
                hasMoreInPass = index < updatedTotal - 1;
                if (hasMoreInPass)
                    nextUsername = launchList[index + 1].Account.Username;
            }

            bool smartLaunch = !AppServices.GeneralSettings.Exists(AppSettingsKeys.General.SmartFastLaunch)
                || AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.SmartFastLaunch);
            bool waitForCaptcha = LaunchCaptchaHelper.IsWaitForCaptchaEnabled();
            bool shouldCheckLastCaptcha = !hasMoreInPass && !waitForCaptcha && updatedTotal > 1 && !request.OpenHome && targetPid > 0 && state.Status == LaunchOutcome.Success;

            ClientGameJoinState observedState = ClientGameJoinState.Pending;
            if ((hasMoreInPass && !cancellationToken.IsCancellationRequested) || shouldCheckLastCaptcha)
            {
                await LaunchQueuePacingHelper.WaitSpacingBetweenLaunchesAsync(
                    request, index + 1, updatedTotal, nextUsername, targetPid, state,
                    safeDelay, asyncJoin, smartLaunch, hasMoreInPass, signal, progress, cancellationToken).ConfigureAwait(false);

                if (!request.OpenHome && targetPid > 0 && state.Status == LaunchOutcome.Success && !cancellationToken.IsCancellationRequested)
                {
                    if (waitForCaptcha)
                    {
                        if (hasMoreInPass)
                        {
                            await WaitIfClientChallengedBeforeNextLaunchAsync(
                                request, index, launchList, targetPid, state, progress, cancellationToken).ConfigureAwait(false);
                            observedState = state.CaptchaDetected ? ClientGameJoinState.Challenged : ClientGameJoinState.JoinedGame;
                        }
                    }
                    else
                    {
                        observedState = await LaunchCaptchaHelper.InspectAutoDeferChallengeAsync(
                            request, targetPid, state, isRetryPass: false,
                            index + 1, updatedTotal, progress, cancellationToken).ConfigureAwait(false);

                        if (observedState == ClientGameJoinState.Challenged ||
                            observedState == ClientGameJoinState.ChallengeFailedToLuaApp)
                        {
                            deferredCaptcha.Add((request, resultSlot));
                        }
                    }
                }
            }

            LaunchQueuePacingHelper.RecordAttemptTiming(
                state, attempt.Metrics, attempt.GapMs, attempt.Recent10mCount,
                request, pass: 1, targetPid, observedState, cancellationToken);
            return lastSpawnedPid;
        }

        private async Task<int> RunCaptchaRetryPassAsync(
            List<(LaunchRequest Request, int ResultIndex)> retryItems,
            int safeDelay,
            bool asyncJoin,
            Action<LaunchProgress>? progress,
            List<LaunchProgress> results,
            int lastSpawnedPid,
            CancellationToken cancellationToken)
        {
            await LaunchCaptchaHelper.WaitCooldownWithProgressAsync(retryItems.Count, progress, cancellationToken).ConfigureAwait(false);

            bool smartLaunch = !AppServices.GeneralSettings.Exists(AppSettingsKeys.General.SmartFastLaunch)
                || AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.SmartFastLaunch);

            for (int r = 0; r < retryItems.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (request, resultIndex) = retryItems[r];
                TaskCompletionSource<bool> signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (stateLock)
                    nextSignal = signal;

                string retryHeader = LocalizationService.Format("Launch.CaptchaRetryingAccount", r + 1, retryItems.Count, request.Account.Username);
                var attempt = await ExecuteAttemptWithRetriesAsync(
                    request, r + 1, retryItems.Count, isSingleAccountBatch: false, pass: 2,
                    progressPrefix: retryHeader, progress, signal, cancellationToken).ConfigureAwait(false);

                LaunchProgress state = attempt.State;
                state.CaptchaDetected = true;
                state.CaptchaRetried = true;

                int targetPid = state.Status == LaunchOutcome.Success ? request.Account.LastLaunchedProcessId : 0;
                if (targetPid > 0)
                    lastSpawnedPid = targetPid;

                bool hasMoreRetries = r < retryItems.Count - 1;
                string? nextRetryUser = hasMoreRetries ? retryItems[r + 1].Request.Account.Username : null;
                ClientGameJoinState observedState = ClientGameJoinState.Pending;

                if (state.Status == LaunchOutcome.Success && !request.OpenHome && targetPid > 0 && !cancellationToken.IsCancellationRequested)
                {
                    await LaunchQueuePacingHelper.WaitSpacingBetweenLaunchesAsync(
                        request, r + 1, retryItems.Count, nextRetryUser, targetPid, state,
                        safeDelay, asyncJoin, smartLaunch, hasNextLaunch: hasMoreRetries,
                        signal, progress, cancellationToken).ConfigureAwait(false);

                    observedState = await LaunchCaptchaHelper.InspectAutoDeferChallengeAsync(
                        request, targetPid, state, isRetryPass: true,
                        r + 1, retryItems.Count, progress, cancellationToken).ConfigureAwait(false);
                }

                results[resultIndex] = state;
                Report(progress, state);
                LaunchQueuePacingHelper.RecordAttemptTiming(
                    state, attempt.Metrics, attempt.GapMs, attempt.Recent10mCount,
                    request, pass: 2, targetPid, observedState, cancellationToken);
            }

            return lastSpawnedPid;
        }
    }
}
