using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Http;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class Account : IComparable<Account>
    {
        [GeneratedRegex(@"\-b (\d+)")]
        private static partial Regex TrackerRegex();

        [GeneratedRegex(@"(?:privateServerLinkCode|shareCode|code)=([^&\s]+)", RegexOptions.IgnoreCase)]
        private static partial Regex LinkCodeRegex();

        private static void AddDirectGameArguments(ProcessStartInfo launchInfo, string ticket, string joinUrl)
        {
            launchInfo.ArgumentList.Add("--app");
            launchInfo.ArgumentList.Add("-t");
            launchInfo.ArgumentList.Add(ticket);
            launchInfo.ArgumentList.Add("-j");
            launchInfo.ArgumentList.Add(joinUrl);
        }

        private static async Task PrepareNativeLaunchEnvironmentAsync(CancellationToken cancellationToken)
        {
            // If Roblox is already updating from another trigger, wait for it first
            await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

            // Purge dead crash handlers and idle background tray instances before launching if enabled
            if (AccountManager.General?.Get<bool>("AutoCleanZombiesOnLaunch", true) ?? true)
            {
                RobloxProcessService.CleanZombieProcesses();
            }

            if (AccountManager.General?.Get<bool>("EnableMultiRbx", true) ?? true)
            {
                try { RobloxSingletonService.Sweep(force: true); } catch (Exception ex) { Program.Logger?.Debug($"Pre-launch singleton sweep failed: {ex.Message}"); }
            }
        }

        private static void SchedulePostLaunchSingletonSweep(int delayMs = 500)
        {
            if (!(AccountManager.General?.Get<bool>("EnableMultiRbx", true) ?? true))
                return;

            ObserveBackgroundTask(Task.Run(async () =>
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                try { RobloxSingletonService.Sweep(force: true); } catch (Exception ex) { Program.Logger?.Debug($"Post-launch singleton sweep failed: {ex.Message}"); }
            }), "post-launch singleton sweep");
        }

        private static async Task<int> StartNativeGameAsync(
            string nativeLaunchUri,
            string ticket,
            string joinUrl,
            string username,
            string browserTrackerId = null,
            string securityToken = null,
            CancellationToken cancellationToken = default)
        {
            await NativeLaunchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareNativeLaunchEnvironmentAsync(cancellationToken).ConfigureAwait(false);

                bool preferDirect = AccountManager.General?.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false) ?? false;
                if (preferDirect)
                {
                    var directResult = RobloxDirectLauncher.LaunchDirect(ticket, joinUrl, username, browserTrackerId, securityToken);
                    if (directResult.Success)
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                        SchedulePostLaunchSingletonSweep(500);
                        return directResult.ProcessId;
                    }

                    Program.Logger.Info($"[StartNativeGameAsync] Direct binary launch unavailable ({directResult.ErrorMessage}); falling back to URI protocol.");
                }

                if (HasRobloxPlayerProtocolHandler() || !preferDirect)
                {
                    try
                    {
                        var existingClientPids = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                        using Process launcher = Process.Start(new ProcessStartInfo
                        {
                            FileName = nativeLaunchUri,
                            UseShellExecute = true
                        });

                        if (launcher == null)
                            throw new InvalidOperationException("Windows did not start the Roblox protocol handler.");

                        int launcherPid = launcher.Id;

                        if (!await WaitForExitAsync(launcher, 1500, cancellationToken).ConfigureAwait(false))
                            Program.Logger.Info($"Roblox client for {username} is running; released handoff gate.");

                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

                        int resolvedGamePid = await ResolveNewClientPidAsync(
                            existingClientPids,
                            launcherPid,
                            TimeSpan.FromSeconds(10),
                            cancellationToken).ConfigureAwait(false);

                        Program.Logger.Info($"Launching Roblox through the registered protocol for {username} (Game PID: {resolvedGamePid}).");
                        SchedulePostLaunchSingletonSweep(500);
                        return resolvedGamePid;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception protocolError)
                    {
                        Program.Logger.Warn($"Roblox protocol launch failed for {username}; using the executable fallback.", protocolError);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                string playerPath = FindRobloxPlayerExecutable();
                if (string.IsNullOrWhiteSpace(playerPath))
                    throw new FileNotFoundException("RobloxPlayerBeta.exe was not found.");

                ProcessStartInfo directLaunch = new ProcessStartInfo(playerPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };
                AddDirectGameArguments(directLaunch, ticket, joinUrl);
                cancellationToken.ThrowIfCancellationRequested();
                using Process fallbackProc = Process.Start(directLaunch);
                if (fallbackProc == null)
                    throw new InvalidOperationException("Windows did not start RobloxPlayerBeta.exe.");
                int fallbackPid = fallbackProc.Id;

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

                Program.Logger.Info($"Launching RobloxPlayerBeta.exe directly for {username} because the Roblox protocol is unavailable.");
                SchedulePostLaunchSingletonSweep(1500);
                return fallbackPid;
            }
            finally
            {
                NativeLaunchGate.Release();
            }
        }

        private static async Task<int> StartNativeHomeAsync(
            string nativeHomeUri,
            string ticket,
            string username,
            string browserTrackerId = null,
            string securityToken = null,
            CancellationToken cancellationToken = default)
        {
            await NativeLaunchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareNativeLaunchEnvironmentAsync(cancellationToken).ConfigureAwait(false);

                bool preferDirect = AccountManager.General?.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false) ?? false;
                if (preferDirect)
                {
                    var directResult = RobloxDirectLauncher.LaunchHomeDirect(username, ticket, browserTrackerId, securityToken);
                    if (directResult.Success)
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                        SchedulePostLaunchSingletonSweep(500);
                        return directResult.ProcessId;
                    }

                    Program.Logger.Info($"[StartNativeHomeAsync] Direct binary launch unavailable ({directResult.ErrorMessage}); falling back to URI protocol.");
                }

                if (HasRobloxPlayerProtocolHandler() || !preferDirect)
                {
                    try
                    {
                        var existingClientPids = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                        using Process launcher = Process.Start(new ProcessStartInfo
                        {
                            FileName = nativeHomeUri,
                            UseShellExecute = true
                        });

                        if (launcher == null)
                            throw new InvalidOperationException("Windows did not start the Roblox protocol handler.");

                        int launcherPid = launcher.Id;

                        if (!await WaitForExitAsync(launcher, 1500, cancellationToken).ConfigureAwait(false))
                        {
                            Program.Logger.Info($"Roblox client for {username} is running; released handoff gate.");
                        }
                        else
                        {
                            Program.Logger.Info($"Launching Roblox Home through the registered protocol for {username}.");
                        }

                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

                        int resolvedHomePid = await ResolveNewClientPidAsync(
                            existingClientPids,
                            launcherPid,
                            TimeSpan.FromSeconds(10),
                            cancellationToken).ConfigureAwait(false);

                        SchedulePostLaunchSingletonSweep(500);
                        return resolvedHomePid;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception protocolError)
                    {
                        Program.Logger.Warn($"Roblox Home protocol launch failed for {username}; using the executable fallback.", protocolError);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                string playerPath = FindRobloxPlayerExecutable();
                if (string.IsNullOrWhiteSpace(playerPath))
                    throw new FileNotFoundException("RobloxPlayerBeta.exe was not found.");

                ProcessStartInfo directLaunch = new ProcessStartInfo(playerPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };
                directLaunch.ArgumentList.Add("--app");
                directLaunch.ArgumentList.Add(nativeHomeUri);
                cancellationToken.ThrowIfCancellationRequested();
                using Process fallbackProc = Process.Start(directLaunch);
                if (fallbackProc == null)
                    throw new InvalidOperationException("Windows did not start RobloxPlayerBeta.exe.");
                int fallbackPid = fallbackProc.Id;

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

                Program.Logger.Info($"Launching Roblox Home through RobloxPlayerBeta.exe directly for {username} because the Roblox protocol is unavailable.");
                SchedulePostLaunchSingletonSweep(1500);
                return fallbackPid;
            }
            finally
            {
                NativeLaunchGate.Release();
            }
        }

        private void EnsureBrowserTrackerId()
        {
            if (string.IsNullOrEmpty(BrowserTrackerID))
            {
                BrowserTrackerID = $"{Random.Shared.Next(100000, 175000)}{Random.Shared.Next(100000, 900000)}";
            }
        }

        public async Task<string> JoinServer(
            long PlaceID,
            string JobID = "",
            bool FollowUser = false,
            bool JoinVIP = false,
            bool Internal = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastNativeLaunchRateLimited = false;
            EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); } catch (Exception Ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {Ex}"); }

            if (AccountManager.ShuffleJobID && string.IsNullOrEmpty(JobID))
                JobID = await Utilities.GetRandomJobId(PlaceID, cancellationToken: cancellationToken).ConfigureAwait(false);

            (bool Success, string Ticket) ticketResult =
                await GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
            if (ticketResult.Success)
            {
                try
                {
                    // Always close any previous/stale Roblox client of the SAME account to prevent duplicate overlapping windows
                    RobloxProcessService.CloseAccountRobloxProcess(this);
                }
                catch (Exception x) { Program.Logger?.Error($"An error occurred attempting to close {Username}'s previous process: {x.Message}"); }

                string Ticket = ticketResult.Ticket;
                string Token = CSRFToken ?? string.Empty;

                string LinkCode = string.IsNullOrEmpty(JobID) ? string.Empty : LinkCodeRegex().Match(JobID)?.Groups[1]?.Value;
                string AccessCode = JobID;

                if (!string.IsNullOrEmpty(LinkCode))
                {
                    JoinVIP = true;
                    AccessCode = string.Empty;

                    RestRequest request = MakeRequest(string.Format("/games/{0}?privateServerLinkCode={1}", PlaceID, LinkCode), Method.Get)
                        .AddHeader("X-CSRF-TOKEN", Token)
                        .AddHeader("Referer", $"https://www.roblox.com/games/{PlaceID}/");

                    RestResponse response = await ExecuteAccountRestAsync(AccountManager.MainClient, "https://www.roblox.com/", request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.OK && ParseAccessCode(response, out string Code))
                    {
                        AccessCode = Code;
                    }
                }

                if (JoinVIP)
                {
                    var request = MakeRequest("user-settings-api/v1/user-settings", Method.Get)
                        .AddHeader("X-CSRF-TOKEN", Token)
                        .AddHeader("Referer", "https://www.roblox.com/my/account");

                    RestResponse result = await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", request, cancellationToken).ConfigureAwait(false);

                    bool needsPrivacyUpdate = false;
                    if (result.IsSuccessful && !string.IsNullOrEmpty(result.Content))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(result.Content);
                            if (doc.RootElement.TryGetProperty("whoCanInviteMeToPrivateServers", out var prop) &&
                                prop.ValueKind == System.Text.Json.JsonValueKind.String &&
                                !string.Equals(prop.GetString(), "AllUsers", StringComparison.OrdinalIgnoreCase))
                            {
                                needsPrivacyUpdate = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Parse private server privacy settings failed: {ex.Message}");
                        }
                    }

                    if (needsPrivacyUpdate)
                    {
                        bool changePrivacy = await AppDispatcher.InvokeAsync(() =>
                            Utilities.YesNoPrompt("Shiro", "Account Manager has detected your account's privacy settings do not allow you to join private servers.", "Would you like to change this setting to Everyone now?")
                        ).ConfigureAwait(false);

                        if (changePrivacy)
                        {
                            if (!await CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                                return "ERROR: Xác thực mã PIN thất bại.";

                            var setRequest = MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                                .AddHeader("X-CSRF-TOKEN", Token)
                                .AddHeader("Referer", "https://www.roblox.com/my/account")
                                .AddJsonBody(new { whoCanInviteMeToPrivateServers = "AllUsers" });

                            await ExecuteAccountRestAsync(AccountManager.ApiClient, "https://apis.roblox.com/", setRequest, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                long LaunchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string JoinUrl;

                if (JoinVIP)
                {
                    string accessParam = !string.IsNullOrEmpty(AccessCode) ? $"&accessCode={Uri.EscapeDataString(AccessCode)}" : string.Empty;
                    string linkParam = !string.IsNullOrEmpty(LinkCode) ? $"&linkCode={Uri.EscapeDataString(LinkCode)}" : string.Empty;
                    JoinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={PlaceID}{accessParam}{linkParam}";
                }
                else if (FollowUser)
                    JoinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={PlaceID}";
                else
                    JoinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(string.IsNullOrEmpty(JobID) ? "" : "Job")}&browserTrackerId={BrowserTrackerID}&placeId={PlaceID}{(string.IsNullOrEmpty(JobID) ? "" : "&gameId=" + JobID)}&isPlayTogetherGame=false";

                string NativeLaunchUri =
                    $"roblox-player:1+launchmode:play+gameinfo:{Ticket}+launchtime:{LaunchTime}+placelauncherurl:{Uri.EscapeDataString(JoinUrl)}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception launchException = null;
                int nativePid = 0;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    nativePid = await StartNativeGameAsync(NativeLaunchUri, Ticket, JoinUrl, Username, BrowserTrackerID, SecurityToken, cancellationToken).ConfigureAwait(false);
                    this.LastLaunchedProcessId = nativePid;

                    ObserveBackgroundTask(
                        MonitorNativeAuthRateLimitAsync(launchStartedUtc, this, cancellationToken),
                        "native auth rate-limit monitor");
                    AccountManager.Instance?.SchedulePresenceRefresh(this);

                    AccountManager.Instance?.NextAccount();

                    ObserveBackgroundTask(ObserveWindowPositionAsync(nativePid, cancellationToken), "Roblox window positioning");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    Program.Logger.Error($"Failed to launch Roblox for {Username}: {x}");
                    AccountManager.Instance?.NextAccount();
                }

                if (launchException != null)
                    return $"ERROR: Failed to launch Roblox for {Username}\n{launchException.Message}";

                AutoReconnectService.Instance.RegisterSession(this, PlaceID, JobID, FollowUser, JoinVIP, nativePid);
                return "Success";
            }
            else
            {
                if (LastAuthTicketRateLimited)
                    return "ERROR: Roblox đang giới hạn xác thực (HTTP 429). Hãy chờ một lúc hoặc đổi máy chủ VPN rồi thử lại.";

                return "ERROR: Không thể lấy Authentication Ticket từ Roblox. Phiên tài khoản vẫn được giữ nguyên, vui lòng kiểm tra Proton VPN hoặc thử lại sau.";
            }
        }

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

                Exception launchException = null;
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
                    Program.Logger.Error($"Failed to launch Roblox Home for {Username}: {x}");
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
