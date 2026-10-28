#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        public async Task<string> LaunchAppHome(
            bool preferChrome = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preferChrome ||
                (AccountManager.General != null && AccountManager.General.Exists("UseChromeHome") && AccountManager.General.Get<bool>("UseChromeHome")))
                return await LaunchHomeInChrome(cancellationToken).ConfigureAwait(false);

            EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); } catch (Exception Ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {Ex}"); }

            // High-reliability Home Launch:
            // Direct LocalStorage DPAPI Injection allows opening Roblox Home directly as this account with 0 network ticket requests,
            // completely eliminating Roblox Error 403 (IP mismatch / redeem rejection on VPN)!
            bool preferDirect = AccountManager.General?.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false) ?? false;
            if (preferDirect && !string.IsNullOrWhiteSpace(SecurityToken))
            {
                var directResult = RobloxDirectLauncher.LaunchHomeDirect(Username, ticket: null, BrowserTrackerID, SecurityToken);
                if (directResult.Success)
                {
                    this.LastLaunchedProcessId = directResult.ProcessId;
                    AccountManager.Instance?.SchedulePresenceRefresh(this);
                    AccountManager.Instance?.NextAccount();
                    ObserveBackgroundTask(ObserveWindowPositionAsync(directResult.ProcessId, cancellationToken), "Roblox window positioning");
                    return "Success";
                }
            }

            (bool Success, string Ticket) ticketResult =
                await GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
            if (ticketResult.Success)
            {
                try
                {
                    // Always close any previous/stale Roblox client of the SAME account to prevent duplicate overlapping windows
                    RobloxProcessService.CloseAccountRobloxProcess(this);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Closing previous process for {Username} threw: {ex.Message}");
                }

                string Ticket = ticketResult.Ticket;
                long LaunchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string NativeHomeUri =
                    $"roblox-player:1+launchmode:app+gameinfo:{Ticket}+launchtime:{LaunchTime}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception? launchException = null;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    int homePid = await StartNativeHomeAsync(NativeHomeUri, Ticket, Username, BrowserTrackerID, SecurityToken, cancellationToken).ConfigureAwait(false);
                    this.LastLaunchedProcessId = homePid;
                    ObserveBackgroundTask(
                        MonitorNativeAuthRateLimitAsync(launchStartedUtc, this, cancellationToken),
                        "native auth rate-limit monitor");
                    AccountManager.Instance?.SchedulePresenceRefresh(this);

                    AccountManager.Instance?.NextAccount();
                    ObserveBackgroundTask(ObserveWindowPositionAsync(homePid, cancellationToken), "Roblox window positioning");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    Program.Logger?.Error($"Failed to launch Roblox Home for {Username}: {x}");
                }

                if (launchException != null)
                    return $"ERROR: Failed to launch Roblox Home for {Username}\n{launchException.Message}";

                return "Success";
            }
            else
            {
                if (LastAuthTicketRateLimited)
                    return "ERROR: Roblox đang giới hạn xác thực (HTTP 429). Hãy chờ một lúc hoặc đổi máy chủ VPN rồi thử lại.";

                return "ERROR: Không thể lấy Authentication Ticket từ Roblox. Phiên tài khoản vẫn được giữ nguyên, vui lòng kiểm tra Proton VPN hoặc thử lại sau.";
            }
        }

        private async Task<string> LaunchHomeInChrome(CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool useConfiguredProxy = AccountManager.General != null &&
                    AccountManager.General.Exists("UseProxies") &&
                    AccountManager.General.Get<bool>("UseProxies") &&
                    ProxyManagerService.GetCachedProxyStrings().Count > 0;

                if (useConfiguredProxy)
                {
                    await AccountBrowser.OpenAsync(
                        this,
                        "https://www.roblox.com/home",
                        UseStealth: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    AccountManager.Instance?.SchedulePresenceRefresh(this);
                    return BrowserHomeResult;
                }

                bool opened = await ChromiumHomeBrowser.OpenAsync(this, cancellationToken).ConfigureAwait(false);
                if (opened)
                {
                    AccountManager.Instance?.SchedulePresenceRefresh(this);
                    return BrowserHomeResult;
                }

                Program.Logger.Warn($"The CDP Home path failed for {Username}; falling back to the legacy Chromium browser path.");
                await AccountBrowser.OpenAsync(
                    this,
                    "https://www.roblox.com/home",
                    UseStealth: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AccountManager.Instance?.SchedulePresenceRefresh(this);
                return BrowserHomeResult;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Program.Logger.Error($"Failed to open Roblox Home in Chrome for {Username}: {exception}");
                return $"ERROR: Failed to open Roblox Home in Chrome\n{exception.Message}";
            }
        }
    }
}
