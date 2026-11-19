#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static partial class RobloxLaunchExecutorHelper
    {
        [GeneratedRegex("^\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase)]
        private static partial Regex QuotedExecutableRegex();

        [GeneratedRegex(@"^([^\s""]+\.exe)(?:\s|$)", RegexOptions.IgnoreCase)]
        private static partial Regex UnquotedExecutableRegex();

        internal static Func<string, string, string, Process?>? TestSpawnRobloxProcessOverride;
        internal static Func<string, Process?>? TestProtocolShellStartOverride;
        internal static int? TestPostSpawnDelayMsOverride;

        internal static async Task<int> ExecuteWithGateAndDirectPreferenceAsync(
            SemaphoreSlim launchGate,
            string callerName,
            Func<RobloxDirectLauncher.LaunchResult> tryDirectLaunch,
            string protocolUri,
            string playerExeArgs,
            string ticket,
            string username,
            string? trackerId,
            CancellationToken cancellationToken)
        {
            await launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AccountLaunchService.PrepareNativeLaunchEnvironmentAsync(cancellationToken).ConfigureAwait(false);

                bool preferDirect = AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.PreferDirectLaunch, false);
                if (preferDirect)
                {
                    var directResult = tryDirectLaunch();
                    directResult.Process?.Dispose();
                    if (directResult.Success)
                    {
                        await Task.Delay(TestPostSpawnDelayMsOverride ?? 250, cancellationToken).ConfigureAwait(false);
                        await AccountLaunchService.WaitForInstallerAsync(cancellationToken).ConfigureAwait(false);
                        AccountLaunchService.SchedulePostLaunchSingletonSweep(500);
                        return directResult.ProcessId;
                    }

                    AppServices.Logger.Info($"[{callerName}] Direct binary launch unavailable ({directResult.ErrorMessage}); falling back to URI protocol.");
                }

                return await ExecuteNativeLaunchAsync(
                    protocolUri,
                    playerExeArgs,
                    ticket,
                    username,
                    trackerId,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                launchGate.Release();
            }
        }

        internal static async Task<int> ExecuteNativeLaunchAsync(
            string protocolUri,
            string playerExeArgs,
            string ticket,
            string username,
            string? trackerId,
            CancellationToken ct)
        {
            if (AccountLaunchService.HasRobloxPlayerProtocolHandler())
            {
                try
                {
                    var existingClientPids = new HashSet<int>(RobloxProcessService.GetRunningRobloxPids());
                    using Process? launcher = StartProtocolProcess(protocolUri, username);

                    if (launcher == null)
                        throw new InvalidOperationException("Windows did not start the Roblox protocol handler.");

                    int launcherPid = launcher.Id;

                    if (!await AccountLaunchService.WaitForExitAsync(launcher, 1500, ct).ConfigureAwait(false))
                        AppServices.Logger.Info($"Roblox client for {username} is running; released handoff gate.");

                    await Task.Delay(TestPostSpawnDelayMsOverride ?? 250, ct).ConfigureAwait(false);
                    await AccountLaunchService.WaitForInstallerAsync(ct).ConfigureAwait(false);

                    int resolvedPid = await AccountLaunchService.ResolveNewClientPidAsync(
                        existingClientPids,
                        launcherPid,
                        TimeSpan.FromSeconds(10),
                        ct).ConfigureAwait(false);

                    if (resolvedPid > 0)
                        RobloxProcessOptimizer.OptimizeLaunchedProcess(resolvedPid);

                    AppServices.Logger.Info($"Launching Roblox through the registered protocol for {username} (Game PID: {resolvedPid}).");
                    AccountLaunchService.SchedulePostLaunchSingletonSweep(500);
                    return resolvedPid;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception protocolError)
                {
                    AppServices.Logger.Warn($"Roblox protocol launch failed for {username}; using the executable fallback.", protocolError);
                }
            }

            ct.ThrowIfCancellationRequested();
            string? playerPath = AccountLaunchService.FindRobloxPlayerExecutable();
            if (string.IsNullOrWhiteSpace(playerPath))
                throw new FileNotFoundException("RobloxPlayerBeta.exe was not found.");

            string effectiveArgs = !string.IsNullOrWhiteSpace(playerExeArgs)
                ? playerExeArgs
                : RobloxDirectLauncher.BuildHomeLaunchArguments(ticket, trackerId);

            ct.ThrowIfCancellationRequested();
            using Process? fallbackProc = SpawnProcess(playerPath, effectiveArgs, username);
            if (fallbackProc == null)
                throw new InvalidOperationException("Windows did not start RobloxPlayerBeta.exe.");

            int fallbackPid = fallbackProc.Id;
            RobloxProcessOptimizer.OptimizeLaunchedProcess(fallbackPid);

            await Task.Delay(TestPostSpawnDelayMsOverride ?? 1000, ct).ConfigureAwait(false);
            await AccountLaunchService.WaitForInstallerAsync(ct).ConfigureAwait(false);

            AppServices.Logger.Info($"Launching RobloxPlayerBeta.exe directly for {username} because the Roblox protocol is unavailable.");
            AccountLaunchService.SchedulePostLaunchSingletonSweep(1500);
            return fallbackPid;
        }

        private static Process? SpawnProcess(string exePath, string args, string username) =>
            TestSpawnRobloxProcessOverride != null
                ? TestSpawnRobloxProcessOverride(exePath, args, username)
                : RobloxDirectLauncher.SpawnRobloxProcess(exePath, args, username);

        private static Process? StartProtocolProcess(string protocolUri, string username)
        {
            if (TryResolveProtocolHandlerExecutable(out string? protocolExe) &&
                !string.IsNullOrWhiteSpace(protocolExe) &&
                (TestSpawnRobloxProcessOverride != null ||
                 (RobloxDirectLauncher.IsTokenDeElevationEnabled() && RobloxDirectLauncher.IsCurrentProcessElevated())))
            {
                string safeUri = protocolUri
                    .Replace("\"", string.Empty, StringComparison.Ordinal)
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal);
                return SpawnProcess(protocolExe, $"\"{safeUri}\"", username);
            }

            if (TestProtocolShellStartOverride != null)
                return TestProtocolShellStartOverride(protocolUri);

            return Process.Start(new ProcessStartInfo
            {
                FileName = protocolUri,
                UseShellExecute = true
            });
        }

        private static bool TryResolveProtocolHandlerExecutable([NotNullWhen(true)] out string? executablePath)
        {
            executablePath = null;
            string? command = AccountLaunchService.ReadProtocolOpenCommand();
            if (string.IsNullOrWhiteSpace(command))
                return false;

            string trimmed = command.Trim();
            Match match = QuotedExecutableRegex().Match(trimmed);
            if (!match.Success)
                match = UnquotedExecutableRegex().Match(trimmed);

            if (!match.Success)
                return false;

            string candidate = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                return false;

            executablePath = candidate;
            return true;
        }
    }
}
