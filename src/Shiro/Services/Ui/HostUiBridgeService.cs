#nullable enable
using System;
using System.Collections.Generic;
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
        public Action? InitializePostStartupHandler { get; set; }
        public Func<int, bool, Task>? RestartWebServerHandler { get; set; }
        public Func<Task>? StopWebServerHandler { get; set; }
        public Action<bool>? SetAutoCookieRefreshEnabledHandler { get; set; }
        public Func<bool, Task>? ResetEncryptionHandler { get; set; }

        public void RefreshAccountView(Account? account = null) => RefreshAccountViewHandler?.Invoke(account);
        public void UpdateAccountValidityOnUi(Account account, bool valid) => UpdateAccountValidityOnUiHandler?.Invoke(account, valid);
        public void SchedulePresenceRefresh(Account? account) => SchedulePresenceRefreshHandler?.Invoke(account);
        public void SignalNextLaunchAccount() => SignalNextLaunchAccountHandler?.Invoke();
        public void CancelLaunching() => CancelLaunchingHandler?.Invoke();
        public void UpdateMultiRoblox() => UpdateMultiRobloxHandler?.Invoke();
        public void CloseAllRobloxFromUi() => CloseAllRobloxFromUiHandler?.Invoke();

        public Task LaunchAppHomeFromHostAsync(Account account) =>
            LaunchAppHomeHandler != null ? LaunchAppHomeHandler(account) : Task.CompletedTask;

        public Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId) =>
            JoinServerHandler != null ? JoinServerHandler(accounts, placeId, jobId) : Task.CompletedTask;

        public void MultiLaunchHomeFromHost() => MultiLaunchHomeHandler?.Invoke();

        public List<Account> GetCurrentSelectedAccountsFromHost() =>
            GetCurrentSelectedAccountsHandler != null ? GetCurrentSelectedAccountsHandler() : new List<Account>();

        public void SaveSettings() => SaveSettingsHandler?.Invoke();
        public void QueueDeferredSaveSettings() => QueueDeferredSaveSettingsHandler?.Invoke();
        public void SetDashboardStatus(string message) => SetDashboardStatusHandler?.Invoke(message);
        public void EnsureBackendInitialized() => EnsureBackendInitializedHandler?.Invoke();
        public void InitializePostStartup() => InitializePostStartupHandler?.Invoke();

        public Task RestartWebServerAsync(int port, bool allowExternal) =>
            RestartWebServerHandler != null ? RestartWebServerHandler(port, allowExternal) : Task.CompletedTask;

        public Task StopWebServerAsync() =>
            StopWebServerHandler != null ? StopWebServerHandler() : Task.CompletedTask;

        public void SetAutoCookieRefreshEnabled(bool enabled) => SetAutoCookieRefreshEnabledHandler?.Invoke(enabled);

        public Task ResetEncryptionAsync(bool prompt) =>
            ResetEncryptionHandler != null ? ResetEncryptionHandler(prompt) : Task.CompletedTask;
    }
}
