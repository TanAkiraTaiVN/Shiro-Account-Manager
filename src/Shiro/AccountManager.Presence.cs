using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager : IDisposable
    {
        private int GetPresenceRefreshMinutes() => presenceService.GetPresenceRefreshMinutes();

        internal void RestartPresenceTimer()
        {
            if (PresenceTimer == null)
                return;

            PresenceTimer.Restart(TimeSpan.FromMinutes(GetPresenceRefreshMinutes()));
        }

        internal void ModernRefreshPresence()
        {
            if (!AppServices.GeneralSettings.GetBool("ShowPresence"))
            {
                AppServices.HostUiBridge.NotifyLiveProcessStatusChanged();
                return;
            }

            _ = ObserveBackgroundTaskAsync(UpdatePresence(), "manual presence refresh");
        }

        internal void SchedulePresenceRefresh(Account account)
        {
            if (shutdownRequested || shutdownFinalized) return;
            presenceService.SchedulePresenceRefresh(account, ObserveBackgroundTaskAsync, SafeLifetimeToken);
        }

        public bool TryEnableMultiRoblox(bool promptIfRobloxRunning = false, bool notify = true) =>
            presenceService.TryEnableMultiRoblox(promptIfRobloxRunning, notify, n => ModernCloseRoblox(n), SetDashboardStatus);

        public Task<bool> TryEnableMultiRobloxAsync(bool promptIfRobloxRunning = true, bool notify = true) =>
            presenceService.TryEnableMultiRobloxAsync(promptIfRobloxRunning, notify, n => ModernCloseRoblox(n), SetDashboardStatus);

        public bool UpdateMultiRoblox() => presenceService.UpdateMultiRoblox();

        private Task UpdatePresence()
        {
            if (!AppServices.GeneralSettings.GetBool("ShowPresence")) return Task.CompletedTask;

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
            if (shutdownRequested || shutdownFinalized) return;
            try
            {
                await presenceService.UpdatePresenceCoreAsync(SafeLifetimeToken).ConfigureAwait(false);
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
