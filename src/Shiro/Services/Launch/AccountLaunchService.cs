#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;

namespace Shiro.Services
{
    /// <summary>
    /// Service responsible for launching Roblox game clients and Desktop App Home.
    /// Handles process creation, protocol invocations, singleton sweep coordination,
    /// and pre/post-launch diagnostics for Account models.
    /// </summary>
    public sealed partial class AccountLaunchService
    {
        public static AccountLaunchService Instance { get; } = new();

        private static readonly SemaphoreSlim NativeLaunchGate = new(1, 1);

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

        /// <summary>
        /// Constructs and cryptographically signs an authentication ticket request for Roblox launch using HBA binding.
        /// </summary>
        public static HttpRequestMessage CreateTicketRequest(Account account, string? csrfToken = null, string body = "{}")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/");
            account.ApplySessionFingerprintAndHba(request, body);
            if (!string.IsNullOrEmpty(csrfToken))
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
            request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return request;
        }

        public static async Task PrepareNativeLaunchEnvironmentAsync(CancellationToken cancellationToken)
        {
            await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

            if (AppSettings.General.Get<bool>("AutoCleanZombiesOnLaunch", true))
            {
                RobloxProcessService.CleanZombieProcesses();
            }

            if (AppSettings.General.Get<bool>("EnableMultiRbx", true))
            {
                try { RobloxSingletonService.Sweep(force: true); }
                catch (Exception ex) { Program.Logger?.Debug($"Pre-launch singleton sweep failed: {ex.Message}"); }
            }
        }

        public static void SchedulePostLaunchSingletonSweep(int delayMs = 500)
        {
            if (!AppSettings.General.Get<bool>("EnableMultiRbx", true))
                return;

            ObserveBackgroundTask(Task.Run(async () =>
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                try { RobloxSingletonService.Sweep(force: true); }
                catch (Exception ex) { Program.Logger?.Debug($"Post-launch singleton sweep failed: {ex.Message}"); }
            }), "post-launch singleton sweep");
        }

        public static async Task<int> StartNativeGameAsync(
            string nativeLaunchUri,
            string ticket,
            string joinUrl,
            string username,
            string? browserTrackerId = null,
            string? securityToken = null,
            CancellationToken cancellationToken = default)
        {
            await NativeLaunchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareNativeLaunchEnvironmentAsync(cancellationToken).ConfigureAwait(false);

                bool preferDirect = AppSettings.General.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
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
                        using Process? launcher = Process.Start(new ProcessStartInfo
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
                string? playerPath = FindRobloxPlayerExecutable();
                if (string.IsNullOrWhiteSpace(playerPath))
                    throw new FileNotFoundException("RobloxPlayerBeta.exe was not found.");

                ProcessStartInfo directLaunch = new ProcessStartInfo(playerPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };
                AddDirectGameArguments(directLaunch, ticket, joinUrl);
                cancellationToken.ThrowIfCancellationRequested();
                using Process? fallbackProc = Process.Start(directLaunch);
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

        public async Task<string> JoinServerAsync(
            Account account,
            long placeId,
            string jobId = "",
            bool followUser = false,
            bool joinVip = false,
            bool internalCall = false,
            CancellationToken cancellationToken = default)
        {
            var result = await JoinServerResultAsync(
                account,
                placeId,
                jobId,
                followUser,
                joinVip,
                internalCall,
                cancellationToken).ConfigureAwait(false);
            return result.ToLegacyString();
        }

        public async Task<LaunchResult> JoinServerResultAsync(
            Account account,
            long placeId,
            string jobId = "",
            bool followUser = false,
            bool joinVip = false,
            bool internalCall = false,
            CancellationToken cancellationToken = default)
        {
            if (account == null) return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("AccountLaunch.InvalidAccount"));
            cancellationToken.ThrowIfCancellationRequested();
            account.LastNativeLaunchRateLimited = false;
            account.EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {ex}"); }

            if (AppSettings.ShuffleJobId && string.IsNullOrEmpty(jobId))
                jobId = await Utilities.GetRandomJobId(placeId, cancellationToken: cancellationToken).ConfigureAwait(false);

            await account.TryRebindSessionToCurrentNetworkAsync(force: false, cancellationToken).ConfigureAwait(false);

            (bool Success, string Ticket) ticketResult =
                await account.GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
            if (ticketResult.Success)
            {
                try
                {
                    RobloxProcessService.CloseAccountRobloxProcess(account);
                }
                catch (Exception x) { Program.Logger?.Error($"An error occurred attempting to close {account.Username}'s previous process: {x.Message}"); }

                string ticket = ticketResult.Ticket;
                string token = account.CSRFToken ?? string.Empty;

                string linkCode = string.IsNullOrEmpty(jobId) ? string.Empty : (LinkCodeRegex().Match(jobId)?.Groups[1]?.Value ?? string.Empty);
                string accessCode = jobId;

                if (!string.IsNullOrEmpty(linkCode))
                {
                    joinVip = true;
                    accessCode = string.Empty;

                    RestRequest request = account.MakeRequest(string.Format("/games/{0}?privateServerLinkCode={1}", placeId, linkCode), Method.Get)
                        .AddHeader("X-CSRF-TOKEN", token)
                        .AddHeader("Referer", $"https://www.roblox.com/games/{placeId}/");

                    RestResponse? response = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Main, "https://www.roblox.com/", request, cancellationToken).ConfigureAwait(false);

                    if (response != null && response.StatusCode == HttpStatusCode.OK && account.ParseAccessCode(response, out string code))
                    {
                        accessCode = code;
                    }
                }

                if (joinVip)
                {
                    var request = account.MakeRequest("user-settings-api/v1/user-settings", Method.Get)
                        .AddHeader("X-CSRF-TOKEN", token)
                        .AddHeader("Referer", "https://www.roblox.com/my/account");

                    RestResponse? result = await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", request, cancellationToken).ConfigureAwait(false);

                    bool needsPrivacyUpdate = false;
                    if (result != null && result.IsSuccessful && !string.IsNullOrEmpty(result.Content))
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
                            if (!await account.CheckPinAsync(true, cancellationToken).ConfigureAwait(false))
                                return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("AccountLaunch.PinAuthFailed"));

                            var setRequest = account.MakeRequest("user-settings-api/v1/user-settings", Method.Post)
                                .AddHeader("X-CSRF-TOKEN", token)
                                .AddHeader("Referer", "https://www.roblox.com/my/account")
                                .AddJsonBody(new { whoCanInviteMeToPrivateServers = "AllUsers" });

                            await account.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", setRequest, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string joinUrl;

                if (joinVip)
                {
                    string accessParam = !string.IsNullOrEmpty(accessCode) ? $"&accessCode={Uri.EscapeDataString(accessCode)}" : string.Empty;
                    string linkParam = !string.IsNullOrEmpty(linkCode) ? $"&linkCode={Uri.EscapeDataString(linkCode)}" : string.Empty;
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={placeId}{accessParam}{linkParam}";
                }
                else if (followUser)
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={placeId}";
                else
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(string.IsNullOrEmpty(jobId) ? "" : "Job")}&browserTrackerId={account.BrowserTrackerID}&placeId={placeId}{(string.IsNullOrEmpty(jobId) ? "" : "&gameId=" + jobId)}&isPlayTogetherGame=false";

                string nativeLaunchUri =
                    $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{launchTime}+placelauncherurl:{Uri.EscapeDataString(joinUrl)}+browsertrackerid:{account.BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception? launchException = null;
                int nativePid = 0;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    nativePid = await StartNativeGameAsync(nativeLaunchUri, ticket, joinUrl, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false);
                    account.LastLaunchedProcessId = nativePid;

                    ObserveBackgroundTask(
                        MonitorNativeAuthRateLimitAsync(launchStartedUtc, account, cancellationToken),
                        "native auth rate-limit monitor");
                    AppSettings.SchedulePresenceRefresh(account);

                    AppSettings.SignalNextLaunchAccount();

                    ObserveBackgroundTask(AdjustWindowPositionAsync(account, nativePid, cancellationToken), "Roblox window positioning");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    Program.Logger?.Error($"Failed to launch Roblox for {account.Username}: {x}");
                    AppSettings.SignalNextLaunchAccount();
                }

                if (launchException != null)
                    return LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox for {account.Username}\n{launchException.Message}");

                AutoReconnectService.Instance.RegisterSession(account, placeId, jobId, followUser, joinVip, nativePid);
                return LaunchResult.Success();
            }
            else
            {
                if (account.LastAuthTicketRateLimited)
                    return LaunchResult.Fail(LaunchOutcome.RateLimited, Shiro.Services.LocalizationService.Get("AccountLaunch.RateLimited"));

                return LaunchResult.Fail(LaunchOutcome.TicketError, Shiro.Services.LocalizationService.Get("AccountLaunch.TicketFailed"));
            }
        }
    }
}
