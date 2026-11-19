#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface IHostUiBridgeService
    {
        void RefreshAccountView(Account? account = null);
        void UpdateAccountValidityOnUi(Account account, bool valid);
        void SchedulePresenceRefresh(Account? account);
        void SignalNextLaunchAccount();
        void CancelLaunching();
        void UpdateMultiRoblox();
        void CloseAllRobloxFromUi();
        Task LaunchAppHomeFromHostAsync(Account account);
        Task LaunchAppHomeAsync(Account account) => LaunchAppHomeFromHostAsync(account);
        Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId);
        Task JoinServerAsync(IReadOnlyList<Account> accounts, long placeId, string jobId) => JoinServerFromHostAsync(accounts, placeId, jobId);
        void MultiLaunchHomeFromHost();
        void MultiLaunchHome() => MultiLaunchHomeFromHost();
        List<Account> GetCurrentSelectedAccountsFromHost();
        List<Account> GetCurrentSelectedAccounts() => GetCurrentSelectedAccountsFromHost();
        void SaveSettings();
        void QueueDeferredSaveSettings();
        void SetDashboardStatus(string message);
        void EnsureBackendInitialized();
        void ShutdownBackend() { }
        void InitializePostStartup();
        Task RestartWebServerAsync(int port, bool allowExternal);
        Task StopWebServerAsync();
        void SetAutoCookieRefreshEnabled(bool enabled);
        Task ResetEncryptionAsync(bool prompt);
        void NotifyAccountsReplaced(IReadOnlyList<Account> accounts) { }
        void NotifyAccountUpdated(Account account) { }
        void NotifyAccountValidityChanged(Account account, bool isValid) { }
        void NotifyLiveProcessStatusChanged() { }
        void NotifyWindowStatus(string message) { }
        void NotifyRobloxProcessesClosed(int totalKilled, bool notify) { }
        List<Account>? TryGetDashboardSelectedAccounts() => null;
        string? TryGetDashboardPlaceId() => null;
        string? TryGetDashboardJobId() => null;
        async Task<LaunchResult> ExecuteSingleLaunchFromApiAsync(
            Account account,
            long placeId,
            string jobId,
            bool followUser,
            bool joinVip,
            CancellationToken cancellationToken = default)
        {
            using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 12_000);
            return await account.JoinServerResult(placeId, jobId, followUser, joinVip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
