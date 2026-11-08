using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager : IDisposable
    {
        private int GetPresenceRefreshMinutes()
        {
            try
            {
                int configured = General != null && General.Exists("PresenceUpdateRate")
                    ? General.Get<int>("PresenceUpdateRate")
                    : 5;
                return Math.Max(1, Math.Min(9999, configured));
            }
            catch
            {
                return 5;
            }
        }

        internal void RestartPresenceTimer()
        {
            if (PresenceTimer == null)
                return;

            PresenceTimer.Restart(TimeSpan.FromMinutes(GetPresenceRefreshMinutes()));
        }

        internal void ModernRefreshPresence()
        {
            if (!General.Get<bool>("ShowPresence"))
            {
                HostUiBridgeService.Default.NotifyLiveProcessStatusChanged();
                return;
            }

            _ = ObserveBackgroundTaskAsync(UpdatePresence(), "manual presence refresh");
        }

        private readonly System.Collections.Generic.HashSet<long> _pendingLaunchedPresenceIds = new();
        private readonly System.Threading.Lock _pendingLaunchedPresenceLock = new();
        private bool _launchedPresenceBatchScheduled;

        internal void SchedulePresenceRefresh(Account account)
        {
            if (account == null || account.UserID <= 0 || !General.Get<bool>("ShowPresence"))
                return;

            lock (_pendingLaunchedPresenceLock)
            {
                _pendingLaunchedPresenceIds.Add(account.UserID);
                if (_launchedPresenceBatchScheduled)
                    return;

                _launchedPresenceBatchScheduled = true;
            }

            _ = ObserveBackgroundTaskAsync(
                FlushLaunchedPresenceBatchAsync(lifetimeCancellation.Token),
                "post-launch presence batch refresh");
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

                    await presenceService.RefreshAsync(batchIds, cancellationToken).ConfigureAwait(false);

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
                Program.Logger.Debug($"Unable to refresh batched post-launch presence: {exception.Message}");
            }
        }

        public bool TryEnableMultiRoblox(bool promptIfRobloxRunning = true, bool notify = true)
        {
            if (UpdateMultiRoblox())
            {
                if (notify)
                    SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.MultiRobloxActive"));
                return true;
            }

            bool autoClose = General.Exists("AutoCloseRobloxOnLaunch") ? General.Get<bool>("AutoCloseRobloxOnLaunch") : false;
            if (autoClose)
            {
                ModernCloseRoblox(notify: notify);
                if (UpdateMultiRoblox())
                {
                    if (notify)
                        SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.AutoClosedRobloxActivatedMulti"));
                    return true;
                }
            }

            if (General.Get<bool>("HideRbxAlert"))
            {
                if (notify)
                    SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.RobloxRunningMultiInactive"));
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
                        ModernCloseRoblox(notify: notify);
                        if (UpdateMultiRoblox())
                        {
                            if (notify)
                                SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.KilledAndActivatedMulti"));
                            return true;
                        }
                    }
                    else
                    {
                        if (notify)
                            SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.MultiRobloxCannotActivate"));
                    }
                }
                else
                {
                    if (notify)
                        SetDashboardStatus(Shiro.Services.LocalizationService.Get("AccountManager.RobloxRunningMultiInactive"));
                    return false;
                }
            }

            return false;
        }

        public bool UpdateMultiRoblox()
        {
            bool Enabled = General.Get<bool>("EnableMultiRbx");

            try
            {
                RobloxSingletonService.Apply(Enabled);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"RobloxSingletonService apply failed: {ex.Message}");
            }

            return !Enabled || RobloxSingletonService.MutexHeld;
        }

        private Task UpdatePresence()
        {
            if (!General.Get<bool>("ShowPresence")) return Task.CompletedTask;

            lock (presenceTaskLock)
            {
                if (presenceRefreshTask != null && !presenceRefreshTask.IsCompleted)
                    return presenceRefreshTask;

                presenceRefreshTask = UpdatePresenceCoreAsync();
                return presenceRefreshTask;
            }
        }

        private async Task UpdatePresenceCoreAsync()
        {
            try
            {
                if (!General.Get<bool>("ShowPresence")) return;

                var snapshot = GetAccountsSnapshot();
                long[] userIds = snapshot.Where(account => account != null && account.UserID > 0).Select(account => account.UserID).ToArray();
                if (userIds.Length == 0) return;

                try
                {
                    await presenceService.RefreshAsync(
                        userIds,
                        lifetimeCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    /* canceled */
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Presence refresh error: {ex.Message}");
                }
            }
            finally
            {
                lock (presenceTaskLock)
                {
                    presenceRefreshTask = null;
                }
            }
        }
    }
}
