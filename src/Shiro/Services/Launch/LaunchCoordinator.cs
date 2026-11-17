#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Owns the only active multi-account launch queue. The Roblox protocol
    /// still signals the next account through AppServices.HostUiBridge.SignalNextLaunchAccount(),
    /// but all waiting, Captcha defer/retry, cancellation and duplicate-launch protection lives here.
    /// </summary>
    internal sealed partial class LaunchCoordinator : ILaunchCoordinator
    {
        private readonly SemaphoreSlim queueGate = new SemaphoreSlim(1, 1);
        private readonly Func<LaunchRequest, CancellationToken, Task<LaunchResult>> executeLaunch;
        private readonly System.Threading.Lock stateLock = new();
        private CancellationTokenSource? activeCancellation;
        private TaskCompletionSource<bool>? nextSignal;
        private TaskCompletionSource<bool>? idleCompletion;
        private List<LaunchRequest>? activeLaunchList;
        private readonly HashSet<string> _deferredCaptchaUsernames = new(StringComparer.OrdinalIgnoreCase);
        private bool acceptingAppends;
        private int currentPass1Index = -1;
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

        public bool TryAppendRequests(IReadOnlyList<LaunchRequest> requests, out int appendedCount)
        {
            appendedCount = 0;
            lock (stateLock)
            {
                if (disposed || !acceptingAppends || activeLaunchList == null ||
                    activeCancellation == null || activeCancellation.IsCancellationRequested)
                {
                    return false;
                }

                if (requests == null || requests.Count == 0)
                    return true;

                var pendingUsernames = new HashSet<string>(_deferredCaptchaUsernames, StringComparer.OrdinalIgnoreCase);
                int startIndex = Math.Max(0, currentPass1Index);
                for (int i = startIndex; i < activeLaunchList.Count; i++)
                {
                    string uname = activeLaunchList[i].Account?.Username ?? string.Empty;
                    if (!string.IsNullOrEmpty(uname))
                        pendingUsernames.Add(uname);
                }

                foreach (LaunchRequest? request in requests)
                {
                    if (request?.Account == null)
                        continue;

                    string uname = request.Account.Username ?? string.Empty;
                    if (!string.IsNullOrEmpty(uname) && !pendingUsernames.Add(uname))
                        continue;

                    activeLaunchList.Add(request);
                    appendedCount++;
                }

                return true;
            }
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

            if (cancellationToken.IsCancellationRequested)
                return new LaunchBatchResult(Array.Empty<LaunchProgress>(), true);

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
                activeLaunchList = launchList;
                acceptingAppends = true;
                currentPass1Index = -1;
                nextSignal = null;
                idleCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            List<LaunchProgress> results = new List<LaunchProgress>(launchList.Count);
            List<(LaunchRequest Request, int ResultIndex)> deferredCaptcha = new();
            int safeDelay = Math.Max(
                AppServices.MinimumAccountJoinDelaySeconds,
                Math.Min(AppServices.MaximumAccountJoinDelaySeconds, delaySeconds));
            int lastSpawnedPid = 0;

            using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 12_000);
            try
            {
                int pass1Index = 0;
                while (true)
                {
                    LaunchRequest? request = null;
                    int totalInPass = 0;
                    lock (stateLock)
                    {
                        if (pass1Index < launchList.Count)
                        {
                            request = launchList[pass1Index];
                            totalInPass = launchList.Count;
                            currentPass1Index = pass1Index;
                        }
                        if (request == null && deferredCaptcha.Count > 0)
                        {
                            foreach (var d in deferredCaptcha)
                                if (!string.IsNullOrEmpty(d.Request.Account?.Username))
                                    _deferredCaptchaUsernames.Add(d.Request.Account.Username);
                        }
                    }

                    if (request != null)
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        lastSpawnedPid = await RunPass1ItemAsync(
                            request, pass1Index, launchList, totalInPass, safeDelay, asyncJoin,
                            progress, results, deferredCaptcha, lastSpawnedPid, linkedCancellation.Token).ConfigureAwait(false);
                        lock (stateLock)
                        {
                            foreach (var d in deferredCaptcha)
                                if (!string.IsNullOrEmpty(d.Request.Account?.Username))
                                    _deferredCaptchaUsernames.Add(d.Request.Account.Username);
                        }
                        pass1Index++;
                        continue;
                    }

                    if (deferredCaptcha.Count > 0 && !linkedCancellation.IsCancellationRequested)
                    {
                        var retrySnapshot = deferredCaptcha.ToList();
                        deferredCaptcha.Clear();
                        lastSpawnedPid = await RunCaptchaRetryPassAsync(
                            retrySnapshot, safeDelay, asyncJoin, progress, results,
                            lastSpawnedPid, linkedCancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    lock (stateLock)
                    {
                        if (pass1Index >= launchList.Count && deferredCaptcha.Count == 0)
                        {
                            acceptingAppends = false;
                            _deferredCaptchaUsernames.Clear();
                            break;
                        }
                    }
                }

                return new LaunchBatchResult(results, false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                List<LaunchRequest> remainingSnapshot;
                lock (stateLock)
                {
                    acceptingAppends = false;
                    _deferredCaptchaUsernames.Clear();
                    remainingSnapshot = launchList.ToList();
                }

                for (int index = results.Count; index < remainingSnapshot.Count; index++)
                {
                    LaunchRequest req = remainingSnapshot[index];
                    LaunchProgress cancelled = new LaunchProgress
                    {
                        Index = index + 1,
                        Total = remainingSnapshot.Count,
                        Account = req.Account,
                        Status = LaunchOutcome.Cancelled,
                        Result = "CANCELLED",
                        Message = $"[{index + 1}/{remainingSnapshot.Count}] {req.Account.Username} was cancelled.",
                        Completed = true
                    };
                    results.Add(cancelled);
                    Report(progress, cancelled);
                }

                Report(progress, new LaunchProgress
                {
                    Index = results.Count,
                    Total = remainingSnapshot.Count,
                    Status = LaunchOutcome.Cancelled,
                    Message = "Launch queue cancelled.",
                    Completed = true
                });
                return new LaunchBatchResult(results, true);
            }
            finally
            {
                cookieGuard.Dispose();
                if (lastSpawnedPid > 0 && RobloxDirectLauncher.TryConsumePostSpawnRestorePending())
                    RobloxDirectLauncher.SchedulePostSpawnCookieRestore(lastSpawnedPid);

                Report(null, new LaunchProgress { Completed = true });
                TaskCompletionSource<bool>? idleToComplete;
                lock (stateLock)
                {
                    acceptingAppends = false;
                    activeLaunchList = null;
                    currentPass1Index = -1;
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
                acceptingAppends = false;
                cancellation = activeCancellation;
            }

            GlobalCancelRequested -= Cancel;
            SafeExecuteHelper.Try(() => cancellation?.Cancel(), "LaunchCoordinator.Dispose");
        }
    }
}
