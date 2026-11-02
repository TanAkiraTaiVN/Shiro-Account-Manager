#nullable enable
using System;
using System.Collections.Generic;
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
        Task JoinServerFromHostAsync(IReadOnlyList<Account> accounts, long placeId, string jobId);
        void MultiLaunchHomeFromHost();
        List<Account> GetCurrentSelectedAccountsFromHost();
        void SaveSettings();
        void QueueDeferredSaveSettings();
        void SetDashboardStatus(string message);
        void EnsureBackendInitialized();
        void InitializePostStartup();
        Task RestartWebServerAsync(int port, bool allowExternal);
        Task StopWebServerAsync();
        void SetAutoCookieRefreshEnabled(bool enabled);
        Task ResetEncryptionAsync(bool prompt);
    }
}
