#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed partial class AccountLaunchService
    {
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

                bool preferDirect = AppSettings.General.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
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
                        using Process? launcher = Process.Start(new ProcessStartInfo
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
                string? playerPath = FindRobloxPlayerExecutable();
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
                using Process? fallbackProc = Process.Start(directLaunch);
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

        public async Task<string> LaunchAppHomeAsync(
            Account account,
            bool preferChrome = false,
            CancellationToken cancellationToken = default)
        {
            var result = await LaunchAppHomeResultAsync(account, preferChrome, cancellationToken).ConfigureAwait(false);
            return result.ToLegacyString();
        }

        public async Task<LaunchResult> LaunchAppHomeResultAsync(
            Account account,
            bool preferChrome = false,
            CancellationToken cancellationToken = default)
        {
            if (account == null) return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("Auto_AccountLaunchSer_230"));
            cancellationToken.ThrowIfCancellationRequested();
            if (preferChrome || AppSettings.General.Get<bool>("UseChromeHome", false))
                return await LaunchHomeInChromeResultAsync(account, cancellationToken).ConfigureAwait(false);

            account.EnsureBrowserTrackerId();

            try { ClientSettingsPatcher.PatchSettings(); }
            catch (Exception ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {ex}"); }

            bool preferDirect = AppSettings.General.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
            if (preferDirect && !string.IsNullOrWhiteSpace(account.SecurityToken))
            {
                var directResult = RobloxDirectLauncher.LaunchHomeDirect(account.Username, ticket: null, account.BrowserTrackerID, account.SecurityToken);
                if (directResult.Success)
                {
                    account.LastLaunchedProcessId = directResult.ProcessId;
                    AppSettings.SchedulePresenceRefresh(account);
                    AppSettings.SignalNextLaunchAccount();
                    ObserveBackgroundTask(AdjustWindowPositionAsync(account, directResult.ProcessId, cancellationToken), "Roblox window positioning");
                    return LaunchResult.Success();
                }
            }

            await account.TryRebindSessionToCurrentNetworkAsync(force: false, cancellationToken).ConfigureAwait(false);

            (bool Success, string Ticket) ticketResult =
                await account.GetAuthTicketAsync(cancellationToken).ConfigureAwait(false);
            if (ticketResult.Success)
            {
                try
                {
                    RobloxProcessService.CloseAccountRobloxProcess(account);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Closing previous process for {account.Username} threw: {ex.Message}");
                }

                string ticket = ticketResult.Ticket;
                long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string nativeHomeUri =
                    $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{launchTime}+browsertrackerid:{account.BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                Exception? launchException = null;
                try
                {
                    DateTime launchStartedUtc = DateTime.UtcNow;
                    int homePid = await StartNativeHomeAsync(nativeHomeUri, ticket, account.Username, account.BrowserTrackerID, account.SecurityToken, cancellationToken).ConfigureAwait(false);
                    account.LastLaunchedProcessId = homePid;
                    ObserveBackgroundTask(
                        MonitorNativeAuthRateLimitAsync(launchStartedUtc, account, cancellationToken),
                        "native auth rate-limit monitor");
                    AppSettings.SchedulePresenceRefresh(account);

                    AppSettings.SignalNextLaunchAccount();
                    ObserveBackgroundTask(AdjustWindowPositionAsync(account, homePid, cancellationToken), "Roblox window positioning");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    launchException = x;
                    Program.Logger?.Error($"Failed to launch Roblox Home for {account.Username}: {x}");
                }

                if (launchException != null)
                    return LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to launch Roblox Home for {account.Username}\n{launchException.Message}");

                return LaunchResult.Success();
            }
            else
            {
                if (account.LastAuthTicketRateLimited)
                    return LaunchResult.Fail(LaunchOutcome.RateLimited, Shiro.Services.LocalizationService.Get("Auto_AccountLaunchSer_231"));

                return LaunchResult.Fail(LaunchOutcome.TicketError, Shiro.Services.LocalizationService.Get("Auto_AccountLaunchSer_232"));
            }
        }

        public async Task<string> LaunchHomeInChromeAsync(Account account, CancellationToken cancellationToken = default)
        {
            var result = await LaunchHomeInChromeResultAsync(account, cancellationToken).ConfigureAwait(false);
            return result.ToLegacyString();
        }

        public async Task<LaunchResult> LaunchHomeInChromeResultAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (account == null) return LaunchResult.Fail(LaunchOutcome.Failed, Shiro.Services.LocalizationService.Get("Auto_AccountLaunchSer_230"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool useConfiguredProxy = AppSettings.General.Get<bool>("UseProxies", false) &&
                    ProxyManagerService.GetCachedProxyStrings().Count > 0;

                if (useConfiguredProxy)
                {
                    await AccountBrowser.OpenAsync(
                        account,
                        "https://www.roblox.com/home",
                        UseStealth: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    AppSettings.SchedulePresenceRefresh(account);
                    return LaunchResult.BrowserHome();
                }

                bool opened = await ChromiumHomeBrowser.OpenAsync(account, cancellationToken).ConfigureAwait(false);
                if (opened)
                {
                    AppSettings.SchedulePresenceRefresh(account);
                    return LaunchResult.BrowserHome();
                }

                Program.Logger.Warn($"The CDP Home path failed for {account.Username}; falling back to the legacy Chromium browser path.");
                await AccountBrowser.OpenAsync(
                    account,
                    "https://www.roblox.com/home",
                    UseStealth: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AppSettings.SchedulePresenceRefresh(account);
                return LaunchResult.BrowserHome();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Program.Logger.Error($"Failed to open Roblox Home in Chrome for {account.Username}: {exception}");
                return LaunchResult.Fail(LaunchOutcome.Failed, $"ERROR: Failed to open Roblox Home in Chrome\n{exception.Message}");
            }
        }
    }
}
