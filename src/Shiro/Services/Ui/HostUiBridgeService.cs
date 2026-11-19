#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class HostUiBridgeService : IHostUiBridgeService
    {
        public static readonly HostUiBridgeService Default = new();

        public Action<Account?>? RefreshAccountViewHandler { get; set; }
        public Action<Account, bool>? UpdateAccountValidityOnUiHandler { get; set; }
        public Action<Account?>? SchedulePresenceRefreshHandler { get; set; }
        public Action? SignalNextLaunchAccountHandler { get; set; }
        public Action? CancelLaunchingHandler { get; set; }
        public Action? UpdateMultiRobloxHandler { get; set; }
        public Action? CloseAllRobloxFromUiHandler { get; set; }
        public Func<Account, Task>? LaunchAppHomeHandler { get; set; }
        public Func<IReadOnlyList<Account>, long, string, Task>? JoinServerHandler { get; set; }
        public Action? MultiLaunchHomeHandler { get; set; }
        public Func<List<Account>>? GetCurrentSelectedAccountsHandler { get; set; }
        public Action? SaveSettingsHandler { get; set; }
        public Action? QueueDeferredSaveSettingsHandler { get; set; }
        public Action<string>? SetDashboardStatusHandler { get; set; }
        public Action? EnsureBackendInitializedHandler { get; set; }
        public Action? ShutdownBackendHandler { get; set; }
        public Action? InitializePostStartupHandler { get; set; }
        public Func<int, bool, Task>? RestartWebServerHandler { get; set; }
        public Func<Task>? StopWebServerHandler { get; set; }
        public Action<bool>? SetAutoCookieRefreshEnabledHandler { get; set; }
        public Func<bool, Task>? ResetEncryptionHandler { get; set; }
        public Action<IReadOnlyList<Account>>? RefreshAccountsOnUiHandler { get; set; }
        public Action<Account>? RefreshSingleAccountOnUiHandler { get; set; }
        public Action<Account, bool>? NotifyAccountValidityOnUiHandler { get; set; }
        public Action? RefreshLiveProcessStatusOnUiHandler { get; set; }
        public Action<string>? UpdateWindowStatusOnUiHandler { get; set; }
        public Action<int, bool>? NotifyRobloxProcessesClosedOnUiHandler { get; set; }
        public Func<List<Account>?>? GetDashboardSelectedAccountsHandler { get; set; }
        public Func<string?>? GetDashboardPlaceIdHandler { get; set; }
        public Func<string?>? GetDashboardJobIdHandler { get; set; }
        public Func<Account, long, string, bool, bool, CancellationToken, Task<LaunchResult>>? ExecuteApiLaunchHandler { get; set; }

        public void RefreshAccountView(Account? account = null) => RefreshAccountViewHandler?.Invoke(account);
        public void UpdateAccountValidityOnUi(Account account, bool valid) => UpdateAccountValidityOnUiHandler?.Invoke(account, valid);
        public void SchedulePresenceRefresh(Account? account) => SchedulePresenceRefreshHandler?.Invoke(account);
        public void SignalNextLaunchAccount() => SignalNextLaunchAccountHandler?.Invoke();
        public void CancelLaunching() => CancelLaunchingHandler?.Invoke();
        public void UpdateMultiRoblox() => UpdateMultiRobloxHandler?.Invoke();
        public void CloseAllRobloxFromUi() => CloseAllRobloxFromUiHandler?.Invoke();
        public void NotifyAccountsReplaced(IReadOnlyList<Account> accounts) => RefreshAccountsOnUiHandler?.Invoke(accounts);
        public void NotifyAccountUpdated(Account account) => RefreshSingleAccountOnUiHandler?.Invoke(account);
        public void NotifyAccountValidityChanged(Account account, bool isValid) => NotifyAccountValidityOnUiHandler?.Invoke(account, isValid);
        public void NotifyLiveProcessStatusChanged() => RefreshLiveProcessStatusOnUiHandler?.Invoke();
        public void NotifyWindowStatus(string message) => UpdateWindowStatusOnUiHandler?.Invoke(message);
        public void NotifyRobloxProcessesClosed(int totalKilled, bool notify) => NotifyRobloxProcessesClosedOnUiHandler?.Invoke(totalKilled, notify);
        public List<Account>? TryGetDashboardSelectedAccounts() => GetDashboardSelectedAccountsHandler?.Invoke();
        public string? TryGetDashboardPlaceId() => GetDashboardPlaceIdHandler?.Invoke();
        public string? TryGetDashboardJobId() => GetDashboardJobIdHandler?.Invoke();

        public async Task LaunchAppHomeFromHostAsync(Account account)
        {
            if (LaunchAppHomeHandler == null)
            {
                EnsureBackendInitialized();
            }

            if (LaunchAppHomeHandler != null)
            {
                await LaunchAppHomeHandler(account).ConfigureAwait(false);
            }
            else
            {
                AppServices.Logger.Warn("[HostUiBridgeService] LaunchAppHomeHandler is not registered even after EnsureBackendInitialized.");
            }
        }

        public async Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId)
        {
            if (JoinServerHandler == null)
            {
                EnsureBackendInitialized();
            }

            if (JoinServerHandler != null)
            {
                await JoinServerHandler(accounts, placeId, jobId).ConfigureAwait(false);
            }
            else
            {
                AppServices.Logger.Warn("[HostUiBridgeService] JoinServerHandler is not registered even after EnsureBackendInitialized.");
            }
        }

        public void MultiLaunchHomeFromHost()
        {
            if (MultiLaunchHomeHandler == null)
            {
                EnsureBackendInitialized();
            }

            if (MultiLaunchHomeHandler != null)
            {
                MultiLaunchHomeHandler();
            }
            else
            {
                AppServices.Logger.Warn("[HostUiBridgeService] MultiLaunchHomeHandler is not registered even after EnsureBackendInitialized.");
            }
        }

        public List<Account> GetCurrentSelectedAccountsFromHost()
        {
            if (GetCurrentSelectedAccountsHandler == null)
            {
                EnsureBackendInitialized();
            }

            return GetCurrentSelectedAccountsHandler != null ? GetCurrentSelectedAccountsHandler() : new List<Account>();
        }

        public void SaveSettings() => SaveSettingsHandler?.Invoke();
        public void QueueDeferredSaveSettings() => QueueDeferredSaveSettingsHandler?.Invoke();
        public void SetDashboardStatus(string message) => SetDashboardStatusHandler?.Invoke(message);

        public void EnsureBackendInitialized()
        {
            if (EnsureBackendInitializedHandler != null)
            {
                EnsureBackendInitializedHandler();
            }
            else
            {
                // Backend not yet wired; no-op rather than recurse into AccountRepository
            }
        }

        public void ShutdownBackend() => ShutdownBackendHandler?.Invoke();

        public void InitializePostStartup()
        {
            if (InitializePostStartupHandler == null)
            {
                EnsureBackendInitialized();
            }

            InitializePostStartupHandler?.Invoke();
        }

        public Task RestartWebServerAsync(int port, bool allowExternal) =>
            RestartWebServerHandler != null ? RestartWebServerHandler(port, allowExternal) : Task.CompletedTask;

        public Task StopWebServerAsync() =>
            StopWebServerHandler != null ? StopWebServerHandler() : Task.CompletedTask;

        public void SetAutoCookieRefreshEnabled(bool enabled) => SetAutoCookieRefreshEnabledHandler?.Invoke(enabled);

        public Task ResetEncryptionAsync(bool prompt) =>
            ResetEncryptionHandler != null ? ResetEncryptionHandler(prompt) : Task.CompletedTask;

        public async Task<LaunchResult> ExecuteSingleLaunchFromApiAsync(
            Account account,
            long placeId,
            string jobId,
            bool followUser,
            bool joinVip,
            CancellationToken cancellationToken = default)
        {
            if (ExecuteApiLaunchHandler == null)
                EnsureBackendInitialized();

            if (ExecuteApiLaunchHandler != null)
                return await ExecuteApiLaunchHandler(account, placeId, jobId, followUser, joinVip, cancellationToken).ConfigureAwait(false);

            using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 12_000);
            return await account.JoinServerResult(placeId, jobId, followUser, joinVip, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
