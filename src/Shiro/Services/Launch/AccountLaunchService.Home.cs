#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
        internal static Func<string, string, string, string?, string?, CancellationToken, Task<int>>? TestStartNativeHomeOverride;

        public static async Task<int> StartNativeHomeAsync(
            string nativeHomeUri,
            string ticket,
            string username,
            string? browserTrackerId = null,
            string? securityToken = null,
            CancellationToken cancellationToken = default)
        {
            await NativeLaunchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareNativeLaunchEnvironmentAsync(cancellationToken).ConfigureAwait(false);

                bool preferDirect = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
                if (preferDirect)
                {
                    var directResult = RobloxDirectLauncher.LaunchHomeDirect(username, ticket, browserTrackerId, securityToken); directResult.Process?.Dispose();
                    if (directResult.Success)
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                        SchedulePostLaunchSingletonSweep(500);
                        return directResult.ProcessId;
                    }

                    AppServices.Logger.Info($"[StartNativeHomeAsync] Direct binary launch unavailable ({directResult.ErrorMessage}); falling back to URI protocol.");
                }

                if (HasRobloxPlayerProtocolHandler())
                {
                    try
                    {
                        var existingClientPids = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                        using Process? launcher = Process.Start(new ProcessStartInfo
                        {
                            FileName = nativeHomeUri,
                            UseShellExecute = true
                        });

                        if (launcher == null) throw new InvalidOperationException("Windows did not start the Roblox protocol handler.");
                        int launcherPid = launcher.Id;
                        bool timedOut = !await WaitForExitAsync(launcher, 1500, cancellationToken).ConfigureAwait(false);
                        AppServices.Logger.Info(timedOut
                            ? $"Roblox client for {username} is running; released handoff gate."
                            : $"Launching Roblox Home through the registered protocol for {username}.");
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                        int resolvedHomePid = await ResolveNewClientPidAsync(existingClientPids, launcherPid, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                        SchedulePostLaunchSingletonSweep(500);
                        return resolvedHomePid;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception protocolError)
                    {
                        AppServices.Logger.Warn($"Roblox Home protocol launch failed for {username}; using the executable fallback.", protocolError);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                string playerPath = FindRobloxPlayerExecutable() ?? throw new FileNotFoundException("RobloxPlayerBeta.exe was not found.");
                ProcessStartInfo directLaunch = new(playerPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Environment.CurrentDirectory
                };
                directLaunch.ArgumentList.Add("--app");
                if (!string.IsNullOrWhiteSpace(ticket)) { directLaunch.ArgumentList.Add("-t"); directLaunch.ArgumentList.Add(ticket); }
                if (!string.IsNullOrWhiteSpace(browserTrackerId)) { directLaunch.ArgumentList.Add("-b"); directLaunch.ArgumentList.Add(browserTrackerId.Trim()); }
                cancellationToken.ThrowIfCancellationRequested();
                using Process fallbackProc = Process.Start(directLaunch) ?? throw new InvalidOperationException("Windows did not start RobloxPlayerBeta.exe.");
                int fallbackPid = fallbackProc.Id;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                await WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                AppServices.Logger.Info($"Launching Roblox Home through RobloxPlayerBeta.exe directly for {username} because the Roblox protocol is unavailable.");
                SchedulePostLaunchSingletonSweep(1500);
                return fallbackPid;
            }
            finally
            {
                NativeLaunchGate.Release();
            }
        }

        public async Task<string> LaunchAppHomeAsync(
            Account account,
            bool preferChrome = false,
            CancellationToken cancellationToken = default)
        {
            var result = await LaunchAppHomeResultAsync(account, preferChrome, cancellationToken).ConfigureAwait(false);
            return result.Message;
        }

        public async Task<LaunchResult> LaunchAppHomeResultAsync(
            Account account,
            bool preferChrome = false,
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
                    account.LastLaunchedProcessId = homePid;
                    if (LaunchTimingService.CurrentAttempt != null)
                    {
                        LaunchTimingService.CurrentAttempt.ProcessId = homePid;
                        LaunchTimingService.CurrentAttempt.SpawnMs = (long)Stopwatch.GetElapsedTime(LaunchTimingService.CurrentAttempt.AttemptStartTicks).TotalMilliseconds;
                    }
                    ObserveBackgroundTask(
                        MonitorNativeAuthRateLimitAsync(launchStartedUtc, account, cancellationToken),
                        "native auth rate-limit monitor");
                    AppServices.HostUiBridge.SchedulePresenceRefresh(account);
                    ObserveBackgroundTask(AdjustWindowPositionAsync(account, homePid, cancellationToken), "Roblox window positioning");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    AppServices.Logger?.Error($"Failed to launch Roblox Home for {account.Username}: {x}");
                }
                finally
                {
                    AppServices.HostUiBridge.SignalNextLaunchAccount();
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
