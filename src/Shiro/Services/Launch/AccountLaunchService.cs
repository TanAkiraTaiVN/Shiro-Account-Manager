#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
        private static readonly AsyncLocal<Func<string, string, string, string, string?, string?, CancellationToken, Task<int>>?> _testStartNativeGameOverride = new();
        private static readonly AsyncLocal<Func<Account, CancellationToken, Task<(bool Success, string Ticket)>>?> _testGetAuthTicketOverride = new();
        private static readonly AsyncLocal<Func<bool>?> _testPrivacyConfirmOverride = new();

        internal static Func<string, string, string, string, string?, string?, CancellationToken, Task<int>>? TestStartNativeGameOverride
        {
            get => _testStartNativeGameOverride.Value;
            set => _testStartNativeGameOverride.Value = value;
        }

        internal static Func<Account, CancellationToken, Task<(bool Success, string Ticket)>>? TestGetAuthTicketOverride
        {
            get => _testGetAuthTicketOverride.Value;
            set => _testGetAuthTicketOverride.Value = value;
        }

        internal static Func<bool>? TestPrivacyConfirmOverride
        {
            get => _testPrivacyConfirmOverride.Value;
            set => _testPrivacyConfirmOverride.Value = value;
        }

        [GeneratedRegex(@"(?:privateServerLinkCode|shareCode|code)=([^&\s]+)", RegexOptions.IgnoreCase)]
        private static partial Regex LinkCodeRegex();

        internal static void AddDirectGameArguments(ProcessStartInfo launchInfo, string ticket, string joinUrl, string? browserTrackerId = null)
        {
            foreach (var a in new[] { "--app", "-t", ticket, "-j", joinUrl }) launchInfo.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(browserTrackerId))
            {
                launchInfo.ArgumentList.Add("-b");
                launchInfo.ArgumentList.Add(browserTrackerId.Trim());
            }
        }

        /// <summary>
        /// Constructs and cryptographically signs Roblox's pre-ticket client-assertion request.
        /// </summary>
        public static HttpRequestMessage CreateClientAssertionRequest(Account account, bool includeHba = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://auth.roblox.com/v1/client-assertion/");
            AppServices.AccountApiService.ApplySessionFingerprintAndHba(account, request, string.Empty, includeHba: includeHba);
            ApplyBrowserOriginHeaders(request);
            return request;
        }

        /// <summary>
        /// Fetches Roblox's /v1/client-assertion/ payload before requesting an authentication ticket,
        /// falling back to "{}" to match web client behavior when unavailable.
        /// </summary>
        public static async Task<string> FetchClientAssertionBodyAsync(
            Account account,
            HttpClient httpClient,
            bool includeHba = true,
            CancellationToken cancellationToken = default)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var request = CreateClientAssertionRequest(account, includeHba);
                    using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (response == null)
                        return "{}";

                    AppServices.AccountSessionClient.TryUpdateRotatedCookie(account, response);
                    if (!response.IsSuccessStatusCode)
                        return "{}";

                    string raw = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
                    if (string.IsNullOrEmpty(raw))
                        return "{}";

                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object ? raw : "{}";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (System.Net.Http.HttpRequestException ex) when (attempt == 0)
                {
                    // Retry once to recover from a stale pooled HTTP/2 socket invalidated by a recent VPN/network change.
                    AppServices.Logger?.Debug($"[AccountLaunchService] Retrying client-assertion probe for {account.Username} after transport error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[AccountLaunchService] client-assertion probe fell back to empty payload for {account.Username}: {ex.Message}");
                    return "{}";
                }
            }

            return "{}";
        }

        /// <summary>
        /// Constructs and cryptographically signs an authentication ticket request for Roblox launch using HBA binding.
        /// </summary>
        public static HttpRequestMessage CreateTicketRequest(Account account, string? csrfToken = null, string body = "{}", bool includeHba = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket");
            AppServices.AccountApiService.ApplySessionFingerprintAndHba(account, request, body, includeHba: includeHba);
            if (!string.IsNullOrEmpty(csrfToken))
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
            ApplyBrowserOriginHeaders(request);
            request.Headers.TryAddWithoutValidation("rbxauthenticationnegotiation", "1");
            request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return request;
        }

        private static void ApplyBrowserOriginHeaders(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        public static async Task PrepareNativeLaunchEnvironmentAsync(CancellationToken cancellationToken)
        {
            await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);

            if (AppServices.GeneralSettings.Get<bool>("AutoCleanZombiesOnLaunch", true))
            {
                RobloxProcessService.CleanZombieProcesses();
            }

            if (AppServices.GeneralSettings.Get<bool>("EnableMultiRbx", true))
            {
                try { RobloxSingletonService.Sweep(force: true); }
                catch (Exception ex) { AppServices.Logger?.Debug($"Pre-launch singleton sweep failed: {ex.Message}"); }
            }
        }

        public static void SchedulePostLaunchSingletonSweep(int delayMs = 500)
        {
            if (!AppServices.GeneralSettings.Get<bool>("EnableMultiRbx", true))
                return;

            ObserveBackgroundTask(Task.Run(async () =>
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                try { RobloxSingletonService.Sweep(force: true); }
                catch (Exception ex) { AppServices.Logger?.Debug($"Post-launch singleton sweep failed: {ex.Message}"); }
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
            _ = securityToken;
            string playerExeArgs = RobloxDirectLauncher.BuildGameLaunchArguments(ticket, joinUrl, browserTrackerId);
            return await RobloxLaunchExecutorHelper.ExecuteWithGateAndDirectPreferenceAsync(
                NativeLaunchGate,
                "StartNativeGameAsync",
                () => RobloxDirectLauncher.LaunchDirect(ticket, joinUrl, username, browserTrackerId),
                nativeLaunchUri,
                playerExeArgs,
                ticket,
                username,
                browserTrackerId,
                cancellationToken).ConfigureAwait(false);
        }

        internal static void CompleteNativeLaunchObservation(Account account, int launchedPid, DateTime launchStartedUtc)
        {
            account.LastLaunchedProcessId = launchedPid;
            if (LaunchTimingService.CurrentAttempt != null)
            {
                LaunchTimingService.CurrentAttempt.ProcessId = launchedPid;
                LaunchTimingService.CurrentAttempt.SpawnMs = (long)Stopwatch.GetElapsedTime(LaunchTimingService.CurrentAttempt.AttemptStartTicks).TotalMilliseconds;
            }

            ObserveBackgroundTask(
                MonitorNativeAuthRateLimitAsync(launchStartedUtc, account, CancellationToken.None),
                "native auth rate-limit monitor");
            AppServices.HostUiBridge.SchedulePresenceRefresh(account);
            ObserveBackgroundTask(AdjustWindowPositionAsync(account, launchedPid, CancellationToken.None), "Roblox window positioning");
        }

        public async Task<string> JoinServerAsync(
            Account account,
            long placeId,
            string jobId = "",
            bool followUser = false,
            bool joinVip = false,
            CancellationToken cancellationToken = default)
        {
            var result = await JoinServerResultAsync(
                account,
                placeId,
                jobId,
                followUser,
                joinVip,
                cancellationToken).ConfigureAwait(false);
            return result.Message;
        }

        public async Task<LaunchResult> JoinServerResultAsync(
            Account account,
            long placeId,
            string jobId = "",
            bool followUser = false,
            bool joinVip = false,
            CancellationToken cancellationToken = default)
        {
            if (account == null) return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("AccountLaunch.InvalidAccount"));
            cancellationToken.ThrowIfCancellationRequested();
            account.LastNativeLaunchRateLimited = false;
            account.EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { AppServices.Logger.Error($"Failed to patch ClientAppSettings: {ex}"); }

            if (AppServices.ServerBrowserState.ShuffleJobId && !followUser && !joinVip && string.IsNullOrEmpty(jobId))
                jobId = await Utilities.GetRandomJobId(placeId, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (TestGetAuthTicketOverride != null)
            {
                bool shouldForceRebind = account.HasHbaBinding && !string.Equals(account.LastBoundIpSignature, RobloxHbaService.GetNetworkSignatureForAccount(account), StringComparison.Ordinal);
                await account.TryRebindSessionToCurrentNetworkAsync(force: shouldForceRebind, cancellationToken).ConfigureAwait(false);
            }
            long ticketStartTicks = Stopwatch.GetTimestamp();
            (bool Success, string Ticket) ticketResult = TestGetAuthTicketOverride != null
                ? await TestGetAuthTicketOverride(account, cancellationToken).ConfigureAwait(false)
                : await account.GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
            if (LaunchTimingService.CurrentAttempt != null)
                LaunchTimingService.CurrentAttempt.TicketMs = (long)Stopwatch.GetElapsedTime(ticketStartTicks).TotalMilliseconds;
            if (ticketResult.Success)
            {
                try
                {
                    RobloxProcessService.CloseAccountRobloxProcess(account);
                }
                catch (Exception x) { AppServices.Logger?.Error($"An error occurred attempting to close {account.Username}'s previous process: {x.Message}"); }

                string ticket = ticketResult.Ticket;
                string token = account.CSRFToken ?? string.Empty;

                string linkCode = string.IsNullOrEmpty(jobId) ? string.Empty : (LinkCodeRegex().Match(jobId)?.Groups[1]?.Value ?? string.Empty);
                string accessCode = jobId;

                if (!string.IsNullOrEmpty(linkCode))
                {
                    joinVip = true;
                    accessCode = string.Empty;

                    RestRequest request = AppServices.AccountApiService.MakeRequest(account, string.Format("/games/{0}?privateServerLinkCode={1}", placeId, linkCode), Method.Get)
                        .AddHeader("X-CSRF-TOKEN", token)
                        .AddHeader("Referer", $"https://www.roblox.com/games/{placeId}/");

                    RestResponse? response = await AppServices.AccountApiService.ExecuteAccountRestAsync(account, AppServices.RobloxApi.Main, "https://www.roblox.com/", request, cancellationToken).ConfigureAwait(false);

                    if (response != null && response.StatusCode == HttpStatusCode.OK && RobloxSettingsApiService.ParseAccessCode(response, out string code))
                    {
                        accessCode = code;
                    }
                }

                if (joinVip)
                {
                    bool privacyOk = await RobloxSettingsApiService.Default.EnsurePrivateServerInvitesAllowedAsync(
                        account,
                        token,
                        async () => TestPrivacyConfirmOverride != null
                            ? TestPrivacyConfirmOverride()
                            : await Utilities.YesNoPromptAsync(
                                "Shiro",
                                "Account Manager has detected your account's privacy settings do not allow you to join private servers.",
                                "Would you like to change this setting to Everyone now?").ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    if (!privacyOk)
                        return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("AccountLaunch.PinAuthFailed"));
                }

                long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string safeJob = RobloxDirectLauncher.SanitizeJobId(jobId); string joinUrl;

                if (joinVip)
                {
                    string accessParam = !string.IsNullOrEmpty(accessCode) ? $"&accessCode={Uri.EscapeDataString(accessCode)}" : string.Empty;
                    string linkParam = !string.IsNullOrEmpty(linkCode) ? $"&linkCode={Uri.EscapeDataString(linkCode)}" : string.Empty;
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={placeId}{accessParam}{linkParam}";
                }
                else if (followUser)
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={placeId}";
                else
                    joinUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(string.IsNullOrEmpty(safeJob) ? "" : "Job")}&browserTrackerId={account.BrowserTrackerID}&placeId={placeId}{(string.IsNullOrEmpty(safeJob) ? "" : "&gameId=" + Uri.EscapeDataString(safeJob))}&isPlayTogetherGame=false";

                string nativeLaunchUri =
                    $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{launchTime}+placelauncherurl:{Uri.EscapeDataString(joinUrl)}+browsertrackerid:{account.BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception? launchException = null;
                int nativePid = 0;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    nativePid = TestStartNativeGameOverride != null
                        ? await TestStartNativeGameOverride(nativeLaunchUri, ticket, joinUrl, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false)
                        : await StartNativeGameAsync(nativeLaunchUri, ticket, joinUrl, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false);
                    CompleteNativeLaunchObservation(account, nativePid, launchStartedUtc);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    AppServices.Logger?.Error($"Failed to launch Roblox for {account.Username}: {x}");
                    LaunchQueuePacingHelper.SignalNextOrDefer();
                }

                if (launchException != null)
                    return LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox for {account.Username}\n{launchException.Message}");

                AutoReconnectService.Instance.RegisterSession(account, placeId, jobId, followUser, joinVip, nativePid);
                LaunchQueuePacingHelper.SignalNextOrDefer();
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
