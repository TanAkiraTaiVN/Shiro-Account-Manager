#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Owns the only active multi-account launch queue. The Roblox protocol
    /// still signals the next account through AppSettings.SignalNextLaunchAccount(),
    /// but all waiting, cancellation and duplicate-launch protection lives here.
    /// </summary>
    internal sealed partial class LaunchCoordinator : ILaunchCoordinator
    {
        private readonly SemaphoreSlim queueGate = new SemaphoreSlim(1, 1);
        private readonly Func<LaunchRequest, CancellationToken, Task<LaunchResult>> executeLaunch;
        private readonly System.Threading.Lock stateLock = new();
        private CancellationTokenSource? activeCancellation;
        private TaskCompletionSource<bool>? nextSignal;
        private TaskCompletionSource<bool>? idleCompletion;
        private bool disposed;

        public const int MaxRateLimitRetries = 2;
        public const int DefaultSmartLaunchMinDelaySeconds = 2;

        public bool IsRunning
        {
            get
            {
                lock (stateLock)
                    return activeCancellation != null;
            }
        }

        public LaunchCoordinator(Func<LaunchRequest, CancellationToken, Task<LaunchResult>> executeLaunch)
        {
            this.executeLaunch = executeLaunch ?? throw new ArgumentNullException(nameof(executeLaunch));
            GlobalCancelRequested += Cancel;
        }

        public LaunchCoordinator(Func<LaunchRequest, CancellationToken, Task<string>> executeLaunch)
            : this(async (req, ct) =>
            {
                if (executeLaunch == null) throw new ArgumentNullException(nameof(executeLaunch));
                string legacy = await executeLaunch(req, ct).ConfigureAwait(false);
                return LaunchResult.FromLegacy(legacy);
            })
        {
        }

        public async Task<LaunchBatchResult> EnqueueAsync(
            IReadOnlyList<LaunchRequest> requests,
            int delaySeconds,
            bool asyncJoin,
            Action<LaunchProgress>? progress,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (stateLock)
                if (disposed)
                    throw new ObjectDisposedException(nameof(LaunchCoordinator));

            List<LaunchRequest> launchList = (requests ?? Array.Empty<LaunchRequest>())
                .Where(request => request?.Account != null)
                .ToList();
            if (launchList.Count == 0)
                return new LaunchBatchResult(Array.Empty<LaunchProgress>(), false);

            if (!await queueGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("A Roblox launch queue is already running.");

            CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (stateLock)
            {
                if (disposed)
                {
                    linkedCancellation.Dispose();
                    queueGate.Release();
                    throw new ObjectDisposedException(nameof(LaunchCoordinator));
                }

                activeCancellation = linkedCancellation;
                nextSignal = null;
                idleCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            List<LaunchProgress> results = new List<LaunchProgress>(launchList.Count);
            int safeDelay = Math.Max(
                AppSettings.MinimumAccountJoinDelaySeconds,
                Math.Min(AppSettings.MaximumAccountJoinDelaySeconds, delaySeconds));
            int lastSpawnedPid = 0;

            using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 12_000);
            try
            {
                for (int index = 0; index < launchList.Count; index++)
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    LaunchRequest request = launchList[index];

                    if (RobloxRateLimitCoordinator.Instance.IsBackingOff)
                    {
                        double backoffSecs = Math.Max(1.0, RobloxRateLimitCoordinator.Instance.RemainingBackoff.TotalSeconds);
                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Status = LaunchOutcome.RateLimited,
                            Message = Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_239", index + 1, launchList.Count, backoffSecs, request.Account.Username)
                        });
                        await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(linkedCancellation.Token).ConfigureAwait(false);
                    }

                    LaunchProgress state = new LaunchProgress
                    {
                        Index = index + 1,
                        Total = launchList.Count,
                        Account = request.Account,
                        Status = LaunchOutcome.Pending,
                        Message = $"[{index + 1}/{launchList.Count}] Launching {request.Account.Username}..."
                    };
                    Report(progress, state);

                    TaskCompletionSource<bool> signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (stateLock)
                        nextSignal = signal;

                    LaunchResult launchResult;
                    try
                    {
                        launchResult = await executeLaunch(request, linkedCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        launchResult = LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox for {request.Account.Username}\n{exception.Message}");
                        Program.Logger?.Error($"Unexpected launch error for {request.Account.Username}: {exception}");
                    }

                    state.Result = launchResult.Message;
                    state.Status = launchResult.Outcome;

                    for (int retry = 1;
                         retry <= MaxRateLimitRetries && state.Status == LaunchOutcome.RateLimited && !linkedCancellation.IsCancellationRequested;
                         retry++)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication);
                        double waitSecs = Math.Max(1.0, RobloxRateLimitCoordinator.Instance.RemainingBackoff.TotalSeconds);
                        Program.Logger?.Warn($"[LaunchCoordinator] Account {request.Account.Username} was rate limited during launch. Waiting {waitSecs:0.0}s before retry {retry}/{MaxRateLimitRetries}...");

                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Status = LaunchOutcome.RateLimited,
                            Message = Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_240", index + 1, launchList.Count, request.Account.Username, waitSecs, retry, MaxRateLimitRetries)
                        });

                        await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(linkedCancellation.Token).ConfigureAwait(false);
                        try
                        {
                            try { RobloxProcessService.CloseAccountRobloxProcess(request.Account); }
                            catch (Exception ex) { Program.Logger?.Debug($"Process cleanup ignored: {ex.Message}"); }

                            launchResult = await executeLaunch(request, linkedCancellation.Token).ConfigureAwait(false);
                            state.Result = launchResult.Message;
                            state.Status = launchResult.Outcome;
                        }
                        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception retryEx)
                        {
                            Program.Logger?.Error($"[LaunchCoordinator] Retry #{retry} failed for {request.Account.Username}: {retryEx}");
                            state.Status = LaunchOutcome.Failed;
                            state.Result = $"ERROR: Failed to launch Roblox for {request.Account.Username}\n{retryEx.Message}";
                        }
                    }

                    if (state.Status == LaunchOutcome.Success)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Authentication);
                        if (request.Account.LastLaunchedProcessId > 0)
                        {
                            lastSpawnedPid = request.Account.LastLaunchedProcessId;
                        }
                    }

                    state.Completed = true;

                    bool wasAlreadyBanned = request.Account.IsBannedOrLocked;
                    bool isTicketAuthError = !request.Account.Valid || state.Status == LaunchOutcome.TicketError;

                    if (isTicketAuthError)
                    {
                        if (!wasAlreadyBanned)
                            request.Account.AccountStatusIssue = string.Empty;
                        request.Account.Valid = false;
                        AppSettings.UpdateAccountValidityOnUi(request.Account, false);

                        if (wasAlreadyBanned || request.Account.IsBannedOrLocked)
                        {
                            Program.Logger?.Warn(Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_241", request.Account.Username, request.Account.AccountStatusIssue));
                            if (launchList.Count == 1)
                            {
                                AppDispatcher.Post(() =>
                                {
                                    ToastService.ShowWarning(Shiro.Services.LocalizationService.Get("Auto_LaunchCoordinato_242"), Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_243", request.Account.Username, request.Account.AccountStatusIssue));
                                });
                            }
                        }
                        else
                        {
                            Program.Logger?.Warn(Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_244", request.Account.Username));
                            if (launchList.Count == 1)
                            {
                                AppDispatcher.Post(() =>
                                {
                                    ToastService.ShowWarning(Shiro.Services.LocalizationService.Get("Status_Expired"), Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_245", request.Account.Username));
                                });
                            }
                        }
                    }

                    state.Message = (state.Status == LaunchOutcome.Success)
                        ? $"[{index + 1}/{launchList.Count}] {request.Account.Username} launched."
                        : (isTicketAuthError
                            ? (request.Account.IsBannedOrLocked
                                ? Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_246", request.Account.Username, request.Account.AccountStatusIssue)
                                : Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_247", request.Account.Username))
                            : $"Warning ({request.Account.Username}): {state.Result}");
                    results.Add(state);
                    Report(progress, state);

                    if (index >= launchList.Count - 1 || linkedCancellation.IsCancellationRequested)
                        continue;

                    if (state.Status != LaunchOutcome.Success)
                        signal.TrySetResult(false);

                    bool smartLaunch = !AppSettings.General.Exists(AppSettingsKeys.General.SmartFastLaunch) || AppSettings.General.Get<bool>(AppSettingsKeys.General.SmartFastLaunch);

                    if (smartLaunch && state.Status == LaunchOutcome.Success)
                    {
                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Message = $"[{index + 1}/{launchList.Count}] {request.Account.Username} launched. Smart Pipeline waiting for instance unlock..."
                        });

                        int targetPid = request.Account.LastLaunchedProcessId;
                        bool unlocked = await RobloxSingletonService.WaitForClientUnlockedAsync(
                            targetPid,
                            TimeSpan.FromSeconds(15),
                            linkedCancellation.Token).ConfigureAwait(false);

                        int minDelayMs = unlocked
                            ? Math.Min(GetSmartLaunchMinDelayMs(null, includeJitter: false), safeDelay * 1000)
                            : (safeDelay * 1000);
                        string statusPrefix = unlocked ? "" : "[Fallback] ";
                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Status = LaunchOutcome.Success,
                            Completed = true,
                            Message = Shiro.Services.LocalizationService.Format("Auto_LaunchCoordinato_248", statusPrefix, index + 1, launchList.Count, request.Account.Username, minDelayMs / 1000.0, launchList[index + 1].Account.Username)
                        });

                        await Task.Delay(minDelayMs, linkedCancellation.Token).ConfigureAwait(false);
                        await WaitIfClientChallengedBeforeNextLaunchAsync(
                            request,
                            index,
                            launchList,
                            targetPid,
                            state,
                            progress,
                            linkedCancellation.Token).ConfigureAwait(false);
                    }
                    else if (asyncJoin)
                    {
                        using (CancellationTokenSource timeoutCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(linkedCancellation.Token))
                        {
                            Task timeout = Task.Delay(
                                TimeSpan.FromSeconds(20),
                                timeoutCancellation.Token);
                            try
                            {
                                await Task.WhenAny(signal.Task, timeout).ConfigureAwait(false);
                            }
                            finally
                            {
                                timeoutCancellation.Cancel();
                            }
                        }

                        if (!signal.Task.IsCompleted && !linkedCancellation.IsCancellationRequested)
                        {
                            state.Status = LaunchOutcome.Timeout;
                            state.Message = $"Timeout ({request.Account.Username}): Roblox handoff did not respond; continuing with the next account.";
                            Report(progress, new LaunchProgress
                            {
                                Index = index + 1,
                                Total = launchList.Count,
                                Account = request.Account,
                                Status = LaunchOutcome.Timeout,
                                Result = state.Result,
                                Completed = true,
                                Message = $"Roblox handoff for {request.Account.Username} timed out; continuing with the next account."
                            });
                        }
                    }
                    else
                    {
                        for (int remaining = safeDelay; remaining > 0; remaining--)
                        {
                            linkedCancellation.Token.ThrowIfCancellationRequested();
                            Report(progress, new LaunchProgress
                            {
                                Index = index + 1,
                                Total = launchList.Count,
                                Account = request.Account,
                                Status = state.Status,
                                Completed = true,
                                Message = $"[{index + 1}/{launchList.Count}] {request.Account.Username} processed. Waiting {remaining}s before {launchList[index + 1].Account.Username}..."
                            });
                            await Task.Delay(1000, linkedCancellation.Token).ConfigureAwait(false);
                        }
                    }
                }

                return new LaunchBatchResult(results, false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                for (int index = results.Count; index < launchList.Count; index++)
                {
                    LaunchRequest request = launchList[index];
                    LaunchProgress cancelled = new LaunchProgress
                    {
                        Index = index + 1,
                        Total = launchList.Count,
                        Account = request.Account,
                        Status = LaunchOutcome.Cancelled,
                        Result = "CANCELLED",
                        Message = $"[{index + 1}/{launchList.Count}] {request.Account.Username} was cancelled.",
                        Completed = true
                    };
                    results.Add(cancelled);
                    Report(progress, cancelled);
                }

                Report(progress, new LaunchProgress
                {
                    Index = results.Count,
                    Total = launchList.Count,
                    Status = LaunchOutcome.Cancelled,
                    Message = "Launch queue cancelled.",
                    Completed = true
                });
                return new LaunchBatchResult(results, true);
            }
            finally
            {
                cookieGuard.Dispose();
                if (lastSpawnedPid > 0)
                {
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(lastSpawnedPid);
                }

                Report(null, new LaunchProgress { Completed = true });
                TaskCompletionSource<bool>? idleToComplete;
                lock (stateLock)
                {
                    nextSignal = null;
                    activeCancellation = null;
                    idleToComplete = idleCompletion;
                }

                linkedCancellation.Dispose();
                queueGate.Release();

                if (idleToComplete != null)
                {
                    idleToComplete.TrySetResult(true);
                    lock (stateLock)
                    {
                        if (ReferenceEquals(idleCompletion, idleToComplete))
                            idleCompletion = null;
                    }
                }
            }
        }

        public void SignalNext()
        {
            lock (stateLock)
                nextSignal?.TrySetResult(true);
        }

        public void Cancel()
        {
            CancellationTokenSource? cancellation;
            lock (stateLock)
                cancellation = activeCancellation;

            SafeExecuteHelper.Try(() => cancellation?.Cancel(), "LaunchCoordinator.Cancel");
        }

        public async Task<bool> WaitForIdleAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Task? idleTask;
            lock (stateLock)
            {
                if (activeCancellation == null && idleCompletion == null)
                    return true;

                idleTask = idleCompletion?.Task;
            }

            if (idleTask == null)
                return true;

            if (timeout <= TimeSpan.Zero)
            {
                await idleTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            Task timeoutTask = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(idleTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            CancellationTokenSource? cancellation;
            lock (stateLock)
            {
                if (disposed)
                    return;

                disposed = true;
                cancellation = activeCancellation;
            }

            GlobalCancelRequested -= Cancel;
            SafeExecuteHelper.Try(() => cancellation?.Cancel(), "LaunchCoordinator.Dispose");
        }
    }
}
