#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Shiro.Classes;

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
            string? expectedBrowserId)
        {
            if (target == null)
                return false;

            // If RobloxCookies.dat contains a concrete rbxuid from RobloxPlayerBeta.exe, it MUST match target.UserID
            if (trackerUserId > 0 && target.UserID > 0 && trackerUserId != target.UserID)
                return false;

            // If browserid is present in RobloxCookies.dat, verify it matches either the account's BrowserTrackerID or the injected tracker ID
            if (!string.IsNullOrWhiteSpace(trackerBrowserId))
            {
                bool matchesAccountTracker = !string.IsNullOrWhiteSpace(target.BrowserTrackerID) &&
                    string.Equals(trackerBrowserId, target.BrowserTrackerID, StringComparison.Ordinal);
                bool matchesInjectedTracker = !string.IsNullOrWhiteSpace(expectedBrowserId) &&
                    string.Equals(trackerBrowserId, expectedBrowserId, StringComparison.Ordinal);

                if (!matchesAccountTracker && !matchesInjectedTracker && (trackerUserId <= 0 || target.UserID <= 0))
                    return false;
            }

            return true;
        }

        public static bool TryExtractTrackerMetadataFromCookiesDatFile(string filePath, out long userId, out string? browserTrackerId)
        {
            userId = 0;
            browserTrackerId = null;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(filePath));
                if (!doc.RootElement.TryGetProperty("CookiesData", out var dataProp))
                    return false;

                string? base64 = dataProp.GetString();
                if (string.IsNullOrWhiteSpace(base64))
                    return false;

                byte[] encrypted = Convert.FromBase64String(base64);
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string netscape = System.Text.Encoding.UTF8.GetString(plain);

                foreach (string rawLine in netscape.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    string[] cols = line.Split('\t');
                    if (cols.Length >= 7 && string.Equals(cols[5], "RBXEventTrackerV2", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = cols[6].Trim().TrimEnd(';');
                        foreach (string kv in val.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = kv.Substring(0, eq).Trim();
                            string v = kv.Substring(eq + 1).Trim();
                            if (string.Equals(key, "rbxuid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out userId);
                            }
                            else if (string.Equals(key, "browserid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                            {
                                browserTrackerId = v;
                            }
                        }
                        return userId > 0 || !string.IsNullOrEmpty(browserTrackerId);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[RobloxDirectLauncher] Could not extract tracker metadata from {Path.GetFileName(filePath)}: {ex.Message}");
            }

            return false;
        }

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
        /// Builds the command-line arguments for launching a Roblox game server directly.
        /// Both -t (ticket) and -j (joinUrl) must always be included so RobloxPlayerBeta.exe enters game-join mode instead of Home mode.
        /// </summary>
        public static string BuildGameLaunchArguments(string ticket, string joinUrl, string? browserTrackerId = null)
        {
            string args = $"--app -t \"{ticket}\" -j \"{joinUrl}\"";
            if (!string.IsNullOrWhiteSpace(browserTrackerId))
            {
                args += $" -b {browserTrackerId}";
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
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                args += $" -t \"{ticket}\"";
            }
            if (!string.IsNullOrWhiteSpace(browserTrackerId))
            {
                args += $" -b {browserTrackerId}";
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
                // Game launches use the one-time authentication ticket (-t) and PlaceLauncher joinUrl (-j)
                // redeemed directly in-memory by RobloxPlayerBeta.exe via auth.roblox.com/v1/authentication-ticket/redeem.
                // We intentionally DO NOT inject securityToken into the global %LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat
                // here, because writing to that shared file causes all open Roblox windows (and active WebView2 challenge tabs)
                // to detect "Security cookie value changed" and hijack other running instances into the same account.
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

                // Apply initial process optimizations (BelowNormal priority & Job Object CPU cap if enabled)
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
                // When an authentication ticket (-t) is provided, RobloxPlayerBeta.exe redeems it via WebLoginController
                // (/v1/authentication-ticket/redeem) and generates a native client-HBA-bound .ROBLOSECURITY cookie.
                // Only fall back to raw RobloxCookies.dat injection when no ticket is available.
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
