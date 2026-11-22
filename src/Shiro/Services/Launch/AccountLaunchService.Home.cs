#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
        private static readonly AsyncLocal<Func<string, string, string, string?, string?, CancellationToken, Task<int>>?> _testStartNativeHomeOverride = new();

        internal static Func<string, string, string, string?, string?, CancellationToken, Task<int>>? TestStartNativeHomeOverride
        {
            get => _testStartNativeHomeOverride.Value;
            set => _testStartNativeHomeOverride.Value = value;
        }

        public static async Task<int> StartNativeHomeAsync(
            string nativeHomeUri,
            string ticket,
            string username,
            string? browserTrackerId = null,
            string? securityToken = null,
            CancellationToken cancellationToken = default)
        {
            string playerExeArgs = RobloxDirectLauncher.BuildHomeLaunchArguments(ticket, browserTrackerId);
            return await RobloxLaunchExecutorHelper.ExecuteWithGateAndDirectPreferenceAsync(
                NativeLaunchGate,
                "StartNativeHomeAsync",
                () => RobloxDirectLauncher.LaunchHomeDirect(username, ticket, browserTrackerId, securityToken),
                nativeHomeUri,
                playerExeArgs,
                ticket,
                username,
                browserTrackerId,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> LaunchAppHomeAsync(
            Account account,
            CancellationToken cancellationToken = default)
        {
            var result = await LaunchAppHomeResultAsync(account, cancellationToken).ConfigureAwait(false);
            return result.Message;
        }

        public async Task<LaunchResult> LaunchAppHomeResultAsync(
            Account account,
            CancellationToken cancellationToken = default)
        {
            if (account == null) return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("AccountLaunch.InvalidAccount"));
            cancellationToken.ThrowIfCancellationRequested();

            account.EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { AppServices.Logger.Error($"Failed to patch ClientAppSettings: {ex}"); }

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
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Closing previous process for {account.Username} threw: {ex.Message}");
                }

                string ticket = ticketResult.Ticket;
                long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string nativeHomeUri =
                    $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{launchTime}+browsertrackerid:{account.BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception? launchException = null;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    int homePid = TestStartNativeHomeOverride != null
                        ? await TestStartNativeHomeOverride(nativeHomeUri, ticket, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false)
                        : await StartNativeHomeAsync(nativeHomeUri, ticket, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false);
                    CompleteNativeLaunchObservation(account, homePid, launchStartedUtc);
                    LaunchQueuePacingHelper.SignalNextOrDefer();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    AppServices.Logger?.Error($"Failed to launch Roblox Home for {account.Username}: {x}");
                    LaunchQueuePacingHelper.SignalNextOrDefer();
                }

                if (launchException != null)
                    return LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox Home for {account.Username}\n{launchException.Message}");

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
