#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace Shiro.Services
{
    /// <summary>
    /// Direct binary process launcher for RobloxPlayerBeta.exe.
    /// Spawns the game client directly with auth ticket parameters, eliminating protocol handoff delays,
    /// capturing the exact Process ID immediately, and enabling instant singleton mutex closing.
    /// </summary>
    public static partial class RobloxDirectLauncher
    {
        public sealed class LaunchResult
        {
            public bool Success { get; init; }
            public Process? Process { get; init; }
            public int ProcessId { get; init; }
            public string? ExecutablePath { get; init; }
            public string? ErrorMessage { get; init; }
        }

        internal static bool CanSyncRotatedCookieForTarget(
            Account? target,
            long trackerUserId,
            string? trackerBrowserId,
            string? expectedBrowserId) =>
            RobloxCookieFileHelper.CanSyncRotatedCookieForTarget(target, trackerUserId, trackerBrowserId, expectedBrowserId);

        public static bool TryExtractTrackerMetadataFromCookiesDatFile(string filePath, out long userId, out string? browserTrackerId) =>
            RobloxCookieFileHelper.TryExtractTrackerMetadataFromCookiesDatFile(filePath, out userId, out browserTrackerId);

        /// <summary>
        /// Generates a random 11-12 digit browser tracker ID matching Roblox's native format.
        /// </summary>
        public static string GenerateRandomTrackerId()
        {
            long high = Random.Shared.Next(100000, 199999);
            long low = Random.Shared.Next(100000, 999999);
            return $"{high}{low}";
        }

        /// <summary>
        /// Validates and trims a Roblox server jobId, allowing 1..64 ASCII letters, digits, '-', or '_'.
        /// </summary>
        public static string SanitizeJobId(string? jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return string.Empty;

            string trimmed = jobId.Trim();
            if (trimmed.Length is < 1 or > 64)
                return string.Empty;

            foreach (char c in trimmed)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                    return string.Empty;
            }

            return trimmed;
        }

        private static string SanitizeCliArgumentValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);
        }

        private static string SanitizeBrowserTrackerId(string? browserTrackerId)
        {
            if (string.IsNullOrWhiteSpace(browserTrackerId))
                return string.Empty;

            string trimmed = browserTrackerId.Trim();
            foreach (char c in trimmed)
            {
                if (!char.IsAsciiDigit(c))
                    return string.Empty;
            }

            return trimmed;
        }

        /// <summary>
        /// Builds the command-line arguments for launching a Roblox game server directly.
        /// Both -t (ticket) and -j (joinUrl) must always be included so RobloxPlayerBeta.exe enters game-join mode instead of Home mode.
        /// </summary>
        public static string BuildGameLaunchArguments(string ticket, string joinUrl, string? browserTrackerId = null)
        {
            string safeTicket = SanitizeCliArgumentValue(ticket);
            string safeJoinUrl = SanitizeCliArgumentValue(joinUrl);
            string args = $"--app -t \"{safeTicket}\" -j \"{safeJoinUrl}\"";
            string safeTracker = SanitizeBrowserTrackerId(browserTrackerId);
            if (safeTracker.Length > 0)
            {
                args += $" -b {safeTracker}";
            }
            return args;
        }

        /// <summary>
        /// Builds the command-line arguments for launching Roblox Desktop App Home directly.
        /// Always includes -t (ticket) when available so RobloxPlayerBeta.exe redeems the ticket via WebLoginController
        /// and binds the session to its native HBA keypair instead of failing with 401 (code 9002).
        /// </summary>
        public static string BuildHomeLaunchArguments(string? ticket, string? browserTrackerId = null)
        {
            string args = "--app";
            string safeTicket = SanitizeCliArgumentValue(ticket);
            if (!string.IsNullOrWhiteSpace(safeTicket))
            {
                args += $" -t \"{safeTicket}\"";
            }
            string safeTracker = SanitizeBrowserTrackerId(browserTrackerId);
            if (safeTracker.Length > 0)
            {
                args += $" -b {safeTracker}";
            }
            return args;
        }

        /// <summary>
        /// Attempts to launch RobloxPlayerBeta.exe directly with credentials and server join instructions.
        /// </summary>
        public static LaunchResult LaunchDirect(string ticket, string joinUrl, string username, string? browserTrackerId = null, string? securityToken = null)
        {
            if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(joinUrl))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "Ticket or JoinUrl is empty."
                };
            }

            string? playerPath = Account.FindRobloxPlayerExecutablePublic();
            if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "RobloxPlayerBeta.exe could not be located on disk."
                };
            }

            try
            {
                string args = BuildGameLaunchArguments(ticket, joinUrl, browserTrackerId);

                Process? process = SpawnRobloxProcess(playerPath, args, username);
                if (process == null)
                {
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = "Process creation returned null for RobloxPlayerBeta.exe."
                    };
                }

                int pid = process.Id;
                Program.Logger?.Info($"[RobloxDirectLauncher] Successfully spawned RobloxPlayerBeta.exe directly for {username} (PID: {pid}).");

                RobloxProcessOptimizer.OptimizeLaunchedProcess(pid);

                return new LaunchResult
                {
                    Success = true,
                    Process = process,
                    ProcessId = pid,
                    ExecutablePath = playerPath
                };
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[RobloxDirectLauncher] Direct launch failed for {username}: {ex.Message}");
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Attempts to launch RobloxPlayerBeta.exe directly to the Home screen for an account.
        /// </summary>
        public static LaunchResult LaunchHomeDirect(string username, string? ticket = null, string? browserTrackerId = null, string? securityToken = null)
        {
            string? playerPath = Account.FindRobloxPlayerExecutablePublic();
            if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = "RobloxPlayerBeta.exe could not be located on disk."
                };
            }

            try
            {
                bool injectedCookie = string.IsNullOrWhiteSpace(ticket) &&
                    !string.IsNullOrWhiteSpace(securityToken) &&
                    InjectAccountCookieToLocalStorage(securityToken, browserTrackerId, username: username);

                string args = BuildHomeLaunchArguments(ticket, browserTrackerId);

                Process? process = SpawnRobloxProcess(playerPath, args, username);
                if (process == null)
                {
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = "Process creation returned null for RobloxPlayerBeta.exe."
                    };
                }

                int pid = process.Id;
                Program.Logger?.Info($"[RobloxDirectLauncher] Successfully spawned RobloxPlayerBeta.exe Home directly for {username} (PID: {pid}).");

                if (injectedCookie)
                {
                    SchedulePostSpawnCookieRestore(pid);
                }

                RobloxProcessOptimizer.OptimizeLaunchedProcess(pid);

                return new LaunchResult
                {
                    Success = true,
                    Process = process,
                    ProcessId = pid,
                    ExecutablePath = playerPath
                };
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[RobloxDirectLauncher] Direct Home launch failed for {username}: {ex.Message}");
                return new LaunchResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
