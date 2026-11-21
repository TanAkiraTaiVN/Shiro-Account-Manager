using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public partial class AccountManager
    {
        public void UpdateAccountValidity(Account account, bool isValid)
        {
            if (account == null) return;
            Registration.NotifyAccountValidity(account, isValid);
        }

        public void UpdateAccountView(Account? account = null)
        {
            if (account != null)
                AppServices.HostUiBridge.NotifyAccountUpdated(account);

            AppServices.HostUiBridge.NotifyLiveProcessStatusChanged();
        }

        internal Task RefreshStaleCookiesAsync(CancellationToken cancellationToken) =>
            AppServices.AccountSessionMaintenance.RefreshStaleCookiesAsync(cookieRefreshGate, cancellationToken);

        internal Task ImportBulkUserPassAsync(string combos, Action<string>? statusCallback = null, CancellationToken cancellationToken = default) =>
            AppServices.AccountSessionMaintenance.ImportBulkUserPassAsync(browserService, combos, statusCallback, SafeLifetimeToken, cancellationToken);

        internal static string MakeSafeFileName(string value, string fallback) =>
            AccountSessionMaintenanceService.MakeSafeFileName(value, fallback);
    }
}
