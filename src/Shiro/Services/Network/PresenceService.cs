#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal sealed class PresenceService : IDisposable
    {
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly System.Threading.Lock recentRefreshLock = new();
        private readonly Dictionary<long, DateTime> recentRefreshes = new Dictionary<long, DateTime>();
        private readonly HashSet<long> _pendingLaunchedPresenceIds = new();
        private readonly System.Threading.Lock _pendingLaunchedPresenceLock = new();
        private bool _launchedPresenceBatchScheduled;
        private bool disposed;

        // A timer tick, a manual refresh and the post-launch refresh can land
        // within the same second.  Presence is not latency critical enough to
        // send the same user IDs repeatedly, especially when several accounts
        // are being launched at once.
        private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

        public async Task RefreshAsync(long[] userIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (disposed || cancellationToken.IsCancellationRequested || userIds == null || userIds.Length == 0)
                return;

            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTime now = DateTime.UtcNow;
                long[] pendingIds;
                lock (recentRefreshLock)
                {
                    pendingIds = userIds
                        .Where(id => id > 0 && (!recentRefreshes.TryGetValue(id, out DateTime last) || now - last >= CoalesceWindow))
                        .ToArray();

                    // Keep this cache bounded if a user imports a large number
                    // of accounts over a long-running session.
                    if (recentRefreshes.Count > 4096)
                    {
                        DateTime cutoff = now - CoalesceWindow - CoalesceWindow;
                        foreach (long staleId in recentRefreshes
                            .Where(pair => pair.Value < cutoff)
                            .Select(pair => pair.Key)
                            .ToArray())
                            recentRefreshes.Remove(staleId);
                    }
                }

                if (pendingIds.Length == 0)
                    return;

                await Presence.UpdatePresence(cancellationToken, pendingIds).ConfigureAwait(false);

                lock (recentRefreshLock)
                    foreach (long userId in pendingIds)
                        recentRefreshes[userId] = DateTime.UtcNow;
            }
            finally
            {
                refreshGate.Release();
            }
        }

        public int GetPresenceRefreshMinutes()
        {
            try
            {
                int configured = AppServices.GeneralSettings.Exists("PresenceUpdateRate")
                    ? AppServices.GeneralSettings.GetInt("PresenceUpdateRate", 5)
                    : 5;
                return Math.Max(1, Math.Min(9999, configured));
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[PresenceService] GetPresenceRefreshMinutes failed, defaulting to 5", ex);
                return 5;
            }
        }

        public void SchedulePresenceRefresh(
            Account? account,
            Func<Task, string, Task>? observeTask = null,
            CancellationToken lifetimeToken = default)
        {
            if (account == null || account.UserID <= 0 || !AppServices.GeneralSettings.GetBool("ShowPresence"))
                return;

            lock (_pendingLaunchedPresenceLock)
            {
                _pendingLaunchedPresenceIds.Add(account.UserID);
                if (_launchedPresenceBatchScheduled)
                    return;

                _launchedPresenceBatchScheduled = true;
            }

            Task task = FlushLaunchedPresenceBatchAsync(lifetimeToken);
            if (observeTask != null)
            {
                _ = observeTask(task, "post-launch presence batch refresh");
            }
            else
            {
                _ = task;
            }
        }

        private async Task FlushLaunchedPresenceBatchAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(8000, cancellationToken).ConfigureAwait(false);

                    long[] batchIds;
                    lock (_pendingLaunchedPresenceLock)
                    {
                        if (_pendingLaunchedPresenceIds.Count == 0)
                        {
                            _launchedPresenceBatchScheduled = false;
                            return;
                        }

                        batchIds = _pendingLaunchedPresenceIds.ToArray();
                        _pendingLaunchedPresenceIds.Clear();
                        _launchedPresenceBatchScheduled = false;
                    }

                    await RefreshAsync(batchIds, cancellationToken).ConfigureAwait(false);

                    lock (_pendingLaunchedPresenceLock)
                    {
                        if (_pendingLaunchedPresenceIds.Count > 0 && !_launchedPresenceBatchScheduled)
                        {
                            _launchedPresenceBatchScheduled = true;
                            continue;
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_pendingLaunchedPresenceLock)
                {
                    _launchedPresenceBatchScheduled = false;
                }
            }
            catch (Exception exception)
            {
                lock (_pendingLaunchedPresenceLock)
                {
                    _launchedPresenceBatchScheduled = false;
                }
                AppServices.Logger.Debug($"Unable to refresh batched post-launch presence: {exception.Message}");
            }
        }

        public async Task UpdatePresenceCoreAsync(CancellationToken cancellationToken = default)
        {
            if (!AppServices.GeneralSettings.GetBool("ShowPresence"))
                return;

            var snapshot = AppServices.AccountRepository.GetAll();
            long[] userIds = snapshot.Where(account => account != null && account.UserID > 0).Select(account => account.UserID).ToArray();
            if (userIds.Length == 0) return;

            try
            {
                await RefreshAsync(userIds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* canceled */
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"Presence refresh error: {ex.Message}");
            }
        }

        public bool TryEnableMultiRoblox(
            bool promptIfRobloxRunning = true,
            bool notify = true,
            Action<bool>? closeRobloxCallback = null,
            Action<string>? statusCallback = null)
        {
            void SetStatus(string msg)
            {
                if (notify)
                {
                    AppServices.HostUiBridge.NotifyWindowStatus(msg);
                    statusCallback?.Invoke(msg);
                }
            }

            if (UpdateMultiRoblox())
            {
                SetStatus(LocalizationService.Get("AccountManager.MultiRobloxActive"));
                return true;
            }

            bool autoClose = AppServices.GeneralSettings.Exists("AutoCloseRobloxOnLaunch") && AppServices.GeneralSettings.GetBool("AutoCloseRobloxOnLaunch");
            if (autoClose)
            {
                closeRobloxCallback?.Invoke(notify);
                if (UpdateMultiRoblox())
                {
                    SetStatus(LocalizationService.Get("AccountManager.AutoClosedRobloxActivatedMulti"));
                    return true;
                }
            }

            if (AppServices.GeneralSettings.GetBool("HideRbxAlert"))
            {
                SetStatus(LocalizationService.Get("AccountManager.RobloxRunningMultiInactive"));
                return false;
            }

            if (promptIfRobloxRunning && !autoClose)
            {
                // Only prompt if Avalonia UI is fully initialized and MainWindow is visible
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null && desktop.MainWindow.IsVisible)
                {
                    bool closeRoblox = Utilities.YesNoPrompt(
                        "Shiro",
                        "Roblox is currently running.",
                        "Multi Roblox cannot be activated while Roblox is open.\n\nWould you like to close running Roblox processes now to enable Multi Roblox?",
                        CanSave: true,
                        SaveIfNo: true);

                    if (closeRoblox)
                    {
                        closeRobloxCallback?.Invoke(notify);
                        if (UpdateMultiRoblox())
                        {
                            SetStatus(LocalizationService.Get("AccountManager.KilledAndActivatedMulti"));
                            return true;
                        }
                    }
                    else
                    {
                        SetStatus(LocalizationService.Get("AccountManager.MultiRobloxCannotActivate"));
                    }
                }
                else
                {
                    SetStatus(LocalizationService.Get("AccountManager.RobloxRunningMultiInactive"));
                    return false;
                }
            }

            return false;
        }

        public bool UpdateMultiRoblox()
        {
            bool enabled = AppServices.GeneralSettings.GetBool("EnableMultiRbx");

            try
            {
                RobloxSingletonService.Apply(enabled);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"RobloxSingletonService apply failed: {ex.Message}");
            }

            return !enabled || RobloxSingletonService.MutexHeld;
        }

        public void Dispose()
        {
            lock (recentRefreshLock)
            {
                if (disposed)
                    return;

                disposed = true;
                recentRefreshes.Clear();
            }

            lock (_pendingLaunchedPresenceLock)
            {
                _pendingLaunchedPresenceIds.Clear();
            }

            refreshGate.Dispose();
        }
    }
}
