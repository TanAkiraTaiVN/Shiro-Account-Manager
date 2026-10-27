using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
{
    public sealed class LaunchRequest
    {
        public Account Account { get; }
        public long PlaceId { get; }
        public string JobId { get; }
        public bool FollowUser { get; }
        public bool VipServer { get; }
        public bool OpenHome { get; }
        public bool PreferChromeHome { get; }

        public LaunchRequest(
            Account account,
            long placeId,
            string jobId,
            bool followUser,
            bool vipServer,
            bool openHome = false,
            bool preferChromeHome = false)
        {
            Account = account;
            PlaceId = placeId;
            JobId = jobId ?? string.Empty;
            FollowUser = followUser;
            VipServer = vipServer;
            OpenHome = openHome;
            PreferChromeHome = preferChromeHome;
        }
    }

    public sealed class LaunchProgress
    {
        public int Index { get; set; }
        public int Total { get; set; }
        public Account Account { get; set; }
        public LaunchOutcome Status { get; set; }
        public string Result { get; set; }
        public string Message { get; set; }
        public bool Completed { get; set; }
    }

    public enum LaunchOutcome
    {
        Pending,
        Success,
        RateLimited,
        TicketError,
        Timeout,
        Failed,
        Cancelled
    }

    internal sealed class LaunchBatchResult
    {
        public IReadOnlyList<LaunchProgress> Results { get; }
        public bool Cancelled { get; }

        public LaunchBatchResult(IReadOnlyList<LaunchProgress> results, bool cancelled)
        {
            Results = results ?? Array.Empty<LaunchProgress>();
            Cancelled = cancelled;
        }
    }

    internal interface ILaunchCoordinator : IDisposable
    {
        bool IsRunning { get; }

        Task<LaunchBatchResult> EnqueueAsync(
            IReadOnlyList<LaunchRequest> requests,
            int delaySeconds,
            bool asyncJoin,
            Action<LaunchProgress> progress,
            CancellationToken cancellationToken = default(CancellationToken));

        void SignalNext();
        void Cancel();
        Task<bool> WaitForIdleAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Owns the only active multi-account launch queue.  The Roblox protocol
    /// still signals the next account through AccountManager.NextAccount(),
    /// but all waiting, cancellation and duplicate-launch protection lives here.
    /// </summary>
    internal sealed class LaunchCoordinator : ILaunchCoordinator
    {
        private readonly SemaphoreSlim queueGate = new SemaphoreSlim(1, 1);
        private readonly Func<LaunchRequest, CancellationToken, Task<string>> executeLaunch;
        private readonly System.Threading.Lock stateLock = new();
        private CancellationTokenSource activeCancellation;
        private TaskCompletionSource<bool> nextSignal;
        private TaskCompletionSource<bool> idleCompletion;
        private bool disposed;

        public const int MaxRateLimitRetries = 2;
        public const int DefaultSmartLaunchMinDelaySeconds = 3;

        public static event Action<LaunchProgress> LaunchProgressChanged;

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

        public bool IsRunning
        {
            get
            {
                lock (stateLock)
                    return activeCancellation != null;
            }
        }

        public LaunchCoordinator(Func<LaunchRequest, CancellationToken, Task<string>> executeLaunch)
        {
            this.executeLaunch = executeLaunch ?? throw new ArgumentNullException(nameof(executeLaunch));
        }

        public async Task<LaunchBatchResult> EnqueueAsync(
            IReadOnlyList<LaunchRequest> requests,
            int delaySeconds,
            bool asyncJoin,
            Action<LaunchProgress> progress,
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
                AccountManager.MinimumAccountJoinDelaySeconds,
                Math.Min(AccountManager.MaximumAccountJoinDelaySeconds, delaySeconds));

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
                            Message = $"⏳ [{index + 1}/{launchList.Count}] Roblox đang giới hạn IP (429). Chờ {backoffSecs:0.0}s trước khi mở {request.Account.Username}..."
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

                    string result;
                    try
                    {
                        result = await executeLaunch(request, linkedCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        result = $"ERROR: Failed to launch Roblox for {request.Account.Username}\n{exception.Message}";
                        try { Program.Logger.Error($"Unexpected launch error for {request.Account.Username}: {exception}"); } catch { }
                    }

                    state.Result = result ?? "ERROR: Roblox returned no launch status.";
                    state.Status = ClassifyResult(state.Result);

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
                            Message = $"⏳ [{index + 1}/{launchList.Count}] {request.Account.Username} bị giới hạn (429). Chờ {waitSecs:0.0}s rồi thử lại ({retry}/{MaxRateLimitRetries})..."
                        });

                        await RobloxRateLimitCoordinator.Instance.WaitForBackoffAsync(linkedCancellation.Token).ConfigureAwait(false);
                        try
                        {
                            try { RobloxProcessService.CloseAccountRobloxProcess(request.Account); } catch { }
                            result = await executeLaunch(request, linkedCancellation.Token).ConfigureAwait(false);
                            state.Result = result ?? "ERROR: Roblox returned no launch status.";
                            state.Status = ClassifyResult(state.Result);
                        }
                        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception retryEx)
                        {
                            Program.Logger?.Error($"[LaunchCoordinator] Retry #{retry} failed for {request.Account.Username}: {retryEx}");
                        }
                    }

                    if (state.Status == LaunchOutcome.Success)
                    {
                        RobloxRateLimitCoordinator.Instance.RecordSuccess(RateLimitCategory.Authentication);
                    }

                    state.Completed = true;

                    bool isTicketAuthError = !request.Account.Valid ||
                        (!string.IsNullOrEmpty(state.Result) && state.Result.Contains("Roblox has probably signed you out", StringComparison.OrdinalIgnoreCase));

                    if (isTicketAuthError && request.Account.Valid)
                    {
                        request.Account.Valid = false;
                        AccountManager.Instance?.BeginInvoke(new Action(() =>
                        {
                            AccountManager.Instance?.UpdateAccountValidity(request.Account, false);
                            AccountManager.Instance?.RefreshView(request.Account);
                        }));
                    }
                    else if (!request.Account.Valid)
                    {
                        isTicketAuthError = true;
                    }

                    state.Message = IsSuccessfulResult(state.Result)
                        ? $"[{index + 1}/{launchList.Count}] {request.Account.Username} launched."
                        : (isTicketAuthError
                            ? $"⚠️ {request.Account.Username}: Phiên đăng nhập đã hết hạn! Bấm '🔑 Re-login' trên thẻ để đăng nhập lại."
                            : $"Warning ({request.Account.Username}): {state.Result}");
                    results.Add(state);
                    Report(progress, state);

                    if (index >= launchList.Count - 1 || linkedCancellation.IsCancellationRequested)
                        continue;

                    // An auth/startup failure has no protocol handoff, so do
                    // not leave an async queue waiting for an event that can
                    // never arrive.
                    if (!IsSuccessfulResult(state.Result))
                        signal.TrySetResult(false);

                    bool smartLaunch = AccountManager.General == null || !AccountManager.General.Exists(AppSettingsKeys.General.SmartFastLaunch) || AccountManager.General.Get<bool>(AppSettingsKeys.General.SmartFastLaunch);

                    if (smartLaunch && IsSuccessfulResult(state.Result))
                    {
                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Message = $"[{index + 1}/{launchList.Count}] {request.Account.Username} launched. Smart Pipeline waiting for instance unlock..."
                        });

                        // Dynamically wait for the client to initialize its main game window
                        int targetPid = request.Account.LastLaunchedProcessId;
                        bool unlocked = await RobloxSingletonService.WaitForClientUnlockedAsync(
                            targetPid,
                            TimeSpan.FromSeconds(15),
                            linkedCancellation.Token).ConfigureAwait(false);

                        int minDelayMs = GetSmartLaunchMinDelayMs(safeDelay, includeJitter: false);
                        string statusPrefix = unlocked ? "⚡" : "⏱️";
                        Report(progress, new LaunchProgress
                        {
                            Index = index + 1,
                            Total = launchList.Count,
                            Account = request.Account,
                            Status = LaunchOutcome.Success,
                            Completed = true,
                            Message = $"{statusPrefix} [{index + 1}/{launchList.Count}] {request.Account.Username} sẵn sàng. Chờ {minDelayMs / 1000.0:0.0}s trước {launchList[index + 1].Account.Username}..."
                        });

                        await Task.Delay(minDelayMs, linkedCancellation.Token).ConfigureAwait(false);
                    }
                    else if (asyncJoin)
                    {
                        // One cancellable timeout is enough here. The old
                        // 250ms polling loop allocated up to 80 timer tasks
                        // per account and added needless scheduler work while
                        // waiting for Roblox's protocol handoff.
                        // Keep a per-account cancellation source for the
                        // timeout itself.  Without it, a successful protocol
                        // handoff leaves a 20-second delay task registered
                        // until the whole batch ends; opening many accounts
                        // could therefore leave dozens of dormant timers in
                        // the scheduler after the queue had already moved on.
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
                Report(null, new LaunchProgress { Completed = true });
                TaskCompletionSource<bool> idleToComplete;
                lock (stateLock)
                {
                    nextSignal = null;
                    activeCancellation = null;
                    idleToComplete = idleCompletion;
                }

                linkedCancellation.Dispose();
                queueGate.Release();

                // Release the gate before waking waiters. A caller that
                // cancels and immediately starts another batch must observe
                // both the old queue's completion and the free semaphore.
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
            CancellationTokenSource cancellation;
            lock (stateLock)
                cancellation = activeCancellation;

            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public async Task<bool> WaitForIdleAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Task idleTask;
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
            CancellationTokenSource cancellation;
            lock (stateLock)
            {
                if (disposed)
                    return;

                disposed = true;
                cancellation = activeCancellation;
            }

            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            // The active queue may still be unwinding from an awaited Roblox
            // request. Leave the gate alive until process shutdown so its
            // finally block can release it without an ObjectDisposedException.
        }

        private static void Report(Action<LaunchProgress> progress, LaunchProgress state)
        {
            try { progress?.Invoke(state); } catch { }
            try { LaunchProgressChanged?.Invoke(state); } catch { }
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
